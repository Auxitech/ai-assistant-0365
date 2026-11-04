using System.Net;
using System.Text.RegularExpressions;

namespace MailRouter.Web.Services;

public static partial class Html
{
    [GeneratedRegex(@"<(br|/p|/div|/li)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();

    public static string ToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = LineBreaks().Replace(html, "\n");
        s = Tags().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        return ManyNewlines().Replace(s, "\n\n").Trim();
    }

    /// <summary>Plain text → safe HTML with line breaks.</summary>
    public static string FromText(string text) =>
        WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>");

    public static string Preview(string text, int max = 400) =>
        text.Length <= max ? text : text[..max] + "…";
}
