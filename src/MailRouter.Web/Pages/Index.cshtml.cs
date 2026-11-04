using MailRouter.Web.Data;
using MailRouter.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MailRouter.Web.Pages;

public class IndexModel(AppDbContext db, GraphAuthService auth, AiService ai, ProcessingService processor) : PageModel
{
    [BindProperty(SupportsGet = true)] public ProcessingStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public MessageSource? Source { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNo { get; set; } = 1;

    public const int PageSize = 50;
    public List<ProcessedMessage> Items { get; private set; } = [];
    public int Total { get; private set; }
    public Dictionary<ProcessingStatus, int> Last24h { get; private set; } = [];
    public AppSettings Settings { get; private set; } = null!;
    public string? Account { get; private set; }
    public bool AiEnabled => ai.IsEnabled;

    public async Task OnGetAsync()
    {
        Settings = await db.GetSettingsAsync();
        Account = (await auth.GetAccountAsync())?.Username;

        var q = db.Messages.AsNoTracking().AsQueryable();
        if (Status != null) q = q.Where(m => m.Status == Status);
        if (Source != null) q = q.Where(m => m.Source == Source);
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var term = Q.Trim().ToLower();
            q = q.Where(m => (m.SenderEmail ?? "").ToLower().Contains(term)
                          || (m.SenderName ?? "").ToLower().Contains(term)
                          || (m.Subject ?? "").ToLower().Contains(term)
                          || (m.RuleName ?? "").ToLower().Contains(term));
        }

        Total = await q.CountAsync();
        Items = await q.OrderByDescending(m => m.ProcessedAt).Skip((Math.Max(1, PageNo) - 1) * PageSize).Take(PageSize).ToListAsync();

        var since = DateTimeOffset.UtcNow.AddDays(-1);
        Last24h = await db.Messages.AsNoTracking()
            .Where(m => m.ProcessedAt > since)
            .GroupBy(m => m.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count);
    }

    public async Task<IActionResult> OnPostRunNowAsync(CancellationToken ct)
    {
        var r = await processor.RunOnceAsync(ct);
        TempData["Flash"] = r.Error != null
            ? $"Run finished with error: {r.Error}"
            : $"Run finished: {r.Fetched} message(s) fetched, {r.Processed} new entr{(r.Processed == 1 ? "y" : "ies")} logged.";
        return RedirectToPage();
    }
}
