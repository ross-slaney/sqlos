using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Configuration;
using SqlOS.Dashboard;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSDashboardSessionServiceTests
{
    private const string CookiePath = "/sqlos";

    [TestMethod]
    public void HasActiveSession_AcceptsCookieIssuedUnderCurrentPassword()
    {
        using var provider = BuildProvider();
        var service = new SqlOSDashboardSessionService(provider.GetRequiredService<IDataProtectionProvider>());
        var cookie = IssueCookie(service, "password-a", TimeSpan.FromHours(1));

        var context = ContextWithCookie(cookie);

        service.HasActiveSession(context, "password-a").Should().BeTrue();
        service.GetSessionExpiry(context, "password-a").Should().NotBeNull();
    }

    [TestMethod]
    public void HasActiveSession_RejectsCookieIssuedUnderPreviousPassword()
    {
        using var provider = BuildProvider();
        var service = new SqlOSDashboardSessionService(provider.GetRequiredService<IDataProtectionProvider>());
        var cookie = IssueCookie(service, "password-a", TimeSpan.FromHours(1));

        var context = ContextWithCookie(cookie);

        service.HasActiveSession(context, "password-b").Should().BeFalse();
        service.GetSessionExpiry(context, "password-b").Should().BeNull();
    }

    [TestMethod]
    public void HasActiveSession_FailsClosedWhenNoPasswordIsConfigured()
    {
        using var provider = BuildProvider();
        var service = new SqlOSDashboardSessionService(provider.GetRequiredService<IDataProtectionProvider>());
        var cookie = IssueCookie(service, "password-a", TimeSpan.FromHours(1));

        var context = ContextWithCookie(cookie);

        service.HasActiveSession(context, null).Should().BeFalse();
        service.HasActiveSession(context, string.Empty).Should().BeFalse();
        service.HasActiveSession(context, "   ").Should().BeFalse();
    }

    [TestMethod]
    public void HasActiveSession_RejectsExpiredTicket()
    {
        using var provider = BuildProvider();
        var service = new SqlOSDashboardSessionService(provider.GetRequiredService<IDataProtectionProvider>());
        var cookie = IssueCookie(service, "password-a", TimeSpan.FromSeconds(-5));

        service.HasActiveSession(ContextWithCookie(cookie), "password-a").Should().BeFalse();
    }

    [TestMethod]
    public void HasActiveSession_RejectsTamperedAndMissingCookies()
    {
        using var provider = BuildProvider();
        var service = new SqlOSDashboardSessionService(provider.GetRequiredService<IDataProtectionProvider>());
        var cookie = IssueCookie(service, "password-a", TimeSpan.FromHours(1));
        var tampered = cookie[..^4] + "AAAA";

        service.HasActiveSession(ContextWithCookie(tampered), "password-a").Should().BeFalse();
        service.HasActiveSession(new DefaultHttpContext(), "password-a").Should().BeFalse();
    }

    [TestMethod]
    public void HasActiveSession_AcrossInstancesSharingKeyRing_RespectsPasswordRotation()
    {
        // Two replicas sharing one Data Protection key ring, as password mode already requires.
        using var provider = BuildProvider();
        var dataProtection = provider.GetRequiredService<IDataProtectionProvider>();
        var replicaA = new SqlOSDashboardSessionService(dataProtection);
        var replicaB = new SqlOSDashboardSessionService(dataProtection);

        var preRotationCookie = IssueCookie(replicaA, "password-a", TimeSpan.FromHours(1));
        replicaB.HasActiveSession(ContextWithCookie(preRotationCookie), "password-a").Should().BeTrue();

        // Both replicas are redeployed with the rotated password.
        var postRotationCookie = IssueCookie(replicaA, "password-b", TimeSpan.FromHours(1));
        replicaB.HasActiveSession(ContextWithCookie(postRotationCookie), "password-b").Should().BeTrue();
        replicaB.HasActiveSession(ContextWithCookie(preRotationCookie), "password-b").Should().BeFalse();
        replicaA.HasActiveSession(ContextWithCookie(preRotationCookie), "password-b").Should().BeFalse();
    }

    [TestMethod]
    public void CreateSession_DoesNotPlacePasswordOrFingerprintInCookie()
    {
        using var provider = BuildProvider();
        var service = new SqlOSDashboardSessionService(provider.GetRequiredService<IDataProtectionProvider>());
        var cookie = IssueCookie(service, "password-a", TimeSpan.FromHours(1));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("password-a")));

        cookie.Should().NotContain("password-a");
        cookie.ToUpperInvariant().Should().NotContain(fingerprint);
        cookie.Should().NotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("password-a")));
    }

    [TestMethod]
    public void CreateSession_RequiresConfiguredPassword()
    {
        using var provider = BuildProvider();
        var service = new SqlOSDashboardSessionService(provider.GetRequiredService<IDataProtectionProvider>());
        var context = new DefaultHttpContext();

        var act = () => service.CreateSession(context, CookiePath, TimeSpan.FromHours(1), allowInsecureCookie: false, configuredPassword: " ");

        act.Should().Throw<InvalidOperationException>();
        context.Response.Headers.SetCookie.ToString().Should().BeEmpty();
    }

    [TestMethod]
    public async Task IsAuthorizedAsync_DevelopmentOnlyMode_IgnoresPasswordBinding()
    {
        using var provider = BuildProvider();
        var service = new SqlOSDashboardSessionService(provider.GetRequiredService<IDataProtectionProvider>());
        var context = new DefaultHttpContext();

        (await service.IsAuthorizedAsync(context, isDevelopment: true, SqlOSDashboardAuthMode.DevelopmentOnly, configuredPassword: null, authorizationCallback: null))
            .Should().BeTrue();
        (await service.IsAuthorizedAsync(context, isDevelopment: false, SqlOSDashboardAuthMode.DevelopmentOnly, configuredPassword: null, authorizationCallback: null))
            .Should().BeFalse();
        (await service.IsAuthorizedAsync(context, isDevelopment: false, SqlOSDashboardAuthMode.DevelopmentOnly, configuredPassword: null, authorizationCallback: _ => Task.FromResult(true)))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task IsAuthorizedAsync_PasswordMode_RequiresSessionBoundToCurrentPassword()
    {
        using var provider = BuildProvider();
        var service = new SqlOSDashboardSessionService(provider.GetRequiredService<IDataProtectionProvider>());
        var cookie = IssueCookie(service, "password-a", TimeSpan.FromHours(1));
        var callbackInvocations = 0;
        Func<HttpContext, Task<bool>> callback = _ =>
        {
            callbackInvocations++;
            return Task.FromResult(true);
        };

        (await service.IsAuthorizedAsync(ContextWithCookie(cookie), isDevelopment: true, SqlOSDashboardAuthMode.Password, "password-a", callback))
            .Should().BeTrue();
        (await service.IsAuthorizedAsync(ContextWithCookie(cookie), isDevelopment: true, SqlOSDashboardAuthMode.Password, "password-b", callback))
            .Should().BeFalse();

        // The host callback only runs once the session is bound to the current password.
        callbackInvocations.Should().Be(1);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        return services.BuildServiceProvider();
    }

    private static string IssueCookie(SqlOSDashboardSessionService service, string password, TimeSpan lifetime)
    {
        var context = new DefaultHttpContext();
        service.CreateSession(context, CookiePath, lifetime, allowInsecureCookie: false, configuredPassword: password);
        var setCookie = context.Response.Headers.SetCookie.ToString();
        var value = setCookie.Split(';', 2)[0];
        value.Should().StartWith("SqlOS.Dashboard.Session=");
        return value["SqlOS.Dashboard.Session=".Length..];
    }

    private static DefaultHttpContext ContextWithCookie(string cookieValue)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"SqlOS.Dashboard.Session={cookieValue}";
        return context;
    }
}
