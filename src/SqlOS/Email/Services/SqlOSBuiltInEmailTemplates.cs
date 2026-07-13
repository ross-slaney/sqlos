namespace SqlOS.Email.Services;

public static class SqlOSBuiltInEmailTemplates
{
    public const string AuthEmailOtpKey = "auth.email-otp";
    public const string AuthMagicLinkKey = "auth.magic-link";
    public const string AuthInvitationKey = "auth.invitation";
    public const string AuthPasswordResetKey = "auth.password-reset";
    public const string AuthEmailVerificationKey = "auth.email-verification";

    public static IReadOnlyList<SqlOSBuiltInEmailTemplateDefinition> All { get; } =
    [
        new(
            AuthEmailOtpKey,
            "Auth email OTP",
            "Your {applicationName} {purposeLabel} code",
            """
            <!DOCTYPE html>
            <html lang="en">
            <body style="margin:0;padding:24px;background:{backgroundColor};font-family:Segoe UI,Arial,sans-serif;color:{accentColor};">
              <div style="max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #e2e8f0;border-radius:20px;padding:32px;">
                <img src="{logoBase64}" alt="{applicationName}" style="max-height:42px;max-width:180px;display:{logoImageDisplay};margin:0 0 16px;" />
                <p style="display:{logoTextDisplay};margin:0 0 12px;font-size:14px;color:#475569;font-weight:600;">{applicationName}</p>
                <h1 style="margin:0 0 12px;font-size:28px;line-height:1.1;color:{accentColor};">{heading}</h1>
                <p style="margin:0 0 20px;font-size:15px;line-height:1.6;color:#475569;">Use this one-time code to finish {action} as {maskedEmail}. It expires in {expiresInMinutes} minute(s).</p>
                <div style="margin:0 0 20px;padding:18px 20px;border-radius:16px;background:#eff6ff;border:1px solid #bfdbfe;font-size:34px;letter-spacing:0.24em;font-weight:700;text-align:center;color:{primaryColor};">{code}</div>
                <p style="margin:0;font-size:13px;line-height:1.6;color:#64748b;">If you did not request this code, you can ignore this email.</p>
              </div>
            </body>
            </html>
            """,
            "Your {applicationName} {purposeLabel} code is {code}. It expires in {expiresInMinutes} minute(s).",
            """{"applicationName":"SqlOS","logoBase64":"","logoImageDisplay":"none","logoTextDisplay":"block","purposeLabel":"sign-in","heading":"Your sign-in code","action":"signing in","maskedEmail":"us***@example.com","code":"123456","expiresInMinutes":"10","primaryColor":"#2563eb","accentColor":"#0f172a","backgroundColor":"#f8fafc"}""",
            SuppressRenderedContentStorage: true),
        new(
            AuthMagicLinkKey,
            "Auth magic link",
            "Sign in to {applicationName}",
            """
            <!DOCTYPE html>
            <html lang="en">
            <body style="margin:0;padding:24px;background:{backgroundColor};font-family:Segoe UI,Arial,sans-serif;color:{accentColor};">
              <div style="max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #e2e8f0;border-radius:20px;padding:32px;">
                <img src="{logoBase64}" alt="{applicationName}" style="max-height:42px;max-width:180px;display:{logoImageDisplay};margin:0 0 16px;" />
                <p style="display:{logoTextDisplay};margin:0 0 12px;font-size:14px;color:#475569;font-weight:600;">{applicationName}</p>
                <h1 style="margin:0 0 12px;font-size:28px;line-height:1.1;color:{accentColor};">Sign in to {applicationName}</h1>
                <p style="margin:0 0 20px;font-size:15px;line-height:1.6;color:#475569;">Use this one-time link to finish signing in as {maskedEmail}. It expires in {expiresInMinutes} minute(s).</p>
                <p style="margin:0 0 20px;"><a href="{loginUrl}" style="display:inline-block;background:{primaryColor};color:#ffffff;text-decoration:none;border-radius:10px;padding:12px 18px;font-weight:600;">Continue sign in</a></p>
                <p style="margin:0 0 12px;font-size:13px;line-height:1.6;color:#64748b;">If the button does not work, open this link: {loginUrl}</p>
                <p style="margin:0;font-size:13px;line-height:1.6;color:#64748b;">If you did not request this link, you can ignore this email.</p>
              </div>
            </body>
            </html>
            """,
            "Sign in to {applicationName} as {maskedEmail}: {loginUrl}. This link expires in {expiresInMinutes} minute(s).",
            """{"applicationName":"SqlOS","logoBase64":"","logoImageDisplay":"none","logoTextDisplay":"block","maskedEmail":"us***@example.com","loginUrl":"https://app.example.test/sqlos/auth/login/magic-link/complete?token=sample","expiresInMinutes":"10","primaryColor":"#2563eb","accentColor":"#0f172a","backgroundColor":"#f8fafc"}""",
            SuppressRenderedContentStorage: true),
        new(
            AuthInvitationKey,
            "Organization invitation",
            "You're invited to {organizationName}",
            """
            <!DOCTYPE html>
            <html lang="en">
            <body style="margin:0;padding:24px;background:{backgroundColor};font-family:Segoe UI,Arial,sans-serif;color:{accentColor};">
              <div style="max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #e2e8f0;border-radius:20px;padding:32px;">
                <img src="{logoBase64}" alt="{applicationName}" style="max-height:42px;max-width:180px;display:{logoImageDisplay};margin:0 0 16px;" />
                <p style="display:{logoTextDisplay};margin:0 0 12px;font-size:14px;color:#475569;font-weight:600;">{applicationName}</p>
                <h1 style="margin:0 0 12px;font-size:28px;line-height:1.1;color:{accentColor};">You're invited to {organizationName}</h1>
                <p style="margin:0 0 20px;font-size:15px;line-height:1.6;color:#475569;">Accept this invitation for {maskedEmail} to join as {role}. This link expires in {expiresInDays} day(s).</p>
                <p style="margin:0 0 20px;"><a href="{acceptUrl}" style="display:inline-block;background:{primaryColor};color:#ffffff;text-decoration:none;border-radius:10px;padding:12px 18px;font-weight:600;">Accept invitation</a></p>
                <p style="margin:0;font-size:13px;line-height:1.6;color:#64748b;">If the button does not work, open this link: {acceptUrl}</p>
              </div>
            </body>
            </html>
            """,
            "You're invited to {organizationName} as {role}. Accept the invitation for {maskedEmail}: {acceptUrl}. This link expires in {expiresInDays} day(s).",
            """{"applicationName":"SqlOS","logoBase64":"","logoImageDisplay":"none","logoTextDisplay":"block","organizationName":"Example Org","maskedEmail":"us***@example.com","role":"member","acceptUrl":"https://app.example.test/sqlos/auth/invitations/accept?token=sample","expiresInDays":"7","primaryColor":"#2563eb","accentColor":"#0f172a","backgroundColor":"#f8fafc"}""",
            SuppressRenderedContentStorage: true),
        new(
            AuthPasswordResetKey,
            "Password reset",
            "Reset your {applicationName} password",
            """
            <!DOCTYPE html>
            <html lang="en">
            <body style="margin:0;padding:24px;background:{backgroundColor};font-family:Segoe UI,Arial,sans-serif;color:{accentColor};">
              <div style="max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #e2e8f0;border-radius:20px;padding:32px;">
                <img src="{logoBase64}" alt="{applicationName}" style="max-height:42px;max-width:180px;display:{logoImageDisplay};margin:0 0 16px;" />
                <p style="display:{logoTextDisplay};margin:0 0 12px;font-size:14px;color:#475569;font-weight:600;">{applicationName}</p>
                <h1 style="margin:0 0 12px;font-size:28px;line-height:1.1;color:{accentColor};">Reset your password</h1>
                <p style="margin:0 0 20px;font-size:15px;line-height:1.6;color:#475569;">Use this link to reset the password for {maskedEmail}. It expires in {expiresInMinutes} minute(s).</p>
                <p style="margin:0 0 20px;"><a href="{resetUrl}" style="display:inline-block;background:{primaryColor};color:#ffffff;text-decoration:none;border-radius:10px;padding:12px 18px;font-weight:600;">Reset password</a></p>
                <p style="margin:0 0 12px;font-size:13px;line-height:1.6;color:#64748b;">If the button does not work, open this link: {resetUrl}</p>
                <p style="margin:0;font-size:13px;line-height:1.6;color:#64748b;">If you did not request a password reset, you can ignore this email.</p>
              </div>
            </body>
            </html>
            """,
            "Reset your {applicationName} password for {maskedEmail}: {resetUrl}. This link expires in {expiresInMinutes} minute(s).",
            """{"applicationName":"SqlOS","logoBase64":"","logoImageDisplay":"none","logoTextDisplay":"block","maskedEmail":"us***@example.com","resetUrl":"https://app.example.test/sqlos/auth/password/reset?token=sample","expiresInMinutes":"60","primaryColor":"#2563eb","accentColor":"#0f172a","backgroundColor":"#f8fafc"}""",
            SuppressRenderedContentStorage: true),
        new(
            AuthEmailVerificationKey,
            "Email verification",
            "Verify your {applicationName} email",
            """
            <!DOCTYPE html>
            <html lang="en">
            <body style="margin:0;padding:24px;background:{backgroundColor};font-family:Segoe UI,Arial,sans-serif;color:{accentColor};">
              <div style="max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #e2e8f0;border-radius:20px;padding:32px;">
                <img src="{logoBase64}" alt="{applicationName}" style="max-height:42px;max-width:180px;display:{logoImageDisplay};margin:0 0 16px;" />
                <p style="display:{logoTextDisplay};margin:0 0 12px;font-size:14px;color:#475569;font-weight:600;">{applicationName}</p>
                <h1 style="margin:0 0 12px;font-size:28px;line-height:1.1;color:{accentColor};">Verify your email</h1>
                <p style="margin:0 0 20px;font-size:15px;line-height:1.6;color:#475569;">Use this link to verify {maskedEmail}. It expires in {expiresInHours} hour(s).</p>
                <p style="margin:0 0 20px;"><a href="{verificationUrl}" style="display:inline-block;background:{primaryColor};color:#ffffff;text-decoration:none;border-radius:10px;padding:12px 18px;font-weight:600;">Verify email</a></p>
                <p style="margin:0 0 12px;font-size:13px;line-height:1.6;color:#64748b;">If the button does not work, open this link: {verificationUrl}</p>
                <p style="margin:0;font-size:13px;line-height:1.6;color:#64748b;">If you did not request this verification, you can ignore this email.</p>
              </div>
            </body>
            </html>
            """,
            "Verify your {applicationName} email for {maskedEmail}: {verificationUrl}. This link expires in {expiresInHours} hour(s).",
            """{"applicationName":"SqlOS","logoBase64":"","logoImageDisplay":"none","logoTextDisplay":"block","maskedEmail":"us***@example.com","verificationUrl":"https://app.example.test/sqlos/auth/email/verify?token=sample","expiresInHours":"24","primaryColor":"#2563eb","accentColor":"#0f172a","backgroundColor":"#f8fafc"}""",
            SuppressRenderedContentStorage: true)
    ];

    public static bool SuppressesRenderedContentStorage(string templateKey)
        => All.Any(template => string.Equals(template.Key, templateKey, StringComparison.Ordinal)
            && template.SuppressRenderedContentStorage);
}

public sealed record SqlOSBuiltInEmailTemplateDefinition(
    string Key,
    string DisplayName,
    string SubjectTemplate,
    string HtmlBodyTemplate,
    string TextBodyTemplate,
    string VariablesJson,
    bool SuppressRenderedContentStorage);
