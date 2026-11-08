using System.Text.Json;
using Lumora.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumora.Agent.Tests;

public class LogParserTests
{
    [Fact]
    public void Recognizes_readiness_players_and_saves()
    {
        Assert.True(LogParser.IsReady("[12:00:01] [Server thread/INFO] [minecraft/DedicatedServer]: Done (5.123s)! For help, type \"help\""));
        Assert.Equal("Steve_1", LogParser.JoinedPlayer("[12:00:02] [Server thread/INFO] [minecraft/MinecraftServer]: Steve_1 joined the game"));
        Assert.Equal("Alex", LogParser.LeftPlayer("[12:00:03] [Server thread/INFO] [minecraft/MinecraftServer]: Alex left the game"));
        Assert.True(LogParser.IsSaved("[12:00:04] [Server thread/INFO] [minecraft/MinecraftServer]: Saved the game"));
        // Chat cannot fake a join: the name must directly follow "]: ".
        Assert.Null(LogParser.JoinedPlayer("[12:00:05] [Server thread/INFO]: <Bob> Eve joined the game"));
        Assert.Equal("plain", LogParser.Clean("\u001b[32mplain\u001b[0m\r"));
    }
}

public class ServerPropertiesTests
{
    [Fact]
    public void Changes_only_given_keys_and_keeps_comments()
    {
        var file = ServerPropertiesFile.Parse("#Minecraft server properties\nmotd=Old\nserver-port=25565\n");
        file.Set("server-port", "25600");
        file.Set("max-players", "50");
        file.Set("motd", "Привет");
        var text = file.ToString();
        Assert.StartsWith("#Minecraft server properties\nmotd=\\u041f", text);
        Assert.Contains("server-port=25600", text);
        Assert.EndsWith("max-players=50\n", text);
        Assert.Equal("Привет", ServerPropertiesFile.Parse(text).Get("motd"));
    }
}

public class ScheduleTests
{
    [Fact]
    public void Fires_once_when_the_time_is_crossed()
    {
        var from = new DateTime(2026, 11, 8, 3, 59, 50);
        Assert.True(Schedule.Crossed("04:00", TimeSpan.Zero, from, from.AddSeconds(20)));
        Assert.False(Schedule.Crossed("04:00", TimeSpan.Zero, from.AddSeconds(20), from.AddSeconds(40)));
        Assert.True(Schedule.Crossed("04:00", TimeSpan.FromMinutes(-5), new DateTime(2026, 11, 8, 3, 54, 50), new DateTime(2026, 11, 8, 3, 55, 10)));
        // Around midnight and with a warning that falls on the previous day.
        Assert.True(Schedule.Crossed("00:02", TimeSpan.FromMinutes(-5), new DateTime(2026, 11, 8, 23, 56, 50), new DateTime(2026, 11, 8, 23, 57, 10)));
        // A sleeping machine does not fire hours later.
        Assert.False(Schedule.Crossed("04:00", TimeSpan.Zero, from, from.AddHours(3)));
    }
}

public sealed class PathGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lumora-guard").FullName;

    [Theory]
    [InlineData("server.properties", true)]
    [InlineData("config/neoforge-server.toml", true)]
    [InlineData("../outside.txt", false)]
    [InlineData("config/../../outside.txt", false)]
    [InlineData(".lumora/install.json", false)]
    [InlineData("/etc/passwd", true)] // leading slash is relative to the server folder
    public void Keeps_paths_inside_the_server(string path, bool allowed)
    {
        var full = PathGuard.Resolve(_root, path);
        Assert.Equal(allowed, full is not null);
        if (full is not null)
            Assert.StartsWith(Path.GetFullPath(_root), full);
    }

    [Fact]
    public void Only_text_settings_are_editable()
    {
        Assert.True(PathGuard.IsEditable("server.properties"));
        Assert.True(PathGuard.IsEditable("config/x.toml"));
        Assert.False(PathGuard.IsEditable("mods/x.jar"));
        Assert.False(PathGuard.IsEditable("logs/latest.log"));
        Assert.True(PathGuard.IsReadable("logs/latest.log"));
    }

    public void Dispose() => Directory.Delete(_root, true);
}

public sealed class ManagedServerTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("lumora-agent").FullName;
    private readonly AgentPaths _paths;
    private readonly SiteClient _site = new("https://site.test", "lma_test");

    public ManagedServerTests() => _paths = new AgentPaths(_data);

    private static ManagedServerConfigDto Config(Guid id, int keep = 2) => new(
        id, "Test", 25600, 3072, ["-XX:+UseG1GC"], false, true, [], [], keep, true, Guid.NewGuid(), 3,
        new Dictionary<string, string> { ["max-players"] = "42" });

    private ManagedServer Create(ManagedServerConfigDto config) =>
        new(config, _paths, new Installer(_paths, _site, NullLogger.Instance), NullLogger.Instance, () => { });

    [Fact]
    public async Task Backups_are_made_pruned_and_restored()
    {
        var id = Guid.NewGuid();
        var server = Create(Config(id, keep: 2));
        var world = Path.Combine(server.Dir, "world");
        Directory.CreateDirectory(Path.Combine(world, "region"));
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "v1");
        await File.WriteAllTextAsync(Path.Combine(world, "region", "r.0.0.mca"), "chunks");

        for (var i = 0; i < 3; i++)
        {
            Assert.True(server.CreateBackup().Ok);
            await WaitAsync(() => server.ListBackups().Count == Math.Min(i + 1, 2) && server.Activity is null);
            await Task.Delay(1100); // backup names have one-second resolution
        }
        var backups = server.ListBackups();
        Assert.Equal(2, backups.Count);

        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "broken");
        Assert.True(server.RestoreBackup(backups[0].Name).Ok);
        await WaitAsync(() => File.ReadAllText(Path.Combine(world, "level.dat")) == "v1");
        Assert.Equal("chunks", await File.ReadAllTextAsync(Path.Combine(world, "region", "r.0.0.mca")));

        Assert.False(server.RestoreBackup("../../etc/passwd.tar.gz").Ok);
        Assert.True(server.DeleteBackup(backups[1].Name).Ok);
        Assert.Single(server.ListBackups());
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Start_needs_an_install_and_files_stay_inside()
    {
        var id = Guid.NewGuid();
        var server = Create(Config(id));
        Assert.Equal(ManagedServerState.NotInstalled, server.State);
        Assert.False(server.Start().Ok);

        await File.WriteAllTextAsync(Path.Combine(server.Dir, "server.properties"), "motd=Hi\n");
        Assert.True(server.WriteFile("server.properties", "motd=Changed\n").Ok);
        Assert.Equal("motd=Changed\n", server.ReadFile("server.properties").Content);
        Assert.False(server.WriteFile("../evil.properties", "x").Ok);
        Assert.False(server.WriteFile("mods/evil.jar", "x").Ok);
        Assert.False(server.ReadFile("../../agent.json").Ok);
        Assert.Contains(server.ListFiles("").Entries, e => e.Name == "server.properties" && e.IsEditable);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Jvm_arguments_use_memory_authlib_and_neoforge_args_file()
    {
        var id = Guid.NewGuid();
        var dir = _paths.ServerDir(id);
        Directory.CreateDirectory(Path.Combine(dir, ".lumora"));
        await File.WriteAllTextAsync(Installer.MarkerPath(dir), JsonSerializer.Serialize(
            new InstallMarker(Guid.NewGuid(), 3, "1.21.1", LoaderType.NeoForge, "21.1.77", 21, "https://site.test/api/yggdrasil")));

        var server = Create(Config(id));
        Assert.Equal(ManagedServerState.Stopped, server.State);
        var args = server.JvmArguments().ToList();
        Assert.Equal("-Xmx3072M", args[1]);
        Assert.Contains("-javaagent:authlib-injector.jar=https://site.test/api/yggdrasil", args);
        Assert.Contains("-XX:+UseG1GC", args);
        Assert.Contains(args, a => a.StartsWith("@libraries/net/neoforged/neoforge/21.1.77/") && a.EndsWith("_args.txt"));
        Assert.Equal("nogui", args[^1]);
        await server.DisposeAsync();
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(100);
        Assert.True(condition());
    }

    public void Dispose()
    {
        _site.Dispose();
        Directory.Delete(_data, true);
    }
}
