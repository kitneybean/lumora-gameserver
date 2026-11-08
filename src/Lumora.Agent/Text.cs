using System.Text;
using System.Text.RegularExpressions;

namespace Lumora.Agent;

/// <summary>Reading the Minecraft server log: readiness, players joining and leaving, saves.</summary>
internal static partial class LogParser
{
    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"\]: (?<name>[A-Za-z0-9_]{1,16}) joined the game\s*$")]
    private static partial Regex Joined();

    [GeneratedRegex(@"\]: (?<name>[A-Za-z0-9_]{1,16}) left the game\s*$")]
    private static partial Regex Left();

    [GeneratedRegex(@"\]: Done \([0-9.,]+s\)!")]
    private static partial Regex Done();

    [GeneratedRegex(@"\]: Saved the game\s*$")]
    private static partial Regex Saved();

    public static string Clean(string line) => Ansi().Replace(line, "").TrimEnd('\r');

    public static bool IsReady(string line) => Done().IsMatch(line);

    public static bool IsSaved(string line) => Saved().IsMatch(line);

    public static string? JoinedPlayer(string line) => Joined().Match(line) is { Success: true } m ? m.Groups["name"].Value : null;

    public static string? LeftPlayer(string line) => Left().Match(line) is { Success: true } m ? m.Groups["name"].Value : null;
}

/// <summary>server.properties that keeps comments and order and changes only the given keys.</summary>
internal sealed class ServerPropertiesFile
{
    private readonly List<string> _lines;

    private ServerPropertiesFile(List<string> lines) => _lines = lines;

    public static ServerPropertiesFile Load(string path) =>
        new(File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : ["#Minecraft server properties"]);

    public static ServerPropertiesFile Parse(string text) => new(text.Replace("\r\n", "\n").Split('\n').ToList());

    public string? Get(string key)
    {
        foreach (var line in _lines)
            if (KeyOf(line) is { } k && k == key)
                return Unescape(line[(line.IndexOf('=') + 1)..]);
        return null;
    }

    public void Set(string key, string value)
    {
        var escaped = Escape(value);
        for (var i = 0; i < _lines.Count; i++)
        {
            if (KeyOf(_lines[i]) == key)
            {
                _lines[i] = $"{key}={escaped}";
                return;
            }
        }
        _lines.Add($"{key}={escaped}");
    }

    public void Save(string path) => File.WriteAllText(path, ToString(), new UTF8Encoding(false));

    public override string ToString() => string.Join('\n', _lines).TrimEnd('\n') + "\n";

    private static string? KeyOf(string line)
    {
        var t = line.TrimStart();
        if (t.Length == 0 || t[0] is '#' or '!')
            return null;
        var eq = t.IndexOf('=');
        return eq <= 0 ? null : t[..eq].Trim();
    }

    // Java properties escaping for the characters that matter in practice; non-ASCII as \uXXXX.
    private static string Escape(string value)
    {
        var sb = new StringBuilder();
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': break;
                default:
                    if (c > 0x7e)
                        sb.Append($"\\u{(int)c:x4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Unescape(string value)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }
            var n = value[++i];
            if (n == 'u' && i + 4 < value.Length && int.TryParse(value.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                sb.Append((char)code);
                i += 4;
            }
            else
                sb.Append(n switch { 'n' => '\n', 't' => '\t', _ => n });
        }
        return sb.ToString();
    }
}

/// <summary>Daily schedules "HH:mm" in the machine's local time.</summary>
internal static class Schedule
{
    /// <summary>Times (with an offset, e.g. -5 minutes for a warning) that fell into (from, to].</summary>
    public static bool Crossed(string time, TimeSpan offset, DateTime from, DateTime to)
    {
        if (!TimeSpan.TryParse(time, System.Globalization.CultureInfo.InvariantCulture, out var t))
            return false;
        if (to <= from || to - from > TimeSpan.FromHours(1))
            return false; // clock jumped or the agent slept: skip instead of firing late
        for (var day = from.Date.AddDays(-1); day <= to.Date.AddDays(1); day = day.AddDays(1))
        {
            var moment = day + t + offset;
            if (moment > from && moment <= to)
                return true;
        }
        return false;
    }
}

/// <summary>Keeps admin file access inside a server folder.</summary>
internal static class PathGuard
{
    private static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".properties", ".json", ".json5", ".toml", ".txt", ".cfg", ".conf", ".yml", ".yaml", ".ini", ".snbt", ".mcmeta", ".csv", ".md",
    };

    private static readonly HashSet<string> ReadOnlyExtensions = new(StringComparer.OrdinalIgnoreCase) { ".log" };

    public const long MaxReadBytes = 1 << 20;
    public const long MaxWriteBytes = 512 * 1024;

    /// <summary>Full path for a relative path inside root, or null if it escapes the root or touches agent files.</summary>
    public static string? Resolve(string root, string? relative)
    {
        var rel = (relative ?? "").Replace('\\', '/').Trim('/');
        if (rel.Contains('\0') || rel.Split('/').Any(p => p is ".." || p.StartsWith(".lumora", StringComparison.OrdinalIgnoreCase)))
            return null;
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, rel));
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (full != rootFull && !full.StartsWith(rootWithSep, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;

        // No symbolic links on the way: they could point outside the server folder.
        var current = rootFull;
        foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
                return null;
        }
        return full;
    }

    public static bool IsEditable(string path) => EditableExtensions.Contains(Path.GetExtension(path));

    public static bool IsReadable(string path) => IsEditable(path) || ReadOnlyExtensions.Contains(Path.GetExtension(path));
}
