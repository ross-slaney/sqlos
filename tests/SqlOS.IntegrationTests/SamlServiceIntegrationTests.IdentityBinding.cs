using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Domain;

namespace SqlOS.IntegrationTests;

public sealed partial class SamlServiceIntegrationTests
{
    [TestMethod]
    public async Task AttackerSamlConnection_AssertingVictimEmail_LeavesNoIdentityArtifacts()
    {
        var (_, admin, saml) = CreateSamlServices();
        var victimEmail = $"victim-{Guid.NewGuid():N}@company.example";
        var attackerSubject = $"attacker-subject-{Guid.NewGuid():N}";
        var victim = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Victim", victimEmail, "P@ssword123!"));
        await MarkEmailVerifiedAsync(victim.Id);
        var victimOrg = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Victim Org {Guid.NewGuid():N}", null));
        var attackerOrg = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Attacker Org {Guid.NewGuid():N}", null));
        await AddMembershipAsync(victimOrg.Id, victim.Id, isActive: true);
        await AddMembershipAsync(attackerOrg.Id, victim.Id, isActive: true);
        await AddScimUserLinkAsync(admin, attackerOrg.Id, victim.Id, victimEmail);
        var client = await CreateSamlClientAsync(admin, "cross-tenant");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSAttackerSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreatePolicySamlConnectionAsync(
            admin,
            attackerOrg.Id,
            certificate,
            "cross-tenant",
            autoProvisionUsers: true,
            autoLinkByEmail: true);

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(
            certificate,
            connection.IdentityProviderEntityId,
            victimEmail,
            "Attacker",
            "Admin",
            flow,
            nameId: attackerSubject);
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        await AssertNoFirstLinkSideEffectsAsync(
            AspireFixture.SharedContext,
            attackerOrg.Id,
            victim.Id,
            connection.Id,
            attackerSubject,
            victimEmail,
            flow.RelayState,
            emailWasVerified: true,
            expectExistingMembership: true);
        (await AspireFixture.SharedContext.Set<SqlOSMembership>()
                .CountAsync(x => x.OrganizationId == victimOrg.Id && x.UserId == victim.Id))
            .Should().Be(1);
    }

    [DataTestMethod]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public async Task AttackerSamlConnection_PolicyAndVerificationMatrix_StillCannotTakeOverByEmail(
        bool autoProvisionUsers,
        bool autoLinkByEmail,
        bool victimEmailVerified)
    {
        var (_, admin, saml) = CreateSamlServices();
        var victimEmail = $"matrix-{Guid.NewGuid():N}@company.example";
        var attackerSubject = $"matrix-subject-{Guid.NewGuid():N}";
        var victim = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Matrix Victim", victimEmail, "P@ssword123!"));
        if (victimEmailVerified)
        {
            await MarkEmailVerifiedAsync(victim.Id);
        }

        var attackerOrg = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Matrix Org {Guid.NewGuid():N}", null));
        await AddMembershipAsync(attackerOrg.Id, victim.Id, isActive: true);
        var client = await CreateSamlClientAsync(admin, "matrix");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSMatrixSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreatePolicySamlConnectionAsync(
            admin,
            attackerOrg.Id,
            certificate,
            "matrix",
            autoProvisionUsers,
            autoLinkByEmail);

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(
            certificate,
            connection.IdentityProviderEntityId,
            victimEmail,
            "Matrix",
            "Attack",
            flow,
            nameId: attackerSubject);
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        await AssertNoFirstLinkSideEffectsAsync(
            AspireFixture.SharedContext,
            attackerOrg.Id,
            victim.Id,
            connection.Id,
            attackerSubject,
            victimEmail,
            flow.RelayState,
            emailWasVerified: victimEmailVerified,
            expectExistingMembership: true);
    }

    [TestMethod]
    public async Task SignedSamlResponse_AlreadyBoundSubject_ResolvesWithoutOwnedDomain()
    {
        var (_, admin, saml) = CreateSamlServices();
        var email = $"bound-{Guid.NewGuid():N}@example.com";
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Bound User", email, "P@ssword123!"));
        await MarkEmailVerifiedAsync(user.Id);
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Bound {Guid.NewGuid():N}", null));
        await AddMembershipAsync(org.Id, user.Id, isActive: true);
        var client = await CreateSamlClientAsync(admin, "bound-subject");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSBoundSubjectIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreateRestrictedSamlConnectionAsync(admin, org.Id, certificate, "bound-subject");
        var subject = $"idp-user-{Guid.NewGuid():N}";
        AspireFixture.SharedContext.Set<SqlOSExternalIdentity>().Add(TestRows.Create<SqlOSExternalIdentity>(new
        {
            Id = $"ext_{Guid.NewGuid():N}",
            UserId = user.Id,
            SsoConnectionId = connection.Id,
            Issuer = connection.IdentityProviderEntityId,
            Subject = subject,
            Email = email,
            CreatedAt = DateTime.UtcNow
        }));
        await AspireFixture.SharedContext.SaveChangesAsync();

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(
            certificate,
            connection.IdentityProviderEntityId,
            $"other-{Guid.NewGuid():N}@unrelated.example",
            "Bound",
            "User",
            flow,
            nameId: subject);
        var redirectUrl = await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        redirectUrl.Should().StartWith("https://client.example.local/callback?code=");
        var authorizationCode = await AspireFixture.SharedContext.Set<SqlOSAuthorizationCode>()
            .SingleAsync(x => x.AuthorizationRequestId == flow.RelayState);
        authorizationCode.UserId.Should().Be(user.Id);
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
                .CountAsync(x => x.SsoConnectionId == connection.Id && x.UserId == user.Id))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithVerifiedOrganizationDomain_LinksExistingMember()
    {
        var (crypto, admin, saml) = CreateSamlServices();
        var domain = $"{Guid.NewGuid():N}.acme.test";
        var email = $"alice@{domain}";
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Alice Acme", email, "P@ssword123!"));
        await MarkEmailVerifiedAsync(user.Id);
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Acme {Guid.NewGuid():N}", null));
        await AddMembershipAsync(org.Id, user.Id, isActive: true);
        await AddOrganizationDomainAsync(crypto, org.Id, domain);
        var client = await CreateSamlClientAsync(admin, "verified-domain");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSVerifiedDomainIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreateRestrictedSamlConnectionAsync(admin, org.Id, certificate, "verified-domain");

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, email, "Alice", "Acme", flow);
        var redirectUrl = await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        redirectUrl.Should().StartWith("https://client.example.local/callback?code=");
        var identity = await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
            .SingleAsync(x => x.SsoConnectionId == connection.Id && x.Subject == email);
        identity.UserId.Should().Be(user.Id);
        (await AspireFixture.SharedContext.Set<SqlOSMembership>()
                .CountAsync(x => x.OrganizationId == org.Id && x.UserId == user.Id))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithOperatorPrimaryDomain_LinksExistingUserWhenAutoProvisioning()
    {
        var (_, admin, saml) = CreateSamlServices();
        var domain = $"{Guid.NewGuid():N}.primary.test";
        var email = $"bob@{domain}";
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Bob Primary", email, "P@ssword123!"));
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(
            $"Primary {Guid.NewGuid():N}",
            null,
            domain));
        var client = await CreateSamlClientAsync(admin, "primary-domain");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSPrimaryDomainIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreatePolicySamlConnectionAsync(
            admin,
            org.Id,
            certificate,
            "primary-domain",
            autoProvisionUsers: true,
            autoLinkByEmail: false);

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, email, "Bob", "Primary", flow);
        var redirectUrl = await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        redirectUrl.Should().StartWith("https://client.example.local/callback?code=");
        var storedEmail = await AspireFixture.SharedContext.Set<SqlOSUserEmail>()
            .SingleAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(email));
        storedEmail.UserId.Should().Be(user.Id);
        storedEmail.IsVerified.Should().BeTrue();
        (await AspireFixture.SharedContext.Set<SqlOSMembership>()
                .AnyAsync(x => x.OrganizationId == org.Id && x.UserId == user.Id && x.IsActive))
            .Should().BeTrue();
        var identity = await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
            .SingleAsync(x => x.SsoConnectionId == connection.Id && x.Subject == email);
        identity.UserId.Should().Be(user.Id);
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithPendingOrganizationDomain_DoesNotLinkExistingUser()
    {
        var (crypto, admin, saml) = CreateSamlServices();
        var domain = $"{Guid.NewGuid():N}.pending.test";
        var email = $"pending@{domain}";
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Pending Domain", email, "P@ssword123!"));
        await MarkEmailVerifiedAsync(user.Id);
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Pending {Guid.NewGuid():N}", null));
        await AddMembershipAsync(org.Id, user.Id, isActive: true);
        await AddOrganizationDomainAsync(crypto, org.Id, domain, SqlOSOrganizationDomainStatuses.PendingOwnership);
        var client = await CreateSamlClientAsync(admin, "pending-domain");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSPendingDomainIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreatePolicySamlConnectionAsync(
            admin,
            org.Id,
            certificate,
            "pending-domain",
            autoProvisionUsers: true,
            autoLinkByEmail: true);

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, email, "Pending", "Domain", flow);
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        await AssertNoFirstLinkSideEffectsAsync(
            AspireFixture.SharedContext,
            org.Id,
            user.Id,
            connection.Id,
            email,
            email,
            flow.RelayState,
            emailWasVerified: true,
            expectExistingMembership: true);
    }

    [TestMethod]
    public async Task ConcurrentFirstLink_SameSubjectWithOwnedDomain_BindsOneIdentity()
    {
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("SamlBindSame");
        var connectionString = setup.Database.GetConnectionString();
        connectionString.Should().NotBeNullOrWhiteSpace();
        var options = Options.Create(AspireFixture.Options);
        var setupStack = CreateIsolatedSamlStack(setup, options);
        await setupStack.Crypto.EnsureActiveSigningKeyAsync();
        await EnsureIsolatedAuthDefaultsAsync(setup, options);

        var domain = $"{Guid.NewGuid():N}.concurrent.test";
        var email = $"same@{domain}";
        var user = await setupStack.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Same Subject", email, "P@ssword123!"));
        await MarkEmailVerifiedAsync(setup, user.Id);
        var org = await setupStack.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Same Subject Org", null, domain));
        await AddMembershipAsync(setup, org.Id, user.Id, isActive: true);
        var client = await CreateSamlClientAsync(setupStack.Admin, "same-subject");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSSameSubjectIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreatePolicySamlConnectionAsync(
            setupStack.Admin,
            org.Id,
            certificate,
            "same-subject",
            autoProvisionUsers: true,
            autoLinkByEmail: true);

        var leftContext = CreateContext(connectionString!);
        var rightContext = CreateContext(connectionString!);
        try
        {
            var left = CreateIsolatedSamlStack(leftContext, options);
            var right = CreateIsolatedSamlStack(rightContext, options);
            var leftFlow = await StartSamlRequestAsync(left.Saml, connection.Id, client.ClientId, leftContext);
            var rightFlow = await StartSamlRequestAsync(right.Saml, connection.Id, client.ClientId, rightContext);
            var leftResponse = BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, email, "Same", "Left", leftFlow);
            var rightResponse = BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, email, "Same", "Right", rightFlow);

            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new[]
            {
                Task.Run(async () =>
                {
                    await ready.Task;
                    return await CaptureAcsAsync(left.Saml, connection.Id, leftResponse, leftFlow.RelayState);
                }),
                Task.Run(async () =>
                {
                    await ready.Task;
                    return await CaptureAcsAsync(right.Saml, connection.Id, rightResponse, rightFlow.RelayState);
                })
            };
            ready.SetResult(true);
            var outcomes = await Task.WhenAll(tasks);

            outcomes.Count(result => result.Redirect != null).Should().BeGreaterThanOrEqualTo(1);
            outcomes.Should().OnlyContain(result =>
                result.Redirect != null || result.Error is InvalidOperationException);
            await using var verify = CreateContext(connectionString!);
            (await verify.Set<SqlOSExternalIdentity>()
                    .CountAsync(x => x.SsoConnectionId == connection.Id && x.UserId == user.Id))
                .Should().Be(1);
            (await verify.Set<SqlOSUserEmail>()
                    .CountAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(email)))
                .Should().Be(1);
        }
        finally
        {
            await leftContext.DisposeAsync();
            await rightContext.DisposeAsync();
            await setup.Database.EnsureDeletedAsync();
        }
    }

    [TestMethod]
    public async Task ConcurrentFirstLink_DistinctAttackerSubjectsWithoutDomain_BindNoneAndIssueNoCodes()
    {
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("SamlBindAttack");
        var connectionString = setup.Database.GetConnectionString();
        connectionString.Should().NotBeNullOrWhiteSpace();
        var options = Options.Create(AspireFixture.Options);
        var setupStack = CreateIsolatedSamlStack(setup, options);
        await setupStack.Crypto.EnsureActiveSigningKeyAsync();
        await EnsureIsolatedAuthDefaultsAsync(setup, options);

        var victimEmail = $"victim-{Guid.NewGuid():N}@company.example";
        var victim = await setupStack.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Concurrent Victim", victimEmail, "P@ssword123!"));
        await MarkEmailVerifiedAsync(setup, victim.Id);
        var org = await setupStack.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Attacker Org", null));
        await AddMembershipAsync(setup, org.Id, victim.Id, isActive: true);
        var client = await CreateSamlClientAsync(setupStack.Admin, "race-attack");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSRaceAttackIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreatePolicySamlConnectionAsync(
            setupStack.Admin,
            org.Id,
            certificate,
            "race-attack",
            autoProvisionUsers: true,
            autoLinkByEmail: true);

        var leftContext = CreateContext(connectionString!);
        var rightContext = CreateContext(connectionString!);
        try
        {
            var left = CreateIsolatedSamlStack(leftContext, options);
            var right = CreateIsolatedSamlStack(rightContext, options);
            var leftFlow = await StartSamlRequestAsync(left.Saml, connection.Id, client.ClientId, leftContext);
            var rightFlow = await StartSamlRequestAsync(right.Saml, connection.Id, client.ClientId, rightContext);
            var leftResponse = BuildSignedSamlResponse(
                certificate,
                connection.IdentityProviderEntityId,
                victimEmail,
                "Left",
                "Attack",
                leftFlow,
                nameId: $"left-{Guid.NewGuid():N}");
            var rightResponse = BuildSignedSamlResponse(
                certificate,
                connection.IdentityProviderEntityId,
                victimEmail,
                "Right",
                "Attack",
                rightFlow,
                nameId: $"right-{Guid.NewGuid():N}");

            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new[]
            {
                Task.Run(async () =>
                {
                    await ready.Task;
                    return await CaptureAcsAsync(left.Saml, connection.Id, leftResponse, leftFlow.RelayState);
                }),
                Task.Run(async () =>
                {
                    await ready.Task;
                    return await CaptureAcsAsync(right.Saml, connection.Id, rightResponse, rightFlow.RelayState);
                })
            };
            ready.SetResult(true);
            var outcomes = await Task.WhenAll(tasks);

            outcomes.Should().OnlyContain(result =>
                result.Redirect == null && result.Error is InvalidOperationException);
            await using var verify = CreateContext(connectionString!);
            (await verify.Set<SqlOSExternalIdentity>().CountAsync(x => x.SsoConnectionId == connection.Id))
                .Should().Be(0);
            (await verify.Set<SqlOSAuthorizationCode>().CountAsync())
                .Should().Be(0);
            (await verify.Set<SqlOSSession>().CountAsync(x => x.UserId == victim.Id))
                .Should().Be(0);
            var storedEmail = await verify.Set<SqlOSUserEmail>()
                .SingleAsync(x => x.UserId == victim.Id);
            storedEmail.IsVerified.Should().BeTrue();
        }
        finally
        {
            await leftContext.DisposeAsync();
            await rightContext.DisposeAsync();
            await setup.Database.EnsureDeletedAsync();
        }
    }

    [TestMethod]
    public async Task ConcurrentFirstLink_DistinctSubjectsWithOwnedDomain_BindsAtMostOneIdentity()
    {
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("SamlBindTwoSub");
        var connectionString = setup.Database.GetConnectionString();
        connectionString.Should().NotBeNullOrWhiteSpace();
        var options = Options.Create(AspireFixture.Options);
        var setupStack = CreateIsolatedSamlStack(setup, options);
        await setupStack.Crypto.EnsureActiveSigningKeyAsync();
        await EnsureIsolatedAuthDefaultsAsync(setup, options);

        var domain = $"{Guid.NewGuid():N}.twosub.test";
        var email = $"owner@{domain}";
        var user = await setupStack.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Two Subject", email, "P@ssword123!"));
        await MarkEmailVerifiedAsync(setup, user.Id);
        var org = await setupStack.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Two Subject Org", null, domain));
        await AddMembershipAsync(setup, org.Id, user.Id, isActive: true);
        var client = await CreateSamlClientAsync(setupStack.Admin, "two-subject");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSTwoSubjectIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreatePolicySamlConnectionAsync(
            setupStack.Admin,
            org.Id,
            certificate,
            "two-subject",
            autoProvisionUsers: true,
            autoLinkByEmail: true);

        var leftContext = CreateContext(connectionString!);
        var rightContext = CreateContext(connectionString!);
        try
        {
            var left = CreateIsolatedSamlStack(leftContext, options);
            var right = CreateIsolatedSamlStack(rightContext, options);
            var leftFlow = await StartSamlRequestAsync(left.Saml, connection.Id, client.ClientId, leftContext);
            var rightFlow = await StartSamlRequestAsync(right.Saml, connection.Id, client.ClientId, rightContext);
            var leftResponse = BuildSignedSamlResponse(
                certificate,
                connection.IdentityProviderEntityId,
                email,
                "Left",
                "Subject",
                leftFlow,
                nameId: $"left-{Guid.NewGuid():N}");
            var rightResponse = BuildSignedSamlResponse(
                certificate,
                connection.IdentityProviderEntityId,
                email,
                "Right",
                "Subject",
                rightFlow,
                nameId: $"right-{Guid.NewGuid():N}");

            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new[]
            {
                Task.Run(async () =>
                {
                    await ready.Task;
                    return await CaptureAcsAsync(left.Saml, connection.Id, leftResponse, leftFlow.RelayState);
                }),
                Task.Run(async () =>
                {
                    await ready.Task;
                    return await CaptureAcsAsync(right.Saml, connection.Id, rightResponse, rightFlow.RelayState);
                })
            };
            ready.SetResult(true);
            var outcomes = await Task.WhenAll(tasks);

            outcomes.Count(result => result.Redirect != null).Should().BeLessThanOrEqualTo(1);
            outcomes.Count(result => result.Error != null).Should().BeGreaterThanOrEqualTo(1);
            await using var verify = CreateContext(connectionString!);
            (await verify.Set<SqlOSExternalIdentity>()
                    .CountAsync(x => x.SsoConnectionId == connection.Id && x.UserId == user.Id))
                .Should().Be(1);
            (await verify.Set<SqlOSAuthorizationCode>().CountAsync(x => x.UserId == user.Id))
                .Should().Be(outcomes.Count(result => result.Redirect != null));
        }
        finally
        {
            await leftContext.DisposeAsync();
            await rightContext.DisposeAsync();
            await setup.Database.EnsureDeletedAsync();
        }
    }

    [TestMethod]
    public async Task CrossOrganizationTakeover_DeviceApproveRebindThenForeignAssertion_CannotSignInAsAnotherOrganizationMember()
    {
        // Regression for #418. The attacker controls the signing key of org A's SAML
        // connection (for example as org A's SSO portal admin), holds any issuer session,
        // and targets an admin of org B.
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("SamlXorgTakeover");
        var connectionString = setup.Database.GetConnectionString();
        connectionString.Should().NotBeNullOrWhiteSpace();
        var options = Options.Create(AspireFixture.Options);
        var setupStack = CreateCrossOrganizationStack(setup, options);
        await setupStack.Crypto.EnsureActiveSigningKeyAsync();
        await EnsureIsolatedAuthDefaultsAsync(setup, options);

        var attackerDomain = $"attacker-{Guid.NewGuid():N}.test";
        var victimDomain = $"victim-{Guid.NewGuid():N}.test";
        var attackerOrg = await setupStack.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Attacker Org", null, attackerDomain));
        var victimOrg = await setupStack.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Victim Org", null, victimDomain));
        var victimEmail = $"ceo@{victimDomain}";
        var victim = await setupStack.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Victim", victimEmail, "P@ssword123!"));
        await setupStack.Admin.CreateMembershipAsync(victimOrg.Id, new SqlOSCreateMembershipRequest(victim.Id, "admin"));
        var attacker = await setupStack.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Attacker", $"eve@{attackerDomain}", "P@ssword123!"));
        var client = await CreateThirdPartyClientAsync(setupStack.Admin, "xorg");
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=SqlOSCrossOrgAttackerIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var attackerConnection = await CreatePolicySamlConnectionAsync(
            setupStack.Admin,
            attackerOrg.Id,
            certificate,
            "xorg",
            autoProvisionUsers: true,
            autoLinkByEmail: true);

        try
        {
            // 1. The attacker starts /authorize for a non-first-party client and identifies with an
            //    attacker-domain address, so discovery binds org A and its connection and redirects
            //    to the attacker's IdP with a SAML request ID the attacker can read.
            SamlFlow flow;
            string requestId;
            await using (var stepContext = CreateContext(connectionString!))
            {
                var stack = CreateCrossOrganizationStack(stepContext, options);
                var codeVerifier = stack.Crypto.GenerateOpaqueToken();
                var authorizationRequest = await stack.Authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
                    "code",
                    client.ClientId,
                    "https://client.example.local/callback",
                    "attacker-state",
                    "openid profile email",
                    stack.Crypto.CreatePkceCodeChallenge(codeVerifier),
                    "S256",
                    null,
                    null,
                    null,
                    null,
                    "hosted",
                    null));
                requestId = authorizationRequest.Id;
                var identify = await stack.Headless.IdentifyAsync(
                    new SqlOSHeadlessIdentifyRequest(requestId, $"anyone@{attackerDomain}"));
                identify.RedirectUrl.Should().NotBeNullOrWhiteSpace();
                flow = ParseSamlFlow(identify.RedirectUrl!);
                flow.RelayState.Should().Be(requestId);
            }

            // 2. With their own issuer session, the attacker asks device approval to move the
            //    interactive request into org B. The consent gate for the non-first-party client
            //    would save the request before any membership check.
            await using (var stepContext = CreateContext(connectionString!))
            {
                var stack = CreateCrossOrganizationStack(stepContext, options);
                var attackerHttp = await CreateIssuerSessionHttpContextAsync(stack, attacker.Id);
                var rebind = async () => await stack.Headless.ApproveDeviceAuthorizationAsync(
                    attackerHttp,
                    new SqlOSHeadlessDeviceAuthorizationApproveRequest(null, victimOrg.Id, requestId));
                await rebind.Should().ThrowAsync<InvalidOperationException>();
            }

            await using (var verify = CreateContext(connectionString!))
            {
                var stored = await verify.Set<SqlOSAuthorizationRequest>().AsNoTracking().SingleAsync(x => x.Id == requestId);
                stored.OrganizationId.Should().Be(attackerOrg.Id);
                stored.ConnectionId.Should().Be(attackerConnection.Id);
                stored.PendingConsentUserId.Should().BeNull();
                stored.ResolvedAuthMethod.Should().BeNull();
                (await verify.Set<SqlOSTemporaryToken>()
                        .AnyAsync(x => x.Purpose == SqlOSTemporaryTokenKinds.AuthPageConsent.Purpose))
                    .Should().BeFalse();
            }

            // 3. The attacker's IdP posts a correctly signed assertion for the victim's address.
            await using (var stepContext = CreateContext(connectionString!))
            {
                var stack = CreateCrossOrganizationStack(stepContext, options);
                var attackerSubject = $"attacker-subject-{Guid.NewGuid():N}";
                var samlResponse = BuildSignedSamlResponse(
                    certificate,
                    attackerConnection.IdentityProviderEntityId,
                    victimEmail,
                    "Evil",
                    "Admin",
                    flow,
                    nameId: attackerSubject);
                var acs = async () => await stack.Saml.HandleAcsAsync(
                    attackerConnection.Id,
                    samlResponse,
                    flow.RelayState,
                    CreateAcsHttpContext());
                await acs.Should().ThrowAsync<InvalidOperationException>();
            }

            await using (var verify = CreateContext(connectionString!))
            {
                (await verify.Set<SqlOSExternalIdentity>().AnyAsync(x => x.SsoConnectionId == attackerConnection.Id))
                    .Should().BeFalse();
                (await verify.Set<SqlOSAuthorizationCode>().AnyAsync(x => x.UserId == victim.Id))
                    .Should().BeFalse();
                (await verify.Set<SqlOSAuthorizationCode>().AnyAsync(x => x.AuthorizationRequestId == requestId))
                    .Should().BeFalse();
                (await verify.Set<SqlOSSession>().AnyAsync(x => x.UserId == victim.Id)).Should().BeFalse();
                (await verify.Set<SqlOSMembership>().CountAsync(x => x.UserId == victim.Id)).Should().Be(1);
                var stored = await verify.Set<SqlOSAuthorizationRequest>().AsNoTracking().SingleAsync(x => x.Id == requestId);
                stored.PendingConsentUserId.Should().BeNull();
                stored.CompletedAt.Should().BeNull();
            }
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }

    [DataTestMethod]
    [DataRow(false, DisplayName = "existing member of the other organization")]
    [DataRow(true, DisplayName = "JIT of a new address in the other organization's domain")]
    public async Task SignedAssertion_ForRequestBoundToAnotherOrganization_IsRejectedAuditedAndWritesNothing(bool jitNewAddress)
    {
        var (_, admin, saml) = CreateSamlServices();
        var attackerDomain = $"attacker-{Guid.NewGuid():N}.test";
        var victimDomain = $"victim-{Guid.NewGuid():N}.test";
        var attackerOrg = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Attacker {Guid.NewGuid():N}", null, attackerDomain));
        var victimOrg = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Victim {Guid.NewGuid():N}", null, victimDomain));
        var victimEmail = $"ceo@{victimDomain}";
        var victim = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Victim", victimEmail, "P@ssword123!"));
        await AddMembershipAsync(victimOrg.Id, victim.Id, isActive: true);
        var victimEmailRecord = await AspireFixture.SharedContext.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .SingleAsync(x => x.UserId == victim.Id);
        var assertedEmail = jitNewAddress ? $"new-hire-{Guid.NewGuid():N}@{victimDomain}" : victimEmail;
        var attackerSubject = $"attacker-subject-{Guid.NewGuid():N}";
        var client = await CreateSamlClientAsync(admin, "xorg-acs");
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=SqlOSCrossOrgAcsIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var attackerConnection = await CreatePolicySamlConnectionAsync(
            admin,
            attackerOrg.Id,
            certificate,
            "xorg-acs",
            autoProvisionUsers: true,
            autoLinkByEmail: true);
        var flow = await StartSamlRequestAsync(saml, attackerConnection.Id, client.ClientId);

        // Any write path that binds the request to another organization, simulated directly.
        var tampered = await AspireFixture.SharedContext.Set<SqlOSAuthorizationRequest>()
            .SingleAsync(x => x.Id == flow.RelayState);
        tampered.OrganizationId = victimOrg.Id;
        await AspireFixture.SharedContext.SaveChangesAsync();

        var samlResponse = BuildSignedSamlResponse(
            certificate,
            attackerConnection.IdentityProviderEntityId,
            assertedEmail,
            "Evil",
            "Admin",
            flow,
            nameId: attackerSubject);
        var action = async () => await saml.HandleAcsAsync(attackerConnection.Id, samlResponse, flow.RelayState, default);

        var rejection = await action.Should().ThrowAsync<InvalidOperationException>();
        SqlOSPublicAuthErrorMapper.Map(rejection.Which, SqlOSPublicAuthErrorSurface.SamlAcs).PublicMessage
            .Should().Be(SqlOSPublicAuthErrorMapper.DefaultExternalSignInMessage);

        await using var verify = CreateIsolatedContext();
        (await verify.Set<SqlOSExternalIdentity>().AnyAsync(x => x.SsoConnectionId == attackerConnection.Id))
            .Should().BeFalse();
        (await verify.Set<SqlOSUserEmail>().CountAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(assertedEmail)))
            .Should().Be(jitNewAddress ? 0 : 1);
        (await verify.Set<SqlOSMembership>().CountAsync(x => x.OrganizationId == victimOrg.Id))
            .Should().Be(1);
        (await verify.Set<SqlOSMembership>().AnyAsync(x => x.OrganizationId == attackerOrg.Id))
            .Should().BeFalse();
        var storedVictimEmail = await verify.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == victim.Id);
        storedVictimEmail.IsVerified.Should().Be(victimEmailRecord.IsVerified);
        storedVictimEmail.VerifiedAt.Should().Be(victimEmailRecord.VerifiedAt);
        var storedRequest = await verify.Set<SqlOSAuthorizationRequest>().SingleAsync(x => x.Id == flow.RelayState);
        storedRequest.CompletedAt.Should().BeNull();
        storedRequest.ResolvedAuthMethod.Should().BeNull();
        storedRequest.PendingConsentUserId.Should().BeNull();
        (await verify.Set<SqlOSAuthorizationCode>().AnyAsync(x => x.AuthorizationRequestId == flow.RelayState))
            .Should().BeFalse();
        (await verify.Set<SqlOSSession>().AnyAsync(x => x.UserId == victim.Id)).Should().BeFalse();
        (await verify.Set<SqlOSSamlReplay>().AnyAsync(x => x.ConnectionId == attackerConnection.Id))
            .Should().BeFalse();

        var denial = await verify.Set<SqlOSAuditEvent>()
            .SingleAsync(x => x.EventType == "user.login.saml.request_denied" && x.OrganizationId == attackerOrg.Id);
        denial.UserId.Should().BeNull();
        denial.DataJson.Should().Contain(attackerConnection.Id);
        denial.DataJson.Should().Contain("organization_mismatch");
        denial.DataJson.Should().Contain(victimOrg.Id);
        denial.DataJson.Should().NotContain(assertedEmail);
        denial.DataJson.Should().NotContain(attackerSubject);
    }

    private static async Task<SqlOSClientApplication> CreateThirdPartyClientAsync(SqlOSAdminService admin, string prefix)
        => await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            $"{prefix}-{Guid.NewGuid():N}"[..20],
            $"{prefix} third-party client",
            "sqlos-tests",
            ["https://client.example.local/callback"],
            AllowedScopes: ["openid", "profile", "email"],
            IsFirstParty: false));

    private static CrossOrganizationStack CreateCrossOrganizationStack(
        TestSqlOSDbContext context,
        IOptions<SqlOSAuthServerOptions> options)
    {
        var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var issuerSession = new SqlOSIssuerSessionService(context, crypto, settings);
        var authorization = new SqlOSAuthorizationServerService(context, admin, auth, crypto, settings, issuerSession, options);
        var saml = new SqlOSSamlService(context, options, admin, crypto, authorization);
        var device = new SqlOSDeviceAuthorizationService(context, admin, auth, crypto, options, issuerSessionService: issuerSession);
        var oidc = new SqlOSOidcAuthService(
            context,
            admin,
            crypto,
            new FakeOidcProviderHttpClientFactory(),
            NullLogger<SqlOSOidcAuthService>.Instance);
        var oidcBrowser = new SqlOSOidcBrowserAuthService(context, admin, auth, authorization, crypto, oidc, options);
        var headless = new SqlOSHeadlessAuthService(
            context,
            admin,
            authorization,
            new SqlOSHomeRealmDiscoveryService(context),
            oidcBrowser,
            saml,
            settings,
            emailOtp,
            options,
            deviceAuthorizationService: device,
            issuerSessionService: issuerSession,
            authService: auth);
        return new CrossOrganizationStack(context, crypto, admin, issuerSession, authorization, saml, headless);
    }

    private static async Task<DefaultHttpContext> CreateIssuerSessionHttpContextAsync(
        CrossOrganizationStack stack,
        string userId)
    {
        var user = await stack.Context.Set<SqlOSUser>().SingleAsync(x => x.Id == userId);
        var signIn = CreateAcsHttpContext();
        await stack.IssuerSession.SignInAsync(signIn, user, organizationId: null, "password");
        var http = CreateAcsHttpContext();
        http.Request.Headers.Cookie = signIn.Response.Headers.SetCookie.ToString().Split(';', 2)[0];
        return http;
    }

    private static DefaultHttpContext CreateAcsHttpContext()
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("tests");
        return http;
    }

    private sealed record CrossOrganizationStack(
        TestSqlOSDbContext Context,
        SqlOSCryptoService Crypto,
        SqlOSAdminService Admin,
        SqlOSIssuerSessionService IssuerSession,
        SqlOSAuthorizationServerService Authorization,
        SqlOSSamlService Saml,
        SqlOSHeadlessAuthService Headless);

    private static IsolatedSamlStack CreateIsolatedSamlStack(TestSqlOSDbContext context, IOptions<SqlOSAuthServerOptions> options)
    {
        var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(context, options, crypto);
        return new IsolatedSamlStack(crypto, admin, CreateSamlService(context, options, admin, crypto));
    }

    private static async Task EnsureIsolatedAuthDefaultsAsync(
        TestSqlOSDbContext context,
        IOptions<SqlOSAuthServerOptions> options)
    {
        var settings = new SqlOSSettingsService(context, options, new TestAuthEmailSender());
        await settings.EnsureDefaultSettingsAsync();
        await settings.EnsureDefaultAuthPageSettingsAsync();
    }

    private static async Task AddOrganizationDomainAsync(
        SqlOSCryptoService crypto,
        string organizationId,
        string domain,
        string status = SqlOSOrganizationDomainStatuses.Active,
        TestSqlOSDbContext? context = null)
    {
        var db = context ?? AspireFixture.SharedContext;
        db.Set<SqlOSOrganizationDomain>().Add(new SqlOSOrganizationDomain
        {
            Id = crypto.GenerateId("dom"),
            OrganizationId = organizationId,
            Domain = domain,
            Status = status,
            VerificationToken = "test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            VerifiedAt = status == SqlOSOrganizationDomainStatuses.Active ? DateTime.UtcNow : null
        });
        await db.SaveChangesAsync();
    }

    private static async Task MarkEmailVerifiedAsync(TestSqlOSDbContext context, string userId)
    {
        // The owner confirmed the address with the email-verification link.
        var user = await context.GetUserAsync(userId, SqlOSUserParts.Emails);
        var email = user.Emails.Single();
        user.VerifyEmail(new OwnershipProof(EmailAddress.Parse(email.Email), OwnershipProofMethod.EmailVerification), DateTime.UtcNow);
        await context.SaveChangesAsync();
    }

    private static async Task AddMembershipAsync(TestSqlOSDbContext context, string organizationId, string userId, bool isActive)
    {
        context.Set<SqlOSMembership>().Add(new SqlOSMembership
        {
            OrganizationId = organizationId,
            UserId = userId,
            Role = "member",
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private sealed record IsolatedSamlStack(SqlOSCryptoService Crypto, SqlOSAdminService Admin, SqlOSSamlService Saml);
}
