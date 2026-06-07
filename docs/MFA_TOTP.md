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
