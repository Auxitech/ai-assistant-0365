using System.ComponentModel.DataAnnotations;

namespace MailRouter.Web.Data;

public enum RuleMatchType
{
    /// <summary>Matches the sender's domain and all its subdomains (e.g. "cdb1.ch" matches "a.cdb1.ch").</summary>
    Domain,
    /// <summary>Matches one exact sender address.</summary>
    Email,
    /// <summary>Matches every sender not matched by a more specific rule.</summary>
    CatchAll,
    /// <summary>
    /// Calls only: a list of phone numbers / prefixes, one per line, optionally with a name
    /// ("+41791234567 Jean Dupont", "+4121" = every Lausanne landline).
    /// </summary>
    [Display(Name = "Phone numbers")]
    Phone,
}

/// <summary>Language of the recorded announcement played to a caller before the call is transferred.</summary>
public enum AnnouncementLanguage
{
    [Display(Name = "No announcement (redirect straight away)")]
    None,
    [Display(Name = "Automatic (caller's language, otherwise FR → DE → EN)")]
    Auto,
    [Display(Name = "French")]
    French,
    [Display(Name = "German")]
    German,
    [Display(Name = "English")]
    English,
    [Display(Name = "French → German → English")]
    AllThree,
}

public enum RuleAction
{
    /// <summary>Reply to the sender with the redirect message AND forward the original to the target address.</summary>
    ReplyAndForward,
    /// <summary>Only reply to the sender with the redirect message.</summary>
    ReplyOnly,
    /// <summary>Only forward the original to the target address, silently.</summary>
    ForwardOnly,
    /// <summary>Do nothing (useful to whitelist your boss / your team ahead of a broader rule).</summary>
    Ignore,
}

[Flags]
public enum ChannelFlags
{
    None = 0,
    Email = 1,
    Teams = 2,
    Both = Email | Teams,
    /// <summary>Teams calls forwarded to the calling bot.</summary>
    Calls = 4,
}

public enum MessageSource
{
    Email,
    Teams,
    Call,
}

public enum ProcessingStatus
{
    /// <summary>Dry-run: what the bot WOULD have done, nothing was sent.</summary>
    DryRun,
    /// <summary>Actions were executed.</summary>
    Done,
    /// <summary>No rule matched, rule says Ignore, or AI said it's not a support request.</summary>
    Skipped,
    /// <summary>Something went wrong; see Error.</summary>
    Failed,
}

public class RoutingRule
{
    public int Id { get; set; }

    [Required, StringLength(100)]
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Lower number = evaluated first.</summary>
    public int Priority { get; set; } = 100;

    public RuleMatchType MatchType { get; set; } = RuleMatchType.Domain;

    /// <summary>
    /// "cdb1.ch" for Domain, "john@cdb1.ch" for Email, ignored for CatchAll.
    /// Phone: one "+41791234567 Optional Name" per line.
    /// </summary>
    [StringLength(20000)]
    public string Pattern { get; set; } = "";

    /// <summary>
    /// Senders this rule must NOT apply to: exact addresses ("samuelrichoz@cdb1.ch") and/or domains ("it.cdb1.ch").
    /// Separated by comma, semicolon, space or newline. An excepted sender falls through to the next matching rule.
    /// </summary>
    [StringLength(2000)]
    public string? Exceptions { get; set; }

    public ChannelFlags Channels { get; set; } = ChannelFlags.Both;

    public RuleAction Action { get; set; } = RuleAction.ReplyAndForward;

    /// <summary>Comma/semicolon separated addresses the original message is forwarded to.</summary>
    [StringLength(500)]
    public string? ForwardTo { get; set; }

    /// <summary>Outlook folder for emails handled by this rule (e.g. "Clients/CDB1"). Empty = use the global folder from Settings.</summary>
    [StringLength(200)]
    public string? MoveToFolder { get; set; }

    /// <summary>
    /// Reply sent to the requester. Placeholders: {SenderName}, {SenderEmail}, {ForwardTo}, {Subject}, {Summary}.
    /// </summary>
    public string ReplyTemplate { get; set; } =
        "Hello {SenderName},\n\nThanks for your message. To make sure your request is handled and tracked, I've forwarded it to {ForwardTo}. Please use that address for any follow-up on this topic.\n\nBest regards";

    /// <summary>Let Claude rewrite the template into a personalised reply (same meaning, sender's language).</summary>
    public bool UseAiReply { get; set; }

    /// <summary>Only act if Claude classifies the message as a help/support request (avoids redirecting "thanks!" or social chat).</summary>
    public bool RequireSupportRequest { get; set; } = true;

    /// <summary>Minimum AI confidence (0..1) required when RequireSupportRequest is on.</summary>
    [Range(0, 1)]
    public double MinConfidence { get; set; } = 0.7;

    /// <summary>Calls channel: number the call is transferred to, in E.164 format (e.g. "+41211234567").</summary>
    [StringLength(32)]
    public string? RedirectPhoneNumber { get; set; }

    /// <summary>
    /// Calls channel: extra caller phone numbers this rule also applies to, whatever the match type
    /// (e.g. employees of the matched company calling from private phones). One "+41791234567 Name" per line.
    /// </summary>
    public string? CallerPhones { get; set; }

    /// <summary>Calls channel: which recorded announcement is played before the transfer.</summary>
    public AnnouncementLanguage CallAnnouncement { get; set; } = AnnouncementLanguage.Auto;
}

public class ProcessedMessage
{
    public int Id { get; set; }
    public MessageSource Source { get; set; }

    /// <summary>Graph message id (unique per source).</summary>
    [StringLength(400)]
    public string ExternalId { get; set; } = "";

    /// <summary>Email conversationId or Teams chatId.</summary>
    [StringLength(400)]
    public string? ConversationId { get; set; }

    public string? SenderName { get; set; }
    public string? SenderEmail { get; set; }
    public string? SenderDomain { get; set; }
    public string? Subject { get; set; }
    public string? BodyPreview { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }

    public int? RuleId { get; set; }
    public string? RuleName { get; set; }
    public RuleAction? Action { get; set; }

    public bool? AiIsSupportRequest { get; set; }
    public bool? AiIsSensitive { get; set; }
    public string? AiCategory { get; set; }
    public double? AiConfidence { get; set; }
    public string? AiSummary { get; set; }

    public bool Replied { get; set; }
    public string? ReplyText { get; set; }
    public string? ForwardedTo { get; set; }
    public string? ForwardNote { get; set; }
    /// <summary>Subject of the forwarded message, as sent (or as it would be sent in dry-run).</summary>
    public string? ForwardSubject { get; set; }
    /// <summary>Full plain-text body of the forwarded message: forward note + original message.</summary>
    public string? ForwardBody { get; set; }

    public ProcessingStatus Status { get; set; }
    public string? Note { get; set; }
    public string? Error { get; set; }
}

/// <summary>Single-row table with runtime settings editable from the UI.</summary>
public class AppSettings
{
    public int Id { get; set; } = 1;

    /// <summary>Master switch for the background poller.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>When on, nothing is sent; the dashboard shows what would have happened.</summary>
    public bool DryRun { get; set; } = true;

    public bool ProcessEmail { get; set; } = true;
    public bool ProcessTeams { get; set; } = true;

    /// <summary>Teams: only 1:1 chats (recommended). Group chats are skipped unless this is off.</summary>
    public bool TeamsOneOnOneOnly { get; set; } = true;

    /// <summary>Mark handled emails (status Done) as read in Outlook.</summary>
    public bool MarkEmailAsRead { get; set; } = true;

    /// <summary>Outlook folder handled emails are moved to (e.g. "AI-Routed" or "Traité/Assistant"). Empty = leave in Inbox.</summary>
    [StringLength(200)]
    public string? HandledFolder { get; set; } = "AI-Routed";

    /// <summary>Mark the Teams chat as read once a message in it was handled (Graph only supports whole-chat read state).</summary>
    public bool MarkTeamsAsRead { get; set; } = true;

    [Range(15, 3600)]
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Handle Teams calls forwarded to the calling bot (needs the Calling:* configuration).</summary>
    public bool ProcessCalls { get; set; }

    /// <summary>Where calls go when no rule applies (E.164, e.g. your mobile). Empty = the call is declined.</summary>
    [StringLength(32)]
    public string? CallFallbackNumber { get; set; }

    /// <summary>Appended to every reply. Many companies require disclosing automated answers.</summary>
    public string? ReplyFooter { get; set; } = DefaultFooter;

    /// <summary>
    /// Mandatory notice added to every reply when the message is forwarded (ReplyAndForward):
    /// it was only forwarded by the assistant, is marked as handled, and the owner won't act on it.
    /// Placeholders: {ForwardTo}, {SenderName}. Empty = no notice.
    /// </summary>
    public string? RedirectNotice { get; set; } = DefaultRedirectNotice;

    public const string DefaultRedirectNotice =
        "Votre message a uniquement été transféré automatiquement par mon assistant et est désormais marqué comme traité de mon côté. Je n'y donnerai pas suite personnellement : merci de passer par le canal prévu ({ForwardTo}) pour que votre demande soit prise en charge.\n\n" +
        "Ihre Nachricht wurde von meinem Assistenten lediglich automatisch weitergeleitet und ist bei mir nun als erledigt markiert. Ich werde mich nicht persönlich darum kümmern: Bitte nutzen Sie den vorgesehenen Kanal ({ForwardTo}), damit Ihre Anfrage bearbeitet wird.\n\n" +
        "Your message was only forwarded automatically by my assistant and is now marked as handled on my side. I will not act on it personally: please use the proper channel ({ForwardTo}) so your request can be processed.";

    /// <summary>
    /// Text placed above the original message when it is forwarded to the proper channel.
    /// Placeholders: {Owner}, {SenderName}, {SenderEmail}, {RuleName}, {Summary}, {Subject}.
    /// Lines containing {Summary} are dropped when there is no AI summary.
    /// </summary>
    public string? ForwardNotice { get; set; } = DefaultForwardNotice;

    public const string DefaultForwardNotice =
        "[FR] Message automatique : ce message a été transféré automatiquement par l'assistant de {Owner}.\n" +
        "{SenderName} <{SenderEmail}> l'a envoyé directement à {Owner} au lieu d'utiliser le canal prévu. {Owner} ne le traitera pas personnellement ; le demandeur a été invité à passer par ce canal. Merci de prendre la demande en charge selon votre processus habituel.\n" +
        "Résumé : {Summary}\n\n" +
        "[DE] Automatische Nachricht: Diese Nachricht wurde vom Assistenten von {Owner} automatisch weitergeleitet.\n" +
        "{SenderName} <{SenderEmail}> hat sie direkt an {Owner} geschickt, statt den vorgesehenen Kanal zu nutzen. {Owner} wird sie nicht persönlich bearbeiten; die anfragende Person wurde gebeten, diesen Kanal zu verwenden. Bitte übernehmen Sie die Anfrage gemäss Ihrem üblichen Prozess.\n" +
        "Zusammenfassung: {Summary}\n\n" +
        "[EN] Automated message: this message was forwarded automatically by {Owner}'s assistant.\n" +
        "{SenderName} <{SenderEmail}> sent it directly to {Owner} instead of using the proper channel. {Owner} will not handle it personally; the requester has been asked to use this channel. Please take the request over through your usual process.\n" +
        "Summary: {Summary}\n\n" +
        "(rule: {RuleName})";

    public const string LegacyFooter = "— This is an automated reply from my assistant.";
    public const string DefaultFooter =
        "— Ceci est une réponse automatique de mon assistant.\n" +
        "— Dies ist eine automatische Antwort meines Assistenten.\n" +
        "— This is an automated reply from my assistant.";

    /// <summary>Tag added to processed emails in Outlook so you can see them at a glance.</summary>
    [StringLength(50)]
    public string? OutlookCategory { get; set; } = "AI-Routed";

    /// <summary>Only messages received after this point are processed (set on first connect so old mail isn't touched).</summary>
    public DateTimeOffset? EmailWatermark { get; set; }
    public DateTimeOffset? TeamsWatermark { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastRunError { get; set; }
}
