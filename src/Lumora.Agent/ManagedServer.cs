using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Lumora.Contracts;

namespace Lumora.Agent;

/// <summary>
/// One game server on this machine: installs it, runs the Java process, keeps the console and the player list,
/// makes backups and gives the admin access to its text files. Long operations run in the background and are
/// serialized; their progress is visible in <see cref="Report"/>.
/// </summary>
public sealed class ManagedServer : IAsyncDisposable
{
    private const int ConsoleCapacity = 2000;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(90);

    private readonly AgentPaths _paths;
    private readonly Installer _installer;
    private readonly ILogger _logger;
    private readonly Action _changed;
    private readonly SemaphoreSlim _op = new(1, 1);
    private readonly LinkedList<string> _console = new();
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly HashSet<string> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DateTimeOffset> _crashes = [];
    private readonly CancellationTokenSource _life = new();

    private Process? _process;
    private bool _stopRequested;
    private TaskCompletionSource? _saved;
    private TimeSpan _lastCpu;
    private DateTimeOffset _lastCpuAt;
    private double _cpu;
    private long _diskMb;
    private DateTimeOffset _diskAt;

    public ManagedServer(ManagedServerConfigDto config, AgentPaths paths, Installer installer, ILogger logger, Action changed)
    {
        Config = config;
        _paths = paths;
        _installer = installer;
        _logger = logger;
        _changed = changed;
        Directory.CreateDirectory(Dir);
        Marker = Installer.ReadMarker(Dir);
        State = Marker is null ? ManagedServerState.NotInstalled : ManagedServerState.Stopped;
    }

    public Guid Id => Config.ServerId;
    public ManagedServerConfigDto Config { get; private set; }
    public string Dir => _paths.ServerDir(Id);
    public InstallMarker? Marker { get; private set; }
    public ManagedServerState State { get; private set; }
    public string? Activity { get; private set; }
    public int? Progress { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public bool IsRunning => _process is { HasExited: false };

    // ---------------- Reporting ----------------

    public ServerStatusReportDto Report()
    {
        List<string> players;
        lock (_players)
            players = _players.Order(StringComparer.OrdinalIgnoreCase).ToList();
        long memory = 0;
        if (_process is { HasExited: false } p)
        {
            try
            {
                p.Refresh();
                memory = p.WorkingSet64 / (1024 * 1024);
                var now = DateTimeOffset.UtcNow;
                var cpu = p.TotalProcessorTime;
                if (_lastCpuAt != default)
                {
                    var wall = (now - _lastCpuAt).TotalMilliseconds;
                    if (wall > 0)
                        _cpu = Math.Round(Math.Clamp((cpu - _lastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100, 0, 100), 1);
                }
                _lastCpu = cpu;
                _lastCpuAt = now;
            }
            catch (InvalidOperationException) { }
        }
        else
            _cpu = 0;
        if (DateTimeOffset.UtcNow - _diskAt > TimeSpan.FromMinutes(5))
        {
            _diskAt = DateTimeOffset.UtcNow;
            _ = Task.Run(() => _diskMb = DirectorySizeMb(Dir));
        }
        return new ServerStatusReportDto(Id, State, Activity, Progress, Marker?.Revision, players, _cpu, memory, _diskMb, StartedAt, LastError);
    }

    /// <summary>Console lines produced since the last call (sent to the site in batches).</summary>
    public List<string> DrainConsole()
    {
        var lines = new List<string>();
        while (lines.Count < 500 && _pending.TryDequeue(out var l))
            lines.Add(l);
        return lines;
    }

    public string[] ConsoleSnapshot()
    {
        lock (_console)
            return _console.ToArray();
    }

    private void Log(string line)
    {
        lock (_console)
        {
            _console.AddLast(line);
            if (_console.Count > ConsoleCapacity)
                _console.RemoveFirst();
        }
        _pending.Enqueue(line);
    }

    private void Note(string text) => Log($"[Lumora] {text}");

    private void SetState(ManagedServerState state, string? activity = null, int? progress = null)
    {
        State = state;
        Activity = activity;
        Progress = progress;
        _changed();
    }

    // ---------------- Configuration ----------------

    public void ApplyConfig(ManagedServerConfigDto config)
    {
        var eulaChanged = config.EulaAccepted != Config.EulaAccepted;
        Config = config;
        if (eulaChanged && Marker is not null)
            WriteEula();
        _changed();
    }

    /// <summary>The site publishes a newer revision than the installed one.</summary>
    public bool UpdateAvailable => Config.BuildId is not null && (Marker is null || Marker.BuildId != Config.BuildId || Marker.Revision < Config.BuildRevision);

    // ---------------- Lifecycle (background operations) ----------------

    /// <summary>Starts a background operation unless another one is running.</summary>
    private AgentResult Run(string name, Func<CancellationToken, Task> work)
    {
        if (!_op.Wait(0))
            return AgentResult.Fail("Сервер занят другой операцией, подождите.");
        _ = Task.Run(async () =>
        {
            try
            {
                LastError = null;
                await work(_life.Token);
            }
            catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
            catch (Exception e)
            {
                LastError = e is AgentException ? e.Message : $"{name}: {e.Message}";
                Note($"Ошибка: {LastError}");
                _logger.LogWarning(e, "{Operation} failed for server {ServerId}", name, Id);
                if (State is ManagedServerState.Installing or ManagedServerState.Starting && !IsRunning)
                    SetState(Marker is null ? ManagedServerState.NotInstalled : ManagedServerState.Stopped);
                else
                    _changed();
            }
            finally
            {
                _op.Release();
            }
        });
        return AgentResult.Success;
    }

    public AgentResult Install() => Run("Установка", async ct =>
    {
        var wasRunning = IsRunning;
        if (wasRunning)
            await StopCoreAsync(ct);
        SetState(ManagedServerState.Installing, "Установка", 0);
        Note("Установка и обновление сервера…");
        Marker = await _installer.InstallAsync(Id, Dir, (a, p) => SetState(ManagedServerState.Installing, a, p), Note, ct);
        WriteProperties();
        WriteEula();
        SetState(ManagedServerState.Stopped);
        if (wasRunning)
            await StartCoreAsync(ct);
    });

    public AgentResult Start()
    {
        if (IsRunning)
            return AgentResult.Fail("Сервер уже запущен.");
        if (Marker is null)
            return AgentResult.Fail("Сервер ещё не установлен: нажмите «Установить».");
        if (!Config.EulaAccepted)
            return AgentResult.Fail("Сначала примите EULA Minecraft в настройках сервера.");
        return Run("Запуск", StartCoreAsync);
    }

    public AgentResult Stop() => IsRunning ? Run("Остановка", StopCoreAsync) : AgentResult.Fail("Сервер не запущен.");

    public AgentResult Restart() => Marker is null
        ? AgentResult.Fail("Сервер ещё не установлен.")
        : Run("Перезапуск", async ct =>
        {
            if (IsRunning)
                await StopCoreAsync(ct);
            await StartCoreAsync(ct);
        });

    public AgentResult Kill()
    {
        if (!IsRunning)
            return AgentResult.Fail("Сервер не запущен.");
        _stopRequested = true;
        Note("Процесс остановлен принудительно.");
        try { _process!.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        return AgentResult.Success;
    }

    public AgentResult SendCommand(string command)
    {
        var line = command.Replace("\r", "").Replace("\n", " ").Trim();
        if (line.Length == 0)
            return AgentResult.Fail("Пустая команда.");
        if (!IsRunning)
            return AgentResult.Fail("Сервер не запущен.");
        Log($"> {line}");
        WriteLine(line);
        return AgentResult.Success;
    }

    /// <summary>Called by the scheduler and on agent start.</summary>
    public async Task StartIfIdleAsync()
    {
        if (!IsRunning && Marker is not null && Config.EulaAccepted && State is not ManagedServerState.Installing)
            Start();
        await Task.CompletedTask;
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        if (Marker is null)
            throw new AgentException("Сервер не установлен.");
        if (!Config.EulaAccepted)
            throw new AgentException("EULA не принята.");
        WriteProperties();
        WriteEula();
        var java = _installer.JavaExecutable(Marker.JavaMajorVersion);
        if (java.Length == 0)
            java = await _installer.EnsureJavaAsync(Marker.JavaMajorVersion, Note, ct);

        var psi = new ProcessStartInfo(java)
        {
            WorkingDirectory = Dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in JvmArguments())
            psi.ArgumentList.Add(arg);

        lock (_players)
            _players.Clear();
        _stopRequested = false;
        SetState(ManagedServerState.Starting, "Запуск");
        Note($"Запуск: {Path.GetFileName(java)} {string.Join(' ', psi.ArgumentList)}");
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnOutput(e.Data);
        process.ErrorDataReceived += (_, e) => OnOutput(e.Data);
        process.Exited += (_, _) => OnExited(process);
        if (!process.Start())
            throw new AgentException("Не удалось запустить Java.");
        _process = process;
        StartedAt = DateTimeOffset.UtcNow;
        _lastCpuAt = default;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    internal IEnumerable<string> JvmArguments()
    {
        var memory = Math.Max(512, Config.MemoryMb);
        yield return $"-Xms{Math.Min(1024, memory)}M";
        yield return $"-Xmx{memory}M";
        yield return $"-javaagent:authlib-injector.jar={Marker!.YggdrasilUrl}";
        foreach (var arg in Config.JvmArguments)
            yield return arg;
        if (Marker.Loader == LoaderType.NeoForge)
            yield return "@" + Installer.NeoForgeArgsFile(Marker.LoaderVersion!).Replace('\\', '/');
        else
        {
            yield return "-jar";
            yield return "server.jar";
        }
        yield return "nogui";
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        var process = _process;
        if (process is null || process.HasExited)
            return;
        _stopRequested = true;
        SetState(ManagedServerState.Stopping, "Остановка");
        Note("Сохраняю мир и останавливаю сервер…");
        WriteLine("stop");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StopTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Note("Сервер не остановился за 90 секунд — завершаю процесс.");
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private void OnOutput(string? data)
    {
        if (data is null)
            return;
        var line = LogParser.Clean(data);
        Log(line);
        if (State == ManagedServerState.Starting && LogParser.IsReady(line))
        {
            SetState(ManagedServerState.Running);
            Note("Сервер готов принимать игроков.");
        }
        else if (LogParser.JoinedPlayer(line) is { } joined)
        {
            lock (_players)
                _players.Add(joined);
            _changed();
        }
        else if (LogParser.LeftPlayer(line) is { } left)
        {
            lock (_players)
                _players.Remove(left);
            _changed();
        }
        else if (LogParser.IsSaved(line))
            _saved?.TrySetResult();
    }

    private void OnExited(Process process)
    {
        int code;
        try { code = process.ExitCode; }
        catch (InvalidOperationException) { code = -1; }
        if (!ReferenceEquals(process, _process))
            return;
        _process = null;
        StartedAt = null;
        lock (_players)
            _players.Clear();
        if (_stopRequested || _life.IsCancellationRequested)
        {
            Note($"Сервер остановлен (код {code}).");
            SetState(ManagedServerState.Stopped);
            return;
        }

        LastError = $"Сервер упал (код выхода {code}). Смотрите консоль и crash-reports.";
        Note(LastError);
        SetState(ManagedServerState.Crashed);
        var now = DateTimeOffset.UtcNow;
        _crashes.RemoveAll(t => now - t > TimeSpan.FromMinutes(10));
        _crashes.Add(now);
        if (!Config.AutoRestart)
            return;
        if (_crashes.Count > 3)
        {
            Note("Сервер падает слишком часто — автоперезапуск отключён до ручного запуска.");
            return;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), _life.Token);
            if (!IsRunning && State == ManagedServerState.Crashed)
            {
                Note("Автоперезапуск после падения…");
                Start();
            }
        });
    }

    private void WriteLine(string line)
    {
        try
        {
            _process?.StandardInput.WriteLine(line);
            _process?.StandardInput.Flush();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            _logger.LogDebug(e, "Could not write to the server console");
        }
    }

    // ---------------- Files written by the agent ----------------

    private void WriteProperties()
    {
        var path = Path.Combine(Dir, "server.properties");
        var isNew = !File.Exists(path);
        var props = ServerPropertiesFile.Load(path);
        if (isNew)
        {
            props.Set("motd", Config.Name);
            props.Set("max-players", "20");
            props.Set("view-distance", "10");
            props.Set("simulation-distance", "10");
            props.Set("prevent-proxy-connections", "false");
        }
        foreach (var (k, v) in Config.Properties)
            props.Set(k, v);
        // Lumora needs these: players are checked by the site through authlib-injector.
        props.Set("server-port", Config.Port.ToString());
        props.Set("online-mode", "true");
        props.Set("enforce-secure-profile", "false");
        props.Save(path);
    }

    private void WriteEula() =>
        File.WriteAllText(Path.Combine(Dir, "eula.txt"),
            $"# Accepted on the Lumora site by an administrator: https://aka.ms/MinecraftEULA\neula={(Config.EulaAccepted ? "true" : "false")}\n");

    // ---------------- Files for the admin ----------------

    public ServerFileListDto ListFiles(string? path)
    {
        var full = PathGuard.Resolve(Dir, path);
        if (full is null || !Directory.Exists(full))
            return new ServerFileListDto(false, "Папка не найдена.", path ?? "", []);
        var entries = new DirectoryInfo(full).EnumerateFileSystemInfos()
            .Where(i => !i.Name.StartsWith(".lumora", StringComparison.OrdinalIgnoreCase) && i.LinkTarget is null)
            .Select(i => new ServerFileEntryDto(i.Name, i is DirectoryInfo, i is FileInfo f ? f.Length : 0, i.LastWriteTimeUtc,
                i is FileInfo fi && PathGuard.IsReadable(fi.Name) && fi.Length <= PathGuard.MaxReadBytes))
            .OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(1000).ToList();
        return new ServerFileListDto(true, null, Normalize(path), entries);
    }

    public ServerFileContentDto ReadFile(string path)
    {
        var full = PathGuard.Resolve(Dir, path);
        if (full is null || !File.Exists(full))
            return new ServerFileContentDto(false, "Файл не найден.", path, null, true);
        if (!PathGuard.IsReadable(full))
            return new ServerFileContentDto(false, "Этот тип файлов нельзя открыть в браузере.", path, null, true);
        var info = new FileInfo(full);
        if (info.Length > PathGuard.MaxReadBytes)
            return new ServerFileContentDto(false, "Файл больше 1 МБ.", path, null, true);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return new ServerFileContentDto(true, null, Normalize(path), reader.ReadToEnd(), !PathGuard.IsEditable(full));
    }

    public AgentResult WriteFile(string path, string content)
    {
        var full = PathGuard.Resolve(Dir, path);
        if (full is null || Directory.Exists(full))
            return AgentResult.Fail("Недопустимый путь.");
        if (!PathGuard.IsEditable(full))
            return AgentResult.Fail("Можно сохранять только текстовые настройки (.properties, .json, .toml, .txt, .cfg, .yml…).");
        if (Encoding.UTF8.GetByteCount(content) > PathGuard.MaxWriteBytes)
            return AgentResult.Fail("Файл больше 512 КБ.");
        if (!Directory.Exists(Path.GetDirectoryName(full)))
            return AgentResult.Fail("Папка не существует.");
        var tmp = full + ".lumora-tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, full, overwrite: true);
        Note($"Файл {Normalize(path)} изменён на сайте.");
        return AgentResult.Success;
    }

    private static string Normalize(string? path) => (path ?? "").Replace('\\', '/').Trim('/');

    // ---------------- Backups ----------------

    private string BackupDir => _paths.BackupDir(Id);

    public IReadOnlyList<BackupDto> ListBackups()
    {
        if (!Directory.Exists(BackupDir))
            return [];
        return new DirectoryInfo(BackupDir).EnumerateFiles("*.tar.gz")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupDto(f.Name, f.Length, f.CreationTimeUtc)).ToList();
    }

    public AgentResult CreateBackup(string reason = "вручную") => Run("Бэкап", ct => BackupCoreAsync(reason, ct));

    private string WorldName => ServerPropertiesFile.Load(Path.Combine(Dir, "server.properties")).Get("level-name") is { Length: > 0 } n
        && PathGuard.Resolve(Dir, n) is not null ? n : "world";

    private async Task BackupCoreAsync(string reason, CancellationToken ct)
    {
        var world = Path.Combine(Dir, WorldName);
        if (!Directory.Exists(world))
            throw new AgentException("Мира ещё нет — запустите сервер хотя бы раз.");
        var previous = (State, Activity);
        Activity = "Бэкап мира";
        _changed();
        var running = IsRunning;
        try
        {
            if (running)
            {
                WriteLine("save-off");
                _saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                WriteLine("save-all flush");
                try { await _saved.Task.WaitAsync(TimeSpan.FromMinutes(2), ct); }
                catch (TimeoutException) { Note("Сервер не подтвердил сохранение за 2 минуты — делаю бэкап как есть."); }
            }
            Directory.CreateDirectory(BackupDir);
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{WorldName}.tar.gz";
            var target = Path.Combine(BackupDir, name);
            var tmp = target + ".tmp";
            await using (var file = File.Create(tmp))
            await using (var gz = new GZipStream(file, CompressionLevel.Fastest))
            await using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
            {
                foreach (var path in Directory.EnumerateFiles(world, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(path) == "session.lock")
                        continue;
                    var entryName = Path.GetRelativePath(Dir, path).Replace('\\', '/');
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName) { DataStream = input };
                    await tar.WriteEntryAsync(entry, ct);
                }
            }
            File.Move(tmp, target);
            Note($"Бэкап {name} ({reason}) готов: {new FileInfo(target).Length / (1024 * 1024)} МБ.");
            Prune();
        }
        finally
        {
            if (running && IsRunning)
                WriteLine("save-on");
            _saved = null;
            if (State == previous.State)
                Activity = previous.Activity;
            _changed();
        }
    }

    private void Prune()
    {
        var keep = Math.Max(1, Config.BackupsToKeep);
        foreach (var old in ListBackups().Skip(keep))
        {
            File.Delete(Path.Combine(BackupDir, old.Name));
            Note($"Удалён старый бэкап {old.Name}.");
        }
    }

    public AgentResult RestoreBackup(string name)
    {
        var file = BackupPath(name);
        if (file is null)
            return AgentResult.Fail("Бэкап не найден.");
        if (IsRunning)
            return AgentResult.Fail("Остановите сервер перед восстановлением.");
        return Run("Восстановление", async ct =>
        {
            Activity = "Восстановление мира";
            _changed();
            var tmp = Path.Combine(Dir, ".lumora", "restore");
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            await using (var input = File.OpenRead(file))
            await using (var gz = new GZipStream(input, CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gz, tmp, overwriteFiles: true, ct);
            foreach (var restored in Directory.EnumerateDirectories(tmp))
            {
                var target = Path.Combine(Dir, Path.GetFileName(restored));
                if (Directory.Exists(target))
                    Directory.Delete(target, true);
                Directory.Move(restored, target);
            }
            Directory.Delete(tmp, true);
            Activity = null;
            Note($"Мир восстановлен из {name}.");
            _changed();
        });
    }

    public AgentResult DeleteBackup(string name)
    {
        var file = BackupPath(name);
        if (file is null)
            return AgentResult.Fail("Бэкап не найден.");
        File.Delete(file);
        Note($"Бэкап {name} удалён.");
        return AgentResult.Success;
    }

    private string? BackupPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\']) >= 0 || name.Contains("..") || !name.EndsWith(".tar.gz", StringComparison.Ordinal))
            return null;
        var path = Path.Combine(BackupDir, name);
        return File.Exists(path) ? path : null;
    }

    // ---------------- Shutdown ----------------

    /// <summary>Stops the game properly when the agent shuts down or the server is taken away from this machine.</summary>
    public async Task ShutdownAsync()
    {
        await _op.WaitAsync();
        try
        {
            if (IsRunning)
                await StopCoreAsync(CancellationToken.None);
        }
        finally
        {
            _op.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        await _life.CancelAsync();
        _life.Dispose();
    }

    private static long DirectorySizeMb(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / (1024 * 1024);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
