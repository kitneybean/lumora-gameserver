using Lumora.Agent;
using Lumora.Contracts;

// lumora-agent register --url <site> --token <token> [--name <name>] [--config <path>] [--data <dir>]
// lumora-agent run [--config <path>]
// lumora-agent version
var command = args.FirstOrDefault() ?? "run";
var options = ParseOptions(args.Skip(1).ToArray());
var configPath = options.GetValueOrDefault("config") ?? Environment.GetEnvironmentVariable("LUMORA_AGENT_CONFIG") ?? AgentConfig.DefaultConfigPath;

switch (command)
{
    case "version" or "--version":
        Console.WriteLine(AgentInfo.Version);
        return 0;

    case "register":
    {
        if (!options.TryGetValue("url", out var url) || !options.TryGetValue("token", out var token))
        {
            Console.Error.WriteLine("Использование: lumora-agent register --url https://site --token lmj_... [--name имя] [--config путь] [--data папка]");
            return 2;
        }
        var dataPath = Path.GetFullPath(options.GetValueOrDefault("data") ?? AgentConfig.DefaultDataPath);
        Directory.CreateDirectory(dataPath);
        try
        {
            var response = await SiteClient.RegisterAsync(url,
                new AgentRegisterRequest(token, options.GetValueOrDefault("name"), MachineInfo.Read(dataPath)), CancellationToken.None);
            new AgentConfig { Url = url.TrimEnd('/'), AgentId = response.AgentId, Name = response.Name, Key = response.Key, DataPath = dataPath }
                .Save(configPath);
            Console.WriteLine($"Агент «{response.Name}» зарегистрирован. Настройки: {configPath}");
            return 0;
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    case "run":
    {
        AgentConfig config;
        try
        {
            config = AgentConfig.Load(configPath);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Services.AddSystemd();
        builder.Services.AddWindowsService(o => o.ServiceName = "LumoraAgent");
        // Game servers need time to save worlds when the machine shuts down.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(150));
        builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Connections", LogLevel.Warning);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(new AgentPaths(config.DataPath));
        builder.Services.AddHostedService<AgentWorker>();
        await builder.Build().RunAsync();
        return 0;
    }

    default:
        Console.Error.WriteLine($"Неизвестная команда {command}. Команды: register, run, version.");
        return 2;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--") && i + 1 < args.Length)
            result[args[i][2..]] = args[++i];
    }
    return result;
}
