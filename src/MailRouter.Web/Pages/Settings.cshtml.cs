using MailRouter.Web.Data;
using MailRouter.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MailRouter.Web.Pages;

public class SettingsModel(AppDbContext db, GraphAuthService auth, AiService ai) : PageModel
{
    [BindProperty] public AppSettings Input { get; set; } = new();

    public string? Account { get; private set; }
    public GraphAuthService.DeviceCodeState? PendingLogin => auth.PendingLogin;
    public string? LoginError => auth.LastLoginError;
    public bool GraphConfigured => auth.IsConfigured;
    public bool AiEnabled => ai.IsEnabled;

    public async Task OnGetAsync()
    {
        Input = await db.GetSettingsAsync();
        Account = (await auth.GetAccountAsync())?.Username;
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!ModelState.IsValid) { await OnGetAsync(); return Page(); }
        var s = await db.GetSettingsAsync();
        bool wasDry = s.DryRun;
        s.Enabled = Input.Enabled;
        s.DryRun = Input.DryRun;
        s.ProcessEmail = Input.ProcessEmail;
        s.ProcessTeams = Input.ProcessTeams;
        s.TeamsOneOnOneOnly = Input.TeamsOneOnOneOnly;
        s.PollIntervalSeconds = Input.PollIntervalSeconds;
        s.ReplyFooter = Input.ReplyFooter;
        s.OutlookCategory = Input.OutlookCategory;
        await db.SaveChangesAsync();
        TempData["Flash"] = wasDry && !s.DryRun
            ? "Saved. LIVE MODE: replies and forwards will now really be sent."
            : "Settings saved.";
        return RedirectToPage();
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
