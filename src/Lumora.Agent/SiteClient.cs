using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Lumora.Contracts;

namespace Lumora.Agent;

/// <summary>HTTP calls of the agent: registration, server manifests and file downloads with hash checks.</summary>
public sealed class SiteClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _public;

    public SiteClient(string siteUrl, string? key)
    {
        _http = new HttpClient { BaseAddress = new Uri(siteUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LumoraAgent/{AgentInfo.Version}");
        if (key is not null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        // Third-party downloads (Java, NeoForge, Mojang) never get the agent key.
        _public = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _public.DefaultRequestHeaders.UserAgent.ParseAdd($"LumoraAgent/{AgentInfo.Version}");
    }

    public static async Task<AgentRegisterResponse> RegisterAsync(string siteUrl, AgentRegisterRequest request, CancellationToken ct)
    {
        using var client = new SiteClient(siteUrl, null);
        using var response = await client._http.PostAsJsonAsync("api/agent/register", request, LumoraJson.Options, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Сайт отказал в регистрации ({(int)response.StatusCode}): {body}");
        }
        return (await response.Content.ReadFromJsonAsync<AgentRegisterResponse>(LumoraJson.Options, ct))!;
    }

    public async Task<ServerManifestDto> GetManifestAsync(Guid serverId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/agent/servers/{serverId}/manifest", ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new AgentException(response.StatusCode == System.Net.HttpStatusCode.Conflict
                ? "У сервера нет опубликованной сборки."
                : $"Сайт не отдал манифест сервера ({(int)response.StatusCode}). {body}");
        }
        return (await response.Content.ReadFromJsonAsync<ServerManifestDto>(LumoraJson.Options, ct))!;
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct) =>
        await ClientFor(url).GetStringAsync(url, ct);

    /// <summary>Downloads to a temp file, checks the hash and moves into place.</summary>
    public async Task DownloadAsync(string url, string destination, string? expectedHash, HashAlgorithmName algorithm, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tmp = destination + ".download";
        try
        {
            using (var response = await ClientFor(url).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode)
                    throw new AgentException($"Не удалось скачать {url}: HTTP {(int)response.StatusCode}.");
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(tmp);
                await input.CopyToAsync(output, ct);
            }
            if (expectedHash is not null)
            {
                var actual = await HashAsync(tmp, algorithm, ct);
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new AgentException($"Файл {Path.GetFileName(destination)} повреждён: контрольная сумма не совпала.");
            }
            File.Move(tmp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    public static async Task<string> HashAsync(string path, HashAlgorithmName algorithm, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var hasher = IncrementalHash.CreateHash(algorithm);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            hasher.AppendData(buffer, 0, read);
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private HttpClient ClientFor(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && _http.BaseAddress is { } b && u.Scheme == b.Scheme && u.Host == b.Host && u.Port == b.Port
            ? _http
            : _public;

    public void Dispose()
    {
        _http.Dispose();
        _public.Dispose();
    }
}

/// <summary>An error with a message for the admin (Russian).</summary>
public sealed class AgentException(string message) : Exception(message);

public static class AgentInfo
{
    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
}
