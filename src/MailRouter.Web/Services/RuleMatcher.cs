using MailRouter.Web.Data;

namespace MailRouter.Web.Services;

public static class RuleMatcher
{
    public static string DomainOf(string email)
    {
        var at = email.LastIndexOf('@');
        return at < 0 ? "" : email[(at + 1)..].Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Picks the rule for a sender. Order: Priority asc, then specificity (Email &gt; Domain &gt; CatchAll),
    /// then longer domain pattern first (so "it.cdb1.ch" wins over "cdb1.ch").
    /// </summary>
    public static RoutingRule? Match(IEnumerable<RoutingRule> rules, string senderEmail, MessageSource source)
    {
        var email = senderEmail.Trim().ToLowerInvariant();
        var domain = DomainOf(email);
        var channel = source == MessageSource.Email ? ChannelFlags.Email : ChannelFlags.Teams;

        return rules
            .Where(r => r.Enabled && r.Channels.HasFlag(channel) && IsMatch(r, email, domain))
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.MatchType switch { RuleMatchType.Email => 0, RuleMatchType.Domain => 1, _ => 2 })
            .ThenByDescending(r => r.Pattern.Length)
            .FirstOrDefault();
    }

    /// <summary>Rules whose pattern matches the sender but which skip them because of an exception (for the tester UI).</summary>
    public static List<RoutingRule> ExcludedBy(IEnumerable<RoutingRule> rules, string senderEmail, MessageSource source)
    {
        var email = senderEmail.Trim().ToLowerInvariant();
        var domain = DomainOf(email);
        var channel = source == MessageSource.Email ? ChannelFlags.Email : ChannelFlags.Teams;
        return rules
            .Where(r => r.Enabled && r.Channels.HasFlag(channel) && PatternMatches(r, email, domain) && IsExcepted(r, email, domain))
            .OrderBy(r => r.Priority)
            .ToList();
    }

    private static bool IsMatch(RoutingRule r, string email, string domain) =>
        PatternMatches(r, email, domain) && !IsExcepted(r, email, domain);

    private static bool PatternMatches(RoutingRule r, string email, string domain)
    {
        var p = r.Pattern.Trim().ToLowerInvariant().TrimStart('@', '*', '.');
        return r.MatchType switch
        {
            RuleMatchType.Email => p.Length > 0 && email == p,
            RuleMatchType.Domain => p.Length > 0 && DomainMatches(domain, p),
            RuleMatchType.CatchAll => email.Length > 0,
            _ => false,
        };
    }

    private static bool IsExcepted(RoutingRule r, string email, string domain) =>
        ParseList(r.Exceptions).Any(x => x.Contains('@') ? email == x : DomainMatches(domain, x));

    private static bool DomainMatches(string domain, string pattern) =>
        domain.Length > 0 && (domain == pattern || domain.EndsWith("." + pattern));

    /// <summary>Exceptions list: addresses and/or domains, normalised.</summary>
    public static List<string> ParseList(string? list) =>
        (list ?? "").Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant().TrimStart('*', '.'))
            .Select(x => x.StartsWith('@') ? x[1..] : x)
            .Where(x => x.Length > 0)
            .Distinct()
            .ToList();

    public static List<string> ParseAddresses(string? list) =>
        (list ?? "").Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => a.Contains('@'))
            .Select(a => a.ToLowerInvariant())
            .Distinct()
            .ToList();

    public static string Render(string template, string senderName, string senderEmail, string forwardTo, string subject, string summary) =>
        template
            .Replace("{SenderName}", string.IsNullOrWhiteSpace(senderName) ? senderEmail : FirstName(senderName))
            .Replace("{SenderEmail}", senderEmail)
            .Replace("{ForwardTo}", forwardTo)
            .Replace("{Subject}", subject)
            .Replace("{Summary}", summary);

    private static string FirstName(string displayName)
    {
        // "Dupont Jean" / "Jean Dupont" / "Dupont, Jean" — keep it simple and safe: use the full display name
        // unless it's "Last, First".
        var comma = displayName.IndexOf(',');
        return comma > 0 ? displayName[(comma + 1)..].Trim() : displayName.Trim();
    }
}
