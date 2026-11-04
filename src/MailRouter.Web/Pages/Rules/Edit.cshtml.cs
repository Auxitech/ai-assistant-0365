using MailRouter.Web.Data;
using MailRouter.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MailRouter.Web.Pages.Rules;

public class EditModel(AppDbContext db) : PageModel
{
    [BindProperty] public RoutingRule Rule { get; set; } = new();
    [BindProperty] public bool ChEmail { get; set; } = true;
    [BindProperty] public bool ChTeams { get; set; } = true;

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id != null)
        {
            var r = await db.Rules.FindAsync(id);
            if (r == null) return NotFound();
            Rule = r;
            ChEmail = r.Channels.HasFlag(ChannelFlags.Email);
            ChTeams = r.Channels.HasFlag(ChannelFlags.Teams);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // Pattern/template may legitimately be empty (CatchAll / ForwardOnly); validated manually below.
        ModelState.Remove("Rule.Pattern");
        ModelState.Remove("Rule.ReplyTemplate");
        Rule.ReplyTemplate ??= "";
        Rule.MoveToFolder = string.IsNullOrWhiteSpace(Rule.MoveToFolder) ? null : Rule.MoveToFolder.Trim().Trim('/');
        var exceptions = RuleMatcher.ParseList(Rule.Exceptions);
        Rule.Exceptions = exceptions.Count == 0 ? null : string.Join("\n", exceptions);
        Rule.Pattern = (Rule.Pattern ?? "").Trim().ToLowerInvariant().TrimStart('@', '*', '.');
        Rule.Channels = (ChEmail ? ChannelFlags.Email : 0) | (ChTeams ? ChannelFlags.Teams : 0);

        if (Rule.Channels == ChannelFlags.None)
            ModelState.AddModelError("", "Select at least one channel.");
        if (Rule.MatchType == RuleMatchType.Domain && (Rule.Pattern.Length == 0 || Rule.Pattern.Contains('@')))
            ModelState.AddModelError("Rule.Pattern", "Enter a domain like cdb1.ch");
        if (Rule.MatchType == RuleMatchType.Email && !Rule.Pattern.Contains('@'))
            ModelState.AddModelError("Rule.Pattern", "Enter a full email address.");
        if (Rule.Action is RuleAction.ReplyAndForward or RuleAction.ForwardOnly && RuleMatcher.ParseAddresses(Rule.ForwardTo).Count == 0)
            ModelState.AddModelError("Rule.ForwardTo", "This action needs at least one forward address.");
        if (Rule.Action is RuleAction.ReplyAndForward or RuleAction.ReplyOnly && string.IsNullOrWhiteSpace(Rule.ReplyTemplate))
            ModelState.AddModelError("Rule.ReplyTemplate", "This action needs a reply template.");

        if (!ModelState.IsValid) return Page();

        if (Rule.Id == 0) db.Rules.Add(Rule);
        else db.Rules.Update(Rule);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Rule '{Rule.Name}' saved.";
        return RedirectToPage("Index");
    }
}
