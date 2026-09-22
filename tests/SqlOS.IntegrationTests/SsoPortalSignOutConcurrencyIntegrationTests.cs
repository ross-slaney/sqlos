using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class SsoPortalSignOutConcurrencyIntegrationTests
{
    [TestMethod]
    public async Task SignOut_RacingProviderMutation_CommitsNoConfigurationChangeAfterRevocation()
    {
        string connectionString;
        string sessionId;
        string cookie;
        var setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("SsoPortalSignOut");
        await using (var setup = BuildStack(setupContext))
        {
            connectionString = setup.Context.Database.GetConnectionString()!;
            await setup.Crypto.EnsureActiveSigningKeyAsync();
            var organization = await setup.Admin.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest("Portal Sign Out Race", null, "portal-sign-out-race.test"));
            var created = await setup.Portal.CreateSessionAsync(
                new SqlOSCreateSsoPortalSessionRequest(organization.Id, Provider: "microsoft-entra"),
                CreateHttpContext());
            var openHttp = CreateHttpContext();
            await setup.Portal.OpenSessionAsync(ExtractToken(created.SetupUrl!), openHttp);
            sessionId = created.Id;
            cookie = openHttp.Response.Headers.SetCookie.ToString().Split(';', 2)[0];
        }

        var pause = new PausePortalRevocationSaveInterceptor();
        await using var signOutStack = BuildStack(CreateContext(connectionString, pause));
        await using var mutationStack = BuildStack(CreateContext(connectionString));
        var staleSession = await mutationStack.Context.Set<SqlOSSsoPortalSession>()
            .SingleAsync(x => x.Id == sessionId);
        staleSession.Provider.Should().Be("microsoft-entra");

        var signOut = CreateHttpContext();
        signOut.Request.Headers.Cookie = cookie;
        var signOutTask = signOutStack.Portal.SignOutAsync(signOut);
        await pause.RevocationSaveReached.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await using (var during = CreateContext(connectionString))
        {
            var uncommitted = await during.Set<SqlOSSsoPortalSession>().AsNoTracking().SingleAsync(x => x.Id == sessionId);
            uncommitted.RevokedAt.Should().BeNull();
            uncommitted.Provider.Should().Be("microsoft-entra");
            (await during.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "sso.portal.session.closed"))
                .Should().BeFalse();
        }

        using var mutationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var mutationTask = mutationStack.Portal.SetProviderAsync(
            staleSession,
            new SqlOSUpdateSsoPortalProviderRequest("okta"),
            CreateHttpContext(),
            mutationTimeout.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        pause.ReleaseRevocationSave.TrySetResult(true);

        await signOutTask;
        var mutation = async () => await mutationTask;
        await mutation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Portal session is invalid or expired.");

        signOut.Response.Headers.SetCookie.ToString().ToLowerInvariant()
            .Should().Contain("sqlos_sso_portal=")
            .And.Contain("httponly")
            .And.Contain("secure")
            .And.Contain("samesite=lax")
            .And.Contain("path=/sqlos/admin/auth/sso-portal");

        await using var verify = CreateContext(connectionString);
        var stored = await verify.Set<SqlOSSsoPortalSession>().AsNoTracking().SingleAsync(x => x.Id == sessionId);
        stored.RevokedAt.Should().NotBeNull();
        stored.RevokedReason.Should().Be(SqlOSSsoPortalService.SignedOutReason);
        stored.Provider.Should().Be("microsoft-entra");
        stored.SessionTokenHash.Should().NotBeNullOrWhiteSpace();
        (await verify.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "sso.portal.provider.selected"))
            .Should().Be(0);
        var closed = await verify.Set<SqlOSAuditEvent>().SingleAsync(x => x.EventType == "sso.portal.session.closed");
        closed.MetadataJson.Should().Contain(sessionId).And.Contain(SqlOSSsoPortalService.SignedOutReason);
        closed.MetadataJson.Should().NotContain(stored.SessionTokenHash);
        closed.MetadataJson.Should().NotContain(cookie["sqlos_sso_portal=".Length..]);

        await using var replayStack = BuildStack(CreateContext(connectionString));
        var replay = CreateHttpContext();
        replay.Request.Headers.Cookie = cookie;
        (await replayStack.Portal.TryGetSessionAsync(replay)).Should().BeNull();
        var replaySession = await replayStack.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == sessionId);
        var replayMutation = async () => await replayStack.Portal.UpdateEnrollmentPolicyAsync(
            replaySession,
            new SqlOSSsoPortalEnrollmentPolicyRequest(false, true),
            CreateHttpContext());
        await replayMutation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Portal session is invalid or expired.");
        (await verify.Set<SqlOSSsoConnection>().SingleAsync(x => x.Id == stored.ConnectionId))
            .AutoProvisionUsers.Should().BeFalse();
    }

    private static PortalStack BuildStack(TestSqlOSDbContext context)
    {
        var options = Options.Create(new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://auth.example.test",
            Issuer = "https://auth.example.test/sqlos/auth",
            BasePath = "/sqlos/auth"
        });
        var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(context, options, crypto);
        var domains = new SqlOSOrganizationDomainService(
            context,
            options,
            crypto,
            admin,
            new RejectingDomainDnsVerifier());
        var portal = new SqlOSSsoPortalService(context, options, crypto, admin, domains);
        return new PortalStack(context, crypto, admin, portal);
    }

    private static TestSqlOSDbContext CreateContext(string connectionString, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<TestSqlOSDbContext>().UseTestProvider(connectionString);
        if (interceptor != null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new TestSqlOSDbContext(builder.Options);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("auth.example.test");
        return context;
    }

    private static string ExtractToken(string setupUrl)
        => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(setupUrl).Query)["token"].ToString();

    private sealed class PortalStack(
        TestSqlOSDbContext context,
        SqlOSCryptoService crypto,
        SqlOSAdminService admin,
        SqlOSSsoPortalService portal) : IAsyncDisposable
    {
        public TestSqlOSDbContext Context { get; } = context;
        public SqlOSCryptoService Crypto { get; } = crypto;
        public SqlOSAdminService Admin { get; } = admin;
        public SqlOSSsoPortalService Portal { get; } = portal;

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class RejectingDomainDnsVerifier : ISqlOSDomainDnsVerifier
    {
        public Task<bool> HasTxtRecordValueAsync(
            string recordName,
            string expectedValue,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class PausePortalRevocationSaveInterceptor : SaveChangesInterceptor
    {
        private int _hasPaused;

        public TaskCompletionSource<bool> RevocationSaveReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseRevocationSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var isSignOut = eventData.Context != null
                && eventData.Context.ChangeTracker.Entries<SqlOSSsoPortalSession>().Any(entry =>
                    entry.State == EntityState.Modified
                    && string.Equals(entry.Entity.RevokedReason, SqlOSSsoPortalService.SignedOutReason, StringComparison.Ordinal));
            if (isSignOut && Interlocked.Exchange(ref _hasPaused, 1) == 0)
            {
                RevocationSaveReached.TrySetResult(true);
                await ReleaseRevocationSave.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
