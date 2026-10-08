using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ZRevive.Launcher;

public sealed record LoginResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("permissionLevel")] int PermissionLevel,
    [property: JsonPropertyName("rank")] string? Rank,
    [property: JsonPropertyName("steamName")] string? SteamName,
    [property: JsonPropertyName("steamAvatar")] string? SteamAvatar,
    [property: JsonPropertyName("serverName")] string? ServerName,
    [property: JsonPropertyName("loginServer")] string? LoginServer,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    // Optional: shown in the launcher when the portal includes it in the login reply.
    [property: JsonPropertyName("crowns")] int? Crowns = null,
    /// <summary>
    /// The portal waives <see cref="SecurityGate"/> for this account - a PC that genuinely cannot
    /// turn Secure Boot on, or a staff member. Decided by the server, never by the launcher, so a
    /// player cannot grant it to themselves by editing a config or claiming a name, and the owner
    /// can hand it out without shipping a new launcher. Defaults to false, so an older portal that
    /// does not send the field enforces the requirement.
    /// </summary>
    [property: JsonPropertyName("securityExempt")] bool SecurityExempt = false);

public sealed record NewsItem(
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("body")] string? Body);

public sealed record NewsResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("items")] List<NewsItem>? Items);

public sealed record ServerInfo(
    [property: JsonPropertyName("serverId")] int ServerId,
    [property: JsonPropertyName("tag")] string? Tag,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("population")] int Population,
    [property: JsonPropertyName("maxPopulation")] int MaxPopulation);

public sealed record StatusResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("serverName")] string? ServerName,
    [property: JsonPropertyName("servers")] List<ServerInfo>? Servers,
    [property: JsonPropertyName("players")] int Players,
    [property: JsonPropertyName("connected")] int Connected);

public sealed record LinkResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("url")] string? Url);

public sealed record SteamStart(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("url")] string? Url);

public sealed record SteamPoll(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("pending")] bool Pending,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("authKey")] string? AuthKey);

public sealed class PortalClient
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    public string BaseUrl { get; set; }

    public PortalClient(string baseUrl) => BaseUrl = baseUrl.TrimEnd('/');

    public async Task<LoginResult> LoginAsync(string authKey)
    {
        var res = await _http.PostAsJsonAsync($"{BaseUrl}/api/launcher/login",
            new { authKey, hwid = MachineId.Current, launcherVersion = Install.LauncherUpdate.CurrentVersion });
        return await res.Content.ReadFromJsonAsync<LoginResult>()
               ?? new LoginResult(false, $"HTTP {(int)res.StatusCode}", null, 0, null, null, null, null, null, null);
    }

    // Same checks as LoginAsync, but SessionId is a short-lived play ticket, so the
    // permanent auth key never goes on the game's command line (H1Z1.log records it).
    // Null when the portal is older and has no /play endpoint.
    public async Task<LoginResult?> PlayAsync(string authKey)
    {
        var res = await _http.PostAsJsonAsync($"{BaseUrl}/api/launcher/play",
            new { authKey, hwid = MachineId.Current, launcherVersion = Install.LauncherUpdate.CurrentVersion });
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        return await res.Content.ReadFromJsonAsync<LoginResult>()
               ?? new LoginResult(false, $"HTTP {(int)res.StatusCode}", null, 0, null, null, null, null, null, null);
    }

    public Task<StatusResult?> StatusAsync() =>
        _http.GetFromJsonAsync<StatusResult>($"{BaseUrl}/api/status");

    // Null when the portal has no /api/news (older portal) or the request fails;
    // the launcher then shows its built-in list.
    public async Task<List<NewsItem>?> NewsAsync()
    {
        try
        {
            var res = await _http.GetAsync($"{BaseUrl}/api/news");
            if (!res.IsSuccessStatusCode) return null;
            var r = await res.Content.ReadFromJsonAsync<NewsResult>();
            return r?.Ok == true && r.Items is { Count: > 0 } ? r.Items : null;
        }
        catch { return null; }
    }

    public async Task<SteamStart> SteamStartAsync()
    {
        var res = await _http.PostAsync($"{BaseUrl}/api/launcher/steam/start", null);
        return await res.Content.ReadFromJsonAsync<SteamStart>()
               ?? new SteamStart(false, $"HTTP {(int)res.StatusCode}", null, null, null);
    }

    public async Task<SteamPoll> SteamPollAsync(string id)
    {
        var res = await _http.PostAsJsonAsync($"{BaseUrl}/api/launcher/steam/poll", new { id });
        return await res.Content.ReadFromJsonAsync<SteamPoll>()
               ?? new SteamPoll(false, false, $"HTTP {(int)res.StatusCode}", null);
    }

    public async Task<LinkResult> AdminLinkAsync(string authKey)
    {
        var res = await _http.PostAsJsonAsync($"{BaseUrl}/api/launcher/admin-link", new { authKey });
        return await res.Content.ReadFromJsonAsync<LinkResult>()
               ?? new LinkResult(false, $"HTTP {(int)res.StatusCode}", null);
    }
}
