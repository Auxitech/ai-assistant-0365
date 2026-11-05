using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace MailRouter.Web.Services;

/// <summary>
/// Teams calling bot. Uses the same Entra ID app registration as Graph by default, with a client secret and
/// APPLICATION permissions added (Calls.Initiate.All, Calls.AccessMedia.All, User.Read.All,
/// CrossTenantInformation.ReadBasic.All), and registered as an Azure Bot with the Teams calling channel.
/// </summary>
public class CallingOptions
{
    /// <summary>App registration (client) id. Defaults to Graph:ClientId.</summary>
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    /// <summary>Your tenant id (GUID or contoso.onmicrosoft.com). Defaults to Graph:TenantId unless that is "organizations".</summary>
    public string TenantId { get; set; } = "";
    /// <summary>Public HTTPS base URL of this app, e.g. https://mailrouter.auxitech.ch</summary>
    public string PublicBaseUrl { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret) &&
        !string.IsNullOrWhiteSpace(TenantId) && PublicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public string CallbackUrl => PublicBaseUrl.TrimEnd('/') + "/api/calls/callback";
}

/// <summary>Graph REST calls made as the calling bot (app-only token).</summary>
public class CallingGraphClient
{
    private const string Base = "https://graph.microsoft.com/v1.0";
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    private readonly HttpClient _http;
    private readonly CallingOptions _opt;
    private readonly ILogger<CallingGraphClient> _log;
    private readonly IConfidentialClientApplication? _app;

    public CallingGraphClient(HttpClient http, IOptions<CallingOptions> opt, ILogger<CallingGraphClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
        if (_opt.IsConfigured)
            _app = ConfidentialClientApplicationBuilder.Create(_opt.AppId)
                .WithClientSecret(_opt.AppSecret)
                .WithTenantId(_opt.TenantId)
                .Build();
    }

    public record CallerInfo(string DisplayName, string? Email, string? PreferredLanguage);

    // ---------- Call control ----------

    /// <summary>Answers with Teams-hosted media; the prompt files are pre-fetched so playback starts instantly.</summary>
    public Task AnswerAsync(string callId, IEnumerable<CallPromptStore.Prompt> prefetch, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/communications/calls/{callId}/answer", new JsonObject
        {
            ["callbackUri"] = _opt.CallbackUrl,
            ["acceptedModalities"] = new JsonArray("audio"),
            ["mediaConfig"] = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.serviceHostedMediaConfig",
                ["preFetchMedia"] = new JsonArray(prefetch.Select(p => (JsonNode)MediaInfo(p)).ToArray()),
            },
        }, ct);

    /// <summary>Returns the playPrompt operation id (its completion arrives as a notification).</summary>
    public async Task<string?> PlayPromptsAsync(string callId, IEnumerable<CallPromptStore.Prompt> prompts, CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Post, $"{Base}/communications/calls/{callId}/playPrompt", new JsonObject
        {
            ["clientContext"] = callId,
            ["prompts"] = new JsonArray(prompts.Select(p => (JsonNode)new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.mediaPrompt",
                ["mediaInfo"] = MediaInfo(p),
            }).ToArray()),
        }, ct);
        return res?["id"]?.GetValue<string>();
    }

    /// <summary>Transfer of an answered call (after the announcement).</summary>
    public Task TransferAsync(string callId, string phoneE164, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/communications/calls/{callId}/transfer", new JsonObject
        {
            ["transferTarget"] = PhoneTarget(phoneE164),
            ["clientContext"] = callId,
        }, ct);

    /// <summary>Redirect of a call that has NOT been answered (no announcement).</summary>
    public Task RedirectAsync(string callId, string phoneE164, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/communications/calls/{callId}/redirect", new JsonObject
        {
            ["targets"] = new JsonArray(PhoneTarget(phoneE164)),
            ["callbackUri"] = _opt.CallbackUrl,
        }, ct);

    public Task RejectAsync(string callId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Base}/communications/calls/{callId}/reject", new JsonObject { ["reason"] = "none" }, ct);

    public Task HangUpAsync(string callId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{Base}/communications/calls/{callId}", null, ct);

    // ---------- Caller lookup ----------

    /// <summary>Users of your own tenant. Null when not found (e.g. a user from another organisation).</summary>
    public async Task<CallerInfo?> GetUserAsync(string userId, CancellationToken ct)
    {
        try
        {
            var j = await SendAsync(HttpMethod.Get, $"{Base}/users/{Uri.EscapeDataString(userId)}?$select=displayName,mail,userPrincipalName,preferredLanguage", null, ct);
            if (j == null) return null;
            var mail = j["mail"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(mail)) mail = j["userPrincipalName"]?.GetValue<string>();
            return new CallerInfo(j["displayName"]?.GetValue<string>() ?? "", mail, j["preferredLanguage"]?.GetValue<string>());
        }
        catch (GraphException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            return null;
        }
    }

    /// <summary>Default domain of another organisation (Teams federation callers), e.g. "cdb1.ch".</summary>
    public async Task<string?> GetTenantDomainAsync(string tenantId, CancellationToken ct)
    {
        try
        {
            var j = await SendAsync(HttpMethod.Get, $"{Base}/tenantRelationships/findTenantInformationByTenantId(tenantId='{Uri.EscapeDataString(tenantId)}')", null, ct);
            return j?["defaultDomainName"]?.GetValue<string>();
        }
        catch (GraphException ex)
        {
            _log.LogWarning("Could not resolve tenant {Tenant}: {Error}", tenantId, ex.Message);
            return null;
        }
    }

    // ---------- Helpers ----------

    private static JsonObject MediaInfo(CallPromptStore.Prompt p) => new()
    {
        ["@odata.type"] = "#microsoft.graph.mediaInfo",
        ["uri"] = p.Uri,
        ["resourceId"] = p.ResourceId,
    };

    private static JsonObject PhoneTarget(string phoneE164) => new()
    {
        ["@odata.type"] = "#microsoft.graph.invitationParticipantInfo",
        ["identity"] = new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.identitySet",
            ["phone"] = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.identity",
                ["id"] = phoneE164,
            },
        },
    };

    private async Task<JsonNode?> SendAsync(HttpMethod method, string url, JsonNode? payload, CancellationToken ct)
    {
        if (_app == null) throw new InvalidOperationException("Calling bot is not configured (Calling:AppId/AppSecret/TenantId/PublicBaseUrl).");
        var token = await _app.AcquireTokenForClient(Scopes).ExecuteAsync(ct);
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        if (payload != null) req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            if (method != HttpMethod.Get)
                _log.LogError("Graph {Method} {Url} failed: {Status} {Body}", method, url, res.StatusCode, body);
            throw new GraphException(res.StatusCode, body);
        }
        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }
}
