using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace MailRouter.Web.Services;

public class GraphOptions
{
    /// <summary>Entra ID app registration (public client flows enabled).</summary>
    public string ClientId { get; set; } = "";
    /// <summary>Tenant id or domain, e.g. "contoso.onmicrosoft.com".</summary>
    public string TenantId { get; set; } = "organizations";
    /// <summary>Defaults to {DataDirectory}/msal_cache.bin.</summary>
    public string TokenCachePath { get; set; } = "";

    public static readonly string[] Scopes =
    [
        "User.Read",
        "Mail.ReadWrite",
        "Mail.Send",
        "Chat.ReadWrite",   // read 1:1/group chats + send chat messages as you
        "offline_access",
    ];
}

/// <summary>
/// Delegated auth for ONE mailbox (yours) using the device code flow.
/// The token cache (refresh token) is persisted to the /data volume, so you sign in once.
/// Delegated auth is required because Teams only allows sending chat messages "as you" with delegated permissions.
/// </summary>
public class GraphAuthService
{
    private readonly GraphOptions _opt;
    private readonly ILogger<GraphAuthService> _log;
    private readonly IPublicClientApplication? _app;
    private readonly object _lock = new();

    public DeviceCodeState? PendingLogin { get; private set; }
    public string? LastLoginError { get; private set; }

    public record DeviceCodeState(string UserCode, string VerificationUrl, DateTimeOffset ExpiresOn);

    public GraphAuthService(IOptions<GraphOptions> opt, ILogger<GraphAuthService> log)
    {
        _opt = opt.Value;
        _log = log;
        if (string.IsNullOrWhiteSpace(_opt.ClientId)) return;

        _app = PublicClientApplicationBuilder.Create(_opt.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _opt.TenantId)
            .WithDefaultRedirectUri()
            .Build();

        var dir = Path.GetDirectoryName(_opt.TokenCachePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _app.UserTokenCache.SetBeforeAccess(args =>
        {
            lock (_lock)
            {
                if (File.Exists(_opt.TokenCachePath))
                    args.TokenCache.DeserializeMsalV3(File.ReadAllBytes(_opt.TokenCachePath));
            }
        });
        _app.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged) return;
            lock (_lock) File.WriteAllBytes(_opt.TokenCachePath, args.TokenCache.SerializeMsalV3());
        });
    }

    public bool IsConfigured => _app != null;

    public async Task<IAccount?> GetAccountAsync() =>
        _app == null ? null : (await _app.GetAccountsAsync()).FirstOrDefault();

    public async Task<string?> GetTenantIdAsync() => (await GetAccountAsync())?.HomeAccountId?.TenantId;

    /// <summary>Returns an access token, or null if not signed in.</summary>
    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        if (_app == null) return null;
        var account = await GetAccountAsync();
        if (account == null) return null;
        try
        {
            var result = await _app.AcquireTokenSilent(GraphOptions.Scopes, account).ExecuteAsync(ct);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            _log.LogWarning(ex, "Graph token expired or consent missing; sign in again from Settings.");
            return null;
        }
    }

    /// <summary>Starts a device-code login in the background; the code is exposed via <see cref="PendingLogin"/>.</summary>
    public void StartDeviceCodeLogin()
    {
        if (_app == null) throw new InvalidOperationException("Graph:ClientId is not configured.");
        LastLoginError = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await _app.AcquireTokenWithDeviceCode(GraphOptions.Scopes, dc =>
                {
                    PendingLogin = new DeviceCodeState(dc.UserCode, dc.VerificationUrl, dc.ExpiresOn);
                    return Task.CompletedTask;
                }).ExecuteAsync();
                _log.LogInformation("Microsoft 365 sign-in completed.");
            }
            catch (Exception ex)
            {
                LastLoginError = ex.Message.Contains("AADSTS7000218")
                    ? "Entra ID app registration: enable 'Allow public client flows' (App registrations → your app → Authentication → Advanced settings → Yes), save, then click Connect again."
                    : ex.Message.Contains("AADSTS65001")
                        ? "Consent missing: grant admin consent for the Graph permissions on the app registration (API permissions → Grant admin consent)."
                        : ex.Message;
                _log.LogError(ex, "Device code login failed");
            }
            finally
            {
                PendingLogin = null;
            }
        });
    }

    public async Task SignOutAsync()
    {
        if (_app == null) return;
        foreach (var a in await _app.GetAccountsAsync()) await _app.RemoveAsync(a);
        lock (_lock) if (File.Exists(_opt.TokenCachePath)) File.Delete(_opt.TokenCachePath);
    }
}
