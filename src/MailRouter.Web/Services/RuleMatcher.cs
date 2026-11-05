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
    /// <param name="sender">
    /// Email address. For calls it can also be a phone number ("+41…"), or "@domain" when only the caller's
    /// organisation is known (Teams users from another tenant).
    /// </param>
    public static RoutingRule? Match(IEnumerable<RoutingRule> rules, string sender, MessageSource source)
    {
        var c = Caller.From(sender);
        return rules
            .Where(r => r.Enabled && r.Channels.HasFlag(ChannelOf(source)) && IsMatch(r, c))
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.MatchType switch { RuleMatchType.Email or RuleMatchType.Phone => 0, RuleMatchType.Domain => 1, _ => 2 })
            .ThenByDescending(r => r.Pattern.Length)
            .FirstOrDefault();
    }

    /// <summary>Rules whose pattern matches the sender but which skip them because of an exception (for the tester UI).</summary>
    public static List<RoutingRule> ExcludedBy(IEnumerable<RoutingRule> rules, string sender, MessageSource source)
    {
        var c = Caller.From(sender);
        return rules
            .Where(r => r.Enabled && r.Channels.HasFlag(ChannelOf(source)) && PatternMatches(r, c) && IsExcepted(r, c))
            .OrderBy(r => r.Priority)
            .ToList();
    }

    private static ChannelFlags ChannelOf(MessageSource source) => source switch
    {
        MessageSource.Email => ChannelFlags.Email,
        MessageSource.Teams => ChannelFlags.Teams,
        _ => ChannelFlags.Calls,
    };

    private readonly record struct Caller(string Email, string Domain, string? Phone)
    {
        public static Caller From(string sender)
        {
            var s = sender.Trim().ToLowerInvariant();
            var phone = NormalizePhone(s);
            return phone != null ? new Caller("", "", phone) : new Caller(s, DomainOf(s), null);
        }
    }

    private static bool IsMatch(RoutingRule r, Caller c) => PatternMatches(r, c) && !IsExcepted(r, c);

    private static bool PatternMatches(RoutingRule r, Caller c)
    {
        var p = r.Pattern.Trim().ToLowerInvariant().TrimStart('@', '*', '.');
        return r.MatchType switch
        {
            // "@domain" (caller organisation only) has no address part, so it never matches an Email rule.
            RuleMatchType.Email => p.Length > 0 && !c.Email.StartsWith('@') && c.Email == p,
            RuleMatchType.Domain => p.Length > 0 && DomainMatches(c.Domain, p),
            RuleMatchType.Phone => c.Phone != null && NormalizePhone(p) is { } prefix && c.Phone.StartsWith(prefix),
            RuleMatchType.CatchAll => c.Email.Length > 0 || c.Phone != null,
            _ => false,
        };
    }

    private static bool IsExcepted(RoutingRule r, Caller c) =>
        ParseList(r.Exceptions).Any(x =>
            x.StartsWith('+') ? c.Phone != null && NormalizePhone(x) is { } prefix && c.Phone.StartsWith(prefix)
            : x.Contains('@') ? c.Email == x
            : DomainMatches(c.Domain, x));

    /// <summary>
    /// "+41 21 123 45 67" / "0041211234567" → "+41211234567". Null if the text isn't an international number
    /// (local numbers like "021…" are ambiguous and rejected).
    /// </summary>
    public static string? NormalizePhone(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("00")) s = "+" + s[2..];
        if (!s.StartsWith('+')) return null;
        var digits = new string(s.Where(char.IsDigit).ToArray());
        if (s[1..].Any(ch => !char.IsDigit(ch) && ch is not (' ' or '-' or '.' or '(' or ')' or '/'))) return null;
        return digits.Length is >= 1 and <= 15 && digits[0] != '0' ? "+" + digits : null;
    }

    /// <summary>A full dialable E.164 number (country code + subscriber number).</summary>
    public static bool IsDialable(string? e164) => e164 != null && e164.Length is >= 8 and <= 16;

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
