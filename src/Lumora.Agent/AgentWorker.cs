using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Lumora.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace Lumora.Agent;

/// <summary>
/// Keeps the connection to the site, runs the servers it is told to run, sends status, console and heartbeats,
/// and fires scheduled restarts and backups.
/// </summary>
public sealed class AgentWorker(AgentConfig config, AgentPaths paths, ILoggerFactory loggers) : BackgroundService
{
    private readonly ILogger _log = loggers.CreateLogger("Lumora.Agent");
    private readonly ConcurrentDictionary<Guid, ManagedServer> _servers = new();
    private readonly ConcurrentDictionary<Guid, byte> _dirty = new();
    private SiteClient _site = null!;
    private Installer _installer = null!;
    private HubConnection _hub = null!;
    private bool _autoStartDone;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(paths.Servers);
        _site = new SiteClient(config.Url, config.Key);
        _installer = new Installer(paths, _site, _log);
        _hub = new HubConnectionBuilder()
            .WithUrl(config.Url.TrimEnd('/') + "/agent/hub", o => o.AccessTokenProvider = () => Task.FromResult<string?>(config.Key))
            .WithAutomaticReconnect(new ForeverRetry())
            .Build();
        _hub.ServerTimeout = TimeSpan.FromSeconds(60);
        _hub.KeepAliveInterval = TimeSpan.FromSeconds(15);
        RegisterHandlers();
        _hub.Reconnected += async _ =>
        {
            _log.LogInformation("Reconnected to {Url}", config.Url);
            await HelloAsync(stoppingToken);
        };

        _ = Task.Run(() => LoopAsync(TimeSpan.FromMilliseconds(300), FlushAsync, stoppingToken), stoppingToken);
        _ = Task.Run(() => LoopAsync(TimeSpan.FromSeconds(10), HeartbeatAsync, stoppingToken), stoppingToken);
        _ = Task.Run(() => SchedulerAsync(stoppingToken), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_hub.State == HubConnectionState.Disconnected)
            {
                try
                {
                    await _hub.StartAsync(stoppingToken);
                    _log.LogInformation("Connected to {Url} as agent {Name}", config.Url, config.Name);
                    await HelloAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _log.LogWarning("Cannot connect to {Url}: {Message}", config.Url, e.Message);
                    if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
                        _log.LogError("The site does not accept this agent key: the agent was revoked or deleted. Register it again.");
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("Stopping game servers…");
        await Task.WhenAll(_servers.Values.Select(s => s.ShutdownAsync()));
        await FlushAsync(CancellationToken.None);
        if (_hub is not null)
            await _hub.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }

    private async Task HelloAsync(CancellationToken ct)
    {
        var configs = await _hub.InvokeAsync<List<ManagedServerConfigDto>>(AgentHubMethods.Hello, MachineInfo.Read(paths.Root), ct);
        var wanted = configs.Select(c => c.ServerId).ToHashSet();
        foreach (var c in configs)
            Apply(c);
        foreach (var id in _servers.Keys.Where(id => !wanted.Contains(id)).ToList())
            await RemoveAsync(id, false);
        if (!_autoStartDone)
        {
            _autoStartDone = true;
            foreach (var s in _servers.Values.Where(s => s.Config.AutoStart))
                await s.StartIfIdleAsync();
        }
        foreach (var id in _servers.Keys)
            _dirty[id] = 1;
    }

    private AgentResult Apply(ManagedServerConfigDto c)
    {
        if (_servers.TryGetValue(c.ServerId, out var existing))
            existing.ApplyConfig(c);
        else
        {
            var server = new ManagedServer(c, paths, _installer, _log, () => _dirty[c.ServerId] = 1);
            _servers[c.ServerId] = server;
            _log.LogInformation("Server {Name} ({Id}) assigned to this agent", c.Name, c.ServerId);
        }
        _dirty[c.ServerId] = 1;
        return AgentResult.Success;
    }

    private async Task<AgentResult> RemoveAsync(Guid id, bool deleteFiles)
    {
        if (!_servers.TryRemove(id, out var server))
            return AgentResult.Success;
        _log.LogInformation("Server {Id} is no longer assigned to this agent", id);
        await server.DisposeAsync();
        if (deleteFiles && Directory.Exists(server.Dir))
            Directory.Delete(server.Dir, true);
        return AgentResult.Success;
    }

    private void RegisterHandlers()
    {
        AgentResult With(Guid id, Func<ManagedServer, AgentResult> action) =>
            _servers.TryGetValue(id, out var s) ? Safe(() => action(s)) : AgentResult.Fail("Этот сервер не назначен агенту.");

        _hub.On<ManagedServerConfigDto, AgentResult>(AgentMethods.ApplyConfig, c => Safe(() => Apply(c)));
        _hub.On<Guid, bool, AgentResult>(AgentMethods.RemoveServer, (id, delete) => RemoveAsync(id, delete));
        _hub.On<Guid, AgentResult>(AgentMethods.Install, id => With(id, s => s.Install()));
        _hub.On<Guid, AgentResult>(AgentMethods.Start, id => With(id, s => s.Start()));
        _hub.On<Guid, AgentResult>(AgentMethods.Stop, id => With(id, s => s.Stop()));
        _hub.On<Guid, AgentResult>(AgentMethods.Restart, id => With(id, s => s.Restart()));
        _hub.On<Guid, AgentResult>(AgentMethods.Kill, id => With(id, s => s.Kill()));
        _hub.On<Guid, string, AgentResult>(AgentMethods.SendCommand, (id, cmd) => With(id, s => s.SendCommand(cmd)));
        _hub.On<Guid, string[]>(AgentMethods.GetConsole, id => _servers.TryGetValue(id, out var s) ? s.ConsoleSnapshot() : []);
        _hub.On<Guid, string, ServerFileListDto>(AgentMethods.ListFiles, (id, path) =>
            _servers.TryGetValue(id, out var s) ? Try(() => s.ListFiles(path), e => new ServerFileListDto(false, e, path, []))
                : new ServerFileListDto(false, "Сервер не найден.", path, []));
        _hub.On<Guid, string, ServerFileContentDto>(AgentMethods.ReadFile, (id, path) =>
            _servers.TryGetValue(id, out var s) ? Try(() => s.ReadFile(path), e => new ServerFileContentDto(false, e, path, null, true))
                : new ServerFileContentDto(false, "Сервер не найден.", path, null, true));
        _hub.On<Guid, string, string, AgentResult>(AgentMethods.WriteFile, (id, path, content) => With(id, s => s.WriteFile(path, content)));
        _hub.On<Guid, BackupDto[]>(AgentMethods.ListBackups, id => _servers.TryGetValue(id, out var s) ? s.ListBackups().ToArray() : []);
        _hub.On<Guid, AgentResult>(AgentMethods.CreateBackup, id => With(id, s => s.CreateBackup()));
        _hub.On<Guid, string, AgentResult>(AgentMethods.RestoreBackup, (id, name) => With(id, s => s.RestoreBackup(name)));
        _hub.On<Guid, string, AgentResult>(AgentMethods.DeleteBackup, (id, name) => With(id, s => s.DeleteBackup(name)));
    }

    private AgentResult Safe(Func<AgentResult> action) =>
        Try(action, e => AgentResult.Fail(e));

    private T Try<T>(Func<T> action, Func<string, T> onError)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AgentException or InvalidOperationException)
        {
            _log.LogWarning(e, "Command failed");
            return onError(e.Message);
        }
    }

    // ---------------- Loops ----------------

    private static async Task LoopAsync(TimeSpan every, Func<CancellationToken, Task> body, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await body(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception) { /* connection hiccups: the next round retries */ }
            try { await Task.Delay(every, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_hub?.State != HubConnectionState.Connected)
            return;
        foreach (var server in _servers.Values)
        {
            var lines = server.DrainConsole();
            if (lines.Count > 0)
                await _hub.SendAsync(AgentHubMethods.Console, new ConsoleLinesDto(server.Id, lines), ct);
        }
        foreach (var id in _dirty.Keys.ToList())
        {
            _dirty.TryRemove(id, out _);
            if (_servers.TryGetValue(id, out var s))
                await _hub.SendAsync(AgentHubMethods.Status, s.Report(), ct);
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        if (_hub?.State != HubConnectionState.Connected)
            return;
        var (cpu, used) = MachineInfo.Usage();
        await _hub.SendAsync(AgentHubMethods.Heartbeat,
            new AgentHeartbeatDto(MachineInfo.Read(paths.Root), cpu, used, _servers.Values.Select(s => s.Report()).ToList()), ct);
    }

    private async Task SchedulerAsync(CancellationToken ct)
    {
        var last = DateTime.Now;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
            catch (OperationCanceledException) { break; }
            var now = DateTime.Now;
            foreach (var s in _servers.Values)
            {
                try
                {
                    foreach (var t in s.Config.BackupTimes)
                        if (Schedule.Crossed(t, TimeSpan.Zero, last, now))
                            s.CreateBackup("по расписанию");
                    if (!s.IsRunning)
                        continue;
                    foreach (var t in s.Config.RestartTimes)
                    {
                        if (Schedule.Crossed(t, TimeSpan.FromMinutes(-5), last, now))
                            s.SendCommand("say Перезапуск сервера через 5 минут.");
                        if (Schedule.Crossed(t, TimeSpan.FromMinutes(-1), last, now))
                            s.SendCommand("say Перезапуск сервера через 1 минуту.");
                        if (Schedule.Crossed(t, TimeSpan.Zero, last, now))
                            s.Restart();
                    }
                }
                catch (Exception e) when (e is IOException or InvalidOperationException)
                {
                    _log.LogWarning(e, "Scheduled task failed for {Server}", s.Id);
                }
            }
            last = now;
        }
    }

    private sealed class ForeverRetry : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

        public TimeSpan? NextRetryDelay(RetryContext context) =>
            context.PreviousRetryCount < Delays.Length ? Delays[context.PreviousRetryCount] : TimeSpan.FromSeconds(30);
    }
}

/// <summary>Facts about this machine for the admin panel.</summary>
public static class MachineInfo
{
    public static AgentMachineDto Read(string dataPath)
    {
        long free = 0, total = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dataPath))!);
            free = drive.AvailableFreeSpace / (1024 * 1024);
            total = drive.TotalSize / (1024 * 1024);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
        return new AgentMachineDto(AgentInfo.Version, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Environment.MachineName, Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024), free, total);
    }

    private static (long Idle, long Total)? _lastCpu;

    /// <summary>Machine CPU % and used memory (MB); Linux reads /proc, other systems report what .NET knows.</summary>
    public static (double Cpu, long UsedMb) Usage()
    {
        if (!OperatingSystem.IsLinux())
            return (0, 0);
        double cpu = 0;
        long used = 0;
        try
        {
            var parts = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
            var idle = parts[3] + (parts.Length > 4 ? parts[4] : 0);
            var total = parts.Sum();
            if (_lastCpu is { } prev && total > prev.Total)
                cpu = Math.Round(100.0 * (1 - (double)(idle - prev.Idle) / (total - prev.Total)), 1);
            _lastCpu = (idle, total);

            long memTotal = 0, available = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:"))
                    memTotal = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
                else if (line.StartsWith("MemAvailable:"))
                    available = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
            }
            used = (memTotal - available) / 1024;
        }
        catch (Exception e) when (e is IOException or FormatException or InvalidOperationException or IndexOutOfRangeException) { }
        return (cpu, used);
    }
}
