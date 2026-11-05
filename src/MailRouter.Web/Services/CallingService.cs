using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MailRouter.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailRouter.Web.Services;

/// <summary>
/// Handles Teams calls forwarded to the calling bot. Flow for a call matching a rule with a phone number:
///   incoming → answer → (established) play announcement → (prompt completed) transfer to the number.
/// Without an announcement the call is redirected without being answered. Unmatched calls go to the
/// fallback number from Settings, or are declined.
/// Notifications are queued and handled one at a time, so the states of a call never race each other.
/// </summary>
public class CallingService(
    IServiceScopeFactory scopes,
    CallingGraphClient graph,
    CallPromptStore prompts,
    IOptions<CallingOptions> opt,
    ILogger<CallingService> log) : BackgroundService
{
    /// <summary>Transfer anyway if the announcement hasn't finished after this long.</summary>
    private static readonly TimeSpan AnnouncementTimeout = TimeSpan.FromSeconds(90);

    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, PendingCall> _pending = new();

    private sealed class PendingCall
    {
        /// <summary>Activity log entry id; set once the entry is saved.</summary>
        public int LogId { get; set; }
        public required string Target { get; init; }
        public required List<CallPromptStore.Prompt> Prompts { get; init; }
        public bool PromptStarted { get; set; }
        public bool TransferRequested { get; set; }
    }

    private sealed record Timeout(string CallId);

    public bool IsConfigured => opt.Value.IsConfigured;

    /// <summary>Called by the webhook endpoint; returns immediately so Teams gets its 202 quickly.</summary>
    public void Enqueue(JsonNode payload) => _queue.Writer.TryWrite(payload);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stop))
        {
            try
            {
                if (item is Timeout t) await OnTimeoutAsync(t.CallId, stop);
                else if (item is JsonNode n)
                    foreach (var notification in n["value"]?.AsArray() ?? [])
                        if (notification != null) await HandleAsync(notification, stop);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Call notification failed");
            }
        }
    }

    private async Task HandleAsync(JsonNode n, CancellationToken ct)
    {
        var resource = (n["resourceUrl"] ?? n["resource"])?.GetValue<string>() ?? "";
        var data = n["resourceData"];
        var type = data?["@odata.type"]?.GetValue<string>() ?? "";
        var changeType = n["changeType"]?.GetValue<string>() ?? "";

        // "/communications/calls/{callId}" or "/app/calls/{callId}/operations/{opId}"
        var segments = resource.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var callsIdx = Array.IndexOf(segments, "calls");
        var callId = callsIdx >= 0 && callsIdx + 1 < segments.Length ? segments[callsIdx + 1] : data?["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(callId)) return;

        if (type == "#microsoft.graph.playPromptOperation")
        {
            if (_pending.TryGetValue(callId, out var p) && p.PromptStarted && !p.TransferRequested
                && data?["status"]?.GetValue<string>() is "completed" or "failed")
                await TransferAsync(callId, p, ct);
            return;
        }
        if (type != "#microsoft.graph.call") return;

        var state = data?["state"]?.GetValue<string>();
        if (changeType == "deleted" || state == "terminated")
        {
            await OnTerminatedAsync(callId, data, ct);
            return;
        }
        if (state == "incoming" && changeType == "created")
        {
            await OnIncomingAsync(callId, data!, ct);
            return;
        }
        if (state == "established" && _pending.TryGetValue(callId, out var pc) && !pc.PromptStarted)
        {
            pc.PromptStarted = true;
            try
            {
                await graph.PlayPromptsAsync(callId, pc.Prompts, ct);
                _ = Task.Delay(AnnouncementTimeout, ct).ContinueWith(_ => _queue.Writer.TryWrite(new Timeout(callId)), TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not play the announcement; transferring straight away");
                await TransferAsync(callId, pc, ct);
            }
        }
    }

    private async Task OnIncomingAsync(string callId, JsonNode call, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Messages.AnyAsync(m => m.Source == MessageSource.Call && m.ExternalId == callId, ct)) return;
        var settings = await db.GetSettingsAsync(ct);

        // --- Who is calling? ---
        var identity = call["source"]?["identity"];
        var user = identity?["user"];
        var phone = RuleMatcher.NormalizePhone(identity?["phone"]?["id"]?.GetValue<string>());
        string name = user?["displayName"]?.GetValue<string>() ?? identity?["phone"]?["displayName"]?.GetValue<string>() ?? "";
        string? email = null, domain = null, preferredLanguage = null;
        if (user?["id"]?.GetValue<string>() is { Length: > 0 } userId)
        {
            var info = await graph.GetUserAsync(userId, ct);
            if (info != null)
            {
                email = info.Email?.ToLowerInvariant();
                preferredLanguage = info.PreferredLanguage;
                if (string.IsNullOrEmpty(name)) name = info.DisplayName;
            }
            else if (user["tenantId"]?.GetValue<string>() is { Length: > 0 } tid)
            {
                domain = (await graph.GetTenantDomainAsync(tid, ct))?.ToLowerInvariant();
            }
        }
        // Rule matching key: address, "@domain" for other organisations, or the phone number.
        var sender = email ?? (domain != null ? "@" + domain : phone) ?? "";

        var rec = new ProcessedMessage
        {
            Source = MessageSource.Call,
            ExternalId = callId,
            ConversationId = callId,
            SenderName = string.IsNullOrEmpty(name) ? (phone ?? "Unknown caller") : name,
            SenderEmail = email ?? phone,
            SenderDomain = email != null ? RuleMatcher.DomainOf(email) : domain,
            Subject = "(Teams call)",
            BodyPreview = $"Incoming call from {(string.IsNullOrEmpty(name) ? "" : name + " ")}{email ?? phone ?? (domain != null ? "(" + domain + ")" : "")}".Trim(),
            ReceivedAt = DateTimeOffset.UtcNow,
            ProcessedAt = DateTimeOffset.UtcNow,
        };
        db.Messages.Add(rec);

        try
        {
            await DecideAsync(callId, rec, settings, await db.Rules.AsNoTracking().ToListAsync(ct), sender, preferredLanguage, phone, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Failed to handle call {CallId}", callId);
            rec.Status = ProcessingStatus.Failed;
            rec.Error = ex.Message;
            _pending.TryRemove(callId, out _);
            await FallbackAsync(callId, rec, settings, ct);
        }
        await db.SaveChangesAsync(ct);

        if (_pending.TryGetValue(callId, out var p)) p.LogId = rec.Id;
    }

    private async Task DecideAsync(string callId, ProcessedMessage rec, AppSettings settings, List<RoutingRule> rules,
        string sender, string? preferredLanguage, string? phone, CancellationToken ct)
    {
        if (!settings.Enabled || !settings.ProcessCalls)
        {
            rec.Note = "Call handling is off in Settings. ";
            await FallbackAsync(callId, rec, settings, ct);
            return;
        }

        var rule = RuleMatcher.Match(rules, sender, MessageSource.Call);
        if (rule != null)
        {
            rec.RuleId = rule.Id;
            rec.RuleName = rule.Name;
            rec.Action = rule.Action;
        }
        var target = RuleMatcher.NormalizePhone(rule?.RedirectPhoneNumber);
        if (rule == null || rule.Action == RuleAction.Ignore || target == null)
        {
            rec.Note = rule == null ? "No matching rule. "
                : rule.Action == RuleAction.Ignore ? "Rule action is Ignore. "
                : "Rule has no phone number to redirect to. ";
            await FallbackAsync(callId, rec, settings, ct);
            return;
        }

        var language = CallPromptStore.GuessLanguage(preferredLanguage, phone);
        var toPlay = prompts.Resolve(rule.CallAnnouncement, language);
        var announcement = toPlay.Count == 0 ? "no announcement" : "announcement " + string.Join(" → ", toPlay.Select(p => p.Language.ToUpperInvariant()));
        rec.ForwardedTo = target;

        if (settings.DryRun)
        {
            rec.Status = ProcessingStatus.DryRun;
            rec.Note = $"Dry-run: would play {announcement} and transfer to {target}. ";
            await FallbackAsync(callId, rec, settings, ct, keepStatus: true);
            return;
        }

        if (toPlay.Count == 0)
        {
            await graph.RedirectAsync(callId, target, ct);
            rec.Status = ProcessingStatus.Done;
            rec.Note = $"Redirected to {target} ({announcement}).";
            return;
        }

        _pending[callId] = new PendingCall { Target = target, Prompts = toPlay };
        await graph.AnswerAsync(callId, toPlay, ct);
        rec.Status = ProcessingStatus.Done;
        rec.Note = $"Answered; playing {announcement}, then transferring to {target}. ";
    }

    /// <summary>Unmatched / disabled / failed: send to the fallback number from Settings, or decline.</summary>
    private async Task FallbackAsync(string callId, ProcessedMessage rec, AppSettings settings, CancellationToken ct, bool keepStatus = false)
    {
        if (!keepStatus && rec.Status != ProcessingStatus.Failed) rec.Status = ProcessingStatus.Skipped;
        var fallback = RuleMatcher.NormalizePhone(settings.CallFallbackNumber);
        try
        {
            if (fallback != null)
            {
                await graph.RedirectAsync(callId, fallback, ct);
                rec.Note += $"Sent to the fallback number {fallback}.";
            }
            else
            {
                await graph.RejectAsync(callId, ct);
                rec.Note += "Declined (no fallback number in Settings).";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Fallback for call {CallId} failed", callId);
            rec.Note += $"Fallback failed: {ex.Message}";
        }
    }

    private async Task TransferAsync(string callId, PendingCall p, CancellationToken ct)
    {
        p.TransferRequested = true;
        try
        {
            await graph.TransferAsync(callId, p.Target, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Transfer of call {CallId} failed", callId);
            await UpdateLogAsync(p.LogId, rec => { rec.Status = ProcessingStatus.Failed; rec.Error = $"Transfer to {p.Target} failed: {ex.Message}"; }, ct);
            try { await graph.HangUpAsync(callId, ct); } catch (Exception hx) { log.LogWarning(hx, "Hang-up failed"); }
        }
    }

    private async Task OnTimeoutAsync(string callId, CancellationToken ct)
    {
        if (_pending.TryGetValue(callId, out var p) && !p.TransferRequested)
        {
            log.LogWarning("Announcement on call {CallId} did not finish in time; transferring", callId);
            await TransferAsync(callId, p, ct);
        }
    }

    private async Task OnTerminatedAsync(string callId, JsonNode? data, CancellationToken ct)
    {
        if (!_pending.TryRemove(callId, out var p)) return;
        var result = data?["resultInfo"];
        var code = result?["code"]?.GetValue<int>();
        var message = result?["message"]?.GetValue<string>();
        await UpdateLogAsync(p.LogId, rec =>
        {
            if (!p.TransferRequested)
                rec.Note += "Caller hung up during the announcement.";
            else if (code is >= 400)
                rec.Note += $"Call ended with code {code}: {message}";
            else
                rec.Note += $"Transferred to {p.Target}.";
        }, ct);
    }

    private async Task UpdateLogAsync(int logId, Action<ProcessedMessage> change, CancellationToken ct)
    {
        if (logId == 0) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rec = await db.Messages.FindAsync([logId], ct);
        if (rec == null) return;
        change(rec);
        await db.SaveChangesAsync(ct);
    }
}
