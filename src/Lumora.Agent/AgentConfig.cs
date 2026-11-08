using System.Text.Json;

namespace Lumora.Agent;

/// <summary>agent.json: where the site is and the agent key. Written once by "register", read by "run".</summary>
public sealed class AgentConfig
{
    public string Url { get; set; } = "";
    public Guid AgentId { get; set; }
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public string DataPath { get; set; } = "";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultConfigPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LumoraAgent", "agent.json")
        : "/etc/lumora-agent/agent.json";

    public static string DefaultDataPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LumoraAgent", "data")
        : "/var/lib/lumora-agent";

    public static AgentConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Нет файла настроек {path}. Сначала зарегистрируйте агента: lumora-agent register --url <сайт> --token <токен>");
        var config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"Файл {path} пуст.");
        if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.Key))
            throw new InvalidOperationException($"В {path} нет адреса сайта или ключа агента.");
        if (string.IsNullOrWhiteSpace(config.DataPath))
            config.DataPath = DefaultDataPath;
        return config;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Folders inside the agent data path.</summary>
public sealed class AgentPaths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string Servers => Path.Combine(Root, "servers");
    public string Backups => Path.Combine(Root, "backups");
    public string Java => Path.Combine(Root, "java");
    public string Cache => Path.Combine(Root, "cache");

    public string ServerDir(Guid id) => Path.Combine(Servers, id.ToString("N"));
    public string BackupDir(Guid id) => Path.Combine(Backups, id.ToString("N"));
}
