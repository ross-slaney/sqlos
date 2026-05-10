# Email Invitations

SqlOS invitations are organization-membership invites. They are email-bound, one-time, expiring links that only activate membership after the invited email is proven by OTP, trusted SSO, existing login, or invite-backed signup.

## Configure Email Delivery

Invitations use the same `ISqlOSAuthEmailSender` delivery abstraction as Email OTP. The ACS sender is configured through Email OTP settings:

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.AuthServer.ConfigureEmailOtp(email =>
    {
        email.AzureCommunicationServicesConnectionString =
            builder.Configuration["SqlOS:EmailOtp:AzureCommunicationServicesConnectionString"];
        email.FromAddress = builder.Configuration["SqlOS:EmailOtp:FromAddress"];
        email.ApplicationName = "ChecklistSquad";
    });

    options.AuthServer.ConfigureInvitations(invites =>
    {
        invites.DefaultLifetime = TimeSpan.FromDays(7);
        invites.ApplicationName = "ChecklistSquad";
    });
});
```

## SDK Usage

Backend developers use injected SqlOS services; v1 does not add a generic public invite REST API for server-to-server usage.

```csharp
var invite = await sqlosAuth.CreateEmailInvitationAsync(
    new SqlOSCreateEmailInvitationRequest(
        OrganizationId: organizationId,
        Email: "jane@example.com",
        Role: "member",
        ClientId: "web",
        RedirectUri: "https://app.example.com/auth/callback"),
    httpContext);
```

Resend, revoke, and accept are also available:

```csharp
await sqlosAuth.ResendEmailInvitationAsync(new SqlOSResendEmailInvitationRequest(invite.Id), httpContext);
await sqlosAuth.RevokeEmailInvitationAsync(new SqlOSRevokeEmailInvitationRequest(invite.Id, "mistyped_email"), httpContext);
await sqlosAuth.AcceptEmailInvitationAsync(new SqlOSAcceptEmailInvitationRequest(token, userId), httpContext);
```

## Hosted AuthPage

The hosted accept entrypoint is:

```text
GET /sqlos/auth/invitations/accept?token=...
```

AuthPage fixes the email field to the invited address and offers the credential methods enabled for that deployment:

- Email OTP login for existing users when `email_otp` is enabled.
- Email OTP signup when `email_otp` signup is enabled.
- Password login/signup when password auth is enabled.
- SSO/HRD when the invited email routes to a configured trusted connection.

If no compatible method is enabled, AuthPage renders a configuration error instead of consuming the invite.

## Headless UI

Headless clients can validate and bootstrap invite UI with:

```text
POST /sqlos/auth/headless/invitations/resolve
```

The request body is:

```json
{ "invitationToken": "..." }
```

Existing headless start/login/signup/OTP/provider requests accept `invitationToken`. Once an authorization request is bound to an invite, later steps can rely on the request id; SqlOS keeps the invite context server-side.

Recommended headless lifecycle:

1. Resolve the token to render the invite landing screen.
2. When the user chooses **sign in** or **create account**, start the normal OAuth authorization request with the selected `view` and the same `invitationToken`.
3. Keep the invited email read-only in your UI. SqlOS will reject a mismatched effective identity.
4. Pass `invitationToken` through follow-up headless actions until the request is bound. Passing it on every action is safe.
5. For OTP invite signup, persist the `challengeToken` and `signupToken` returned by `/signup/email-otp/start` until `/signup/email-otp/verify`; those raw tokens are not recoverable from `GET /headless/requests/{requestId}`.

Avoid using plain links between invite/login/signup screens after an authorization request exists. Switch views in client state or include the same request id so the bound invitation and OAuth request are preserved.

## Dashboard

Open an organization in the Auth dashboard and use the **Invitations** tab to:

- create and send an invitation
- resend an invitation with a rotated token
- revoke a pending invitation
- copy a pending link when email delivery is disabled or failed
- inspect status, expiry, and delivery errors

## Security Rules

- Invite tokens are stored hashed.
- New invites revoke older pending invites for the same organization and normalized email.
- Accepted, revoked, and expired invites cannot be used.
- The accepting identity must have the same normalized email as the invitation.
- Existing active memberships are accepted idempotently and keep their current role.
- Inactive memberships are reactivated with the invited role.
- Rate limits apply by email, IP, organization, and inviter.
- Acceptance marks the invited email verified when the account has that address but it is still unverified.

## Email Customization

Use `ConfigureInvitations` to set `ApplicationName` or replace the default message:

```csharp
options.AuthServer.ConfigureInvitations(invites =>
{
    invites.BuildMessage = context => new SqlOSAuthEmailMessage(
        context.Email,
        $"You're invited to {context.OrganizationName}",
        $"<p>Accept: <a href=\"{context.AcceptUrl}\">{context.AcceptUrl}</a></p>",
        $"Accept: {context.AcceptUrl}");
});
```

## Tests

The unit suite covers create/send, hashed tokens, acceptance, reuse rejection, email mismatch rejection, idempotent active membership acceptance, rate limiting, and custom invite messages.
