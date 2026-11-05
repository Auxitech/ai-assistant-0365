using MailRouter.Web.Data;
using MailRouter.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MailRouter.Web.Pages;

public class SettingsModel(AppDbContext db, GraphAuthService auth, AiService ai, CallPromptStore prompts, IOptions<CallingOptions> calling) : PageModel
{
    [BindProperty] public AppSettings Input { get; set; } = new();

    public string? Account { get; private set; }
    public GraphAuthService.DeviceCodeState? PendingLogin => auth.PendingLogin;
    public string? LoginError => auth.LastLoginError;
    public bool GraphConfigured => auth.IsConfigured;
    public bool AiEnabled => ai.IsEnabled;
    public CallingOptions Calling => calling.Value;
    public CallPromptStore.PromptInfo? Prompt(string lang) => prompts.Info(lang);

    public async Task OnGetAsync()
    {
        Input = await db.GetSettingsAsync();
        Account = (await auth.GetAccountAsync())?.Username;
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var fallback = RuleMatcher.NormalizePhone(Input.CallFallbackNumber);
        if (!string.IsNullOrWhiteSpace(Input.CallFallbackNumber) && !RuleMatcher.IsDialable(fallback))
            ModelState.AddModelError("Input.CallFallbackNumber", "Enter a full international number, e.g. +41 79 123 45 67, or leave it empty.");
        if (!ModelState.IsValid)
        {
            // Keep what was typed; only refresh the read-only parts.
            var current = await db.GetSettingsAsync();
            Input.EmailWatermark = current.EmailWatermark;
            Input.TeamsWatermark = current.TeamsWatermark;
            Account = (await auth.GetAccountAsync())?.Username;
            return Page();
        }
        var s = await db.GetSettingsAsync();
        bool wasDry = s.DryRun;
        s.Enabled = Input.Enabled;
        s.DryRun = Input.DryRun;
        s.ProcessEmail = Input.ProcessEmail;
        s.ProcessTeams = Input.ProcessTeams;
        s.TeamsOneOnOneOnly = Input.TeamsOneOnOneOnly;
        s.MarkEmailAsRead = Input.MarkEmailAsRead;
        s.HandledFolder = string.IsNullOrWhiteSpace(Input.HandledFolder) ? null : Input.HandledFolder.Trim().Trim('/');
        s.MarkTeamsAsRead = Input.MarkTeamsAsRead;
        s.PollIntervalSeconds = Input.PollIntervalSeconds;
        s.ReplyFooter = Input.ReplyFooter;
        s.RedirectNotice = string.IsNullOrWhiteSpace(Input.RedirectNotice) ? null : Input.RedirectNotice.Trim();
        s.ForwardNotice = string.IsNullOrWhiteSpace(Input.ForwardNotice) ? null : Input.ForwardNotice.Trim();
        s.OutlookCategory = Input.OutlookCategory;
        s.ProcessCalls = Input.ProcessCalls;
        s.CallFallbackNumber = fallback;
        await db.SaveChangesAsync();
        TempData["Flash"] = wasDry && !s.DryRun
            ? "Saved. LIVE MODE: replies and forwards will now really be sent."
            : "Settings saved.";
        return RedirectToPage();
    }

    [BindProperty] public string? TrySender { get; set; }
    [BindProperty] public string? TrySubject { get; set; }
    [BindProperty] public string? TryBody { get; set; }
    public AiVerdict? TryResult { get; private set; }
    public string? TryError { get; private set; }

    public async Task<IActionResult> OnPostTryAiAsync(CancellationToken ct)
    {
        ModelState.Clear();
        await OnGetAsync();
        if (!ai.IsEnabled) { TryError = "AI is disabled (no ANTHROPIC_API_KEY)."; return Page(); }
        if (string.IsNullOrWhiteSpace(TryBody)) { TryError = "Enter a message."; return Page(); }
        try
        {
            var sender = string.IsNullOrWhiteSpace(TrySender) ? "someone@example.com" : TrySender.Trim();
            TryResult = await ai.ClassifyAsync("Email", sender, sender, TrySubject ?? "", TryBody, ct);
            if (TryResult == null) TryError = "The AI declined to classify this message (it would be held back).";
        }
        catch (Exception ex) { TryError = ex.Message; }
        return Page();
    }

    public async Task<IActionResult> OnPostUploadPromptAsync(string lang, IFormFile? file, CancellationToken ct)
    {
        if (file == null || file.Length == 0) { TempData["Flash"] = "Choose a WAV file first."; return RedirectToPage(null, null, "calls"); }
        await using var stream = file.OpenReadStream();
        var error = await prompts.SaveAsync(lang, stream, file.Length, ct);
        TempData["Flash"] = error ?? $"Announcement ({lang.ToUpperInvariant()}) saved.";
        return RedirectToPage(null, null, "calls");
    }

    public IActionResult OnPostDeletePrompt(string lang)
    {
        prompts.Delete(lang);
        TempData["Flash"] = $"Announcement ({lang.ToUpperInvariant()}) removed.";
        return RedirectToPage(null, null, "calls");
    }

    public async Task<IActionResult> OnPostConnectAsync()
    {
        auth.StartDeviceCodeLogin();
        // The device code arrives from Entra ID within a second or two; wait so the page can show it.
        for (var i = 0; i < 20 && auth.PendingLogin == null && auth.LastLoginError == null; i++)
            await Task.Delay(250);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisconnectAsync()
    {
        await auth.SignOutAsync();
        TempData["Flash"] = "Disconnected from Microsoft 365.";
        return RedirectToPage();
    }
}
