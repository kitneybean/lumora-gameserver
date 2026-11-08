using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Lumora.Contracts;
using Lumora.Shared.Sync;

namespace Lumora.Agent;

/// <summary>What is installed in a server folder (.lumora/install.json).</summary>
public sealed record InstallMarker(Guid BuildId, int Revision, string MinecraftVersion, LoaderType Loader, string? LoaderVersion, int JavaMajorVersion, string YggdrasilUrl);

/// <summary>Installs Java, the server core (NeoForge or vanilla), authlib-injector and the build files.</summary>
public sealed class Installer(AgentPaths paths, SiteClient site, ILogger logger)
{
    private static readonly SemaphoreSlim JavaLock = new(1, 1);

    public static string MarkerPath(string serverDir) => Path.Combine(serverDir, ".lumora", "install.json");

    public static InstallMarker? ReadMarker(string serverDir)
    {
        try
        {
            var path = MarkerPath(serverDir);
            return File.Exists(path) ? JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(path)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Full install or update. <paramref name="progress"/> gets (activity, percent, console line).</summary>
    public async Task<InstallMarker> InstallAsync(Guid serverId, string dir, Action<string, int?> progress, Action<string> log, CancellationToken ct)
    {
        progress("Получаю манифест сборки", 0);
        var manifest = await site.GetManifestAsync(serverId, ct);
        if (manifest.Loader is not (LoaderType.NeoForge or LoaderType.Vanilla))
            throw new AgentException($"Сборки с загрузчиком {manifest.Loader} агент пока не ставит: поддерживаются NeoForge и ванильный Minecraft.");
        if (manifest.AuthlibInjector is null)
            throw new AgentException("Сайт ещё не скачал authlib-injector — без него вход игроков не заработает. Попробуйте через минуту.");
        Directory.CreateDirectory(Path.Combine(dir, ".lumora"));
        var previous = ReadMarker(dir);

        progress($"Java {manifest.JavaMajorVersion}", 5);
        var java = await EnsureJavaAsync(manifest.JavaMajorVersion, log, ct);

        var coreChanged = previous is null || previous.Loader != manifest.Loader || previous.LoaderVersion != manifest.LoaderVersion
            || previous.MinecraftVersion != manifest.MinecraftVersion;
        if (coreChanged || !CoreLooksInstalled(dir, manifest))
        {
            if (manifest.Loader == LoaderType.NeoForge)
            {
                progress($"NeoForge {manifest.LoaderVersion}", 20);
                await InstallNeoForgeAsync(dir, java, manifest.LoaderVersion!, log, ct);
            }
            else
            {
                progress($"Minecraft {manifest.MinecraftVersion}", 20);
                await InstallVanillaAsync(dir, manifest.MinecraftVersion, ct);
            }
        }

        progress("authlib-injector", 45);
        var injector = Path.Combine(dir, "authlib-injector.jar");
        if (!File.Exists(injector) || await SiteClient.HashAsync(injector, HashAlgorithmName.SHA256, ct) != manifest.AuthlibInjector.Sha256)
            await site.DownloadAsync(manifest.AuthlibInjector.Url, injector, manifest.AuthlibInjector.Sha256, HashAlgorithmName.SHA256, ct);

        progress("Файлы сборки", 50);
        await SyncFilesAsync(dir, manifest, progress, log, ct);

        var marker = new InstallMarker(manifest.BuildId, manifest.Revision, manifest.MinecraftVersion, manifest.Loader,
            manifest.LoaderVersion, manifest.JavaMajorVersion, manifest.YggdrasilUrl);
        await File.WriteAllTextAsync(MarkerPath(dir), JsonSerializer.Serialize(marker), ct);
        progress("Готово", 100);
        log($"Установлена сборка «{manifest.BuildName}» r{manifest.Revision} ({manifest.MinecraftVersion}, {manifest.Loader} {manifest.LoaderVersion}).");
        return marker;
    }

    private static bool CoreLooksInstalled(string dir, ServerManifestDto m) => m.Loader == LoaderType.NeoForge
        ? File.Exists(Path.Combine(dir, NeoForgeArgsFile(m.LoaderVersion!)))
        : File.Exists(Path.Combine(dir, "server.jar"));

    public static string NeoForgeArgsFile(string version) =>
        Path.Combine("libraries", "net", "neoforged", "neoforge", version, OperatingSystem.IsWindows() ? "win_args.txt" : "unix_args.txt");

    // ---------------- Java ----------------

    public string JavaExecutable(int major)
    {
        var home = Path.Combine(paths.Java, major.ToString());
        var exe = OperatingSystem.IsWindows() ? "java.exe" : "java";
        if (!Directory.Exists(home))
            return "";
        var found = Directory.EnumerateFiles(home, exe, SearchOption.AllDirectories)
            .FirstOrDefault(p => Path.GetFileName(Path.GetDirectoryName(p)) == "bin");
        if (found is null)
            return "";
        // A half-deleted runtime (disk cleanup, antivirus) cannot start: treat it as missing so it is downloaded again.
        var root = Path.GetDirectoryName(Path.GetDirectoryName(found))!;
        return File.Exists(Path.Combine(root, "lib", "jvm.cfg")) && File.Exists(Path.Combine(root, "lib", "modules")) ? found : "";
    }

    /// <summary>Eclipse Temurin JRE from api.adoptium.net, kept in &lt;data&gt;/java/&lt;major&gt;.</summary>
    public async Task<string> EnsureJavaAsync(int major, Action<string> log, CancellationToken ct)
    {
        await JavaLock.WaitAsync(ct);
        try
        {
            var existing = JavaExecutable(major);
            if (existing.Length > 0)
                return existing;

            var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "aarch64",
                var a => throw new AgentException($"Архитектура {a} не поддерживается."),
            };
            log($"Скачиваю Java {major} (Eclipse Temurin JRE, {os}/{arch})…");
            var json = await site.GetStringAsync(
                $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture={arch}&image_type=jre&os={os}&vendor=eclipse", ct);
            using var doc = JsonDocument.Parse(json);
            var package = doc.RootElement.EnumerateArray().First().GetProperty("binary").GetProperty("package");
            var link = package.GetProperty("link").GetString()!;
            var checksum = package.GetProperty("checksum").GetString()!;
            var archive = Path.Combine(paths.Cache, package.GetProperty("name").GetString()!);
            await site.DownloadAsync(link, archive, checksum, HashAlgorithmName.SHA256, ct);

            var home = Path.Combine(paths.Java, major.ToString());
            var tmp = home + ".tmp";
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(archive, tmp);
            else
            {
                await using var file = File.OpenRead(archive);
                await using var gz = new GZipStream(file, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gz, tmp, overwriteFiles: true, ct);
            }
            if (Directory.Exists(home))
                Directory.Delete(home, true);
            Directory.Move(tmp, home);
            File.Delete(archive);
            var exe = JavaExecutable(major);
            if (exe.Length == 0)
                throw new AgentException("В архиве Java не нашлось bin/java.");
            log($"Java {major} установлена.");
            return exe;
        }
        finally
        {
            JavaLock.Release();
        }
    }

    // ---------------- Server core ----------------

    private async Task InstallNeoForgeAsync(string dir, string java, string version, Action<string> log, CancellationToken ct)
    {
        var url = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar";
        var installer = Path.Combine(paths.Cache, $"neoforge-{version}-installer.jar");
        string? sha1 = null;
        try
        {
            sha1 = (await site.GetStringAsync(url + ".sha1", ct)).Trim().Split(' ')[0];
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("No .sha1 next to the NeoForge installer {Url}", url);
        }
        await site.DownloadAsync(url, installer, sha1, HashAlgorithmName.SHA1, ct);
        log($"Запускаю установщик NeoForge {version}…");
        // The installer lists every extracted file; keep only its own messages.
        var code = await RunAsync(java, ["-jar", installer, "--installServer", dir], dir,
            line => { if (line.Length > 0 && !char.IsWhiteSpace(line[0])) log(line); }, ct);
        if (code != 0 || !File.Exists(Path.Combine(dir, NeoForgeArgsFile(version))))
            throw new AgentException($"Установщик NeoForge завершился с кодом {code}. Подробности — в консоли сервера.");
        foreach (var junk in new[] { "run.sh", "run.bat", "user_jvm_args.txt" })
        {
            // The agent starts Java itself; these scripts would only confuse whoever looks into the folder.
            var p = Path.Combine(dir, junk);
            if (File.Exists(p))
                File.Delete(p);
        }
        foreach (var log2 in Directory.EnumerateFiles(dir, "*installer*.log"))
            File.Delete(log2);
    }

    private async Task InstallVanillaAsync(string dir, string version, CancellationToken ct)
    {
        using var manifest = JsonDocument.Parse(await site.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct));
        var entry = manifest.RootElement.GetProperty("versions").EnumerateArray()
            .FirstOrDefault(v => v.GetProperty("id").GetString() == version);
        if (entry.ValueKind == JsonValueKind.Undefined)
            throw new AgentException($"Mojang не знает версию {version}.");
        using var versionJson = JsonDocument.Parse(await site.GetStringAsync(entry.GetProperty("url").GetString()!, ct));
        var server = versionJson.RootElement.GetProperty("downloads").GetProperty("server");
        await site.DownloadAsync(server.GetProperty("url").GetString()!, Path.Combine(dir, "server.jar"),
            server.GetProperty("sha1").GetString(), HashAlgorithmName.SHA1, ct);
    }

    // ---------------- Build files ----------------

    private async Task SyncFilesAsync(string dir, ServerManifestDto manifest, Action<string, int?> progress, Action<string> log, CancellationToken ct)
    {
        var index = new FileHashIndex(dir, Path.Combine(dir, ".lumora", "files.json"));
        var strict = manifest.SyncRules.Where(r => r.Policy == SyncPolicy.Strict)
            .SelectMany(r => EnumerateRelative(dir, r.Path)).ToList();
        var plan = FileSyncPlanner.Plan(manifest.Files, manifest.SyncRules, new Dictionary<string, bool>(), index.Sha1Of, strict);

        foreach (var path in plan.Deletions)
        {
            if (PathGuard.Resolve(dir, path) is { } full && File.Exists(full))
            {
                File.Delete(full);
                index.Forget(path);
                log($"Удалён {path}");
            }
        }
        for (var i = 0; i < plan.Downloads.Count; i++)
        {
            var file = plan.Downloads[i];
            var full = PathGuard.Resolve(dir, file.Path) ?? throw new AgentException($"Недопустимый путь в сборке: {file.Path}");
            progress($"Файлы сборки: {i + 1} из {plan.Downloads.Count}", 50 + 45 * (i + 1) / Math.Max(1, plan.Downloads.Count));
            await site.DownloadAsync(file.Url, full, file.Sha1, HashAlgorithmName.SHA1, ct);
            index.Forget(file.Path);
        }
        if (plan.Downloads.Count > 0 || plan.Deletions.Count > 0)
            log($"Файлы сборки: скачано {plan.Downloads.Count}, удалено {plan.Deletions.Count}.");
        index.Save();
    }

    private static IEnumerable<string> EnumerateRelative(string root, string folder)
    {
        var full = PathGuard.Resolve(root, folder);
        if (full is null || !Directory.Exists(full))
            return [];
        return Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'));
    }

    // ---------------- Processes ----------------

    private static async Task<int> RunAsync(string exe, IEnumerable<string> args, string cwd, Action<string> log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new AgentException($"Не удалось запустить {exe}.");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log(LogParser.Clean(e.Data)); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log(LogParser.Clean(e.Data)); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return process.ExitCode;
    }
}
