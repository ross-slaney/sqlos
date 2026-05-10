# Email OTP

SqlOS Email OTP provides passwordless email-code login and signup across hosted AuthPage, headless UI, and backend SDK usage.

## Azure Communication Services

Provision ACS Email with:

```bash
AZURE_SUBSCRIPTION_ID=<subscription-id> \
AZURE_RESOURCE_GROUP=<resource-group> \
AZURE_DNS_ZONE_NAME=example.com \
AZURE_DNS_ZONE_RESOURCE_GROUP=<dns-zone-resource-group> \
ACS_EMAIL_DOMAIN=example.com \
ACS_EMAIL_SENDER_USERNAME=no-reply \
ACS_EMAIL_SENDER_DISPLAY_NAME="Example" \
./scripts/azure/setup-acs-email.sh --apply-dns --yes
```

Set runtime configuration:

```bash
SqlOS__EmailOtp__AzureCommunicationServicesConnectionString=<acs-connection-string>
SqlOS__EmailOtp__FromAddress=no-reply@example.com
```

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

    options.AuthServer.SeedAuthPage(page =>
    {
        page.EnabledCredentialTypes = ["email_otp"];
        page.EnablePasswordSignup = false;
    });
});
```

## Hosted AuthPage

Enable `email_otp` in AuthPage settings. Hosted login and signup use the same auth-page renderer and do not issue an auth code until the code is verified.

## Headless UI

Headless browser clients use:

```text
POST /sqlos/auth/headless/email-otp/start
POST /sqlos/auth/headless/email-otp/verify
POST /sqlos/auth/headless/signup/email-otp/start
POST /sqlos/auth/headless/signup/email-otp/verify
```

The signup flow preserves `customFields`, which lets app-owned UIs pass profile and onboarding context while SqlOS still owns the auth transaction.

## SDK Usage

Existing users:

```csharp
var start = await sqlosAuth.RequestEmailOtpAsync(
    new SqlOSEmailOtpStartRequest("jane@example.com", "web"),
    httpContext);

var login = await sqlosAuth.VerifyEmailOtpAsync(
    new SqlOSEmailOtpVerifyRequest(start.ChallengeToken, code),
    httpContext);
```

New users:

```csharp
var start = await sqlosAuth.RequestEmailOtpSignupAsync(
    new SqlOSEmailOtpSignupStartRequest(
        DisplayName: "Jane Doe",
        Email: "jane@example.com",
        ClientId: "web",
        OrganizationName: "Example Co",
        OrganizationId: null,
        CustomFields: null),
    httpContext);

var login = await sqlosAuth.VerifyEmailOtpSignupAsync(
    new SqlOSEmailOtpSignupVerifyRequest(start.SignupToken, start.ChallengeToken, code),
    httpContext);
```

## Invitations

Email OTP can be used to accept organization invitations. Invite-backed OTP signup creates the user, marks the invited email verified, creates/reactivates membership, consumes the invite, and issues the final session or OAuth redirect only after verification.

See [Email Invitations](INVITATIONS.md).
