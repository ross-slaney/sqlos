using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Email.Interfaces;

namespace SqlOS.BehaviorLock.Host.Fakes;

/// <summary>
/// The per-host set of fakes that replace every SqlOS seam which would otherwise leave the
/// process: email, SMS, outbound HTTP (social/OIDC upstreams, CIMD documents, JWKS, calendar
/// providers), and DNS. One instance per host; nothing is static, so hosts can run in parallel.
/// </summary>
public sealed class BehaviorLockFakes
{
    public BehaviorLockFakes(IHttpContextAccessor httpContextAccessor)
    {
        HttpContextAccessor = httpContextAccessor;
        Effects = new EffectLog(httpContextAccessor);
        Email = new CapturingEmailSender(Effects);
        Sms = new FakeSmsDeliveryChannel(Effects);
        Dns = new FakeDnsTxtVerifier(Effects);
        Cimd = new CimdDocuments();
        Oidc = new FakeOidcUpstream();
        Calendar = new FakeCalendarUpstream();
        Http = new RecordingHttpClientFactory(Effects, Cimd, Oidc, Calendar);
    }

    public IHttpContextAccessor HttpContextAccessor { get; }
    public EffectLog Effects { get; }
    public CapturingEmailSender Email { get; }
    public FakeSmsDeliveryChannel Sms { get; }
    public FakeDnsTxtVerifier Dns { get; }
    public CimdDocuments Cimd { get; }
    public FakeOidcUpstream Oidc { get; }
    public FakeCalendarUpstream Calendar { get; }
    public RecordingHttpClientFactory Http { get; }

    /// <summary>
    /// Registers the fakes. Call after <c>AddSqlOS</c>: SqlOS registers its production seams with
    /// <c>Add*</c>, so these replace them for single-service resolution.
    /// </summary>
    public void Register(IServiceCollection services)
    {
        services.AddSingleton(this);
        services.AddSingleton(Effects);
        services.AddSingleton(Dns);
        services.AddSingleton(Cimd);
        services.RemoveAll<ISqlOSAuthEmailSender>();
        services.RemoveAll<ISqlOSEmailSender>();
        services.RemoveAll<ISqlOSOtpDeliveryChannel>();
        services.RemoveAll<ISqlOSDomainDnsVerifier>();
        services.RemoveAll<IHttpClientFactory>();
        services.AddSingleton<ISqlOSAuthEmailSender>(Email);
        services.AddSingleton<ISqlOSEmailSender>(Email);
        services.AddSingleton<ISqlOSOtpDeliveryChannel>(Sms);
        services.AddSingleton<ISqlOSDomainDnsVerifier>(Dns);
        services.AddSingleton<IHttpClientFactory>(Http);
    }
}
