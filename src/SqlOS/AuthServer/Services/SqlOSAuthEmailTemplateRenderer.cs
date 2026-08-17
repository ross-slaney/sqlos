using System.Net;
using SqlOS.AuthServer.Configuration;

namespace SqlOS.AuthServer.Services;

internal static class SqlOSAuthEmailTemplateRenderer
{
    public static string BuildOtpHtmlBody(SqlOSEmailOtpMessageContext context)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(context.ChallengeLifetime.TotalMinutes));
        var action = context.Purpose == "signup" ? "creating your account" : "signing in";
        var heading = context.Purpose == "signup" ? "Your sign-up code" : "Your sign-in code";
        var intro = $"Use this one-time code to finish {action} as {context.MaskedEmail}. It expires in {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
        return BuildShell(
            context.Branding,
            WebUtility.HtmlEncode(heading),
            WebUtility.HtmlEncode(intro),
            $"""
            <div style="margin:0 0 20px;padding:18px 20px;border-radius:16px;background:{Tint(context.Branding.PrimaryColor)};border:1px solid {BorderTint(context.Branding.PrimaryColor)};font-size:34px;letter-spacing:0.24em;font-weight:700;text-align:center;color:{Css(context.Branding.PrimaryColor, "#2563eb")};">{WebUtility.HtmlEncode(context.Code)}</div>
            <p style="margin:0;font-size:13px;line-height:1.6;color:#64748b;">If you didn't request this code, you can ignore this email.</p>
            """);
    }

    public static string BuildOtpTextBody(SqlOSEmailOtpMessageContext context)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(context.ChallengeLifetime.TotalMinutes));
        var action = context.Purpose == "signup" ? "sign-up" : "sign-in";
        return $"Your {context.Branding.ApplicationName} {action} code is {context.Code}. It expires in {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
    }

    public static string BuildMagicLinkHtmlBody(SqlOSMagicLinkMessageContext context)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(context.TokenLifetime.TotalMinutes));
        var loginUrl = WebUtility.HtmlEncode(context.LoginUrl);
        var intro = $"Use this one-time link to finish signing in as {context.MaskedEmail}. It expires in {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
        return BuildShell(
            context.Branding,
            $"Sign in to {WebUtility.HtmlEncode(context.ApplicationName)}",
            WebUtility.HtmlEncode(intro),
            $"""
            <p style="margin:0 0 20px;"><a href="{loginUrl}" style="display:inline-block;background:{Css(context.Branding.PrimaryColor, "#2563eb")};color:{ButtonText(context.Branding.PrimaryColor)};text-decoration:none;border-radius:10px;padding:12px 18px;font-weight:600;">Continue sign in</a></p>
            <p style="margin:0 0 12px;font-size:13px;line-height:1.6;color:#64748b;">If the button does not work, open this link: {loginUrl}</p>
            <p style="margin:0;font-size:13px;line-height:1.6;color:#64748b;">If you did not request this link, you can ignore this email.</p>
            """);
    }

    public static string BuildMagicLinkTextBody(SqlOSMagicLinkMessageContext context)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(context.TokenLifetime.TotalMinutes));
        return $"Sign in to {context.ApplicationName} as {context.MaskedEmail}: {context.LoginUrl}. This link expires in {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
    }

    public static string BuildInvitationHtmlBody(SqlOSInvitationMessageContext context)
    {
        var days = Math.Max(1, (int)Math.Ceiling(context.Lifetime.TotalDays));
        var intro = $"Accept this invitation for {context.MaskedEmail} to join as {context.Role}. This link expires in {days} day{(days == 1 ? string.Empty : "s")}.";
        var acceptUrl = WebUtility.HtmlEncode(context.AcceptUrl);
        return BuildShell(
            context.Branding,
            $"You're invited to {WebUtility.HtmlEncode(context.OrganizationName)}",
            WebUtility.HtmlEncode(intro),
            $"""
            <p style="margin:0 0 20px;"><a href="{acceptUrl}" style="display:inline-block;background:{Css(context.Branding.PrimaryColor, "#2563eb")};color:{ButtonText(context.Branding.PrimaryColor)};text-decoration:none;border-radius:10px;padding:12px 18px;font-weight:600;">Accept invitation</a></p>
            <p style="margin:0;font-size:13px;line-height:1.6;color:#64748b;">If the button does not work, open this link: {acceptUrl}</p>
            """);
    }

    public static string BuildInvitationTextBody(SqlOSInvitationMessageContext context)
    {
        var days = Math.Max(1, (int)Math.Ceiling(context.Lifetime.TotalDays));
        return $"You're invited to {context.OrganizationName} as {context.Role}. Accept the invitation for {context.MaskedEmail}: {context.AcceptUrl}. This link expires in {days} day{(days == 1 ? string.Empty : "s")}.";
    }

    public static string BuildPasswordResetHtmlBody(SqlOSPasswordResetMessageContext context)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(context.TokenLifetime.TotalMinutes));
        var resetUrl = WebUtility.HtmlEncode(context.ResetUrl);
        var intro = $"Use this link to reset the password for {context.MaskedEmail}. It expires in {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
        return BuildShell(
            context.Branding,
            "Reset your password",
            WebUtility.HtmlEncode(intro),
            $"""
            <p style="margin:0 0 20px;"><a href="{resetUrl}" style="display:inline-block;background:{Css(context.Branding.PrimaryColor, "#2563eb")};color:{ButtonText(context.Branding.PrimaryColor)};text-decoration:none;border-radius:10px;padding:12px 18px;font-weight:600;">Reset password</a></p>
            <p style="margin:0 0 12px;font-size:13px;line-height:1.6;color:#64748b;">If the button does not work, open this link: {resetUrl}</p>
            <p style="margin:0;font-size:13px;line-height:1.6;color:#64748b;">If you did not request a password reset, you can ignore this email.</p>
            """);
    }

    public static string BuildPasswordResetTextBody(SqlOSPasswordResetMessageContext context)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(context.TokenLifetime.TotalMinutes));
        return $"Reset your {context.ApplicationName} password for {context.MaskedEmail}: {context.ResetUrl}. This link expires in {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
    }

    private static string BuildShell(SqlOSAuthEmailBranding branding, string heading, string intro, string body)
    {
        var background = Css(branding.BackgroundColor, "#f8fafc");
        var accent = Css(branding.AccentColor, "#0f172a");
        var logo = BuildLogo(branding);
        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <body style="margin:0;padding:24px;background:{{background}};font-family:Segoe UI,Arial,sans-serif;color:{{accent}};">
          <div style="max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #e2e8f0;border-radius:20px;padding:32px;">
            {{logo}}
            <h1 style="margin:0 0 12px;font-size:28px;line-height:1.1;color:{{accent}};">{{heading}}</h1>
            <p style="margin:0 0 20px;font-size:15px;line-height:1.6;color:#475569;">{{intro}}</p>
            {{body}}
          </div>
        </body>
        </html>
        """;
    }

    private static string BuildLogo(SqlOSAuthEmailBranding branding)
    {
        if (!string.IsNullOrWhiteSpace(branding.LogoBase64))
        {
            return $"""<p style="margin:0 0 16px;"><img src="{WebUtility.HtmlEncode(branding.LogoBase64)}" alt="{WebUtility.HtmlEncode(branding.ApplicationName)}" style="max-height:42px;max-width:180px;display:block;" /></p>""";
        }

        return $"""<p style="margin:0 0 12px;font-size:14px;color:#475569;font-weight:600;">{WebUtility.HtmlEncode(branding.ApplicationName)}</p>""";
    }

    private static string Css(string? value, string fallback)
        => SqlOSCssColor.Render(value, fallback);

    private static string Tint(string value)
        => string.Equals(Css(value, "#2563eb"), "#2563eb", StringComparison.OrdinalIgnoreCase) ? "#eff6ff" : "#f8fafc";

    private static string BorderTint(string value)
        => string.Equals(Css(value, "#2563eb"), "#2563eb", StringComparison.OrdinalIgnoreCase) ? "#bfdbfe" : "#e2e8f0";

    private static string ButtonText(string value)
        => IsDarkColor(value) ? "#ffffff" : "#0f172a";

    private static bool IsDarkColor(string? value)
    {
        if (!SqlOSCssColor.TryGetRgb(value, out var red, out var green, out var blue))
        {
            return true;
        }

        return ((red * 299) + (green * 587) + (blue * 114)) / 1000 < 140;
    }
}
