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
    [BindProperty] public bool ChCalls { get; set; }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id != null)
        {
            var r = await db.Rules.FindAsync(id);
            if (r == null) return NotFound();
            Rule = r;
            ChEmail = r.Channels.HasFlag(ChannelFlags.Email);
            ChTeams = r.Channels.HasFlag(ChannelFlags.Teams);
            ChCalls = r.Channels.HasFlag(ChannelFlags.Calls);
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
        var invalidPhones = new List<string>();
        var phoneEntries = new List<RuleMatcher.PhoneEntry>();
        if (Rule.MatchType == RuleMatchType.Phone)
        {
            // Keep names as typed; store numbers normalised ("+41791234567 Jean Dupont" per line).
            phoneEntries = RuleMatcher.ParsePhoneList(Rule.Pattern, invalidPhones);
            if (invalidPhones.Count == 0) Rule.Pattern = RuleMatcher.FormatPhoneList(phoneEntries);
        }
        else
            Rule.Pattern = (Rule.Pattern ?? "").Trim().ToLowerInvariant().TrimStart('@', '*', '.');
        Rule.Channels = (ChEmail ? ChannelFlags.Email : 0) | (ChTeams ? ChannelFlags.Teams : 0) | (ChCalls ? ChannelFlags.Calls : 0);
        var messages = ChEmail || ChTeams;
        var phone = RuleMatcher.NormalizePhone(Rule.RedirectPhoneNumber);
        Rule.RedirectPhoneNumber = string.IsNullOrWhiteSpace(Rule.RedirectPhoneNumber) ? null : phone ?? Rule.RedirectPhoneNumber.Trim();

        if (Rule.Channels == ChannelFlags.None)
            ModelState.AddModelError("", "Select at least one channel.");
        if (Rule.MatchType == RuleMatchType.Domain && (Rule.Pattern.Length == 0 || Rule.Pattern.Contains('@')))
            ModelState.AddModelError("Rule.Pattern", "Enter a domain like cdb1.ch");
        if (Rule.MatchType == RuleMatchType.Email && !Rule.Pattern.Contains('@'))
            ModelState.AddModelError("Rule.Pattern", "Enter a full email address.");
        if (invalidPhones.Count > 0)
            ModelState.AddModelError("Rule.Pattern", $"No phone number found on {invalidPhones.Count} line(s): " +
                string.Join(" | ", invalidPhones.Take(5)) + (invalidPhones.Count > 5 ? " …" : ""));
        else if (Rule.MatchType == RuleMatchType.Phone && phoneEntries.Count == 0)
            ModelState.AddModelError("Rule.Pattern", "Enter at least one phone number, e.g. +41 79 123 45 67 Jean Dupont");
        if (Rule.MatchType == RuleMatchType.Phone && messages)
            ModelState.AddModelError("", "Phone number rules only apply to calls: untick Email and Teams.");
        if (messages && Rule.Action is RuleAction.ReplyAndForward or RuleAction.ForwardOnly && RuleMatcher.ParseAddresses(Rule.ForwardTo).Count == 0)
            ModelState.AddModelError("Rule.ForwardTo", "This action needs at least one forward address.");
        if (messages && Rule.Action is RuleAction.ReplyAndForward or RuleAction.ReplyOnly && string.IsNullOrWhiteSpace(Rule.ReplyTemplate))
            ModelState.AddModelError("Rule.ReplyTemplate", "This action needs a reply template.");
        if (ChCalls && Rule.Action != RuleAction.Ignore && !RuleMatcher.IsDialable(phone))
            ModelState.AddModelError("Rule.RedirectPhoneNumber", "Enter the full international number calls are transferred to, e.g. +41 21 123 45 67.");

        if (!ModelState.IsValid) return Page();

        if (Rule.Id == 0) db.Rules.Add(Rule);
        else db.Rules.Update(Rule);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Rule '{Rule.Name}' saved.";
        return RedirectToPage("Index");
    }
}
