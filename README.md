# Mail Router — redirect help requests to the right channel

A small self-hosted web app that watches **your** Outlook inbox and Teams chats. When someone asks you for help directly instead of following the procedure, it:

1. matches the sender against **rules you configure** (by domain, e.g. `cdb1.ch`, or by exact address),
2. optionally asks **Claude** whether the message is really a help request (so "thanks!" or small talk isn't redirected),
3. **forwards** the request to the correct address (e.g. `support@cdb1.ch`),
4. **replies** to the sender telling them where it went and where to go next time,
5. logs everything in a **dashboard** so you can see what happened and why.

Stack: ASP.NET Core 10 (Razor Pages), SQLite, Microsoft Graph (delegated), Anthropic C# SDK, Docker.

## How it works

```
 Outlook Inbox ─┐                 ┌─ Rules (domain / email / catch-all, priority)
                ├─► Poller (60s) ─┤─ Claude triage: help request? category, summary, language
 Teams 1:1 chat ┘                 └─ Action: Reply+Forward │ Reply only │ Forward only │ Ignore
                                          │
                     Email: Graph forward + reply on the same thread, tag "AI-Routed"
                     Teams: chat reply as you + email to the forward address
                                          │
                                 SQLite log ─► Dashboard
```

Safety built in:
- **Dry-run mode is ON by default**: nothing is sent; the dashboard shows what *would* have happened.
- Only messages received **after the first run** are processed. Old mail is never touched.
- The bot replies **once per email conversation** (and at most once per 24h per Teams chat). Follow-up messages are still forwarded.
- Auto-replies, newsletters and bounces are skipped (based on `Auto-Submitted` / `Precedence` headers).
- Your own messages are ignored.
- Message content is treated as untrusted data in the prompts.

## Setup

### 1. Register an app in Entra ID (one time, ~5 min)

Azure portal → **Entra ID → App registrations → New registration**
- Name: `Mail Router`, Supported accounts: *this organization only*. No redirect URI.
- **Authentication** → *Allow public client flows* = **Yes** (needed for device-code sign-in).
- **API permissions** → Microsoft Graph → **Delegated**:
  `User.Read`, `Mail.ReadWrite`, `Mail.Send`, `Chat.ReadWrite`, `offline_access`
  → **Grant admin consent** (or ask your M365 admin).
- Copy the **Application (client) ID** and **Directory (tenant) ID**.

> Delegated permissions are required: Microsoft only lets an app send Teams chat messages *as a user* with delegated auth. The app only ever accesses the mailbox of the account you sign in with.

### 2. Configure and run

```bash
cp .env.example .env      # fill in ADMIN__PASSWORD, GRAPH__CLIENTID, GRAPH__TENANTID, ANTHROPIC_API_KEY
docker compose up -d --build
```

Open http://localhost:8080 and log in with `ADMIN__PASSWORD`.

### 3. Connect your mailbox

**Settings → Connect Microsoft 365** shows a code. Open the link, enter the code, sign in with your account. The refresh token is stored in the `/data` volume, so you only do this once (re-do it if your org revokes sessions).

### 4. Add rules

**Rules → New rule**, e.g.:

| Prio | Name | Match | Action | Forward to |
|---|---|---|---|---|
| 10 | My manager | Email `boss@mycompany.ch` | Ignore | |
| 10 | My team | Domain `team.mycompany.ch` | Ignore | |
| 100 | CDB1 customers | Domain `cdb1.ch` | Reply & forward | `support@cdb1.ch` |
| 100 | Internal users | Domain `mycompany.ch` | Reply & forward | `helpdesk@mycompany.ch` |
| 900 | Everyone else | Catch-all | Ignore | |

Matching: lowest priority number first; on a tie, an exact email beats a domain and a longer domain beats a shorter one. A domain rule also matches subdomains (`cdb1.ch` matches `it.cdb1.ch`, but not `notcdb1.ch`). Use **Rules → Test a sender** to check.

Reply template placeholders: `{SenderName}`, `{SenderEmail}`, `{ForwardTo}`, `{Subject}`, `{Summary}`. Tick **Personalise the reply with AI** to have Claude adapt the wording and language to each message while keeping the same addresses.

### 5. Go live

Watch the dashboard in dry-run for a few days. When the decisions look right, turn **Dry-run** off in Settings.

## Kubernetes

Manifests and a step-by-step guide are in [deploy/k8s/](deploy/k8s/README.md): `kubectl apply -k deploy/k8s` after setting your registry, hostname and ingress class. Run only **one** instance in total (Docker or Kubernetes, never both).

## Configuration reference

| Env var | Meaning |
|---|---|
| `ADMIN__PASSWORD` | Dashboard password (required) |
| `GRAPH__CLIENTID` / `GRAPH__TENANTID` | Entra ID app registration |
| `ANTHROPIC_API_KEY` | Claude API key. Optional; without it, AI triage and personalisation are skipped |
| `ANTHROPIC__MODEL` | Default `claude-opus-5-5` (`claude-haiku-5-5` is much cheaper for high volume) |
| `DataDirectory` | Default `/data` in the container |
| `CALLING__APPSECRET` | Client secret of the same app registration, for the Teams calling bot (optional, see below) |
| `CALLING__APPID` / `CALLING__TENANTID` | Only to use a different app registration for the bot. Default: the `GRAPH__` values |
| `CALLING__PUBLICBASEURL` | Public HTTPS URL of this app, e.g. `https://mailrouter.auxitech.ch` (Teams calls back to it) |

Everything else (dry-run, polling interval, Teams 1:1 only, footer, Outlook tag) is configured in the UI.

## Teams calls (optional)

Rules with the **Calls** channel can send your Teams calls to another number. The caller first hears a short recorded announcement, in the language set on the rule, and the call is then transferred. Without an announcement, the call is redirected straight away.

How it works: in Teams you forward **unanswered** calls to a calling bot. The bot matches the caller against your rules:
- **Matched:** it plays the announcement and transfers the call to the rule's number.
- **No rule:** the call goes to the fallback number from Settings, or is declined.

You can still pick up any call yourself before it's forwarded.

Callers are matched like this:
- **Your own colleagues:** by email address.
- **Teams users from other organisations:** by their organisation's domain (address-level rules and exceptions can't apply to them).
- **Phone callers:** by a *Phone prefix* rule (e.g. `+4121`), or by the catch-all.

**Prerequisites** (tenant admin):
1. On the **same app registration** as in step 1 of Setup:
   - add a **client secret** (Certificates & secrets);
   - add these **Application** permissions (API permissions → Microsoft Graph → Application permissions) and grant admin consent: `Calls.Initiate.All`, `Calls.AccessMedia.All`, `User.Read.All`, `CrossTenantInformation.ReadBasic.All`.

   Keep "Allow public client flows" on. The mailbox sign-in keeps using the delegated permissions; only the bot uses the secret. `GRAPH__TENANTID` must be your tenant (domain or GUID), not `organizations`.
2. An **Azure Bot** resource, created with **Use existing app registration** (type *Single Tenant*) and that app's client id, with the Microsoft Teams channel and **Calling** enabled. Set the webhook to `https://<your host>/api/calls/callback` (shown in Settings).
3. A Teams **resource account** linked to the bot, with a **Teams Phone** licence and PSTN connectivity (Calling Plan, Operator Connect or Direct Routing). These are needed to transfer to outside phone numbers.
4. In Teams → Settings → Calls: **If unanswered** → forward to that resource account.

Then set the `CALLING__*` variables, upload the announcements in **Settings → Teams calls** (WAV, 16 kHz, 16-bit, mono), turn on **Handle Teams calls**, and tick **Calls** on the rules that should apply.

In dry-run, calls are logged with what *would* have happened and then sent to the fallback number (or declined). Calls appear in the activity log with a 📞 icon. The callback endpoint only accepts requests signed by Microsoft for your bot. If you restrict the ingress to some IPs, leave `/api/calls/` reachable from Microsoft.

## Notes & limits

- **Run behind HTTPS** (reverse proxy such as Traefik, Caddy or nginx) if it's reachable from anywhere other than your machine. The dashboard shows email content.
- Teams: the sender's email comes from the chat membership. External or federated users without an email in the chat can't be matched and are logged as skipped.
- Polling (not webhooks) keeps it simple: no public URL is needed. Default interval is 60s.
- Check your company policy on automated replies sent on your behalf. A footer saying it's automated is enabled by default.
- The database is created automatically on first start (EF Core `EnsureCreated`). Schema changes in later versions will need a migration or a fresh volume.

## Develop locally

```bash
cd src/MailRouter.Web
Admin__Password=dev Graph__ClientId=... Graph__TenantId=... ANTHROPIC_API_KEY=... dotnet run
```
