using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

/// <summary>
/// Consent gating matrix for <see cref="SqlOSAuthorizationServerService.CompleteAuthorizationRequestLoginAsync"/>
/// plus the approve/deny re-entry paths and the scope display-name catalog.
/// </summary>
[TestClass]
public sealed class SqlOSConsentTests
{
    private const string PkceChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ThirdPartyClientId = "third-party-app";
    private const string ThirdPartyRedirect = "https://third.example.test/callback";

    [TestMethod]
    public async Task FirstPartyClient_NeverRequiresConsent()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("first-party");
        var request = await harness.CreateAuthorizationRequestAsync("test-client", "https://client.example.test/callback", "openid");

        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);

        completion.RequiresConsent.Should().BeFalse();
        completion.ConsentToken.Should().BeNull();
        completion.RedirectUrl.Should().NotBeNull();
        completion.RedirectUrl.Should().Contain("code=");
    }

    [TestMethod]
    public async Task ThirdPartyClient_FirstVisit_RequiresConsentWithTokenAndDisplayScopes()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("third-party-first");
        var request = await harness.CreateThirdPartyRequestAsync("openid todo:read");

        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);

        completion.RequiresConsent.Should().BeTrue();
        completion.ConsentToken.Should().NotBeNullOrWhiteSpace();
        completion.RedirectUrl.Should().BeNull();
        completion.ConsentScopes.Should().NotBeNull();
        completion.ConsentScopes!.Select(x => x.Scope).Should().BeEquivalentTo("openid", "todo:read");
        completion.ConsentScopes.Should().OnlyContain(
            x => x.DisplayName == x.Scope,
            "without catalog entries the display name falls back to the raw scope");
        request.CompletedAt.Should().BeNull("no code may be issued before the user consents");
        (await harness.Context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task ThirdPartyClient_CoveringGrant_CompletesSilently()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("third-party-covered");
        var approved = await harness.RunConsentApprovalAsync(user, "openid todo:read");
        approved.RedirectUrl.Should().Contain("code=");

        var second = await harness.CreateThirdPartyRequestAsync("openid todo:read");
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            second,
            user,
            "password",
            harness.Http);

        completion.RequiresConsent.Should().BeFalse();
        completion.RedirectUrl.Should().Contain("code=");
    }

    [TestMethod]
    public async Task ThirdPartyClient_EscalatedScope_RequiresConsentAgain()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("third-party-escalated");
        await harness.RunConsentApprovalAsync(user, "openid");

        var escalated = await harness.CreateThirdPartyRequestAsync("openid todo:read");
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            escalated,
            user,
            "password",
            harness.Http);

        completion.RequiresConsent.Should().BeTrue();
        completion.ConsentScopes!.Select(x => x.Scope).Should().Contain("todo:read");
    }

    [TestMethod]
    public async Task ThirdPartyClient_PromptConsent_ForcesConsentDespiteCoveringGrant()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("third-party-forced");
        await harness.RunConsentApprovalAsync(user, "openid todo:read");

        var forced = await harness.CreateThirdPartyRequestAsync("openid todo:read", prompt: "consent");
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            forced,
            user,
            "password",
            harness.Http);

        completion.RequiresConsent.Should().BeTrue();
    }

    [TestMethod]
    public async Task DeviceAuthorizationRequests_SkipTheConsentGate()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("third-party-device");
        var request = await harness.CreateThirdPartyRequestAsync("openid");
        request.DeviceAuthorizationId = "dev_gate_test";
        await harness.Context.SaveChangesAsync();

        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);

        completion.RequiresConsent.Should().BeFalse(
            "the device-approval page is its own explicit consent surface");
    }

    [TestMethod]
    public async Task ApproveConsentAsync_WritesUnionGrant_Audits_AndRunsRemainingInterstitials()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("third-party-approve");
        await harness.RunConsentApprovalAsync(user, "openid");
        var escalated = await harness.RunConsentApprovalAsync(user, "openid todo:read");

        escalated.RequiresConsent.Should().BeFalse();
        escalated.RedirectUrl.Should().Contain("code=");
        var client = await harness.Context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == ThirdPartyClientId);
        var grant = await harness.Context.Set<SqlOSConsentGrant>()
            .SingleAsync(x => x.UserId == user.Id && x.ClientApplicationId == client.Id && x.RevokedAt == null);
        SqlOSScopePolicy.Split(grant.Scope).Should().BeEquivalentTo(
            new[] { "openid", "todo:read" },
            "approval stores the union of the existing grant and the newly granted set");
        (await harness.Context.Set<SqlOSAuditEvent>()
            .CountAsync(x => x.EventType == "oauth.consent.granted")).Should().Be(2);
    }

    [TestMethod]
    public async Task DenyConsentAsync_CancelsRequest_AndReturnsAccessDeniedRedirect()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("third-party-deny");
        var request = await harness.CreateThirdPartyRequestAsync("openid todo:read");
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);

        var redirect = await harness.Authorization.DenyConsentAsync(
            completion.ConsentToken!,
            request.Id,
            harness.Http);

        redirect.Should().StartWith($"{ThirdPartyRedirect}?");
        redirect.Should().Contain("error=access_denied");
        request.CancelledAt.Should().NotBeNull();
        (await harness.Context.Set<SqlOSAuditEvent>()
            .AnyAsync(x => x.EventType == "oauth.consent.denied")).Should().BeTrue();
        (await harness.Context.Set<SqlOSConsentGrant>().CountAsync()).Should().Be(0, "a denied request writes no grant");
    }

    [TestMethod]
    public async Task ApproveConsentAsync_TokenBoundToDifferentRequest_IsRejected()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("third-party-binding");
        var first = await harness.CreateThirdPartyRequestAsync("openid");
        var second = await harness.CreateThirdPartyRequestAsync("openid");
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            first,
            user,
            "password",
            harness.Http);

        var act = async () => await harness.Authorization.ApproveConsentAsync(
            completion.ConsentToken!,
            second.Id,
            harness.Http);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not valid for this authorization request*");
        (await harness.Context.Set<SqlOSConsentGrant>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task ApproveConsentAsync_ReplayedToken_IsRejected()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("third-party-replay");
        var request = await harness.CreateThirdPartyRequestAsync("openid");
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);
        await harness.Authorization.ApproveConsentAsync(completion.ConsentToken!, request.Id, harness.Http);

        var replay = async () => await harness.Authorization.ApproveConsentAsync(
            completion.ConsentToken!,
            request.Id,
            harness.Http);

        await replay.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid or expired*");
    }

    [TestMethod]
    public async Task ConsentScopes_UseCatalogDisplayNames_AndFallBackToRawScopes()
    {
        await using var harness = await Harness.CreateAsync(options => options
            .SeedScopeDisplayName("todo:read", "Read your tasks", "See every task on your boards."));
        await harness.Admin.UpsertSeededScopeDisplayNamesAsync();
        var user = await harness.CreateMemberAsync("catalog");
        var request = await harness.CreateThirdPartyRequestAsync("openid todo:read");

        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);

        completion.RequiresConsent.Should().BeTrue();
        var scopes = completion.ConsentScopes!;
        scopes.Single(x => x.Scope == "todo:read").DisplayName.Should().Be("Read your tasks");
        scopes.Single(x => x.Scope == "todo:read").Description.Should().Be("See every task on your boards.");
        scopes.Single(x => x.Scope == "openid").DisplayName.Should().Be("openid");
        scopes.Single(x => x.Scope == "openid").Description.Should().BeNull();
    }

    [TestMethod]
    public async Task UpsertSeededScopeDisplayNames_CreatesUpdatesAndOrphans()
    {
        await using var harness = await Harness.CreateAsync(options => options
            .SeedScopeDisplayName("todo:read", "Read your tasks")
            .SeedScopeDisplayName("todo:write", "Change your tasks"));
        await harness.Admin.UpsertSeededScopeDisplayNamesAsync();

        var created = await harness.Context.Set<SqlOSScopeDisplayName>().ToListAsync();
        created.Should().HaveCount(2);
        created.Should().OnlyContain(x => x.ConfigurationOwner == SqlOSConfigurationOwners.Code);
        created.Should().OnlyContain(x => x.ConfigurationSourceKey == x.Scope);
        created.Should().OnlyContain(x => x.ConfigurationFingerprint != null && x.LastReconciledAt != null);

        harness.AuthOptions.ScopeDisplaySeeds.Clear();
        harness.AuthOptions.SeedScopeDisplayName("todo:read", "Read every task");
        await harness.Admin.UpsertSeededScopeDisplayNamesAsync();

        var updated = await harness.Context.Set<SqlOSScopeDisplayName>().SingleAsync(x => x.Scope == "todo:read");
        updated.DisplayName.Should().Be("Read every task");
        updated.ConfigurationOrphanedAt.Should().BeNull();
        var orphaned = await harness.Context.Set<SqlOSScopeDisplayName>().SingleAsync(x => x.Scope == "todo:write");
        orphaned.ConfigurationOrphanedAt.Should().NotBeNull("removed seeds are orphaned, not deleted");
        (await harness.Context.Set<SqlOSAuditEvent>()
            .AnyAsync(x => x.EventType == "configuration.reconciled"
                && (x.DataJson ?? string.Empty).Contains("scope_display_name"))).Should().BeTrue();
    }

    [TestMethod]
    public async Task UpsertSeededScopeDisplayNames_DeDupsDuplicateSeedScopes_FirstSeedWins()
    {
        // SqlOSOptionsValidator rejects duplicate seeds at AddSqlOS time; the reconciler
        // still de-dups defensively (first seed wins) so a duplicate can never queue two
        // inserts for the unique Scope key in one SaveChanges.
        await using var harness = await Harness.CreateAsync(options => options
            .SeedScopeDisplayName("todo:read", "First seed wins", "First description.")
            .SeedScopeDisplayName("todo:read", "Second seed loses"));

        await harness.Admin.UpsertSeededScopeDisplayNamesAsync();

        var entry = await harness.Context.Set<SqlOSScopeDisplayName>().SingleAsync(x => x.Scope == "todo:read");
        entry.DisplayName.Should().Be("First seed wins");
        entry.Description.Should().Be("First description.");
        entry.ConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Code);
    }

    [TestMethod]
    public async Task UpsertSeededScopeDisplayNames_DoesNotSteallDashboardOwnedRows()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Admin.CreateScopeDisplayNameAsync(
            new SqlOSCreateScopeDisplayNameRequest("todo:read", "Dashboard name"));

        harness.AuthOptions.SeedScopeDisplayName("todo:read", "Code name");
        var act = async () => await harness.Admin.UpsertSeededScopeDisplayNamesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*owned by 'dashboard'*");
        (await harness.Context.Set<SqlOSScopeDisplayName>().SingleAsync(x => x.Scope == "todo:read"))
            .DisplayName.Should().Be("Dashboard name");
    }

    [TestMethod]
    public async Task DashboardScopeDisplayNameEdits_RespectCodeOwnership()
    {
        await using var harness = await Harness.CreateAsync(options => options
            .SeedScopeDisplayName("todo:read", "Read your tasks"));
        await harness.Admin.UpsertSeededScopeDisplayNamesAsync();
        var codeOwned = await harness.Context.Set<SqlOSScopeDisplayName>().SingleAsync(x => x.Scope == "todo:read");

        var update = async () => await harness.Admin.UpdateScopeDisplayNameAsync(
            codeOwned.Id,
            new SqlOSUpdateScopeDisplayNameRequest("Renamed"));
        await update.Should().ThrowAsync<InvalidOperationException>().WithMessage("*owned by the 'code'*");

        var delete = async () => await harness.Admin.DeleteScopeDisplayNameAsync(codeOwned.Id);
        await delete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*owned by the 'code'*");

        var dashboardOwned = await harness.Admin.CreateScopeDisplayNameAsync(
            new SqlOSCreateScopeDisplayNameRequest("todo:write", "Change your tasks"));
        var updatedEntry = await harness.Admin.UpdateScopeDisplayNameAsync(
            dashboardOwned.Id,
            new SqlOSUpdateScopeDisplayNameRequest("Change tasks", "Edit or delete tasks."));
        updatedEntry.DisplayName.Should().Be("Change tasks");
        await harness.Admin.DeleteScopeDisplayNameAsync(dashboardOwned.Id);
        (await harness.Context.Set<SqlOSScopeDisplayName>().AnyAsync(x => x.Scope == "todo:write")).Should().BeFalse();
    }

    [TestMethod]
    public async Task RevokedGrant_RequiresConsentAgain_AndReapprovalStartsFresh()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("third-party-revoked");
        await harness.RunConsentApprovalAsync(user, "openid todo:read");
        var client = await harness.Context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == ThirdPartyClientId);
        var grant = await harness.Context.Set<SqlOSConsentGrant>()
            .SingleAsync(x => x.UserId == user.Id && x.ClientApplicationId == client.Id);
        await harness.Consent.RevokeGrantAsync(user.Id, grant.Id, "user_revoked");

        var completion = await harness.RunConsentApprovalAsync(user, "openid");
        completion.RedirectUrl.Should().Contain("code=");

        var reactivated = await harness.Context.Set<SqlOSConsentGrant>()
            .SingleAsync(x => x.UserId == user.Id && x.ClientApplicationId == client.Id && x.RevokedAt == null);
        SqlOSScopePolicy.Split(reactivated.Scope).Should().BeEquivalentTo(
            new[] { "openid" },
            "a revoked grant does not silently widen a fresh approval");
    }

    [TestMethod]
    public async Task ScopeDisplayNameCrud_RejectsOversizedFields_WithStableMessages()
    {
        await using var harness = await Harness.CreateAsync();

        var overlongScope = async () => await harness.Admin.CreateScopeDisplayNameAsync(
            new SqlOSCreateScopeDisplayNameRequest(new string('s', 201), "Read your tasks"));
        await overlongScope.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Scope cannot exceed 200 characters.");

        var overlongDisplayName = async () => await harness.Admin.CreateScopeDisplayNameAsync(
            new SqlOSCreateScopeDisplayNameRequest("todo:read", new string('d', 201)));
        await overlongDisplayName.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Display name cannot exceed 200 characters.");

        var overlongDescription = async () => await harness.Admin.CreateScopeDisplayNameAsync(
            new SqlOSCreateScopeDisplayNameRequest("todo:read", "Read your tasks", new string('d', 1001)));
        await overlongDescription.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Description cannot exceed 1000 characters.");

        var created = await harness.Admin.CreateScopeDisplayNameAsync(
            new SqlOSCreateScopeDisplayNameRequest("todo:read", "Read your tasks"));
        var overlongUpdate = async () => await harness.Admin.UpdateScopeDisplayNameAsync(
            created.Id,
            new SqlOSUpdateScopeDisplayNameRequest(new string('d', 201)));
        await overlongUpdate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Display name cannot exceed 200 characters.");

        (await harness.Context.Set<SqlOSScopeDisplayName>().CountAsync()).Should().Be(
            1,
            "rejected creates must not persist catalog rows");
    }

    [TestMethod]
    public async Task RevokeGrantAsync_AlreadyRevokedGrant_SurfacesStableAlreadyRevokedFailure()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("double-revoke");
        await harness.RunConsentApprovalAsync(user, "openid");
        var grant = await harness.Context.Set<SqlOSConsentGrant>().SingleAsync(x => x.UserId == user.Id);
        await harness.Consent.RevokeGrantAsync(user.Id, grant.Id, "user_revoked");

        // The same typed failure covers the concurrent case: the losing revoker's
        // concurrency conflict reloads the row and re-lands here.
        var again = async () => await harness.Consent.RevokeGrantAsync(user.Id, grant.Id, "user_revoked");

        await again.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Consent grant is already revoked.");
    }

    [TestMethod]
    public void EnsureUnionWithinLimits_GuardsTheGrantScopeColumnLength()
    {
        // 39 scopes of 100 chars join to 3,938 chars — inside the 4,000-char column.
        var existing = SqlOSScopePolicy.Join(Enumerable.Range(0, 39).Select(HundredCharScope));

        var withinLimit = () => SqlOSConsentService.EnsureUnionWithinLimits(existing, new[] { "openid" });
        withinLimit.Should().NotThrow();

        var overflow = () => SqlOSConsentService.EnsureUnionWithinLimits(existing, new[] { HundredCharScope(39) });
        overflow.Should().Throw<InvalidOperationException>()
            .WithMessage("The combined consent grant scope cannot exceed 4000 characters.");
    }

    [TestMethod]
    public async Task UpsertGrantAsync_UnionBeyondColumnLength_FailsDeterministically_AndLeavesTheGrantUnchanged()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("union-overflow");
        var client = await harness.Context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == ThirdPartyClientId);
        var existingScopes = Enumerable.Range(0, 39).Select(HundredCharScope).ToList();
        await harness.Consent.UpsertGrantAsync(user.Id, client.Id, existingScopes);

        var overflow = async () => await harness.Consent.UpsertGrantAsync(
            user.Id,
            client.Id,
            new[] { HundredCharScope(39) });

        await overflow.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The combined consent grant scope cannot exceed 4000 characters.");
        var grant = await harness.Context.Set<SqlOSConsentGrant>()
            .SingleAsync(x => x.UserId == user.Id && x.ClientApplicationId == client.Id && x.RevokedAt == null);
        SqlOSScopePolicy.Split(grant.Scope).Should().BeEquivalentTo(
            existingScopes,
            "a rejected union must not partially widen the stored grant");
    }

    [TestMethod]
    public async Task ConsentReloadSessionFallback_DifferentUsersSession_ReturnsNoToken()
    {
        await using var harness = await Harness.CreateAsync();
        var owner = await harness.CreateMemberAsync("reload-owner");
        var switcher = await harness.CreateMemberAsync("reload-switcher");
        var request = await harness.CreateThirdPartyRequestAsync("openid todo:read");
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            owner,
            "password",
            harness.Http);
        completion.RequiresConsent.Should().BeTrue();
        request.PendingConsentUserId.Should().Be(
            owner.Id,
            "the consent gate persists the user it showed the interstitial to");

        var mintedForSwitcher = await harness.Authorization.TryCreateConsentTokenForRequestReloadAsync(
            request,
            await harness.CreateSessionHttpContextAsync(switcher));
        mintedForSwitcher.Should().BeNull(
            "a reload must not re-bind pending consent to whichever account the browser cookie now carries");

        var mintedForOwner = await harness.Authorization.TryCreateConsentTokenForRequestReloadAsync(
            request,
            await harness.CreateSessionHttpContextAsync(owner));
        mintedForOwner.Should().NotBeNullOrWhiteSpace(
            "the user who actually reached consent must still be able to reload the view");
    }

    [TestMethod]
    public async Task ApproveConsentAsync_TokenUserMismatchesPendingConsentBinding_IsRejected()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("binding-mismatch");
        var request = await harness.CreateThirdPartyRequestAsync("openid");
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);
        request.PendingConsentUserId = "usr_someone_else";
        await harness.Context.SaveChangesAsync();

        var act = async () => await harness.Authorization.ApproveConsentAsync(
            completion.ConsentToken!,
            request.Id,
            harness.Http);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The consent session is not valid for this user.");
        (await harness.Context.Set<SqlOSConsentGrant>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task ApproveConsentAsync_ClientMetadataChangedSinceMint_IsRejected()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("stale-metadata");
        var request = await harness.CreateThirdPartyRequestAsync("openid todo:read");
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);
        completion.RequiresConsent.Should().BeTrue();

        var client = await harness.Context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == ThirdPartyClientId);
        client.RedirectUrisJson = $"[\"{ThirdPartyRedirect}\",\"https://attacker.example.test/callback\"]";
        await harness.Context.SaveChangesAsync();

        var act = async () => await harness.Authorization.ApproveConsentAsync(
            completion.ConsentToken!,
            request.Id,
            harness.Http);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The application's registration changed while consent was pending. Start the request again.");
        (await harness.Context.Set<SqlOSConsentGrant>().CountAsync()).Should().Be(
            0,
            "an approval of a stale consent screen must not persist a grant");
        (await harness.Context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task ApproveConsentAsync_LegacyTokenWithoutFingerprint_IsStillAccepted()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("legacy-payload");
        var request = await harness.CreateThirdPartyRequestAsync("openid");
        // A token minted mid-deploy carries neither AuthenticatedAt nor
        // ClientMetadataFingerprint; the 10-minute token lifetime bounds the exposure.
        var legacyToken = await harness.Crypto.CreateTemporaryTokenAsync(
            SqlOSAuthorizationServerService.ConsentTokenPurpose,
            user.Id,
            request.ClientApplicationId,
            null,
            new { AuthorizationRequestId = request.Id, AuthenticationMethod = "password" },
            TimeSpan.FromMinutes(10));

        var approved = await harness.Authorization.ApproveConsentAsync(legacyToken, request.Id, harness.Http);

        approved.RedirectUrl.Should().Contain("code=");
    }

    [TestMethod]
    public async Task ApproveConsentAsync_PreservesKnownAuthenticatedAt_OnTheIssuedCode()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("auth-time");
        var request = await harness.CreateThirdPartyRequestAsync("openid todo:read");
        var authenticatedAt = DateTime.UtcNow.AddMinutes(-42);

        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "saml",
            harness.Http,
            knownAuthenticatedAt: authenticatedAt);
        completion.RequiresConsent.Should().BeTrue();

        var approved = await harness.Authorization.ApproveConsentAsync(
            completion.ConsentToken!,
            request.Id,
            harness.Http);

        approved.RedirectUrl.Should().Contain("code=");
        var code = await harness.Context.Set<SqlOSAuthorizationCode>()
            .SingleAsync(x => x.AuthorizationRequestId == request.Id);
        code.AuthTime.Should().NotBeNull();
        code.AuthTime!.Value.Should().BeCloseTo(
            authenticatedAt,
            TimeSpan.FromSeconds(1),
            "the consent interstitial must not inflate auth_time to the approval click");
    }

    [TestMethod]
    public async Task ApproveConsentAsync_OversizedScopeUnion_RejectsBeforeConsumingTheToken()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("union-precheck");
        var client = await harness.Context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == ThirdPartyClientId);
        var existingScopes = Enumerable.Range(0, 39).Select(HundredCharScope).ToList();
        await harness.Consent.UpsertGrantAsync(user.Id, client.Id, existingScopes);
        client.AllowedScopesJson = System.Text.Json.JsonSerializer.Serialize(
            new[] { "openid", HundredCharScope(39) });
        await harness.Context.SaveChangesAsync();

        var request = await harness.CreateThirdPartyRequestAsync(HundredCharScope(39));
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);
        completion.RequiresConsent.Should().BeTrue();

        var overflow = async () => await harness.Authorization.ApproveConsentAsync(
            completion.ConsentToken!,
            request.Id,
            harness.Http);
        await overflow.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The combined consent grant scope cannot exceed 4000 characters.");

        var tokenRow = await harness.Context.Set<SqlOSTemporaryToken>()
            .SingleAsync(x => x.Purpose == SqlOSAuthorizationServerService.ConsentTokenPurpose);
        tokenRow.ConsumedAt.Should().BeNull("the overflow precheck must fire before the one-time token is consumed");

        // Once the stored grant no longer overflows, the SAME token still approves.
        var grant = await harness.Context.Set<SqlOSConsentGrant>()
            .SingleAsync(x => x.UserId == user.Id && x.RevokedAt == null);
        await harness.Consent.RevokeGrantAsync(user.Id, grant.Id, "user_revoked");
        var approved = await harness.Authorization.ApproveConsentAsync(
            completion.ConsentToken!,
            request.Id,
            harness.Http);
        approved.RedirectUrl.Should().Contain("code=");
    }

    [TestMethod]
    public async Task SilentSsoConsentGate_PreservesSessionAuthenticatedAt_OnTheIssuedCode()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("silent-sso-auth-time");
        var sessionAuthenticatedAt = DateTime.UtcNow.AddMinutes(-42);
        var sessionHttp = await harness.CreateSessionHttpContextAsync(user, sessionAuthenticatedAt);
        var request = await harness.CreateThirdPartyRequestAsync("openid todo:read");

        // Silent SSO: the browser reaches consent through an existing auth-page session, so
        // the gate must stamp the ORIGINAL sign-in moment into the pending consent token.
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            sessionHttp);
        completion.RequiresConsent.Should().BeTrue();

        var approved = await harness.Authorization.ApproveConsentAsync(
            completion.ConsentToken!,
            request.Id,
            harness.Http);

        approved.RedirectUrl.Should().Contain("code=");
        var code = await harness.Context.Set<SqlOSAuthorizationCode>()
            .SingleAsync(x => x.AuthorizationRequestId == request.Id);
        code.AuthTime.Should().NotBeNull();
        code.AuthTime!.Value.Should().BeCloseTo(
            sessionAuthenticatedAt,
            TimeSpan.FromSeconds(1),
            "consent reached through silent SSO must keep the session cookie's authentication moment");
    }

    [TestMethod]
    public async Task PasswordFlowConsent_ApprovalAfterDelay_StampsGateTime_NotApprovalTime()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("password-auth-time");
        var request = await harness.CreateThirdPartyRequestAsync("openid");

        // Local password/OTP/signup callers supply no knownAuthenticatedAt; the gate runs
        // immediately after credential verification, so the gate moment IS the
        // authentication moment and must be stamped into the pending consent token.
        var gateTime = DateTime.UtcNow;
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);
        completion.RequiresConsent.Should().BeTrue();

        // The user parks on the consent screen before approving.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var approved = await harness.Authorization.ApproveConsentAsync(
            completion.ConsentToken!,
            request.Id,
            harness.Http);
        var approvalTime = DateTime.UtcNow;

        approved.RedirectUrl.Should().Contain("code=");
        var code = await harness.Context.Set<SqlOSAuthorizationCode>()
            .SingleAsync(x => x.AuthorizationRequestId == request.Id);
        code.AuthTime.Should().NotBeNull();
        code.AuthTime!.Value.Should().BeCloseTo(
            gateTime,
            TimeSpan.FromSeconds(1),
            "the consent gate stamps the credential-verification moment");
        (approvalTime - code.AuthTime.Value).Should().BeGreaterThan(
            TimeSpan.FromSeconds(1.5),
            "approval after a delay must not substitute the approval click for the authentication time");
    }

    [TestMethod]
    public async Task CoveringGrant_StampedAgainstStaleMetadata_ForcesConsentReprompt()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("stale-grant");
        await harness.RunConsentApprovalAsync(user, "openid");
        var client = await harness.Context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == ThirdPartyClientId);
        var grant = await harness.Context.Set<SqlOSConsentGrant>()
            .SingleAsync(x => x.UserId == user.Id && x.ClientApplicationId == client.Id && x.RevokedAt == null);
        grant.ClientMetadataFingerprint.Should().NotBeNullOrWhiteSpace(
            "approval stamps the metadata fingerprint the grant was approved against");

        // The client's security-sensitive metadata changes AFTER the grant was written
        // (modeling a CIMD refresh that raced the approval, so the tripwire never saw it).
        client.RedirectUrisJson = $"[\"{ThirdPartyRedirect}\",\"https://attacker.example.test/callback\"]";
        await harness.Context.SaveChangesAsync();

        var request = await harness.CreateThirdPartyRequestAsync("openid");
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);

        completion.RequiresConsent.Should().BeTrue(
            "a grant approved against stale metadata is never silently reused; the user re-consents");

        // Legacy grants written before fingerprint stamping carry null and stay accepted.
        grant.ClientMetadataFingerprint = null;
        await harness.Context.SaveChangesAsync();
        var legacyRequest = await harness.CreateThirdPartyRequestAsync("openid");
        var legacyCompletion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            legacyRequest,
            user,
            "password",
            harness.Http);
        legacyCompletion.RequiresConsent.Should().BeFalse("null fingerprints are legacy data and remain covering");
    }

    [TestMethod]
    public async Task DenyConsentAsync_TokenBoundToDifferentRequest_IsRejected_WithoutConsumingTheToken()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("deny-binding");
        var first = await harness.CreateThirdPartyRequestAsync("openid");
        var second = await harness.CreateThirdPartyRequestAsync("openid");
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            first,
            user,
            "password",
            harness.Http);

        var act = async () => await harness.Authorization.DenyConsentAsync(
            completion.ConsentToken!,
            second.Id,
            harness.Http);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not valid for this authorization request*");
        var tokenRow = await harness.Context.Set<SqlOSTemporaryToken>()
            .SingleAsync(x => x.Purpose == SqlOSAuthorizationServerService.ConsentTokenPurpose);
        tokenRow.ConsumedAt.Should().BeNull(
            "a binding failure must not burn the flow's only approval/denial credential");

        // The same token still denies its own request afterwards.
        var redirect = await harness.Authorization.DenyConsentAsync(
            completion.ConsentToken!,
            first.Id,
            harness.Http);
        redirect.Should().Contain("error=access_denied");
    }

    [TestMethod]
    public async Task OrphanedScopeDisplayEntry_FallsBackToRawScope_OnConsent()
    {
        await using var harness = await Harness.CreateAsync(options => options
            .SeedScopeDisplayName("todo:read", "Read your tasks", "See every task on your boards."));
        await harness.Admin.UpsertSeededScopeDisplayNamesAsync();

        harness.AuthOptions.ScopeDisplaySeeds.Clear();
        await harness.Admin.UpsertSeededScopeDisplayNamesAsync();
        (await harness.Context.Set<SqlOSScopeDisplayName>().SingleAsync(x => x.Scope == "todo:read"))
            .ConfigurationOrphanedAt.Should().NotBeNull();

        var user = await harness.CreateMemberAsync("orphan-fallback");
        var request = await harness.CreateThirdPartyRequestAsync("openid todo:read");
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);

        completion.RequiresConsent.Should().BeTrue();
        var display = completion.ConsentScopes!.Single(x => x.Scope == "todo:read");
        display.DisplayName.Should().Be(
            "todo:read",
            "an orphaned entry's stale text must not keep rendering; the raw scope is shown instead");
        display.Description.Should().BeNull();
    }

    [TestMethod]
    public async Task DeleteScopeDisplayNameAsync_AllowsOrphanedCodeOwnedRows_AndKeepsRejectingLiveOnes()
    {
        await using var harness = await Harness.CreateAsync(options => options
            .SeedScopeDisplayName("todo:read", "Read your tasks"));
        await harness.Admin.UpsertSeededScopeDisplayNamesAsync();
        var entry = await harness.Context.Set<SqlOSScopeDisplayName>().SingleAsync(x => x.Scope == "todo:read");

        var deleteLive = async () => await harness.Admin.DeleteScopeDisplayNameAsync(entry.Id);
        await deleteLive.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*owned by the 'code'*");

        harness.AuthOptions.ScopeDisplaySeeds.Clear();
        await harness.Admin.UpsertSeededScopeDisplayNamesAsync();
        entry.ConfigurationOrphanedAt.Should().NotBeNull();

        await harness.Admin.DeleteScopeDisplayNameAsync(entry.Id);
        (await harness.Context.Set<SqlOSScopeDisplayName>().AnyAsync(x => x.Scope == "todo:read"))
            .Should().BeFalse("an orphaned code-owned row has no source-control home left; deleting it is the supported cleanup");
        (await harness.Context.Set<SqlOSAuditEvent>()
            .AnyAsync(x => x.EventType == "scope_display_name.deleted")).Should().BeTrue();
    }

    private static string HundredCharScope(int index) => $"{new string('s', 96)}{index:D4}";

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            TestSqlOSInMemoryDbContext context,
            SqlOSAuthServerOptions authOptions,
            SqlOSAuthorizationServerService authorization,
            SqlOSConsentService consent,
            SqlOSAdminService admin,
            SqlOSCryptoService crypto,
            DefaultHttpContext http)
        {
            Context = context;
            AuthOptions = authOptions;
            Authorization = authorization;
            Consent = consent;
            Admin = admin;
            Crypto = crypto;
            Http = http;
        }

        public TestSqlOSInMemoryDbContext Context { get; }
        public SqlOSAuthServerOptions AuthOptions { get; }
        public SqlOSAuthorizationServerService Authorization { get; }
        public SqlOSConsentService Consent { get; }
        public SqlOSAdminService Admin { get; }
        public SqlOSCryptoService Crypto { get; }
        public DefaultHttpContext Http { get; }
        public string OrganizationId { get; private set; } = null!;

        public static async Task<Harness> CreateAsync(Action<SqlOSAuthServerOptions>? configure = null)
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);
            var authOptions = new SqlOSAuthServerOptions
            {
                PublicOrigin = "https://auth.example.test",
                Issuer = "https://auth.example.test/sqlos/auth"
            };
            authOptions.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
            authOptions.SeedClient(client =>
            {
                client.ClientId = ThirdPartyClientId;
                client.Name = "Third Party App";
                client.RedirectUris = [ThirdPartyRedirect];
                client.ClientType = "public_pkce";
                client.RequirePkce = true;
                client.IsFirstParty = false;
                client.AllowedScopes = ["openid", "todo:read", "todo:write"];
            });
            configure?.Invoke(authOptions);
            var options = Options.Create(authOptions);
            var crypto = TestCryptoService.Create(context, options, new EphemeralDataProtectionProvider());
            var admin = new SqlOSAdminService(context, options, crypto);
            var emailSender = new TestAuthEmailSender { IsConfigured = true };
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
            var authPage = new SqlOSAuthPageSessionService(context, crypto, settings);
            var consent = new SqlOSConsentService(context, crypto);
            var authorization = new SqlOSAuthorizationServerService(
                context,
                admin,
                auth,
                crypto,
                settings,
                authPage,
                options,
                consentService: consent);
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("auth.example.test");
            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.EnsureDefaultSettingsAsync();
            await settings.EnsureDefaultAuthPageSettingsAsync();
            await settings.EnsureDefaultMfaSettingsAsync();
            var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Consent Org", null));

            return new Harness(context, authOptions, authorization, consent, admin, crypto, http)
            {
                OrganizationId = organization.Id
            };
        }

        /// <summary>
        /// Mints a live auth-page session cookie for the user (mirroring
        /// SqlOSAuthPageSessionService.SignInAsync) and returns an HttpContext carrying it,
        /// so reload paths observe the browser's current session.
        /// </summary>
        public async Task<DefaultHttpContext> CreateSessionHttpContextAsync(SqlOSUser user, DateTime? authenticatedAt = null)
        {
            var rawCookie = await Crypto.CreateTemporaryTokenAsync(
                "auth_page_session",
                user.Id,
                clientApplicationId: null,
                organizationId: OrganizationId,
                payload: new { AuthenticationMethod = "password", AuthenticatedAt = authenticatedAt ?? DateTime.UtcNow },
                lifetime: TimeSpan.FromMinutes(30));
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("auth.example.test");
            http.Request.Headers.Cookie = $"sqlos_auth_page={rawCookie}";
            return http;
        }

        public async Task<SqlOSUser> CreateMemberAsync(string prefix)
        {
            var user = await Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                $"{prefix} user",
                $"{prefix}-{Guid.NewGuid():N}@example.test",
                "P@ssword123!"));
            await Admin.CreateMembershipAsync(OrganizationId, new SqlOSCreateMembershipRequest(user.Id, "member"));
            return user;
        }

        public Task<SqlOSAuthorizationRequest> CreateThirdPartyRequestAsync(string scope, string? prompt = null)
            => CreateAuthorizationRequestAsync(ThirdPartyClientId, ThirdPartyRedirect, scope, prompt);

        public async Task<SqlOSAuthorizationRequest> CreateAuthorizationRequestAsync(
            string clientId,
            string redirectUri,
            string scope,
            string? prompt = null)
            => await Authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
                "code",
                clientId,
                redirectUri,
                Guid.NewGuid().ToString("N"),
                scope,
                PkceChallenge,
                "S256",
                null,
                null,
                prompt,
                null,
                "hosted",
                null));

        /// <summary>
        /// Drives a full third-party authorization through the consent interstitial: completes
        /// the login, and when consent is required, approves it and returns the re-entered
        /// completion (which must have run the remaining interstitials).
        /// </summary>
        public async Task<SqlOSAuthorizationRequestLoginResult> RunConsentApprovalAsync(SqlOSUser user, string scope)
        {
            var request = await CreateThirdPartyRequestAsync(scope);
            var completion = await Authorization.CompleteAuthorizationRequestLoginAsync(
                request,
                user,
                "password",
                Http);
            if (!completion.RequiresConsent)
            {
                return completion;
            }

            return await Authorization.ApproveConsentAsync(completion.ConsentToken!, request.Id, Http);
        }

        public async ValueTask DisposeAsync() => await Context.DisposeAsync();
    }
}
