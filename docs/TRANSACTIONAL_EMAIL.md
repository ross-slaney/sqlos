# Transactional Email

SqlOS transactional email provides audited, template-key based delivery for both host application messages and built-in AuthServer messages. Every SqlOS implementation automatically gets built-in templates for Email OTP, organization invitations, and password reset emails during startup.

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

## Dashboard

Open `SqlOS Dashboard > Communications`.

- Templates: create, edit, activate, deactivate, delete templates without delivery history, and preview rendered output with sample variables.
- Messages: filter delivery log entries by status, template key, recipient, and date range.
- Built-in auth templates: `auth.email-otp`, `auth.invitation`, and `auth.password-reset` are created automatically and can be customized like other templates.

## Retention and PII posture

SqlOS stores delivery history with recipient, template key/version, status, timestamps, provider message id, sanitized error, rendered subject, and rendered text preview. It does not persist arbitrary variables JSON by default because variables can contain secrets. Rendered HTML bodies are not stored unless `options.Email.PersistRenderedHtmlPreview` is enabled.

Use `options.Email.DeliveryRetention` as the retention policy value for host cleanup jobs. SqlOS records the option but does not run a destructive cleanup worker in V0.

## Auth email flows

Email OTP, organization invitations, and password reset emails use the built-in transactional templates when transactional delivery is configured. Their rendered bodies are suppressed in delivery history because they contain codes or token-bearing links.

Existing custom `BuildMessage` hooks and custom `ISqlOSAuthEmailSender` implementations still work as escape hatches for advanced auth email behavior.
