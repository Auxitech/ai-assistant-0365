using MailRouter.Web.Data;
using MailRouter.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MailRouter.Web.Pages.Rules;

public class IndexModel(AppDbContext db) : PageModel
{
    public List<RoutingRule> Rules { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public string? TestAddress { get; set; }
    [BindProperty(SupportsGet = true)] public MessageSource TestSource { get; set; } = MessageSource.Email;
    public RoutingRule? TestResult { get; private set; }
    public bool Tested { get; private set; }
    public List<RoutingRule> TestExcludedBy { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Rules = await db.Rules.AsNoTracking().OrderBy(r => r.Priority).ThenBy(r => r.Name).ToListAsync();
        if (!string.IsNullOrWhiteSpace(TestAddress))
        {
            Tested = true;
            TestResult = RuleMatcher.Match(Rules, TestAddress, TestSource);
            TestExcludedBy = RuleMatcher.ExcludedBy(Rules, TestAddress, TestSource);
        }
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var r = await db.Rules.FindAsync(id);
        if (r != null) { r.Enabled = !r.Enabled; await db.SaveChangesAsync(); }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var r = await db.Rules.FindAsync(id);
        if (r != null) { db.Rules.Remove(r); await db.SaveChangesAsync(); TempData["Flash"] = $"Rule '{r.Name}' deleted."; }
        return RedirectToPage();
    }
}
