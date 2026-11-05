using System.Security.Cryptography;
using System.Text;
using MailRouter.Web.Data;
using Microsoft.Extensions.Options;

namespace MailRouter.Web.Services;

/// <summary>
/// Recorded call announcements, one WAV file per language, kept in {DataDirectory}/call-prompts.
/// Teams fetches them over HTTPS from /api/calls/prompts/{lang}.wav, so they must be 16 kHz, 16-bit, mono PCM WAV.
/// </summary>
public class CallPromptStore(IConfiguration config, IOptions<CallingOptions> calling)
{
    public static readonly string[] Languages = ["fr", "de", "en"];
    public const long MaxBytes = 5 * 1024 * 1024;

    public record Prompt(string Language, string Uri, string ResourceId);
    public record PromptInfo(string Language, long Bytes, TimeSpan Duration, DateTimeOffset Updated);

    private string Dir => Path.Combine(config["DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "App_Data"), "call-prompts");

    public string? PathFor(string lang) =>
        Languages.Contains(lang) && File.Exists(Path.Combine(Dir, lang + ".wav")) ? Path.Combine(Dir, lang + ".wav") : null;

    public PromptInfo? Info(string lang)
    {
        var path = PathFor(lang);
        if (path == null) return null;
        var fi = new FileInfo(path);
        // 16 kHz × 16 bit × mono = 32 000 bytes per second (header ignored).
        return new PromptInfo(lang, fi.Length, TimeSpan.FromSeconds(Math.Max(0, fi.Length - 44) / 32000.0), fi.LastWriteTimeUtc);
    }

    /// <summary>Validates and stores a recording. Returns an error message, or null on success.</summary>
    public async Task<string?> SaveAsync(string lang, Stream input, long length, CancellationToken ct)
    {
        if (!Languages.Contains(lang)) return "Unknown language.";
        if (length <= 44) return "The file is empty.";
        if (length > MaxBytes) return "The file is too large (max 5 MB, about 2½ minutes).";

        using var ms = new MemoryStream();
        await input.CopyToAsync(ms, ct);
        var error = ValidateWav(ms.ToArray());
        if (error != null) return error;

        Directory.CreateDirectory(Dir);
        var tmp = Path.Combine(Dir, lang + ".wav.tmp");
        await File.WriteAllBytesAsync(tmp, ms.ToArray(), ct);
        File.Move(tmp, Path.Combine(Dir, lang + ".wav"), overwrite: true);
        return null;
    }

    public void Delete(string lang)
    {
        var path = PathFor(lang);
        if (path != null) File.Delete(path);
    }

    /// <summary>
    /// The recordings to play, in order. A single language falls back to all available recordings (FR → DE → EN)
    /// when it hasn't been recorded. Empty = no announcement, just redirect.
    /// </summary>
    public List<Prompt> Resolve(AnnouncementLanguage setting, string? callerLanguage)
    {
        var lang = setting switch
        {
            AnnouncementLanguage.None => "none",
            AnnouncementLanguage.French => "fr",
            AnnouncementLanguage.German => "de",
            AnnouncementLanguage.English => "en",
            AnnouncementLanguage.Auto => callerLanguage,
            _ => null,
        };
        if (lang == "none") return [];
        if (lang != null && PathFor(lang) != null) return [ToPrompt(lang)];
        return Languages.Where(l => PathFor(l) != null).Select(ToPrompt).ToList();
    }

    private Prompt ToPrompt(string lang)
    {
        var version = File.GetLastWriteTimeUtc(PathFor(lang)!).Ticks;
        // resourceId identifies the cached audio on the Teams side: change it whenever the file changes.
        var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{lang}:{version}")));
        return new Prompt(lang, $"{calling.Value.PublicBaseUrl.TrimEnd('/')}/api/calls/prompts/{lang}.wav?v={version}", id.ToString());
    }

    private static string? ValidateWav(byte[] b)
    {
        if (b.Length < 44 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
            return "Not a WAV file.";
        // Walk the chunks to find "fmt ".
        for (int pos = 12; pos + 8 <= b.Length;)
        {
            var id = Encoding.ASCII.GetString(b, pos, 4);
            var size = BitConverter.ToInt32(b, pos + 4);
            if (size < 0) break;
            if (id == "fmt " && pos + 8 + 16 <= b.Length)
            {
                int format = BitConverter.ToUInt16(b, pos + 8);
                int channels = BitConverter.ToUInt16(b, pos + 10);
                int rate = BitConverter.ToInt32(b, pos + 12);
                int bits = BitConverter.ToUInt16(b, pos + 22);
                return format == 1 && channels == 1 && rate == 16000 && bits == 16
                    ? null
                    : $"Teams needs PCM WAV, 16 kHz, 16-bit, mono. This file is {(format == 1 ? "PCM" : $"format {format}")}, {rate} Hz, {bits}-bit, {channels} channel(s).";
            }
            pos += 8 + size + (size & 1);
        }
        return "WAV format chunk not found.";
    }

    /// <summary>
    /// Best guess of the caller's language: their Teams preferred language, otherwise the phone country code
    /// (Swiss landlines by area code). Null = unknown.
    /// </summary>
    public static string? GuessLanguage(string? preferredLanguage, string? phoneE164)
    {
        var pl = preferredLanguage?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(pl) && pl.Length >= 2 && Languages.Contains(pl[..2])) return pl[..2];
        if (phoneE164 == null) return null;

        if (phoneE164.StartsWith("+41"))
        {
            var area = phoneE164.Length >= 5 ? phoneE164[3..5] : "";
            if (area is "21" or "22" or "24" or "26" or "27" or "32") return "fr";       // Romandie
            if (area is "31" or "33" or "34" or "41" or "43" or "44" or "52" or "55" or "56" or "58" or "61" or "62" or "71" or "81")
                return "de";
            return null; // mobiles (07x), Ticino (091), others: unknown
        }
        string[] fr = ["+33", "+32", "+352", "+377"], de = ["+49", "+43", "+423"], en = ["+44", "+1", "+353", "+61", "+64"];
        if (fr.Any(phoneE164.StartsWith)) return "fr";
        if (de.Any(phoneE164.StartsWith)) return "de";
        if (en.Any(phoneE164.StartsWith)) return "en";
        return null;
    }
}
