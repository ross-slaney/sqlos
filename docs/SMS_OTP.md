# SMS OTP

SqlOS SMS OTP provides passwordless phone-code login and signup through Twilio Verify. It is disabled by default and only becomes visible when all runtime Twilio settings are present and `phone_otp` is enabled in AuthPage credentials.

SMS is a lower-assurance credential than passkeys, WebAuthn, TOTP, or hardware-backed MFA. It is vulnerable to SIM swap, number reassignment, carrier delivery failure, and SMS interception. SqlOS does not count `phone_otp` as strong MFA by default, and admin/owner elevation should require a stronger method.

## Twilio Verify

### Todo sample setup

This is the exact setup path for testing SMS OTP in the Todo sample.

1. Sign in to the [Twilio Console](https://console.twilio.com/) and select the account/project that should own the SqlOS Verify traffic.
2. Upgrade the account before testing arbitrary user phone numbers. Twilio trial accounts can only send SMS/Voice/WhatsApp OTP messages to destination numbers that are already verified on the account, and trial accounts expire after 30 days. That is fine for one developer phone, but it is not enough for open signup testing.
3. On the Console dashboard, open **Account Info**.
4. Copy **Account SID**. It starts with `AC`.
5. Click **Show** for **Auth Token**, then copy it. Treat this as a secret.
6. Create one Verify Service:
   - Open **Verify** in the left navigation.
   - Go to **Services**.
   - Click **Create new Service**.
   - Friendly name: `SqlOS Todo Dev`.
   - Ensure SMS is enabled.
   - Use a 6-digit code.
   - Save the **Service SID**. It starts with `VA`.
7. Run the Todo sample from the repo root:

   ```bash
   TWILIO_ACCOUNT_SID=<account-sid> \
   TWILIO_AUTH_TOKEN=<auth-token> \
   TWILIO_VERIFY_SERVICE_SID=<verify-service-sid> \
   TWILIO_DEFAULT_REGION=US \
   TodoSample__EnablePhoneOtp=true \
   dotnet run --project examples/SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj
   ```

8. Open `http://localhost:5080/`. The home page should show **SMS code sign in** and **SMS code sign up**. `/sample/config` should report `"phoneOtpEnabled": true`.

Start SMS testing from the Todo sample home page, not directly from `/sqlos/auth/login`. A direct AuthPage sign-in creates a SqlOS auth-page session and shows `/sqlos/auth/login?status=signed-in`, but it does not give the Todo SPA an OAuth access token. The Todo app gets its token only when the flow starts at `http://localhost:5080/` and returns through `/callback.html`.

Do not buy or configure a Programmable Messaging phone number for this SqlOS integration. SqlOS calls Twilio Verify v2 with the Verify Service SID and `sms` channel; Verify manages the SMS sender path for the verification message.

Twilio documents Verify Services as resources that can be created in the Console or API, and service SIDs use the `VA...` format. The Verify API starts SMS OTP delivery by creating a Verification under that Service SID with `channel=sms`; phone numbers must be E.164 at the Twilio boundary. SqlOS normalizes phone input to E.164 before it calls Twilio.

### Setup script

The helper script creates the same Verify Service and prints the same app environment block:

```bash
TWILIO_ACCOUNT_SID=<account-sid> \
TWILIO_AUTH_TOKEN=<auth-token> \
TWILIO_VERIFY_SERVICE_NAME="SqlOS Todo Dev" \
./scripts/twilio/setup-twilio-verify.sh
```

If `TWILIO_VERIFY_SERVICE_SID` is already set, the script reuses that service instead of creating a new one.

## SqlOS Configuration

For a normal app, wire the same three Twilio values into `ConfigurePhoneOtp`:

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.AuthServer.ConfigurePhoneOtp(phone =>
    {
        phone.Enabled = builder.Configuration.GetValue<bool>("SqlOS:PhoneOtp:Enabled");
        phone.TwilioAccountSid = builder.Configuration["SqlOS:PhoneOtp:TwilioAccountSid"];
        phone.TwilioAuthToken = builder.Configuration["SqlOS:PhoneOtp:TwilioAuthToken"];
        phone.TwilioVerifyServiceSid = builder.Configuration["SqlOS:PhoneOtp:TwilioVerifyServiceSid"];
        phone.DefaultRegion = builder.Configuration["SqlOS:PhoneOtp:DefaultRegion"] ?? "US";
        phone.CountryAllowList = ["US", "CA"];
        phone.MaxSendsPerPhone = 5;
        phone.MaxSendsPerIp = 60;
    });

    options.AuthServer.SeedAuthPage(page =>
    {
        page.EnabledCredentialTypes = ["password", "email_otp", "phone_otp"];
    });
});
```

Runtime environment:

```bash
SqlOS__PhoneOtp__Enabled=true
SqlOS__PhoneOtp__TwilioAccountSid=<account-sid>
SqlOS__PhoneOtp__TwilioAuthToken=<auth-token>
SqlOS__PhoneOtp__TwilioVerifyServiceSid=<verify-service-sid>
SqlOS__PhoneOtp__DefaultRegion=US
```

Startup validation fails if `Enabled` is true without `TwilioAccountSid`, `TwilioAuthToken`, and `TwilioVerifyServiceSid`.

## Runtime Behavior

- Phone numbers are parsed and normalized to E.164 before storage or Twilio calls.
- Stored phone lookup uses a hash; display and challenge phone values are encrypted.
- Unknown-account login starts return the same public message as known-account starts.
- Signup rejects an already registered phone before sending another paid Verify challenge.
- Local rate limits run before provider sends by phone, account, IP, and client.
- Phone enrollment or phone-number change requires an authenticated session.
- Audit events are written for challenge start, provider send failure, verification success/failure, throttling, and phone enrollment.

## Hosted AuthPage

Enable `phone_otp` in AuthPage settings. Hosted pages expose:

```text
GET  /sqlos/auth/login/phone-otp
POST /sqlos/auth/login/phone-otp/start
POST /sqlos/auth/login/phone-otp/verify
GET  /sqlos/auth/signup/phone-otp
POST /sqlos/auth/signup/phone-otp/start
POST /sqlos/auth/signup/phone-otp/verify
```

## Headless UI

Headless browser clients use:

```text
POST /sqlos/auth/headless/phone-otp/start
POST /sqlos/auth/headless/phone-otp/verify
POST /sqlos/auth/headless/signup/phone-otp/start
POST /sqlos/auth/headless/signup/phone-otp/verify
```

The `start` responses include raw one-time tokens that SqlOS intentionally cannot return from a later request reload. Keep `challengeToken`, and for signup also `signupToken`, in component state or browser `sessionStorage` until verification.

## SDK Usage

Existing users:

```csharp
var start = await sqlosAuth.RequestPhoneOtpAsync(
    new SqlOSPhoneOtpStartRequest("+12025550105", "web", OrganizationId: null),
    httpContext);

var login = await sqlosAuth.VerifyPhoneOtpAsync(
    new SqlOSPhoneOtpVerifyRequest(start.ChallengeToken, code),
    httpContext);
```

New users:

```csharp
var start = await sqlosAuth.RequestPhoneOtpSignupAsync(
    new SqlOSPhoneOtpSignupStartRequest(
        DisplayName: "Jane Doe",
        PhoneNumber: "+12025550105",
        ClientId: "web",
        OrganizationName: "Example Co",
        OrganizationId: null,
        CustomFields: null),
    httpContext);

var login = await sqlosAuth.VerifyPhoneOtpSignupAsync(
    new SqlOSPhoneOtpSignupVerifyRequest(start.SignupToken, start.ChallengeToken, code),
    httpContext);
```

## Example Apps

Todo sample:

```bash
TodoSample__EnablePhoneOtp=true
TWILIO_ACCOUNT_SID=<account-sid>
TWILIO_AUTH_TOKEN=<auth-token>
TWILIO_VERIFY_SERVICE_SID=<verify-service-sid>
```

Retail example:

```bash
SqlOS__PhoneOtp__Enabled=true
TWILIO_ACCOUNT_SID=<account-sid>
TWILIO_AUTH_TOKEN=<auth-token>
TWILIO_VERIFY_SERVICE_SID=<verify-service-sid>
```

Both examples also accept the equivalent SqlOS configuration keys: `SqlOS__PhoneOtp__TwilioAccountSid`, `SqlOS__PhoneOtp__TwilioAuthToken`, and `SqlOS__PhoneOtp__TwilioVerifyServiceSid`.
