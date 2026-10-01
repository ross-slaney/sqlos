using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.Calendar.Models;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSEmailOwnershipClaimTests
{
    [TestMethod]
    public async Task Claim_OnUnverifiedEmail_EvictsEverythingAttachedBeforeTheProof()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: false);

        var outcome = await SqlOSEmailOwnershipClaim.ClaimAsync(
            context,
            seeded.Email,
            Proof(seeded.Email, OwnershipProofMethod.EmailOtp),
            SqlOSEmailClaimPresentation.None,
            DateTime.UtcNow,
            default);
        await context.SaveChangesAsync();

        outcome.Claimed.Should().BeTrue();
        context.ChangeTracker.Clear();
        (await context.Set<SqlOSUserEmail>().SingleAsync()).IsVerified.Should().BeTrue();
        (await context.Set<SqlOSCredential>().SingleAsync()).RevokedAt.Should().NotBeNull();
        (await context.Set<SqlOSUserAuthenticator>().SingleAsync()).RevocationReason.Should().Be(SqlOSEmailOwnershipClaim.RevocationReason);
        (await context.Set<SqlOSRecoveryCode>().SingleAsync()).RevokedAt.Should().NotBeNull();
        (await context.Set<SqlOSUserPhoneNumber>().SingleAsync()).RemovedAt.Should().NotBeNull();
        (await context.Set<SqlOSExternalIdentity>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSSession>().SingleAsync()).RevocationReason.Should().Be(SqlOSEmailOwnershipClaim.RevocationReason);
        (await context.Set<SqlOSRefreshToken>().SingleAsync()).RevokedAt.Should().NotBeNull();
        var audit = await context.Set<SqlOSAuditEvent>().SingleAsync(x => x.EventType == SqlOSEmailOwnershipClaim.AuditEventType);
        audit.UserId.Should().Be(seeded.UserId);
        audit.DataJson.Should().Contain("passwordCredentialIds").And.Contain(seeded.PasswordId)
            .And.Contain(seeded.AuthenticatorId).And.Contain(seeded.PhoneId)
            .And.Contain(seeded.OidcIdentityId).And.Contain(seeded.SamlIdentityId)
            .And.Contain("\"proof\":\"email_otp\"");
    }

    [TestMethod]
    public async Task Claim_OnVerifiedEmail_ChangesNothing()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: true);

        var outcome = await SqlOSEmailOwnershipClaim.ClaimAsync(
            context,
            seeded.Email,
            Proof(seeded.Email, OwnershipProofMethod.Oidc),
            SqlOSEmailClaimPresentation.None,
            DateTime.UtcNow,
            default);

        outcome.Claimed.Should().BeFalse();
        context.ChangeTracker.HasChanges().Should().BeFalse();
        outcome.Revert(context);
    }

    [TestMethod]
    public async Task Claim_KeepsOnlyTheCredentialPresentedInThisFlow()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: false);

        await SqlOSEmailOwnershipClaim.ClaimAsync(
            context,
            seeded.Email,
            Proof(seeded.Email, OwnershipProofMethod.Invitation),
            SqlOSEmailClaimPresentation.FromAuthenticationMethod("password+totp"),
            DateTime.UtcNow,
            default);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        (await context.Set<SqlOSCredential>().SingleAsync()).RevokedAt.Should().BeNull();
        (await context.Set<SqlOSUserAuthenticator>().SingleAsync()).RevokedAt.Should().BeNull();
        (await context.Set<SqlOSRecoveryCode>().SingleAsync()).RevokedAt.Should().BeNull();
        (await context.Set<SqlOSUserPhoneNumber>().SingleAsync()).RemovedAt.Should().NotBeNull();
        (await context.Set<SqlOSExternalIdentity>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task Claim_RevokesConsentGrantsAndCalendarConnections_ButKeepsMemberships()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: false);

        await SqlOSEmailOwnershipClaim.ClaimAsync(
            context,
            seeded.Email,
            Proof(seeded.Email, OwnershipProofMethod.EmailOtp),
            SqlOSEmailClaimPresentation.None,
            DateTime.UtcNow,
            default);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        var grant = await context.Set<SqlOSConsentGrant>().SingleAsync();
        grant.RevokedAt.Should().NotBeNull("an approval given before the claim must not let that client skip consent");
        grant.RevocationReason.Should().Be(SqlOSEmailOwnershipClaim.RevocationReason);
        var calendar = await context.Set<SqlOSCalendarConnection>().SingleAsync(x => x.Id == seeded.CalendarConnectionId);
        calendar.Status.Should().Be(SqlOSCalendarConnectionStatus.Revoked);
        calendar.RevokedAt.Should().NotBeNull();
        calendar.RevokedReason.Should().Be(SqlOSEmailOwnershipClaim.RevocationReason);
        calendar.AccessTokenEncrypted.Should().BeNull();
        calendar.RefreshTokenEncrypted.Should().BeNull();
        (await context.Set<SqlOSCalendarConnection>().SingleAsync(x => x.Id == seeded.OrganizationCalendarConnectionId))
            .Status.Should().Be(SqlOSCalendarConnectionStatus.Active, "the organization's own calendar connection is not the user's");
        var membership = await context.Set<SqlOSMembership>().SingleAsync();
        membership.IsActive.Should().BeTrue("organizations control their memberships; an admin reviews a claimed account's memberships");
        membership.OrganizationId.Should().Be(seeded.OrganizationId);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "calendar.connection.disconnected"
            && x.ActorId == seeded.CalendarConnectionId)).Should().Be(1);
        var audit = await context.Set<SqlOSAuditEvent>().SingleAsync(x => x.EventType == SqlOSEmailOwnershipClaim.AuditEventType);
        audit.DataJson.Should().Contain("consentGrantIds").And.Contain(seeded.ConsentGrantId)
            .And.Contain("calendarConnectionIds").And.Contain(seeded.CalendarConnectionId)
            .And.NotContain(seeded.OrganizationCalendarConnectionId);
    }

    [DataTestMethod]
    [DataRow("google", true, false, false, false)]
    [DataRow("oidc", true, false, false, false)]
    [DataRow("saml+upstream_mfa", false, true, false, false)]
    [DataRow("phone_otp", false, false, true, false)]
    [DataRow("password", false, false, false, true)]
    [DataRow("email_otp", false, false, false, false)]
    [DataRow(null, false, false, false, false)]
    public void Presentation_MapsTheSignInMethodToTheCredentialKind(
        string? method,
        bool oidc,
        bool saml,
        bool phone,
        bool password)
    {
        var presentation = SqlOSEmailClaimPresentation.FromAuthenticationMethod(method);

        presentation.KeepOidcIdentities.Should().Be(oidc);
        presentation.KeepSamlIdentities.Should().Be(saml);
        presentation.KeepPhoneNumbers.Should().Be(phone);
        presentation.KeepPasswordCredentials.Should().Be(password);
        presentation.KeepsIdentity(new SqlOSExternalIdentity { OidcConnectionId = "oidc_1" }).Should().Be(oidc);
        presentation.KeepsIdentity(new SqlOSExternalIdentity { SsoConnectionId = "sso_1" }).Should().Be(saml);
        new SqlOSEmailClaimPresentation { PasswordCredentialId = "cred_1" }
            .KeepsCredential(new SqlOSCredential { Id = "cred_1" }).Should().BeTrue();
    }

    [TestMethod]
    public async Task Revert_DiscardsAStagedClaimButKeepsTheCallersEarlierChanges()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: false);
        var user = await context.Set<SqlOSUser>().SingleAsync();
        user.DisplayName = "Changed before the claim";

        var outcome = await SqlOSEmailOwnershipClaim.ClaimAsync(
            context,
            seeded.Email,
            Proof(seeded.Email, OwnershipProofMethod.Saml),
            SqlOSEmailClaimPresentation.None,
            DateTime.UtcNow,
            default);
        context.Set<SqlOSMembership>().Add(new SqlOSMembership { OrganizationId = "org_x", UserId = seeded.UserId, CreatedAt = DateTime.UtcNow });
        outcome.Revert(context);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        (await context.Set<SqlOSUser>().SingleAsync()).DisplayName.Should().Be("Changed before the claim");
        (await context.Set<SqlOSUserEmail>().SingleAsync()).IsVerified.Should().BeFalse();
        (await context.Set<SqlOSCredential>().SingleAsync()).RevokedAt.Should().BeNull();
        (await context.Set<SqlOSExternalIdentity>().CountAsync()).Should().Be(2);
        (await context.Set<SqlOSSession>().SingleAsync()).RevokedAt.Should().BeNull();
        (await context.Set<SqlOSConsentGrant>().SingleAsync()).RevokedAt.Should().BeNull();
        var calendar = await context.Set<SqlOSCalendarConnection>().SingleAsync(x => x.Id == seeded.CalendarConnectionId);
        calendar.RevokedAt.Should().BeNull();
        calendar.RefreshTokenEncrypted.Should().NotBeNull();
        (await context.Set<SqlOSMembership>().CountAsync(x => x.OrganizationId == "org_x")).Should().Be(0);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == SqlOSEmailOwnershipClaim.AuditEventType)).Should().Be(0);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "calendar.connection.disconnected")).Should().Be(0);
    }

    [TestMethod]
    public async Task Claim_RefusesAProofForAnotherMailbox()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: false);
        var otherMailbox = new OwnershipProof(EmailAddress.Parse("someone-else@example.com"), OwnershipProofMethod.EmailOtp);

        await FluentActions.Invoking(() => SqlOSEmailOwnershipClaim.ClaimAsync(
                context,
                seeded.Email,
                otherMailbox,
                SqlOSEmailClaimPresentation.None,
                DateTime.UtcNow,
                default))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The ownership proof is for another mailbox.");
        context.ChangeTracker.Entries().Should().OnlyContain(entry => entry.State == EntityState.Unchanged, "a refused claim stages nothing");
        (await context.Set<SqlOSUserEmail>().SingleAsync()).IsVerified.Should().BeFalse();
    }

    [TestMethod]
    public async Task Claim_AcceptsAProofForAnotherSpellingOfTheSameMailbox()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: false);
        var otherSpelling = new OwnershipProof(EmailAddress.Parse("  OWNER@Example.COM "), OwnershipProofMethod.MagicLink);

        var outcome = await SqlOSEmailOwnershipClaim.ClaimAsync(
            context,
            seeded.Email,
            otherSpelling,
            SqlOSEmailClaimPresentation.None,
            DateTime.UtcNow,
            default);
        await context.SaveChangesAsync();

        outcome.Claimed.Should().BeTrue();
        var audit = await context.Set<SqlOSAuditEvent>().SingleAsync(x => x.EventType == SqlOSEmailOwnershipClaim.AuditEventType);
        audit.MetadataJson.Should().Contain("\"proof\":\"magic_link\"");
    }

    private static OwnershipProof Proof(SqlOSUserEmail email, OwnershipProofMethod method)
        => new(EmailAddress.Parse(email.Email), method);

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase($"claim-{Guid.NewGuid():N}")
            .Options);

    private static async Task<SeededAccount> SeedAccountAsync(TestSqlOSInMemoryDbContext context, bool verified)
    {
        var now = DateTime.UtcNow;
        var user = new SqlOSUser { Id = "usr_claim", DisplayName = "Account", CreatedAt = now, UpdatedAt = now };
        var email = new SqlOSUserEmail
        {
            Id = "eml_claim",
            UserId = user.Id,
            Email = "owner@example.com",
            NormalizedEmail = "OWNER@EXAMPLE.COM",
            IsPrimary = true,
            IsVerified = verified,
            CreatedAt = now
        };
        context.AddRange(
            user,
            email,
            new SqlOSCredential { Id = "cred_claim", UserId = user.Id, SecretHash = "hash", CreatedAt = now },
            new SqlOSUserAuthenticator { Id = "auth_claim", UserId = user.Id, SecretProtected = "secret", IsConfirmed = true, CreatedAt = now },
            new SqlOSRecoveryCode { Id = "rc_claim", UserId = user.Id, CodeHash = "code", CreatedAt = now },
            new SqlOSUserPhoneNumber { Id = "phn_claim", UserId = user.Id, PhoneNumber = "+15555550100", PhoneNumberHash = "phone", IsVerified = true, CreatedAt = now, UpdatedAt = now },
            new SqlOSExternalIdentity { Id = "ext_oidc", UserId = user.Id, OidcConnectionId = "oidc_1", Issuer = "https://issuer", Subject = "squatter", CreatedAt = now },
            new SqlOSExternalIdentity { Id = "ext_saml", UserId = user.Id, SsoConnectionId = "sso_1", Issuer = "urn:idp", Subject = "squatter", CreatedAt = now },
            new SqlOSSession { Id = "ses_claim", UserId = user.Id, CreatedAt = now, LastSeenAt = now, IdleExpiresAt = now.AddHours(1), AbsoluteExpiresAt = now.AddDays(1) },
            new SqlOSRefreshToken { Id = "rt_claim", SessionId = "ses_claim", TokenHash = "rt", FamilyId = "fam", CreatedAt = now, ExpiresAt = now.AddDays(1) },
            new SqlOSConsentGrant { Id = "cgr_claim", UserId = user.Id, ClientApplicationId = "cli_attacker", Scope = "openid", GrantedAt = now, UpdatedAt = now },
            new SqlOSCalendarConnection
            {
                Id = "cal_claim",
                ProviderType = SqlOSCalendarProviderType.Google,
                OidcConnectionId = "oidc_1",
                UserId = user.Id,
                DisplayName = "Squatter calendar",
                AccessTokenEncrypted = "dp:access",
                RefreshTokenEncrypted = "dp:refresh",
                CreatedAt = now,
                UpdatedAt = now
            },
            new SqlOSCalendarConnection
            {
                Id = "cal_org",
                ProviderType = SqlOSCalendarProviderType.Google,
                OidcConnectionId = "oidc_1",
                OrganizationId = "org_member",
                DisplayName = "Organization calendar",
                AccessTokenEncrypted = "dp:org-access",
                RefreshTokenEncrypted = "dp:org-refresh",
                CreatedAt = now,
                UpdatedAt = now
            },
            new SqlOSMembership { OrganizationId = "org_member", UserId = user.Id, Role = "member", IsActive = true, CreatedAt = now });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var trackedEmail = await context.Set<SqlOSUserEmail>().SingleAsync();
        return new SeededAccount(
            user.Id,
            trackedEmail,
            "cred_claim",
            "auth_claim",
            "phn_claim",
            "ext_oidc",
            "ext_saml",
            "cgr_claim",
            "cal_claim",
            "cal_org",
            "org_member");
    }

    private sealed record SeededAccount(
        string UserId,
        SqlOSUserEmail Email,
        string PasswordId,
        string AuthenticatorId,
        string PhoneId,
        string OidcIdentityId,
        string SamlIdentityId,
        string ConsentGrantId,
        string CalendarConnectionId,
        string OrganizationCalendarConnectionId,
        string OrganizationId);
}
