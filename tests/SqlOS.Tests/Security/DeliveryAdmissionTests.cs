using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.Extensions;
using SqlOS.Security;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.Security;

/// <summary>
/// Email codes and sign-in links are admitted through the delivery buckets (#424): one atomic
/// reservation per send, limits reported in the 7.x order (the address, then the IP address, then
/// the client), a limit of zero admits nothing, and a send the cooldown refuses gives its
/// reservation back. The real-SQL races are in <c>DeliveryAdmissionIntegrationTests</c>.
/// </summary>
[TestClass]
public sealed class DeliveryAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private const string Address = "alice@example.test";
    private const string Ip = "203.0.113.10";
    private const string Client = "cli_1";

    [TestMethod]
    public async Task Email_codes_are_admitted_up_to_the_hourly_limit_for_an_address()
    {
        var admission = new SqlOSDeliveryAdmissionService();
        var options = new SqlOSEmailOtpOptions { MaxChallengesPerHour = 2 };

        (await AdmitCodeAsync(admission, options)).Admitted.Should().BeTrue();
        (await AdmitCodeAsync(admission, options)).Admitted.Should().BeTrue();
        var refused = await AdmitCodeAsync(admission, options);

        refused.Admitted.Should().BeFalse();
        refused.RefusedLimit.Should().Be("email");
        refused.RetryAfter.Should().Be(Now.AddHours(1), "the limit is per hour");
        (await AdmitCodeAsync(admission, options, address: "bob@example.test")).Admitted.Should().BeTrue("another address has its own limit");
        (await AdmitCodeAsync(admission, options, now: Now.AddHours(1).AddSeconds(1))).Admitted.Should().BeTrue("the window has passed");
    }

    [TestMethod]
    public async Task A_refusal_names_the_first_reached_limit_in_the_7x_order()
    {
        var admission = new SqlOSDeliveryAdmissionService();
        var options = new SqlOSEmailOtpOptions { MaxChallengesPerHour = 1, MaxChallengesPerIpPerHour = 1, MaxChallengesPerClientPerHour = 1 };

        (await AdmitCodeAsync(admission, options)).Admitted.Should().BeTrue();

        (await AdmitCodeAsync(admission, options)).RefusedLimit.Should().Be("email", "the address is checked first");
        (await AdmitCodeAsync(admission, options, address: "bob@example.test")).RefusedLimit.Should().Be("ip");
        (await AdmitCodeAsync(admission, options, address: "bob@example.test", ip: "198.51.100.7")).RefusedLimit.Should().Be("client");
        (await AdmitCodeAsync(admission, options, address: "bob@example.test", ip: null, client: null)).Admitted.Should().BeTrue("an absent IP address or client has no limit");
    }

    [TestMethod]
    public async Task A_refused_send_reserves_nothing()
    {
        var admission = new SqlOSDeliveryAdmissionService();
        var options = new SqlOSEmailOtpOptions { MaxChallengesPerHour = 5, MaxChallengesPerIpPerHour = 1 };

        (await AdmitCodeAsync(admission, options)).Admitted.Should().BeTrue();
        for (var refused = 0; refused < 3; refused++)
        {
            (await AdmitCodeAsync(admission, options)).RefusedLimit.Should().Be("ip");
        }

        // The refused sends did not count against the address: four more are admitted from other IP addresses.
        for (var other = 1; other <= 4; other++)
        {
            (await AdmitCodeAsync(admission, options, ip: $"198.51.100.{other}")).Admitted.Should().BeTrue();
        }

        (await AdmitCodeAsync(admission, options, ip: "198.51.100.9")).RefusedLimit.Should().Be("email");
    }

    [TestMethod]
    public async Task A_limit_of_zero_admits_nothing_and_reserves_nothing()
    {
        var admission = new SqlOSDeliveryAdmissionService();

        var closedAddress = await AdmitCodeAsync(admission, new SqlOSEmailOtpOptions { MaxChallengesPerHour = 0 });
        closedAddress.Admitted.Should().BeFalse();
        closedAddress.RefusedLimit.Should().Be("email");

        var closedIp = new SqlOSEmailOtpOptions { MaxChallengesPerHour = 1, MaxChallengesPerIpPerHour = 0 };
        (await AdmitCodeAsync(admission, closedIp)).RefusedLimit.Should().Be("ip");
        (await AdmitCodeAsync(admission, closedIp)).RefusedLimit.Should().Be("ip", "the closed limit reserved nothing for the address");
        (await AdmitCodeAsync(admission, closedIp, ip: null)).Admitted.Should().BeTrue("without an IP address the closed limit does not apply");
        (await AdmitCodeAsync(admission, closedIp)).RefusedLimit.Should().Be("email", "a reached limit before the closed one is reported first, as 7.x checked it first");
    }

    [TestMethod]
    public async Task A_withdrawn_admission_gives_its_reservations_back()
    {
        var admission = new SqlOSDeliveryAdmissionService();
        var options = new SqlOSEmailOtpOptions { MaxChallengesPerHour = 1, MaxChallengesPerIpPerHour = 1, MaxChallengesPerClientPerHour = 1 };

        var admitted = await AdmitCodeAsync(admission, options);
        admitted.Reservations.Select(reservation => reservation.Scope).Should().Equal(
            SqlOSDeliveryAdmissionService.EmailOtpEmailScope,
            SqlOSDeliveryAdmissionService.EmailOtpIpScope,
            SqlOSDeliveryAdmissionService.EmailOtpClientScope);
        admitted.Reservations.Should().OnlyContain(reservation => reservation.WindowStartedAt == Now);
        (await AdmitCodeAsync(admission, options)).Admitted.Should().BeFalse();

        await admission.WithdrawAsync(admitted, Now, CancellationToken.None);

        (await AdmitCodeAsync(admission, options)).Admitted.Should().BeTrue("every bucket the withdrawn send reserved was given back");
        await admission.WithdrawAsync(DeliveryAdmission.Refuse("email", Now), Now, CancellationToken.None);
    }

    [TestMethod]
    public async Task Sign_in_links_have_their_own_limits()
    {
        var admission = new SqlOSDeliveryAdmissionService();
        var links = new SqlOSMagicLinkOptions { MaxLinksPerEmailPerWindow = 1, RateLimitWindow = TimeSpan.FromMinutes(30) };
        var codes = new SqlOSEmailOtpOptions { MaxChallengesPerHour = 1 };

        (await AdmitCodeAsync(admission, codes)).Admitted.Should().BeTrue();
        (await AdmitLinkAsync(admission, links)).Admitted.Should().BeTrue("an email code does not count against sign-in links");
        var refused = await AdmitLinkAsync(admission, links);

        refused.RefusedLimit.Should().Be("email");
        refused.RetryAfter.Should().Be(Now.AddMinutes(30), "the link window is configured");
        (await AdmitLinkAsync(admission, links, now: Now.AddMinutes(31))).Admitted.Should().BeTrue();
    }

    [TestMethod]
    public async Task Sign_in_link_refusals_follow_the_7x_order()
    {
        var admission = new SqlOSDeliveryAdmissionService();
        var links = new SqlOSMagicLinkOptions { MaxLinksPerEmailPerWindow = 5, MaxLinksPerIpPerWindow = 1, MaxLinksPerClientPerWindow = 1 };

        (await AdmitLinkAsync(admission, links)).Admitted.Should().BeTrue();

        (await AdmitLinkAsync(admission, links)).RefusedLimit.Should().Be("ip");
        (await AdmitLinkAsync(admission, links, ip: "198.51.100.7")).RefusedLimit.Should().Be("client");
    }

    [TestMethod]
    public void The_origin_of_a_request_is_its_remote_address_and_raw_user_agent()
    {
        var withAgent = new DefaultHttpContext();
        withAgent.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(Ip);
        withAgent.Request.Headers.UserAgent = "Agent/1.0";

        AdmissionOrigin.Of(withAgent).Should().Be(new AdmissionOrigin(Ip, "Agent/1.0"));
        AdmissionOrigin.Of(new DefaultHttpContext()).Should().Be(new AdmissionOrigin(null, string.Empty), "7.x read an absent header as empty");
        AdmissionOrigin.Of((HttpContext?)null).Should().Be(AdmissionOrigin.None);
        AdmissionOrigin.Of(new SqlOSRequestContext(SqlOSRequestSurface.Hosted, Ip, "Agent/1.0", "req", "corr"))
            .Should().Be(new AdmissionOrigin(Ip, "Agent/1.0"));
    }

    [TestMethod]
    public async Task The_gate_admits_each_kind_with_its_configured_limits()
    {
        await using var context = new TestSqlOSInMemoryDbContext(
            new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var options = Options.Create(new SqlOSAuthServerOptions());
        options.Value.EmailOtp.MaxChallengesPerHour = 1;
        options.Value.MagicLink.MaxLinksPerEmailPerWindow = 1;
        var crypto = TestCryptoService.Create(context, options);
        var gate = SqlOSAdmissionGate.Create(context, new SqlOSAdminService(context, options, crypto), crypto, options);
        var address = EmailAddress.Parse("  Alice@Example.TEST ");
        var origin = new AdmissionOrigin(Ip, "Agent/1.0");

        (await gate.AdmitEmailCodeAsync(address, origin, Client, Now, CancellationToken.None)).Admitted.Should().BeTrue();
        (await gate.AdmitEmailCodeAsync(EmailAddress.Parse(Address), origin, Client, Now, CancellationToken.None)).RefusedLimit
            .Should().Be("email", "the address's spellings share its canonical key");
        (await gate.AdmitSignInLinkAsync(address, origin, Client, Now, CancellationToken.None)).Admitted.Should().BeTrue();
        (await gate.AdmitSignInLinkAsync(address, origin, Client, Now, CancellationToken.None)).RefusedLimit.Should().Be("email");
    }

    [TestMethod]
    public void AddSqlOS_registers_the_admission_gate_processes_pass()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestSqlOSInMemoryDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddSqlOS<TestSqlOSInMemoryDbContext>(options => options.AuthServer.Issuer = "https://tests.example/sqlos/auth");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAdmissionGate>().Should().BeOfType<SqlOSAdmissionGate>();
    }

    private static Task<DeliveryAdmission> AdmitCodeAsync(
        SqlOSDeliveryAdmissionService admission,
        SqlOSEmailOtpOptions options,
        string address = Address,
        string? ip = Ip,
        string? client = Client,
        DateTimeOffset? now = null)
        => admission.AdmitEmailOtpAsync(EmailAddress.Parse(address).Canonical, ip, client, options, now ?? Now, CancellationToken.None);

    private static Task<DeliveryAdmission> AdmitLinkAsync(
        SqlOSDeliveryAdmissionService admission,
        SqlOSMagicLinkOptions options,
        string address = Address,
        string? ip = Ip,
        string? client = Client,
        DateTimeOffset? now = null)
        => admission.AdmitMagicLinkAsync(EmailAddress.Parse(address).Canonical, ip, client, options, now ?? Now, CancellationToken.None);
}
