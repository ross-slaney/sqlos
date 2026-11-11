# Transactional Email

SqlOS transactional email provides audited, template-key based delivery for both host application messages and built-in AuthServer messages. Every SqlOS implementation automatically gets built-in templates for Email OTP, magic links, organization invitations, and password reset emails during startup.

## ACS setup

Configure Azure Communication Services Email with a verified sender:

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.ConfigureEmail(email =>
    {
        email.AzureCommunicationServicesConnectionString =
            builder.Configuration["SqlOS:Email:AzureCommunicationServicesConnectionString"];
        email.FromAddress =
            builder.Configuration["SqlOS:Email:FromAddress"];
    });
});
```

The same ACS provisioning script used for Email OTP can create the email domain and sender:

```bash
./scripts/azure/setup-acs-email.sh --apply-dns --yes
```

## Template syntax

Templates use constrained `{variable}` placeholders in subject, HTML, and text bodies.

- HTML body substitutions are HTML-encoded.
- Subject and text substitutions are plain text replacements.
- Missing variables fail preview and send with a typed validation error.
- Extra variables are ignored.

Example:

```csharp
await email.SendAsync(new SqlOSSendEmailRequest(
    TemplateKey: "order-shipped",
    To: "user@example.com",
    Variables: new Dictionary<string, object?>
    {
        ["orderId"] = "123",
        ["trackingUrl"] = "https://tracking.example.test/123"
    },
    IdempotencyKey: "order-123-shipped"));
```

## List-Unsubscribe headers

Pass `ListUnsubscribe` to add unsubscribe headers to mail a recipient can opt out of:

```csharp
await email.SendAsync(new SqlOSSendEmailRequest(
    TemplateKey: "weekly-digest",
    To: "user@example.com",
    Variables: new Dictionary<string, object?>(),
    ListUnsubscribe: new SqlOSEmailListUnsubscribe(
        OneClickUrl: new Uri("https://app.example.com/email/unsubscribe/one-click?token=abc"),
        MailTo: new Uri("mailto:unsubscribe@example.com?subject=unsubscribe"))));
```

SqlOS sends:

```text
List-Unsubscribe: <https://app.example.com/email/unsubscribe/one-click?token=abc>, <mailto:unsubscribe@example.com?subject=unsubscribe>
List-Unsubscribe-Post: List-Unsubscribe=One-Click
```

- `OneClickUrl` must be an absolute `https` URI without user info. It must unsubscribe the recipient on an RFC 8058 `POST` with the body `List-Unsubscribe=One-Click`, with no further interaction, and must not unsubscribe on `GET`.
- `MailTo` is optional and must be a `mailto:` URI. Without `OneClickUrl`, SqlOS omits `List-Unsubscribe-Post`.
- Internationalized host names must use punycode.
- Invalid values throw `InvalidOperationException` before SqlOS records a delivery or calls the provider.
- SqlOS does not store the header URLs or the recipient's opt-out. The host's endpoint records opt-outs and the host checks them before sending.

The built-in ACS sender passes the headers as ACS custom headers. Custom `ISqlOSEmailSender` and `ISqlOSAuthEmailSender` implementations receive them in `Headers` and must copy every entry onto the outgoing message. Check a delivered message's raw headers: mailbox providers offer one-click unsubscribe only when the sending provider's DKIM signature covers both headers and leaves their values unencoded.

## Dashboard

Open `SqlOS Dashboard > Communications`.

- Templates: create, edit, activate, deactivate, delete templates without delivery history, and preview rendered output with sample variables.
- Messages: filter delivery log entries by status, template key, recipient, and date range.
- Built-in auth templates: `auth.email-otp`, `auth.magic-link`, `auth.invitation`, and `auth.password-reset` are created automatically and can be customized like other templates.

## Retention and PII posture

SqlOS stores delivery history with recipient, template key/version, status, timestamps, provider message id, sanitized error, rendered subject, and rendered text preview. It does not persist arbitrary variables JSON by default because variables can contain secrets. Rendered HTML bodies are not stored unless `options.Email.PersistRenderedHtmlPreview` is enabled.

Use `options.Email.DeliveryRetention` as the retention policy value for host cleanup jobs. SqlOS records the option but does not run a destructive cleanup worker in V0.

## Auth email flows

Email OTP, magic links, organization invitations, and password reset emails use the built-in transactional templates when transactional delivery is configured. Their rendered bodies are suppressed in delivery history because they contain codes or token-bearing links.

Upgrade behavior is additive for normal AuthServer email configuration. Existing `AuthServer.ConfigureEmailOtp(...)` ACS settings continue to work; configuring `options.Email` is optional unless you want separate transactional email credentials.

Existing custom `BuildMessage` hooks still send through `ISqlOSAuthEmailSender`. Existing custom `ISqlOSAuthEmailSender` implementations also remain usable as the default rendered-template sender when no `options.Email` ACS sender is configured. Hosts that want a separate provider for all transactional templates can register `ISqlOSEmailSender` directly. Custom senders must copy `SqlOSEmailMessage.Headers` onto the outgoing message.
