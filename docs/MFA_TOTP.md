# MFA and TOTP

SqlOS supports authenticator-app MFA as the first strong step-up factor. The
runtime is policy-driven:

- MFA is optional by default.
- Admins can disable MFA entirely.
- Users can self-enroll by default when MFA is enabled.
- Admins can require MFA globally or per organization.
- Organization policy can require MFA for all users, or for owner/admin roles.
- Recovery codes are generated after TOTP enrollment and are single-use.

## Configure Defaults

Use startup options for code defaults:

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.AuthServer.ConfigureMfa(mfa =>
    {
        mfa.Enabled = true;
        mfa.AllowUserSelfEnrollmentByDefault = true;
        mfa.RecoveryCodesEnabledByDefault = true;
        mfa.Totp.Issuer = "Contoso";
        mfa.Totp.MaxFailedAttemptsPerChallenge = 5;
        mfa.Totp.MaxFailedAttemptsPerUser = 10;
        mfa.Totp.MaxFailedAttemptsPerIp = 25;
        mfa.Totp.MaxFailedAttemptsPerClient = 25;
        mfa.Totp.MaxFailedAttemptsPerDevice = 10;
        mfa.Totp.MaxFailedAttemptsPerAuthorizationRequest = 10;
        mfa.Totp.FailedAttemptWindow = TimeSpan.FromMinutes(10);
    });
});
```

MFA verification reserves capacity before comparing either a TOTP or recovery
code, then releases that reservation after a successful comparison so only
failed checks consume the configured failure budgets. Challenge, user, IP,
client, account-and-client-bound browser fingerprint, and hosted
authorization-request budgets are persisted in SQL and shared by all application
replicas, so issuing another challenge cannot create more guesses. Once any
applicable budget is exhausted, SqlOS rejects the attempt with the same public
invalid-code error and does not compare the submitted code or reveal the limiting
scope. Audit events record the outcome but are not used as the security counter.

Startup defaults create the persisted MFA settings row on first boot. To
reapply settings on each boot, use `SeedMfaPolicy`, matching the existing auth
page and email settings pattern.

```csharp
options.AuthServer.SeedMfaPolicy(mfa =>
{
    mfa.Enabled = true;
    mfa.UserSelfEnrollmentEnabled = true;
    mfa.RequireForAllUsers = false;
});
```

After the row exists, dashboard/API changes own the live values unless a startup
seed reapplies them.

## Admin API

Dashboard/API settings endpoints:

- `GET /sqlos/admin/auth/api/settings/mfa`
- `PUT /sqlos/admin/auth/api/settings/mfa`
- `GET /sqlos/admin/auth/api/organizations/{organizationId}/mfa-policy`
- `PUT /sqlos/admin/auth/api/organizations/{organizationId}/mfa-policy`

## Direct Auth API

If a primary login requires MFA, the login result has:

- `requiresMfa: true`
- `mfaToken`
- `requiresMfaEnrollment`
- `mfaMethods`
- `tokens: null`

Verify an existing factor:

```http
POST /sqlos/auth/mfa/challenge/verify
{
  "mfaToken": "...",
  "code": "123456"
}
```

For forced enrollment:

Only call the enrollment endpoints when the same login result has
`requiresMfaEnrollment: true`. Completing a password or other first factor is
not permission to add or replace an authenticator for a normal MFA challenge.

```http
POST /sqlos/auth/mfa/challenge/totp/enroll/start
{
  "mfaToken": "...",
  "displayName": "Authenticator app"
}
```

Then verify:

```http
POST /sqlos/auth/mfa/challenge/totp/enroll/verify
{
  "mfaToken": "...",
  "enrollmentToken": "...",
  "code": "123456"
}
```

The `mfaToken` and `enrollmentToken` are one bound proof. SqlOS verifies that
they have the same user, organization, client, flow, and authorization request
before confirming the authenticator, rotating recovery codes, or issuing any
session, token, or authorization code. Tokens from different login attempts
are not interchangeable, and a challenge-bound enrollment token cannot be
verified through the account self-enrollment API.

## Enrollment modes

- **Account self-enrollment** starts from an already authenticated account
  settings experience with `StartTotpEnrollmentAsync`. The host application is
  responsible for protecting that account route with its authenticated session
  and any recent-authentication policy it requires.
- **Required enrollment** is available only when the MFA policy decision stored
  on that exact challenge says the user has no permitted strong factor and must
  enroll before continuing.
- **Normal MFA verification** for a user with an existing authenticator accepts
  only the existing TOTP or recovery code. First-factor completion alone never
  permits adding or replacing a factor.

## Hosted OAuth

Hosted authorization-code login evaluates MFA after the user and organization
are resolved and before the authorization code is issued. A required policy
renders either:

- a second-factor code form, when the user already has TOTP or recovery codes;
- a forced authenticator-app enrollment form, when the user has no strong
  factor yet.

Successful MFA appends the second factor to `amr`, for example:

```text
amr: password
amr: totp
```

## Secret Custody

TOTP secrets are stored through `SqlOSCryptoService.ProtectSecret`, which uses
ASP.NET Data Protection when available. Production deployments should configure
a Data Protection key ring outside the application database.
