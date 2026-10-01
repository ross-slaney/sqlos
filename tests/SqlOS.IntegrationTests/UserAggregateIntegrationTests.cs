using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Policies;
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// The User aggregate against real SQL on the configured provider: its private lists persist and
/// load through EF Core's field access, host queries read them as in 7.x, a part loads only its
/// live members, the members' concurrency tokens hold, and a claim commits once.
/// </summary>
[TestClass]
public sealed class UserAggregateIntegrationTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task An_account_round_trips_and_host_queries_read_it_as_before()
    {
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("UserRoundTrip");
        try
        {
            var user = SqlOSUser.Register("Alice", EmailAddress.Parse("alice@example.test"), Now);
            user.SetPassword("Alice-Password-1", PasswordPolicy.Default, Now);
            var authenticator = user.EnrollTotp("protected-secret", null, new TotpParameters("SHA1", 6, 30), Now);
            user.ConfirmTotp(authenticator.Id, 100, Now);
            user.IssueRecoveryCodes(["AAAA-BBBB", "CCCC-DDDD"], Now);
            user.AddVerifiedPhone("+12025550100", "protected-phone", Now);
            setup.Set<SqlOSUser>().Add(user);
            await setup.SaveChangesAsync();

            await using var host = NewContext(setup);
            var read = await host.Set<SqlOSUser>()
                .AsNoTracking()
                .Include(x => x.Emails)
                .Include(x => x.Credentials)
                .Include(x => x.Authenticators)
                .Include(x => x.RecoveryCodes)
                .Include(x => x.PhoneNumbers)
                .Include(x => x.MfaPolicyOverride)
                .SingleAsync(x => x.Id == user.Id);
            read.Emails.Should().ContainSingle().Which.NormalizedEmail.Should().Be("ALICE@EXAMPLE.TEST");
            read.Credentials.Should().ContainSingle().Which.Type.Should().Be("password");
            read.Authenticators.Should().ContainSingle().Which.IsConfirmed.Should().BeTrue();
            read.RecoveryCodes.Should().HaveCount(2);
            read.PhoneNumbers.Should().ContainSingle().Which.IsPrimary.Should().BeTrue();
            read.MfaPolicyOverride!.RequireMfa.Should().BeTrue();
            (await host.Set<SqlOSUser>().Where(x => x.Id == user.Id).Select(x => x.Emails.Count).SingleAsync()).Should().Be(1);
            (await host.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == user.Id)).IsVerified.Should().BeFalse();
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }

    [TestMethod]
    public async Task A_part_loads_its_live_members_into_the_tracked_account()
    {
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("UserParts");
        try
        {
            var user = SqlOSUser.Register("Alice", EmailAddress.Parse("alice@example.test"), Now);
            var first = user.EnrollTotp("secret-1", null, new TotpParameters("SHA1", 6, 30), Now);
            var second = user.EnrollTotp("secret-2", null, new TotpParameters("SHA1", 6, 30), Now.AddMinutes(1));
            setup.Set<SqlOSUser>().Add(user);
            await setup.SaveChangesAsync();
            first.RevokedAt.Should().NotBeNull("the second enrollment replaced the unconfirmed first");

            await using var context = NewContext(setup);
            var stored = await context.Set<SqlOSUser>().SingleAsync(x => x.Id == user.Id);
            stored.LoadedParts.Should().Be(SqlOSUserParts.None);
            FluentActions.Invoking(() => stored.FindAuthenticator(second.Id)).Should().Throw<SqlOSDomainException>();

            await context.LoadUserPartsAsync(stored, SqlOSUserParts.Authenticators);

            stored.Authenticators.Should().ContainSingle(authenticator => authenticator.Id == second.Id, "only live members load");
            stored.FindAuthenticator(second.Id).Should().NotBeNull();
            (await context.FindUserAsync(user.Id, SqlOSUserParts.Authenticators)).Should().BeSameAs(stored, "the tracked account is reused");
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }

    [TestMethod]
    public async Task Two_units_of_work_cannot_both_accept_a_code_or_spend_a_recovery_code()
    {
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("UserTokens");
        try
        {
            var user = SqlOSUser.Register("Alice", EmailAddress.Parse("alice@example.test"), Now);
            var authenticator = user.EnrollTotp("secret", null, new TotpParameters("SHA1", 6, 30), Now);
            user.ConfirmTotp(authenticator.Id, 100, Now);
            user.IssueRecoveryCodes(["AAAA-BBBB"], Now);
            setup.Set<SqlOSUser>().Add(user);
            await setup.SaveChangesAsync();

            await using var left = NewContext(setup);
            await using var right = NewContext(setup);
            var leftUser = await left.GetUserAsync(user.Id, SqlOSUserParts.Authenticators | SqlOSUserParts.RecoveryCodes);
            var rightUser = await right.GetUserAsync(user.Id, SqlOSUserParts.Authenticators | SqlOSUserParts.RecoveryCodes);

            leftUser.AcceptTotpCode(authenticator.Id, 101, Now).Should().BeTrue();
            rightUser.AcceptTotpCode(authenticator.Id, 101, Now).Should().BeTrue("each unit of work saw step 100");
            await left.SaveChangesAsync();
            await FluentActions.Invoking(() => right.SaveChangesAsync())
                .Should().ThrowAsync<DbUpdateConcurrencyException>("LastAcceptedTimeStep is a concurrency token: one code, one sign-in");

            await using var leftAgain = NewContext(setup);
            await using var rightAgain = NewContext(setup);
            var leftCodes = await leftAgain.GetUserAsync(user.Id, SqlOSUserParts.RecoveryCodes);
            var rightCodes = await rightAgain.GetUserAsync(user.Id, SqlOSUserParts.RecoveryCodes);
            leftCodes.UseRecoveryCode("AAAA-BBBB", Now).Should().BeTrue();
            rightCodes.UseRecoveryCode("AAAA-BBBB", Now).Should().BeTrue();
            await leftAgain.SaveChangesAsync();
            await FluentActions.Invoking(() => rightAgain.SaveChangesAsync())
                .Should().ThrowAsync<DbUpdateConcurrencyException>("ConsumedAt is a concurrency token: a recovery code is spent once");
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }

    [TestMethod]
    public async Task A_claim_commits_its_evictions_and_one_audit_row_in_one_save()
    {
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("UserClaim");
        try
        {
            var options = Options.Create(new SqlOSAuthServerOptions());
            var admin = new SqlOSAdminService(setup, options, new SqlOSCryptoService(setup, options));
            var google = await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
                SqlOSOidcProviderType.Google,
                "Google",
                "google-client",
                "google-secret",
                ["https://app.example.local/callback/google"],
                true,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null));
            var squatter = SqlOSUser.RegisterFromExternalIdentity(
                "Squatter",
                EmailAddress.Parse("owner@example.test"),
                ExternalIdentityLink.Oidc(google.Id, "Google", "https://accounts.google.com", "squatter", "owner@example.test"),
                Now);
            var password = squatter.SetPassword("Squatter-Password-1", PasswordPolicy.Default, Now);
            var phone = squatter.AddVerifiedPhone("+12025550100", "protected-phone", Now);
            setup.Set<SqlOSUser>().Add(squatter);
            await setup.SaveChangesAsync();
            var identityId = squatter.ExternalIdentities.Single().Id;

            await using var context = NewContext(setup);
            var user = await context.Set<SqlOSUser>().SingleAsync(x => x.Id == squatter.Id);
            var outcome = await ClaimEmailOwnership.StageAsync(
                context,
                user,
                new OwnershipProof(EmailAddress.Parse("OWNER@example.test"), OwnershipProofMethod.MagicLink),
                PresentedCredentials.None,
                Now.AddMinutes(5),
                CancellationToken.None);
            await context.SaveChangesAsync();
            await context.SaveChangesAsync();

            outcome.Claimed.Should().BeTrue();
            await using var verify = NewContext(setup);
            (await verify.Set<SqlOSUserEmail>().SingleAsync()).IsVerified.Should().BeTrue();
            (await verify.Set<SqlOSCredential>().SingleAsync(x => x.Id == password.Id)).RevokedAt.Should().Be(Now.AddMinutes(5));
            (await verify.Set<SqlOSUserPhoneNumber>().SingleAsync(x => x.Id == phone.Id)).RemovalReason.Should().Be("email_claimed");
            (await verify.Set<SqlOSExternalIdentity>().CountAsync()).Should().Be(0, "the claim deleted the identity's row");
            var audit = await verify.Set<SqlOSAuditEvent>().SingleAsync(x => x.EventType == ClaimEmailOwnership.AuditEventType);
            audit.OccurredAt.Should().Be(Now.AddMinutes(5));
            audit.DataJson.Should().Contain($"\"passwordCredentialIds\":[\"{password.Id}\"]")
                .And.Contain($"\"phoneNumberIds\":[\"{phone.Id}\"]")
                .And.Contain($"{{\"id\":\"{identityId}\",\"kind\":\"oidc\",\"connectionId\":\"{google.Id}\"}}")
                .And.Contain("\"proof\":\"magic_link\"");
            user.ExternalIdentities.Should().BeEmpty("the saved deletion leaves the account");
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }

    private static TestSqlOSDbContext NewContext(TestSqlOSDbContext setup)
        => new(new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseTestProvider(setup.Database.GetConnectionString()!)
            .Options);
}
