using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.AuthServer.Services;
using SqlOS.Calendar.Models;
using SqlOS.Domain;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

/// <summary>
/// The claim of an unverified address (#420, #422, #423): the user aggregate evicts what the account
/// owned before the proof, and the claim process evicts what reaches across aggregates, staged on
/// one unit of work with one <c>user.email.claimed</c> row.
/// </summary>
[TestClass]
public sealed class ClaimEmailOwnershipTests
{
    [TestMethod]
    public async Task Claim_OnUnverifiedEmail_EvictsEverythingAttachedBeforeTheProof()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: false);
        var user = await context.Set<SqlOSUser>().SingleAsync();

        var outcome = await ClaimEmailOwnership.StageAsync(
            context,
            user,
            Proof(seeded.Address, OwnershipProofMethod.EmailOtp),
            PresentedCredentials.None,
            DateTime.UtcNow,
            default);
        await context.SaveChangesAsync();

        outcome.Claimed.Should().BeTrue();
        context.ChangeTracker.Clear();
        (await context.Set<SqlOSUserEmail>().SingleAsync()).IsVerified.Should().BeTrue();
        (await context.Set<SqlOSCredential>().SingleAsync()).RevokedAt.Should().NotBeNull();
        (await context.Set<SqlOSUserAuthenticator>().SingleAsync()).RevocationReason.Should().Be(ClaimEmailOwnership.RevocationReason);
        (await context.Set<SqlOSRecoveryCode>().SingleAsync()).RevokedAt.Should().NotBeNull();
        (await context.Set<SqlOSUserPhoneNumber>().SingleAsync()).RemovedAt.Should().NotBeNull();
        (await context.Set<SqlOSExternalIdentity>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSSession>().SingleAsync()).RevocationReason.Should().Be(ClaimEmailOwnership.RevocationReason);
        (await context.Set<SqlOSRefreshToken>().SingleAsync()).RevokedAt.Should().NotBeNull();
        var audit = await context.Set<SqlOSAuditEvent>().SingleAsync(x => x.EventType == ClaimEmailOwnership.AuditEventType);
        audit.UserId.Should().Be(seeded.UserId);
        audit.DataJson.Should().Contain("passwordCredentialIds").And.Contain(seeded.PasswordId)
            .And.Contain(seeded.AuthenticatorId).And.Contain(seeded.PhoneId)
            .And.Contain(seeded.OidcIdentityId).And.Contain(seeded.SamlIdentityId)
            .And.Contain("\"proof\":\"email_otp\"");
    }

    [TestMethod]
    public async Task Claim_RowIsThe721RowByteForByte()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: false);
        var user = await context.Set<SqlOSUser>().SingleAsync();
        var claimedAt = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        await ClaimEmailOwnership.StageAsync(context, user, Proof(seeded.Address, OwnershipProofMethod.MagicLink), PresentedCredentials.None, claimedAt, default);
        await context.SaveChangesAsync();

        var audit = await context.Set<SqlOSAuditEvent>().AsNoTracking().SingleAsync(x => x.EventType == ClaimEmailOwnership.AuditEventType);
        audit.Id.Should().MatchRegex("^evt_[0-9a-f]{24}$");
        audit.Action.Should().Be("user.email.claimed");
        audit.Source.Should().Be("authserver");
        audit.ActorType.Should().Be("user");
        audit.ActorId.Should().Be(seeded.UserId);
        audit.UserId.Should().Be(seeded.UserId);
        audit.OrganizationId.Should().BeNull();
        audit.ContextJson.Should().BeNull();
        audit.IpAddress.Should().BeNull();
        audit.TargetsJson.Should().Be($$"""[{"type":"user","id":"{{seeded.UserId}}"}]""");
        audit.OccurredAt.Should().Be(claimedAt, "7.2.1 recorded the claim at the instant it happened");
        audit.IngestedAt.Should().Be(claimedAt);
        audit.MetadataJson.Should().Be(audit.DataJson);
        audit.DataJson.Should().Be(
            $$$"""{"emailId":"{{{seeded.EmailId}}}","proof":"magic_link","revoked":{"passwordCredentialIds":["{{{seeded.PasswordId}}}"],"authenticatorIds":["{{{seeded.AuthenticatorId}}}"],"recoveryCodes":1,"phoneNumberIds":["{{{seeded.PhoneId}}}"],"externalIdentities":[{"id":"{{{seeded.OidcIdentityId}}}","kind":"oidc","connectionId":"oidc_1"},{"id":"{{{seeded.SamlIdentityId}}}","kind":"saml","connectionId":"sso_1"}],"consentGrantIds":["{{{seeded.ConsentGrantId}}}"],"calendarConnectionIds":["{{{seeded.CalendarConnectionId}}}"]}}""");
    }

    [TestMethod]
    public async Task Claim_OnVerifiedEmail_ChangesNothing()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: true);
        var user = await context.Set<SqlOSUser>().SingleAsync();

        var outcome = await ClaimEmailOwnership.StageAsync(
            context,
            user,
            Proof(seeded.Address, OwnershipProofMethod.Oidc),
            PresentedCredentials.None,
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
        var user = await context.Set<SqlOSUser>().SingleAsync();

        await ClaimEmailOwnership.StageAsync(
            context,
            user,
            Proof(seeded.Address, OwnershipProofMethod.Invitation),
            PresentedCredentials.FromAuthenticationMethod("password+totp"),
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
    public async Task Claim_KeepsThePasswordBeingReset()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: false);
        var user = await context.Set<SqlOSUser>().SingleAsync();

        await ClaimEmailOwnership.StageAsync(
            context,
            user,
            Proof(seeded.Address, OwnershipProofMethod.PasswordReset),
            PresentedCredentials.PasswordBeingReset(seeded.PasswordId),
            DateTime.UtcNow,
            default,
            sessionRevocationReason: "password_reset");
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        (await context.Set<SqlOSCredential>().SingleAsync()).RevokedAt.Should().BeNull();
        (await context.Set<SqlOSUserAuthenticator>().SingleAsync()).RevokedAt.Should().NotBeNull();
        (await context.Set<SqlOSSession>().SingleAsync()).RevocationReason.Should().Be("password_reset");
        var audit = await context.Set<SqlOSAuditEvent>().SingleAsync(x => x.EventType == ClaimEmailOwnership.AuditEventType);
        audit.DataJson.Should().Contain("\"passwordCredentialIds\":[]").And.Contain("\"proof\":\"password_reset\"");
    }

    [TestMethod]
    public async Task Claim_RevokesConsentGrantsAndCalendarConnections_ButKeepsMemberships()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: false);
        var user = await context.Set<SqlOSUser>().SingleAsync();

        await ClaimEmailOwnership.StageAsync(
            context,
            user,
            Proof(seeded.Address, OwnershipProofMethod.EmailOtp),
            PresentedCredentials.None,
            DateTime.UtcNow,
            default);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        var grant = await context.Set<SqlOSConsentGrant>().SingleAsync();
        grant.RevokedAt.Should().NotBeNull("an approval given before the claim must not let that client skip consent");
        grant.RevocationReason.Should().Be(ClaimEmailOwnership.RevocationReason);
        grant.UpdatedAt.Should().Be(grant.RevokedAt!.Value);
        var calendar = await context.Set<SqlOSCalendarConnection>().SingleAsync(x => x.Id == seeded.CalendarConnectionId);
        calendar.Status.Should().Be(SqlOSCalendarConnectionStatus.Revoked);
        calendar.RevokedAt.Should().NotBeNull();
        calendar.RevokedReason.Should().Be(ClaimEmailOwnership.RevocationReason);
        calendar.AccessTokenEncrypted.Should().BeNull();
        calendar.RefreshTokenEncrypted.Should().BeNull();
        (await context.Set<SqlOSCalendarConnection>().SingleAsync(x => x.Id == seeded.OrganizationCalendarConnectionId))
            .Status.Should().Be(SqlOSCalendarConnectionStatus.Active, "the organization's own calendar connection is not the user's");
        var membership = await context.Set<SqlOSMembership>().SingleAsync();
        membership.IsActive.Should().BeTrue("organizations control their memberships; an admin reviews a claimed account's memberships");
        membership.OrganizationId.Should().Be(seeded.OrganizationId);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "calendar.connection.disconnected"
            && x.ActorId == seeded.CalendarConnectionId)).Should().Be(1);
        var audit = await context.Set<SqlOSAuditEvent>().SingleAsync(x => x.EventType == ClaimEmailOwnership.AuditEventType);
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
        var presentation = PresentedCredentials.FromAuthenticationMethod(method);

        presentation.KeepOidcIdentities.Should().Be(oidc);
        presentation.KeepSamlIdentities.Should().Be(saml);
        presentation.KeepPhoneNumbers.Should().Be(phone);
        presentation.KeepPasswordCredentials.Should().Be(password);
        presentation.KeepsIdentity(TestRows.Create<SqlOSExternalIdentity>(new { OidcConnectionId = "oidc_1" })).Should().Be(oidc);
        presentation.KeepsIdentity(TestRows.Create<SqlOSExternalIdentity>(new { SsoConnectionId = "sso_1" })).Should().Be(saml);
        PresentedCredentials.PasswordBeingReset("cred_1")
            .KeepsCredential(TestRows.Create<SqlOSCredential>(new { Id = "cred_1" })).Should().BeTrue();
    }

    [TestMethod]
    public async Task Revert_DiscardsAStagedClaimButKeepsTheCallersEarlierChanges()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: false);
        var user = await context.Set<SqlOSUser>().SingleAsync();
        user.UpdateProfile("Changed before the claim", user.DefaultEmail, DateTime.UtcNow);

        var outcome = await ClaimEmailOwnership.StageAsync(
            context,
            user,
            Proof(seeded.Address, OwnershipProofMethod.Saml),
            PresentedCredentials.None,
            DateTime.UtcNow,
            default);
        context.Set<SqlOSMembership>().Add(new SqlOSMembership { OrganizationId = "org_x", UserId = seeded.UserId, CreatedAt = DateTime.UtcNow });
        outcome.Revert(context);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        (await context.Set<SqlOSUser>().SingleAsync()).DisplayName.Should().Be("Changed before the claim");
        (await context.Set<SqlOSConsentGrant>().SingleAsync()).RevokedAt.Should().BeNull();
        (await context.Set<SqlOSUserEmail>().SingleAsync()).IsVerified.Should().BeFalse();
        (await context.Set<SqlOSCredential>().SingleAsync()).RevokedAt.Should().BeNull();
        (await context.Set<SqlOSExternalIdentity>().CountAsync()).Should().Be(2);
        (await context.Set<SqlOSSession>().SingleAsync()).RevokedAt.Should().BeNull();
        var calendar = await context.Set<SqlOSCalendarConnection>().SingleAsync(x => x.Id == seeded.CalendarConnectionId);
        calendar.RevokedAt.Should().BeNull();
        calendar.RefreshTokenEncrypted.Should().NotBeNull();
        (await context.Set<SqlOSMembership>().CountAsync(x => x.OrganizationId == "org_x")).Should().Be(0);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == ClaimEmailOwnership.AuditEventType)).Should().Be(0, "a discarded claim leaves no event behind for a later save to audit");
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "calendar.connection.disconnected")).Should().Be(0);
    }

    [TestMethod]
    public async Task Revert_AfterAFailedSave_LeavesTheUnitOfWorkSavable()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: false);
        var user = await context.Set<SqlOSUser>().SingleAsync();

        var outcome = await ClaimEmailOwnership.StageAsync(
            context,
            user,
            Proof(seeded.Address, OwnershipProofMethod.Saml),
            PresentedCredentials.None,
            DateTime.UtcNow,
            default);
        context.ChangeTracker.DetectChanges();
        outcome.Revert(context);
        context.Set<SqlOSMembership>().Add(new SqlOSMembership { OrganizationId = "org_after", UserId = seeded.UserId, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        (await context.Set<SqlOSExternalIdentity>().CountAsync()).Should().Be(2, "the identities the discarded claim unlinked are still linked");
        (await context.Set<SqlOSMembership>().CountAsync(x => x.OrganizationId == "org_after")).Should().Be(1);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == ClaimEmailOwnership.AuditEventType)).Should().Be(0);
    }

    [TestMethod]
    public async Task Claim_RefusesAProofForAnotherMailbox()
    {
        await using var context = CreateContext();
        await SeedAccountAsync(context, verified: false);
        var user = await context.Set<SqlOSUser>().SingleAsync();
        var otherMailbox = new OwnershipProof(EmailAddress.Parse("someone-else@example.com"), OwnershipProofMethod.EmailOtp);

        await FluentActions.Invoking(() => ClaimEmailOwnership.StageAsync(
                context,
                user,
                otherMailbox,
                PresentedCredentials.None,
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
        await SeedAccountAsync(context, verified: false);
        var user = await context.Set<SqlOSUser>().SingleAsync();
        var otherSpelling = new OwnershipProof(EmailAddress.Parse("  OWNER@Example.COM "), OwnershipProofMethod.MagicLink);

        var outcome = await ClaimEmailOwnership.StageAsync(
            context,
            user,
            otherSpelling,
            PresentedCredentials.None,
            DateTime.UtcNow,
            default);
        await context.SaveChangesAsync();

        outcome.Claimed.Should().BeTrue();
        var audit = await context.Set<SqlOSAuditEvent>().SingleAsync(x => x.EventType == ClaimEmailOwnership.AuditEventType);
        audit.MetadataJson.Should().Contain("\"proof\":\"magic_link\"");
    }

    [TestMethod]
    public async Task Claim_LoadsTheAccountItClaims_EvenWhenTheCallerLoadedOnlyTheUser()
    {
        await using var context = CreateContext();
        var seeded = await SeedAccountAsync(context, verified: false);
        var user = await context.Set<SqlOSUser>().SingleAsync();
        user.LoadedParts.Should().Be(SqlOSUserParts.None, "a user materialized by a plain query has no parts loaded");

        await ClaimEmailOwnership.StageAsync(context, user, Proof(seeded.Address, OwnershipProofMethod.EmailOtp), PresentedCredentials.None, DateTime.UtcNow, default);

        user.LoadedParts.Should().HaveFlag(SqlOSUserParts.Claim);
        user.Credentials.Should().ContainSingle(credential => credential.RevokedAt != null);
    }

    private static OwnershipProof Proof(string address, OwnershipProofMethod method)
        => new(EmailAddress.Parse(address), method);

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase($"claim-{Guid.NewGuid():N}")
            .Options);

    /// <summary>
    /// An account whose address nobody proved yet, with something of every kind attached: a
    /// password, a confirmed authenticator, a recovery code, a verified phone, an OpenID identity
    /// that registered it, a SAML identity linked before 7.2.1 required a verified address, a
    /// session with a refresh token, a consent grant, its own and its organization's calendar
    /// connections, and a membership.
    /// </summary>
    private static async Task<SeededAccount> SeedAccountAsync(TestSqlOSInMemoryDbContext context, bool verified)
    {
        var now = DateTime.UtcNow;
        var user = verified
            ? SqlOSUser.RegisterFromExternalIdentity(
                "Account",
                new OwnershipProof(EmailAddress.Parse("owner@example.com"), OwnershipProofMethod.Oidc),
                ExternalIdentityLink.Oidc("oidc_1", "Custom", "https://issuer", "squatter", "owner@example.com"),
                now)
            : SqlOSUser.RegisterFromExternalIdentity(
                "Account",
                EmailAddress.Parse("owner@example.com"),
                ExternalIdentityLink.Oidc("oidc_1", "Custom", "https://issuer", "squatter", "owner@example.com"),
                now);
        var password = user.SetPassword("Squatter-Password-1!", SqlOS.AuthServer.Policies.PasswordPolicy.Default, now);
        var authenticator = user.EnrollTotp("secret", null, new TotpParameters("SHA1", 6, 30), now);
        user.ConfirmTotp(authenticator.Id, acceptedTimeStep: 1, now);
        user.IssueRecoveryCodes(["code"], now);
        var phone = user.AddVerifiedPhone("+15555550100", "protected-phone", now);
        var oidcIdentityId = user.ExternalIdentities.Single().Id;
        var emailId = user.Emails.Single().Id;
        context.Add(user);
        var samlIdentity = TestRows.Create<SqlOSExternalIdentity>(new
        {
            Id = "ext_saml",
            UserId = user.Id,
            SsoConnectionId = "sso_1",
            Issuer = "urn:idp",
            Subject = "squatter",
            CreatedAt = now
        });
        context.AddRange(
            samlIdentity,
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
        // The setup is not what the tests observe.
        context.Set<SqlOSAuditEvent>().RemoveRange(await context.Set<SqlOSAuditEvent>().ToListAsync());
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return new SeededAccount(
            user.Id,
            "owner@example.com",
            emailId,
            password.Id,
            authenticator.Id,
            phone.Id,
            oidcIdentityId,
            "ext_saml",
            "cgr_claim",
            "cal_claim",
            "cal_org",
            "org_member");
    }

    private sealed record SeededAccount(
        string UserId,
        string Address,
        string EmailId,
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
