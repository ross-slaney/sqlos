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
    });
});
```

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

Every authorization issuance path evaluates the current global, organization,
role, and user MFA policy before it creates an authorization code, AuthPage
session, access token, refresh token, or device approval. Hosted password login,
OIDC and SAML callbacks, all hosted signup variants, silent SSO, headless login,
and device approval share that same pre-issuance gate.

A required policy renders either:

- a second-factor code form, when the user already has TOTP or recovery codes;
- a forced authenticator-app enrollment form, when the user has no strong
  factor yet.

Primary authentication alone cannot issue authority. Email OTP, password,
invitation, and ordinary upstream OIDC or SAML do not count as MFA. Phone OTP
satisfies MFA only when `PhoneOtp.SatisfiesMfa` is enabled. Explicitly trusted
upstream MFA remains accepted. If policy becomes stricter between the first
factor and issuance, SqlOS re-evaluates the current policy and refuses to issue.

Successful MFA appends the second factor to `amr`, for example:

```text
amr: password
amr: totp
```

## Secret Custody

TOTP secrets are stored through `SqlOSCryptoService.ProtectSecret`, which uses
ASP.NET Data Protection when available. Production deployments should configure
a Data Protection key ring outside the application database.
