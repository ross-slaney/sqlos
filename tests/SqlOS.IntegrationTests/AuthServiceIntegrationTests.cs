using System.Data;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class AuthServiceIntegrationTests
{
    [TestMethod]
    public async Task OAuthRefresh_OmittedOrganization_AfterMembershipRemoval_IsRejected()
    {
        var email = $"refresh-offboard-{Guid.NewGuid():N}@example.com";
        SqlOSTokenResponse tokens;
        string userId;
        await using (var issuance = BuildIsolatedLifecycleStack())
        {
            var signup = await issuance.Auth.SignUpAsync(
                new SqlOSSignupRequest(
                    "Refresh Offboard",
                    email,
                    "P@ssword123!",
                    $"Refresh Offboard {Guid.NewGuid():N}",
                    "test-client",
                    null),
                new DefaultHttpContext());
            tokens = signup.Tokens!;
            userId = await issuance.Context.Set<SqlOS.AuthServer.Models.SqlOSUser>()
                .Where(x => x.DefaultEmail == email)
                .Select(x => x.Id)
                .SingleAsync();
        }

        await using (var offboarding = BuildIsolatedContext())
        {
            var membership = await offboarding.Set<SqlOS.AuthServer.Models.SqlOSMembership>()
                .SingleAsync(x => x.UserId == userId && x.OrganizationId == tokens.OrganizationId);
            membership.IsActive = false;
            await offboarding.SaveChangesAsync();
        }

        await using var refreshInstance = BuildIsolatedLifecycleStack();
        var action = async () => await refreshInstance.Auth.RefreshAsync(
            new SqlOSRefreshRequest(tokens.RefreshToken, OrganizationId: null));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Session is no longer active.");
        (await refreshInstance.Context.Set<SqlOS.AuthServer.Models.SqlOSSession>()
            .SingleAsync(x => x.Id == tokens.SessionId)).RevocationReason.Should().Be("membership_inactive");
        (await refreshInstance.Context.Set<SqlOS.AuthServer.Models.SqlOSAuditEvent>()
            .AnyAsync(x => x.EventType == "auth.lifecycle.denied"
                && x.UserId == userId
                && x.OrganizationId == tokens.OrganizationId)).Should().BeTrue();
    }

    [TestMethod]
    public async Task AuthPageSession_AfterMembershipRemoval_IsRejectedAcrossDbContexts()
    {
        var email = $"cookie-offboard-{Guid.NewGuid():N}@example.com";
        string userId;
        string organizationId;
        string rawCookie;
        await using (var issuance = BuildIsolatedLifecycleStack())
        {
            var signup = await issuance.Auth.SignUpAsync(
                new SqlOSSignupRequest(
                    "Cookie Offboard",
                    email,
                    "P@ssword123!",
                    $"Cookie Offboard {Guid.NewGuid():N}",
                    "test-client",
                    null),
                new DefaultHttpContext());
            userId = await issuance.Context.Set<SqlOS.AuthServer.Models.SqlOSUser>()
                .Where(x => x.DefaultEmail == email)
                .Select(x => x.Id)
                .SingleAsync();
            organizationId = signup.Tokens!.OrganizationId!;
            rawCookie = await issuance.Crypto.CreateTemporaryTokenAsync(
                "auth_page_session",
                userId,
                clientApplicationId: null,
                organizationId: organizationId,
                payload: new { AuthenticationMethod = "password" },
                lifetime: TimeSpan.FromMinutes(30));
        }

        await using (var offboarding = BuildIsolatedContext())
        {
            var membership = await offboarding.Set<SqlOS.AuthServer.Models.SqlOSMembership>()
                .SingleAsync(x => x.UserId == userId && x.OrganizationId == organizationId);
            membership.IsActive = false;
            await offboarding.SaveChangesAsync();
        }

        await using var reuseInstance = BuildIsolatedLifecycleStack();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Cookie = $"sqlos_auth_page={rawCookie}";

        (await reuseInstance.AuthPage.TryGetSessionAsync(httpContext)).Should().BeNull();
        (await reuseInstance.Crypto.FindTemporaryTokenAsync("auth_page_session", rawCookie)).Should().BeNull();
    }

    [TestMethod]
    public async Task ValidateAccessToken_IdleExpiredOrLifecycleInvalidSession_IsRejected()
    {
        SqlOSTokenResponse idleTokens;
        await using (var issuance = BuildIsolatedLifecycleStack())
        {
            idleTokens = (await issuance.Auth.SignUpAsync(
                new SqlOSSignupRequest(
                    "Idle SQL",
                    $"idle-sql-{Guid.NewGuid():N}@example.com",
                    "P@ssword123!",
                    $"Idle SQL {Guid.NewGuid():N}",
                    "test-client",
                    null),
                new DefaultHttpContext())).Tokens!;
        }

        await using (var offboarding = BuildIsolatedContext())
        {
            var session = await offboarding.Set<SqlOS.AuthServer.Models.SqlOSSession>()
                .SingleAsync(x => x.Id == idleTokens.SessionId);
            session.IdleExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await offboarding.SaveChangesAsync();
        }

        await using (var validation = BuildIsolatedLifecycleStack())
        {
            (await validation.Auth.ValidateAccessTokenAsync(idleTokens.AccessToken, AspireFixture.Options.DefaultAudience))
                .Should().BeNull();
            (await validation.Context.Set<SqlOS.AuthServer.Models.SqlOSSession>()
                .SingleAsync(x => x.Id == idleTokens.SessionId)).RevocationReason.Should().Be("session_idle_expired");
        }

        var lifecycleEmail = $"access-offboard-{Guid.NewGuid():N}@example.com";
        SqlOSTokenResponse lifecycleTokens;
        string lifecycleUserId;
        await using (var issuance = BuildIsolatedLifecycleStack())
        {
            lifecycleTokens = (await issuance.Auth.SignUpAsync(
                new SqlOSSignupRequest(
                    "Access Offboard",
                    lifecycleEmail,
                    "P@ssword123!",
                    $"Access Offboard {Guid.NewGuid():N}",
                    "test-client",
                    null),
                new DefaultHttpContext())).Tokens!;
            lifecycleUserId = await issuance.Context.Set<SqlOS.AuthServer.Models.SqlOSUser>()
                .Where(x => x.DefaultEmail == lifecycleEmail)
                .Select(x => x.Id)
                .SingleAsync();
        }

        await using (var offboarding = BuildIsolatedContext())
        {
            var organization = await offboarding.Set<SqlOS.AuthServer.Models.SqlOSOrganization>()
                .SingleAsync(x => x.Id == lifecycleTokens.OrganizationId);
            organization.IsActive = false;
            await offboarding.SaveChangesAsync();
        }

        await using (var validation = BuildIsolatedLifecycleStack())
        {
            (await validation.Auth.ValidateAccessTokenAsync(lifecycleTokens.AccessToken, AspireFixture.Options.DefaultAudience))
                .Should().BeNull();
            (await validation.Context.Set<SqlOS.AuthServer.Models.SqlOSSession>()
                .SingleAsync(x => x.Id == lifecycleTokens.SessionId)).RevocationReason.Should().Be("organization_inactive");
            (await validation.Context.Set<SqlOS.AuthServer.Models.SqlOSAuditEvent>()
                .AnyAsync(x => x.EventType == "auth.lifecycle.denied" && x.UserId == lifecycleUserId))
                .Should().BeTrue();
        }
    }

    [TestMethod]
    public async Task LogoutAll_RevokesPendingAuthorizationArtifactsAcrossDbContexts()
    {
        const string verifier = "sql-logout-verifier-123456789012345678901234";
        string userId;
        string organizationId;
        string pendingCode;
        string pendingMfaToken;
        string deviceAuthorizationId;
        string clientId;
        var email = $"pending-artifact-{Guid.NewGuid():N}@example.com";
        await using (var issuance = BuildIsolatedLifecycleStack())
        {
            var signup = await issuance.Auth.SignUpAsync(
                new SqlOSSignupRequest(
                    "Pending Artifact SQL",
                    email,
                    "P@ssword123!",
                    $"Pending Artifact {Guid.NewGuid():N}",
                    "test-client",
                    null),
                new DefaultHttpContext());
            var user = await issuance.Context.Set<SqlOSUser>()
                .SingleAsync(x => x.DefaultEmail == email);
            userId = user.Id;
            organizationId = signup.Tokens!.OrganizationId!;
            var client = await issuance.Context.Set<SqlOSClientApplication>()
                .SingleAsync(x => x.ClientId == "test-client");
            clientId = client.ClientId;
            var authorizationRequest = new SqlOSAuthorizationRequest
            {
                Id = $"req_{Guid.NewGuid():N}",
                ClientApplicationId = client.Id,
                ClientApplication = client,
                RedirectUri = "https://client.example.test/callback",
                State = "pending-artifact-state",
                Scope = "openid",
                CodeChallenge = issuance.Crypto.CreatePkceCodeChallenge(verifier),
                CodeChallengeMethod = "S256",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            };
            issuance.Context.Set<SqlOSAuthorizationRequest>().Add(authorizationRequest);
            await issuance.Context.SaveChangesAsync();
            var redirect = await issuance.Authorization.IssueAuthorizationRedirectAsync(
                authorizationRequest,
                user,
                organizationId,
                "password",
                new DefaultHttpContext());
            pendingCode = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();
            pendingMfaToken = await issuance.Crypto.CreateTemporaryTokenAsync(
                SqlOSAuthService.MfaChallengePurpose,
                userId,
                client.Id,
                organizationId,
                new { Flow = "client" },
                TimeSpan.FromMinutes(5));
            deviceAuthorizationId = $"dev_{Guid.NewGuid():N}";
            var now = DateTime.UtcNow;
            issuance.Context.Set<SqlOSDeviceAuthorization>().Add(new SqlOSDeviceAuthorization
            {
                Id = deviceAuthorizationId,
                DeviceCodeHash = issuance.Crypto.HashToken($"device-{Guid.NewGuid():N}"),
                UserCodeHash = issuance.Crypto.HashToken($"code-{Guid.NewGuid():N}"),
                UserCode = "PENDING2",
                ClientApplicationId = client.Id,
                Status = SqlOSDeviceAuthorizationService.ApprovedStatus,
                ApprovedUserId = userId,
                ApprovedOrganizationId = organizationId,
                AuthenticationMethod = "password",
                CreatedAt = now,
                ApprovedAt = now,
                ExpiresAt = now.AddMinutes(10)
            });
            await issuance.Context.SaveChangesAsync();
        }

        await using (var revocation = BuildIsolatedLifecycleStack())
        {
            await revocation.Auth.LogoutAllAsync(userId);
        }

        await using var verification = BuildIsolatedLifecycleStack();
        var exchange = async () => await verification.Authorization.ExchangeAuthorizationCodeAsync(
            new SqlOSTokenRequest(
                "authorization_code",
                pendingCode,
                "https://client.example.test/callback",
                clientId,
                verifier,
                RefreshToken: null,
                Resource: null),
            new DefaultHttpContext());
        await exchange.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authorization code is no longer valid.");
        (await verification.Crypto.FindTemporaryTokenAsync(
            SqlOSAuthService.MfaChallengePurpose,
            pendingMfaToken)).Should().BeNull();
        var deviceAuthorization = await verification.Context.Set<SqlOSDeviceAuthorization>()
            .SingleAsync(x => x.Id == deviceAuthorizationId);
        deviceAuthorization.Status.Should().Be(SqlOSDeviceAuthorizationService.DeniedStatus);
        deviceAuthorization.DeniedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task SsoOrganizationRevocation_AfterRefreshOrganizationSwitch_RevokesFamilyAcrossDbContexts()
    {
        var suffix = Guid.NewGuid().ToString("N");
        string portalSessionId;
        string switchedSessionId;
        string clientAudience;
        string sourceOrganizationId;
        string targetOrganizationId;
        SqlOSTokenResponse switchedTargetTokens;
        SqlOSTokenResponse unrelatedSourceTokens;

        await using (var issuance = BuildIsolatedLifecycleStack())
        {
            var sourceOrganization = await issuance.Admin.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest($"SQL Switch Source {suffix}", null));
            var targetOrganization = await issuance.Admin.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest($"SQL Switch Target {suffix}", null));
            sourceOrganizationId = sourceOrganization.Id;
            targetOrganizationId = targetOrganization.Id;

            var portalResult = await issuance.Portal.CreateSessionAsync(
                new SqlOSCreateSsoPortalSessionRequest(targetOrganization.Id),
                new DefaultHttpContext());
            portalSessionId = portalResult.Id;
            var connection = await issuance.Context.Set<SqlOSSsoConnection>()
                .SingleAsync(x => x.OrganizationId == targetOrganization.Id);
            connection.IsEnabled = true;
            connection.IdentityProviderEntityId = $"urn:sql-switch:{suffix}";
            connection.SingleSignOnUrl = "https://idp.sql-switch.test/sso";
            connection.X509CertificatePem = "-----BEGIN CERTIFICATE-----\nTEST\n-----END CERTIFICATE-----";
            issuance.Context.Set<SqlOSOrganizationDomain>().Add(new SqlOSOrganizationDomain
            {
                Id = issuance.Crypto.GenerateId("dom"),
                OrganizationId = targetOrganization.Id,
                Domain = $"{suffix}.sql-switch.test",
                Status = SqlOSOrganizationDomainStatuses.Active,
                VerificationToken = "verified",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                VerifiedAt = DateTime.UtcNow
            });

            var switchedUser = await issuance.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "SQL Switched User",
                $"switched@{suffix}.sql-switch.test",
                "P@ssword123!"));
            var unrelatedUser = await issuance.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "SQL Unrelated User",
                $"unrelated@{suffix}.sql-switch.test",
                "P@ssword123!"));
            var verifiedEmails = await issuance.Context.Set<SqlOSUserEmail>()
                .Where(x => x.UserId == switchedUser.Id || x.UserId == unrelatedUser.Id)
                .ToListAsync();
            foreach (var email in verifiedEmails)
            {
                email.IsVerified = true;
                email.VerifiedAt = DateTime.UtcNow;
            }

            await issuance.Admin.CreateMembershipAsync(
                sourceOrganization.Id,
                new SqlOSCreateMembershipRequest(switchedUser.Id, "member"));
            await issuance.Admin.CreateMembershipAsync(
                targetOrganization.Id,
                new SqlOSCreateMembershipRequest(switchedUser.Id, "member"));
            await issuance.Admin.CreateMembershipAsync(
                sourceOrganization.Id,
                new SqlOSCreateMembershipRequest(unrelatedUser.Id, "member"));
            var client = await issuance.Context.Set<SqlOSClientApplication>()
                .SingleAsync(x => x.ClientId == "test-client");
            clientAudience = client.Audience;

            var switchedSourceTokens = await issuance.Auth.CreateSessionTokensForUserAsync(
                switchedUser,
                client,
                sourceOrganization.Id,
                "password",
                "SQL lifecycle integration",
                "203.0.113.50");
            switchedTargetTokens = await issuance.Auth.RefreshAsync(
                new SqlOSRefreshRequest(switchedSourceTokens.RefreshToken, targetOrganization.Id));
            switchedSessionId = switchedTargetTokens.SessionId;
            unrelatedSourceTokens = await issuance.Auth.CreateSessionTokensForUserAsync(
                unrelatedUser,
                client,
                sourceOrganization.Id,
                "password",
                "SQL lifecycle integration",
                "203.0.113.51");

            (await issuance.Context.Set<SqlOSSession>()
                .SingleAsync(x => x.Id == switchedSessionId))
                .OrganizationId.Should().Be(sourceOrganization.Id);
            (await issuance.Context.Set<SqlOSRefreshToken>()
                .AnyAsync(x => x.SessionId == switchedSessionId
                    && x.ReplacementOrganizationId == targetOrganization.Id)).Should().BeTrue();
            await issuance.Context.SaveChangesAsync();
        }

        await using (var revocation = BuildIsolatedLifecycleStack())
        {
            var portalSession = await revocation.Context.Set<SqlOSSsoPortalSession>()
                .SingleAsync(x => x.Id == portalSessionId);
            var result = await revocation.Portal.RevokeOrganizationSessionsAsync(
                portalSession,
                new SqlOSSsoPortalRevokeOrganizationSessionsRequest(true),
                new DefaultHttpContext());

            result.RevokedSessions.Should().Be(1);
            var switchedSession = await revocation.Context.Set<SqlOSSession>()
                .SingleAsync(x => x.Id == switchedSessionId);
            switchedSession.RevocationReason.Should().Be("sso_required");
            (await revocation.Context.Set<SqlOSRefreshToken>()
                .Where(x => x.SessionId == switchedSessionId)
                .AllAsync(x => x.RevokedAt != null)).Should().BeTrue();
        }

        await using var verification = BuildIsolatedLifecycleStack();
        (await verification.Auth.ValidateAccessTokenAsync(
            switchedTargetTokens.AccessToken,
            clientAudience)).Should().BeNull();
        var switchedRefresh = async () => await verification.Auth.RefreshAsync(
            new SqlOSRefreshRequest(switchedTargetTokens.RefreshToken, targetOrganizationId));
        await switchedRefresh.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Refresh token is no longer valid.");

        (await verification.Auth.ValidateAccessTokenAsync(
            unrelatedSourceTokens.AccessToken,
            clientAudience)).Should().NotBeNull();
        var unrelatedRefresh = await verification.Auth.RefreshAsync(
            new SqlOSRefreshRequest(unrelatedSourceTokens.RefreshToken, sourceOrganizationId));
        unrelatedRefresh.OrganizationId.Should().Be(sourceOrganizationId);
    }

    [TestMethod]
    public async Task OrganizationDeactivation_SerializesAgainstStalePortalMutationAcrossDbContexts()
    {
        var suffix = Guid.NewGuid().ToString("N");
        string organizationId;
        string portalSessionId;
        string pendingSetupToken;
        string openedPortalCookie;
        SqlOSUpdateOrganizationRequest deactivationRequest;

        await using (var issuance = BuildIsolatedLifecycleStack())
        {
            var organization = await issuance.Admin.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest(
                    $"Portal Deactivation Race {suffix}",
                    null,
                    $"{suffix}.portal-race.test"));
            organizationId = organization.Id;
            deactivationRequest = new SqlOSUpdateOrganizationRequest(
                organization.Name,
                organization.Slug,
                organization.PrimaryDomain,
                IsActive: false);
            var created = await issuance.Portal.CreateSessionAsync(
                new SqlOSCreateSsoPortalSessionRequest(organization.Id, Provider: "okta"),
                new DefaultHttpContext());
            portalSessionId = created.Id;
            var pending = await issuance.Portal.CreateSessionAsync(
                new SqlOSCreateSsoPortalSessionRequest(organization.Id),
                new DefaultHttpContext());
            pendingSetupToken = QueryHelpers.ParseQuery(new Uri(pending.SetupUrl!).Query)["token"].ToString();
            var opened = await issuance.Portal.CreateSessionAsync(
                new SqlOSCreateSsoPortalSessionRequest(organization.Id),
                new DefaultHttpContext());
            var openContext = new DefaultHttpContext();
            await issuance.Portal.OpenSessionAsync(
                QueryHelpers.ParseQuery(new Uri(opened.SetupUrl!).Query)["token"].ToString(),
                openContext);
            openedPortalCookie = openContext.Response.Headers.SetCookie.ToString().Split(';', 2)[0];
        }

        await using var mutation = BuildIsolatedLifecycleStack();
        var stalePortalSession = await mutation.Context.Set<SqlOSSsoPortalSession>()
            .SingleAsync(x => x.Id == portalSessionId);
        stalePortalSession.Provider.Should().Be("okta");

        await using var deactivation = BuildIsolatedLifecycleStack();
        await using var deactivationTransaction = await deactivation.Context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable);
        await deactivation.Admin.UpdateOrganizationAsync(
            organizationId,
            deactivationRequest);

        var mutationTask = mutation.Portal.SetProviderAsync(
            stalePortalSession,
            new SqlOSUpdateSsoPortalProviderRequest("google-workspace"),
            new DefaultHttpContext());
        await Task.Delay(250);
        mutationTask.IsCompleted.Should().BeFalse(
            "the portal mutation must serialize behind the organization lifecycle transaction");

        await deactivationTransaction.CommitAsync();

        var mutationAction = async () => await mutationTask;
        await mutationAction.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Portal session is invalid or expired.");

        await using var verification = BuildIsolatedLifecycleStack();
        (await verification.Context.Set<SqlOSOrganization>()
            .SingleAsync(x => x.Id == organizationId)).IsActive.Should().BeFalse();
        var revokedSession = await verification.Context.Set<SqlOSSsoPortalSession>()
            .SingleAsync(x => x.Id == portalSessionId);
        revokedSession.Provider.Should().Be("okta");
        revokedSession.RevokedAt.Should().NotBeNull();
        revokedSession.RevokedReason.Should().Be("organization_deactivated");
        (await verification.Context.Set<SqlOSSsoPortalSession>()
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync()).Should().OnlyContain(x =>
                x.RevokedAt != null && x.RevokedReason == "organization_deactivated");

        var pendingOpen = async () => await verification.Portal.OpenSessionAsync(
            pendingSetupToken,
            new DefaultHttpContext());
        await pendingOpen.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Portal setup token is invalid or expired.");
        var openedRequest = new DefaultHttpContext();
        openedRequest.Request.Headers.Cookie = openedPortalCookie;
        (await verification.Portal.TryGetSessionAsync(openedRequest)).Should().BeNull();

        (await verification.Context.Set<SqlOSAuditEvent>().AnyAsync(x =>
            x.EventType == "sso.portal.sessions.revoked"
            && x.OrganizationId == organizationId
            && x.MetadataJson != null
            && x.MetadataJson.Contains("\"revokedSessions\":3")))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task Signup_Refresh_Logout_RoundTrips()
    {
        var auth = BuildAuthService();
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = "IntegrationTest";

        var signup = await auth.SignUpAsync(new SqlOSSignupRequest(
            "Alice",
            $"alice-{Guid.NewGuid():N}@example.com",
            "P@ssword123!",
            "Acme Corp",
            "test-client",
            null), http);

        signup.Tokens.Should().NotBeNull();
        signup.Tokens!.OrganizationId.Should().NotBeNullOrWhiteSpace();

        var refreshed = await auth.RefreshAsync(new SqlOSRefreshRequest(signup.Tokens.RefreshToken, signup.Tokens.OrganizationId));
        refreshed.AccessToken.Should().NotBeNullOrWhiteSpace();
        refreshed.RefreshToken.Should().NotBe(signup.Tokens.RefreshToken);

        await auth.LogoutAsync(refreshed.RefreshToken, null);

        var act = async () => await auth.RefreshAsync(new SqlOSRefreshRequest(refreshed.RefreshToken, refreshed.OrganizationId));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task Refresh_TwoInstancesRacingOnSameToken_BothSucceed_NoOrphans()
    {
        // The full multi-instance scenario the grace window + concurrency
        // token are designed to fix. Two SqlOSAuthService instances on
        // separate DbContexts race to refresh the same token at the same
        // instant — simulating two app servers behind a load balancer
        // both serving a parallel SSR Promise.all.
        //
        // With EF Core optimistic concurrency on `ConsumedAt`:
        //   - One UPDATE wins the rotation race
        //   - The other(s) get DbUpdateConcurrencyException, fall through
        //     to the grace window path, and return the SAME cached access
        //     token the winner produced
        //   - Exactly ONE replacement refresh token is inserted (no
        //     orphaned siblings polluting the family)
        //
        // Without the concurrency token, both rotations would silently
        // succeed and the family would have duplicate replacements.
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = "ConcurrencyRaceTest";

        // Bootstrap a user and grab a single starting refresh token via
        // the shared context.
        var bootstrapAuth = BuildAuthService();
        var signup = await bootstrapAuth.SignUpAsync(new SqlOSSignupRequest(
            "Erin",
            $"erin-{Guid.NewGuid():N}@example.com",
            "P@ssword123!",
            "Acme Corp",
            "test-client",
            null), http);

        var refreshToken = signup.Tokens!.RefreshToken;
        var orgId = signup.Tokens.OrganizationId;

        // Build TWO completely separate DbContext + service stacks
        // pointing at the same database. This is the key — each has its
        // own change tracker, so the race is genuine, not synthetic.
        var instanceA = BuildIsolatedAuthService();
        var instanceB = BuildIsolatedAuthService();

        // Fire both refresh calls in parallel and wait for both to finish.
        // Use Task.WhenAll to maximize the chance of overlapping inside
        // the SaveChanges window. Re-run a few times if the race doesn't
        // overlap on the first attempt — the test passes if the
        // invariants hold no matter which call wins.
        var task1 = instanceA.Service.RefreshAsync(new SqlOSRefreshRequest(refreshToken, orgId));
        var task2 = instanceB.Service.RefreshAsync(new SqlOSRefreshRequest(refreshToken, orgId));

        var results = await Task.WhenAll(task1, task2);

        // Both calls succeeded.
        results[0].AccessToken.Should().NotBeNullOrWhiteSpace();
        results[1].AccessToken.Should().NotBeNullOrWhiteSpace();

        // Critical invariant: both calls returned the SAME token pair. The
        // winner produced it; the loser hit the grace window path and
        // returned the cached response.
        results[0].AccessToken.Should().Be(results[1].AccessToken,
            "both concurrent refreshes must yield the same access token (winner produces, loser reads from cache)");
        results[0].RefreshToken.Should().Be(results[1].RefreshToken,
            "both app instances must converge on the exact same forward refresh credential");

        // Critical invariant: no orphaned refresh tokens. The family
        // should contain only the original (now consumed) and exactly one
        // replacement. Grace retries are read-only and cannot add siblings.
        instanceA.Dispose();
        instanceB.Dispose();

        var verifyCtx = BuildIsolatedContext();
        try
        {
            // Find the family ID from the original token.
            var crypto = new SqlOSCryptoService(verifyCtx, Microsoft.Extensions.Options.Options.Create(AspireFixture.Options), AspireFixture.DataProtectionProvider);
            var originalHash = crypto.HashToken(refreshToken);
            var original = await verifyCtx.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
                .FirstAsync(x => x.TokenHash == originalHash);
            var familyId = original.FamilyId;

            // Count rows that are direct rotations of the original (i.e.
            // have ReplacedByTokenId pointing AT the new token row, where
            // the new token's ConsumedAt is null and it was created by
            // the rotation flow). These are the rows the rotation race
            // could have multiplied.
            var rotationsFromOriginal = await verifyCtx.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
                .CountAsync(x => x.FamilyId == familyId && x.Id == original.ReplacedByTokenId);

            rotationsFromOriginal.Should().Be(1,
                "exactly ONE rotation replacement should exist for the original token; orphans here would mean the concurrency token failed");
            (await verifyCtx.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
                .CountAsync(x => x.FamilyId == familyId)).Should().Be(2,
                "the grace-window loser must not mint a sibling refresh token");

            // Original must be marked consumed.
            original.ConsumedAt.Should().NotBeNull();
            original.ReplacedByTokenId.Should().NotBeNullOrEmpty();
            original.ReplacementTokenResponse.Should().StartWith("dpt:",
                "the winner must cache the exact token pair under time-limited Data Protection");
        }
        finally
        {
            await verifyCtx.DisposeAsync();
        }
    }

    /// <summary>
    /// Builds an isolated SqlOSAuthService with its own DbContext pointing
    /// at the shared SQL Server. Used to genuinely race two instances
    /// without sharing change-tracker state.
    /// </summary>
    private static (SqlOSAuthService Service, TestSqlOSDbContext Context) BuildIsolatedServiceTuple(
        IDataProtectionProvider? dataProtectionProvider = null,
        IInterceptor? interceptor = null)
    {
        var ctx = BuildIsolatedContext(interceptor);
        var options = Microsoft.Extensions.Options.Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(
            ctx,
            options,
            dataProtectionProvider ?? AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(ctx, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(ctx, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(ctx, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(ctx, options, admin, crypto, settings, emailOtp);
        return (auth, ctx);
    }

    private sealed class IsolatedAuthService : IDisposable
    {
        public SqlOSAuthService Service { get; }
        private readonly TestSqlOSDbContext _context;
        public IsolatedAuthService(SqlOSAuthService service, TestSqlOSDbContext context)
        {
            Service = service;
            _context = context;
        }
        public void Dispose() => _context.Dispose();
    }

    private sealed class IsolatedAuthorizationServer : IDisposable
    {
        public SqlOSAuthorizationServerService Service { get; }
        private readonly TestSqlOSDbContext _context;

        public IsolatedAuthorizationServer(
            SqlOSAuthorizationServerService service,
            TestSqlOSDbContext context)
        {
            Service = service;
            _context = context;
        }

        public void Dispose() => _context.Dispose();
    }

    private static IsolatedAuthService BuildIsolatedAuthService(
        IDataProtectionProvider? dataProtectionProvider = null,
        IInterceptor? interceptor = null)
    {
        var (svc, ctx) = BuildIsolatedServiceTuple(dataProtectionProvider, interceptor);
        return new IsolatedAuthService(svc, ctx);
    }

    private static IsolatedAuthorizationServer BuildIsolatedAuthorizationServer()
    {
        var context = BuildIsolatedContext();
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
        var authorization = new SqlOSAuthorizationServerService(
            context,
            admin,
            auth,
            crypto,
            settings,
            authPageSession,
            options);
        return new IsolatedAuthorizationServer(authorization, context);
    }

    private static TestSqlOSDbContext BuildIsolatedContext(IInterceptor? interceptor = null)
    {
        var builder = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseSqlServer(AspireFixture.SqlConnectionString);
        if (interceptor != null)
        {
            builder.AddInterceptors(interceptor);
        }

        var dbOptions = builder.Options;
        return new TestSqlOSDbContext(dbOptions);
    }

    private static IsolatedLifecycleStack BuildIsolatedLifecycleStack()
    {
        var context = BuildIsolatedContext();
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var authPage = new SqlOSAuthPageSessionService(context, crypto, settings);
        var authorization = new SqlOSAuthorizationServerService(
            context,
            admin,
            auth,
            crypto,
            settings,
            authPage,
            options);
        var domains = new SqlOSOrganizationDomainService(
            context,
            options,
            crypto,
            admin,
            new RejectingDomainDnsVerifier());
        var portal = new SqlOSSsoPortalService(context, options, crypto, admin, domains);
        return new IsolatedLifecycleStack(context, crypto, admin, auth, authPage, authorization, portal);
    }

    private sealed class IsolatedLifecycleStack(
        TestSqlOSDbContext context,
        SqlOSCryptoService crypto,
        SqlOSAdminService admin,
        SqlOSAuthService auth,
        SqlOSAuthPageSessionService authPage,
        SqlOSAuthorizationServerService authorization,
        SqlOSSsoPortalService portal) : IAsyncDisposable
    {
        public TestSqlOSDbContext Context { get; } = context;
        public SqlOSCryptoService Crypto { get; } = crypto;
        public SqlOSAdminService Admin { get; } = admin;
        public SqlOSAuthService Auth { get; } = auth;
        public SqlOSAuthPageSessionService AuthPage { get; } = authPage;
        public SqlOSAuthorizationServerService Authorization { get; } = authorization;
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

    [TestMethod]
    public async Task Refresh_WithSameTokenTwiceWithinGraceWindow_ReturnsSameTokenPair()
    {
        // Issue #18 — proves the grace window survives a real DB round trip.
        // Two refresh calls with the same consumed refresh token, both
        // happening within the default 30s grace window, must return the
        // SAME token pair and must NOT revoke the token family.
        var auth = BuildAuthService();
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = "GraceWindowIntegrationTest";

        var signup = await auth.SignUpAsync(new SqlOSSignupRequest(
            "Carol",
            $"carol-{Guid.NewGuid():N}@example.com",
            "P@ssword123!",
            "Acme Corp",
            "test-client",
            null), http);

        var firstRefresh = await auth.RefreshAsync(
            new SqlOSRefreshRequest(signup.Tokens!.RefreshToken, signup.Tokens.OrganizationId));

        // Replay the SAME original refresh token immediately. This is the
        // canonical "two parallel SSR calls hit refresh at the same instant"
        // scenario the grace window is designed to fix.
        var secondRefresh = await auth.RefreshAsync(
            new SqlOSRefreshRequest(signup.Tokens.RefreshToken, signup.Tokens.OrganizationId));

        secondRefresh.AccessToken.Should().Be(firstRefresh.AccessToken,
            "the grace window should hand back the cached access token");
        secondRefresh.RefreshToken.Should().Be(firstRefresh.RefreshToken,
            "the grace window should hand back the same forward refresh token");

        // The forward refresh token from the first call should still be
        // valid — proving the family was NOT revoked by the replay.
        var thirdRefresh = await auth.RefreshAsync(
            new SqlOSRefreshRequest(firstRefresh.RefreshToken, firstRefresh.OrganizationId));
        thirdRefresh.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task Refresh_GraceWindowAcrossEightInstances_ReturnsSamePairAndOneHead()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = "GraceWindowMultiInstanceTest";
        var bootstrapAuth = BuildAuthService();
        var signup = await bootstrapAuth.SignUpAsync(new SqlOSSignupRequest(
            "Dana",
            $"dana-{Guid.NewGuid():N}@example.com",
            "P@ssword123!",
            "Multi Instance Corp",
            "test-client",
            null), http);

        var originalToken = signup.Tokens!.RefreshToken;
        var organizationId = signup.Tokens.OrganizationId;
        var winner = await bootstrapAuth.RefreshAsync(
            new SqlOSRefreshRequest(originalToken, organizationId));

        for (var instanceNumber = 0; instanceNumber < 8; instanceNumber++)
        {
            using var instance = BuildIsolatedAuthService();
            var retry = await instance.Service.RefreshAsync(
                new SqlOSRefreshRequest(originalToken, organizationId));
            retry.AccessToken.Should().Be(winner.AccessToken);
            retry.RefreshToken.Should().Be(winner.RefreshToken);
        }

        await using var verifyContext = BuildIsolatedContext();
        var crypto = new SqlOSCryptoService(
            verifyContext,
            Options.Create(AspireFixture.Options),
            AspireFixture.DataProtectionProvider);
        var original = await verifyContext.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
            .SingleAsync(x => x.TokenHash == crypto.HashToken(originalToken));
        var family = await verifyContext.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
            .Where(x => x.FamilyId == original.FamilyId)
            .ToListAsync();

        family.Should().HaveCount(2);
        family.Should().ContainSingle(x => x.ConsumedAt == null && x.RevokedAt == null);
        family.Single(x => x.ConsumedAt == null).TokenHash.Should().Be(
            crypto.HashToken(winner.RefreshToken));
    }

    [TestMethod]
    public async Task OAuthTokenEndpoint_RefreshRaceAcrossInstances_ReturnsSamePairAndOneHead()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = "OAuthTokenEndpointRaceTest";
        var bootstrapAuth = BuildAuthService();
        var signup = await bootstrapAuth.SignUpAsync(new SqlOSSignupRequest(
            "Endpoint Race",
            $"endpoint-race-{Guid.NewGuid():N}@example.com",
            "P@ssword123!",
            "Endpoint Race Corp",
            "test-client",
            null), http);

        using var instanceA = BuildIsolatedAuthorizationServer();
        using var instanceB = BuildIsolatedAuthorizationServer();
        var request = new SqlOSTokenRequest(
            SqlOSOAuthGrantTypes.RefreshToken,
            null,
            null,
            null,
            null,
            signup.Tokens!.RefreshToken,
            null);

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskA = Task.Run(async () =>
        {
            await ready.Task;
            return await instanceA.Service.ExchangeAuthorizationCodeAsync(request, new DefaultHttpContext());
        });
        var taskB = Task.Run(async () =>
        {
            await ready.Task;
            return await instanceB.Service.ExchangeAuthorizationCodeAsync(request, new DefaultHttpContext());
        });
        ready.SetResult(true);

        var results = await Task.WhenAll(taskA, taskB);
        results[0].Tokens.AccessToken.Should().Be(results[1].Tokens.AccessToken);
        results[0].Tokens.RefreshToken.Should().Be(results[1].Tokens.RefreshToken);

        await using var verifyContext = BuildIsolatedContext();
        var crypto = new SqlOSCryptoService(
            verifyContext,
            Options.Create(AspireFixture.Options),
            AspireFixture.DataProtectionProvider);
        var original = await verifyContext.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
            .SingleAsync(x => x.TokenHash == crypto.HashToken(signup.Tokens.RefreshToken));
        var family = await verifyContext.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
            .Where(x => x.FamilyId == original.FamilyId)
            .ToListAsync();
        family.Should().HaveCount(2);
        family.Should().ContainSingle(x => x.ConsumedAt == null && x.RevokedAt == null);
    }

    [TestMethod]
    public async Task Refresh_LostDataProtectionKeyRing_RevokesSessionAndEntireFamily()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = "LostDataProtectionKeyTest";
        var bootstrapAuth = BuildAuthService();
        var signup = await bootstrapAuth.SignUpAsync(new SqlOSSignupRequest(
            "Lost Key",
            $"lost-key-{Guid.NewGuid():N}@example.com",
            "P@ssword123!",
            "Lost Key Corp",
            "test-client",
            null), http);

        await bootstrapAuth.RefreshAsync(new SqlOSRefreshRequest(
            signup.Tokens!.RefreshToken,
            signup.Tokens.OrganizationId));

        // A new ephemeral provider models an instance that cannot read the
        // original key ring after key loss or a bad deployment. The grant
        // must fail closed and revoke the complete lineage.
        using var isolated = BuildIsolatedAuthService(new EphemeralDataProtectionProvider());
        var act = async () => await isolated.Service.RefreshAsync(new SqlOSRefreshRequest(
            signup.Tokens.RefreshToken,
            signup.Tokens.OrganizationId));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Refresh token has already been used.");

        await using var verifyContext = BuildIsolatedContext();
        var crypto = new SqlOSCryptoService(
            verifyContext,
            Options.Create(AspireFixture.Options),
            AspireFixture.DataProtectionProvider);
        var original = await verifyContext.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
            .SingleAsync(x => x.TokenHash == crypto.HashToken(signup.Tokens.RefreshToken));
        var session = await verifyContext.Set<SqlOS.AuthServer.Models.SqlOSSession>()
            .SingleAsync(x => x.Id == original.SessionId);
        var family = await verifyContext.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
            .Where(x => x.FamilyId == original.FamilyId)
            .ToListAsync();

        session.RevokedAt.Should().NotBeNull();
        session.RevocationReason.Should().Be("refresh_token_response_invalid");
        family.Should().OnlyContain(x => x.RevokedAt != null);
        family.Should().OnlyContain(x => x.ReplacementTokenResponse == null);
    }

    [TestMethod]
    public async Task Refresh_ReplayRevocationRacingLegitimateDescendant_LeavesNoActiveHead()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = "ReplayRotationRaceTest";
        var bootstrapAuth = BuildAuthService();
        var signup = await bootstrapAuth.SignUpAsync(new SqlOSSignupRequest(
            "Replay Race",
            $"replay-race-{Guid.NewGuid():N}@example.com",
            "P@ssword123!",
            "Replay Race Corp",
            "test-client",
            null), http);

        var r1 = await bootstrapAuth.RefreshAsync(new SqlOSRefreshRequest(
            signup.Tokens!.RefreshToken,
            signup.Tokens.OrganizationId));

        // Force R0 outside grace while R1 remains the legitimate live head.
        await using (var setupContext = BuildIsolatedContext())
        {
            var crypto = new SqlOSCryptoService(
                setupContext,
                Options.Create(AspireFixture.Options),
                AspireFixture.DataProtectionProvider);
            var r0 = await setupContext.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
                .SingleAsync(x => x.TokenHash == crypto.HashToken(signup.Tokens.RefreshToken));
            r0.ConsumedAt = DateTime.UtcNow.AddMinutes(-1);
            await setupContext.SaveChangesAsync();
        }

        var pause = new PauseRefreshRotationInterceptor();
        using var legitimate = BuildIsolatedAuthService(interceptor: pause);
        using var replay = BuildIsolatedAuthService();

        var legitimateGrant = legitimate.Service.RefreshAsync(
            new SqlOSRefreshRequest(r1.RefreshToken, r1.OrganizationId));
        await pause.RotationReached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var replayAct = async () => await replay.Service.RefreshAsync(
            new SqlOSRefreshRequest(signup.Tokens.RefreshToken, signup.Tokens.OrganizationId));
        await replayAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Refresh token has already been used.");

        pause.ReleaseRotation.TrySetResult(true);
        var legitimateAct = async () => await legitimateGrant;
        await legitimateAct.Should().ThrowAsync<InvalidOperationException>();

        await using var verifyContext = BuildIsolatedContext();
        var verifyCrypto = new SqlOSCryptoService(
            verifyContext,
            Options.Create(AspireFixture.Options),
            AspireFixture.DataProtectionProvider);
        var original = await verifyContext.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
            .SingleAsync(x => x.TokenHash == verifyCrypto.HashToken(signup.Tokens.RefreshToken));
        var session = await verifyContext.Set<SqlOS.AuthServer.Models.SqlOSSession>()
            .SingleAsync(x => x.Id == original.SessionId);
        var family = await verifyContext.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
            .Where(x => x.FamilyId == original.FamilyId)
            .ToListAsync();

        session.RevokedAt.Should().NotBeNull();
        session.RevocationReason.Should().Be("refresh_token_reuse");
        family.Should().HaveCount(2, "the rejected legitimate rotation must roll back R2");
        family.Should().OnlyContain(x => x.RevokedAt != null);
        family.Should().NotContain(x => x.ConsumedAt == null && x.RevokedAt == null);
    }

    private sealed class PauseRefreshRotationInterceptor : SaveChangesInterceptor
    {
        private int _hasPaused;

        public TaskCompletionSource<bool> RotationReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseRotation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var context = eventData.Context;
            var isRefreshRotation = context != null
                && context.ChangeTracker.Entries<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
                    .Any(entry => entry.State == EntityState.Added)
                && context.ChangeTracker.Entries<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
                    .Any(entry => entry.State == EntityState.Modified
                        && entry.Property(x => x.ConsumedAt).IsModified
                        && entry.Entity.ConsumedAt != null);

            if (isRefreshRotation && Interlocked.Exchange(ref _hasPaused, 1) == 0)
            {
                RotationReached.TrySetResult(true);
                await ReleaseRotation.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    [TestMethod]
    public async Task Login_WithMultipleOrganizations_ReturnsPendingAuthToken()
    {
        var admin = BuildAdminService();
        var auth = BuildAuthService();
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Bob",
            $"bob-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var org1 = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Org One", null));
        var org2 = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Org Two", null));
        await admin.CreateMembershipAsync(org1.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        await admin.CreateMembershipAsync(org2.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));

        var result = await auth.LoginWithPasswordAsync(new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null), new DefaultHttpContext());
        result.RequiresOrganizationSelection.Should().BeTrue();
        result.PendingAuthToken.Should().NotBeNullOrWhiteSpace();
        result.Organizations.Should().HaveCount(2);
    }

    private static SqlOSAuthService BuildAuthService()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(AspireFixture.SharedContext, admin, crypto, settings, emailSender, options);
        return new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings, emailOtp);
    }

    private static SqlOSAdminService BuildAdminService()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        return new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
    }
}
