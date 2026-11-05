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
        var c = Caller.From(sender, source);
        return rules
            .Where(r => r.Enabled && r.Channels.HasFlag(ChannelOf(source)) && IsMatch(r, c))
            .OrderBy(r => r.Priority)
            .ThenBy(r => Specificity(r, c))
            // Phone callers: the longest matching number/prefix wins; otherwise the longest pattern.
            .ThenByDescending(r => c.Phone != null ? MatchPhone(r, c.Phone)?.Prefix.Length ?? 0 : r.Pattern.Length)
            .FirstOrDefault();
    }

    /// <summary>0 = listed phone number or exact address, 1 = domain, 2 = catch-all.</summary>
    private static int Specificity(RoutingRule r, Caller c) =>
        c.Phone != null && MatchPhone(r, c.Phone) != null ? 0
        : r.MatchType switch { RuleMatchType.Email => 0, RuleMatchType.Domain => 1, _ => 2 };

    /// <summary>Rules whose pattern matches the sender but which skip them because of an exception (for the tester UI).</summary>
    public static List<RoutingRule> ExcludedBy(IEnumerable<RoutingRule> rules, string sender, MessageSource source)
    {
        var c = Caller.From(sender, source);
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
        public static Caller From(string sender, MessageSource source)
        {
            var s = sender.Trim().ToLowerInvariant();
            // Phone numbers only exist for calls; email/chat senders are always addresses.
            var phone = source == MessageSource.Call ? NormalizePhone(s) : null;
            return phone != null ? new Caller("", "", phone) : new Caller(s, DomainOf(s), null);
        }
    }

    private static bool IsMatch(RoutingRule r, Caller c) => PatternMatches(r, c) && !IsExcepted(r, c);

    private static bool PatternMatches(RoutingRule r, Caller c)
    {
        // Phone numbers listed on the rule (Phone match type, or "also these numbers" on any rule).
        if (c.Phone != null && MatchPhone(r, c.Phone) != null) return true;
        var p = r.Pattern.Trim().ToLowerInvariant().TrimStart('@', '*', '.');
        return r.MatchType switch
        {
            // "@domain" (caller organisation only) has no address part, so it never matches an Email rule.
            RuleMatchType.Email => p.Length > 0 && !c.Email.StartsWith('@') && c.Email == p,
            RuleMatchType.Domain => p.Length > 0 && DomainMatches(c.Domain, p),
            RuleMatchType.Phone => false, // handled above
            RuleMatchType.CatchAll => c.Email.Length > 0 || c.Phone != null,
            _ => false,
        };
    }

    private static bool IsExcepted(RoutingRule r, Caller c) =>
        ParseList(r.Exceptions).Any(x =>
            x.StartsWith('+') ? c.Phone != null && NormalizePhone(x) is { } prefix && c.Phone.StartsWith(prefix)
            : x.Contains('@') ? c.Email == x
            : DomainMatches(c.Domain, x));

    public record PhoneEntry(string Prefix, string? Name);

    // A phone number inside a line of text: starts with + or a digit, digits with spaces . / ( ) - in between.
    private static readonly System.Text.RegularExpressions.Regex PhoneInText =
        new(@"[+0-9][0-9 ./()\-]*[0-9]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Parses a phone list: one entry per line, number and optional name in any order, separated by spaces,
    /// tabs, ";" or "," (so rows pasted from Excel work). Unparseable lines are added to <paramref name="invalid"/>.
    /// </summary>
    public static List<PhoneEntry> ParsePhoneList(string? text, List<string>? invalid = null)
    {
        var result = new List<PhoneEntry>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            // The longest digit run is the number, so digits in a name ("Site 2") don't win.
            var m = PhoneInText.Matches(line).MaxBy(x => x.Value.Count(char.IsDigit));
            var number = m == null ? null : NormalizePhone(m.Value);
            if (number == null) { invalid?.Add(line); continue; }
            var name = (line[..m!.Index] + " " + line[(m.Index + m.Length)..]).Trim(' ', '\t', ';', ',', ':', '-', '|');
            name = System.Text.RegularExpressions.Regex.Replace(name, @"[\t;,|]+", " ").Trim();
            if (result.All(e => e.Prefix != number))
                result.Add(new PhoneEntry(number, name.Length == 0 ? null : name));
        }
        return result;
    }

    public static string FormatPhoneList(IEnumerable<PhoneEntry> entries) =>
        string.Join("\n", entries.Select(e => e.Name == null ? e.Prefix : $"{e.Prefix} {e.Name}"));

    /// <summary>All phone entries of a rule: its Phone match list plus its extra caller numbers.</summary>
    public static List<PhoneEntry> PhoneEntries(RoutingRule rule) =>
        (rule.MatchType == RuleMatchType.Phone ? ParsePhoneList(rule.Pattern) : [])
            .Concat(ParsePhoneList(rule.CallerPhones)).ToList();

    /// <summary>The most specific phone entry of a rule matching the caller (longest prefix), or null.</summary>
    public static PhoneEntry? MatchPhone(RoutingRule rule, string phoneE164) =>
        PhoneEntries(rule).Where(e => phoneE164.StartsWith(e.Prefix)).MaxBy(e => e.Prefix.Length);

    /// <summary>
    /// "+41 21 123 45 67" / "0041211234567" / "021 123 45 67" → "+41211234567". National numbers starting with
    /// a single 0 are taken as Swiss. Null if the text isn't a phone number.
    /// </summary>
    public static string? NormalizePhone(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("00")) s = "+" + s[2..];
        else if (s.StartsWith('0')) s = "+41" + s[1..];
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
