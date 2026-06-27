# Magic Link

SqlOS magic links provide passwordless email sign-in with a short-lived, single-use token. They use the same AuthServer user, client, organization, email branding, transactional email, session, and audit infrastructure as Email OTP.

Magic links are a primary local credential, not an MFA factor. If tenant MFA policy requires TOTP, SqlOS still returns the normal MFA challenge after the link is completed.

## Enable AuthPage magic links

Add `magic_link` to the AuthPage credential list:

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.AuthServer.SeedAuthPage(page =>
    {
        page.EnabledCredentialTypes = ["magic_link"];
        page.EnablePasswordSignup = false;
    });
});
```

To make Email OTP primary while still allowing password fallback:

```csharp
options.AuthServer.SeedAuthPage(page =>
{
    page.EnabledCredentialTypes = ["email_otp", "password"];
    page.EnablePasswordSignup = false;
});
```

## Hosted AuthPage flow

Hosted AuthPage exposes:

```text
GET  /sqlos/auth/login/magic-link
POST /sqlos/auth/login/magic-link/start
GET  /sqlos/auth/login/magic-link/complete?token=...
POST /sqlos/auth/login/magic-link/complete
```

The `GET /complete` route only renders a confirmation form. It does not consume the token. The form posts the token to the `POST /complete` route, which consumes the token, validates the client or authorization-request binding, issues the session or OAuth redirect, and handles organization selection or MFA as needed.

This pattern keeps mailbox scanners, link previewers, and security gateways from signing the user in just by fetching the link.

## Headless flow

Headless UIs call SqlOS for state transitions and render the returned view model:

```text
POST /sqlos/auth/headless/magic-link/start
POST /sqlos/auth/headless/magic-link/complete
```

Start requires the active authorization request id and email:

```json
{
  "requestId": "sar_...",
  "email": "jane@example.com"
}
```

Complete requires the token from the email link:

```json
{
  "token": "..."
}
```

SqlOS runs home realm discovery before creating the link. If the email must use SSO, the start response redirects to the identity provider instead of sending a local link.

## SDK and backend API flow

Backends can use `SqlOSAuthService` directly:

```csharp
await authService.RequestMagicLinkAsync(
    new SqlOSMagicLinkStartRequest(
        Email: "jane@example.com",
        ClientId: "web",
        OrganizationId: null),
    httpContext,
    ct);
```

The start response is intentionally generic for known and unknown addresses:

```text
If an account exists for ja***@example.com, check your email for a sign-in link.
```

Complete the link with:

```csharp
var login = await authService.CompleteMagicLinkAsync(
    new SqlOSMagicLinkCompleteRequest(token),
    httpContext,
    ct);
```

## Email delivery and templates

SqlOS creates the built-in `auth.magic-link` transactional template automatically. It uses the same Email Branding settings as Email OTP and invitations. Rendered bodies are suppressed in delivery history because they contain token-bearing links.

For complete control, configure the message builder:

```csharp
options.AuthServer.ConfigureMagicLink(link =>
{
    link.ApplicationName = "Acme";
    link.BuildMessage = ctx => new SqlOSAuthEmailMessage(
        ctx.Email,
        $"Sign in to {ctx.ApplicationName}",
        $"<p><a href=\"{ctx.LoginUrl}\">Sign in</a></p>",
        $"Sign in: {ctx.LoginUrl}");
});
```

## Token and abuse controls

Magic-link tokens are stored only as hashes in `SqlOSTemporaryTokens`. The payload binds each token to the normalized email, user email id when one exists, client id, optional organization id, optional authorization request id, source IP, user agent, and sent status.

Configure lifetime, resend cooldown, and rate limits:

```csharp
options.AuthServer.ConfigureMagicLink(link =>
{
    link.TokenLifetime = TimeSpan.FromMinutes(10);
    link.ResendCooldown = TimeSpan.FromSeconds(30);
    link.RateLimitWindow = TimeSpan.FromHours(1);
    link.MaxLinksPerEmailPerWindow = 5;
    link.MaxLinksPerIpPerWindow = 60;
    link.MaxLinksPerClientPerWindow = 300;
});
```

Audit events include `magic_link.requested`, `magic_link.completed`, `magic_link.rejected`, `magic_link.rate_limit_rejected`, and `magic_link.send_failed`.
