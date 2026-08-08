using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class AuthServerSigningKeyResilienceIntegrationTests
{
    [TestMethod]
    public async Task AuthorizationCodeExchange_SharedKeyRing_MultiInstanceUsesSameCustodiedKey()
    {
        TestSqlOSDbContext? setupContext = null;
        string? connectionString = null;
        var keyRingPath = CreateKeyRingDirectory("shared");

        try
        {
            setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSSharedKeyRing");
            connectionString = setupContext.Database.GetConnectionString();
            connectionString.Should().NotBeNullOrWhiteSpace();
            var clientId = $"shared-ring-{Guid.NewGuid():N}"[..30];
            var redirectUri = $"https://client.example.test/{clientId}/callback";
            var options = CreateOptions(clientId, redirectUri);
            var setupStack = BuildStack(setupContext, options, CreateFileSystemProvider(keyRingPath));
            var flow = await PrepareAuthorizationCodeAsync(setupStack, clientId, redirectUri);
            var signingKey = await setupContext.Set<SqlOSSigningKey>().SingleAsync(key => key.IsActive);
            signingKey.KeyReference.Should().StartWith("sqlos-dp-signing:v1:");
            signingKey.KeyReference.Should().NotContain("PRIVATE KEY");

            await setupContext.DisposeAsync();
            setupContext = null;
            await using var replacementContext = CreateContext(connectionString!);
            var replacementStack = BuildStack(
                replacementContext,
                options,
                CreateFileSystemProvider(keyRingPath));

            var tokenResult = await ExchangeAuthorizationCodeAsync(
                replacementStack,
                flow.Code,
                flow.CodeVerifier,
                clientId,
                redirectUri);

            tokenResult.Tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
            tokenResult.Tokens.OrganizationId.Should().Be(flow.OrganizationId);
            var activeKeys = await replacementContext.Set<SqlOSSigningKey>().Where(key => key.IsActive).ToListAsync();
            activeKeys.Should().ContainSingle().Which.Id.Should().Be(signingKey.Id);
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokenResult.Tokens.AccessToken);
            jwt.Header.Kid.Should().Be(signingKey.Kid);
            jwt.Header.Alg.Should().Be(SecurityAlgorithms.RsaSha256);
        }
        finally
        {
            if (setupContext != null)
            {
                await setupContext.DisposeAsync();
            }
            await DeleteDatabaseAsync(connectionString);
            DeleteKeyRingDirectory(keyRingPath);
        }
    }

    [TestMethod]
    public async Task AuthorizationCodeExchange_LostKeyRing_FailsClosedWithoutSilentRotation()
    {
        TestSqlOSDbContext? setupContext = null;
        string? connectionString = null;
        var originalKeyRingPath = CreateKeyRingDirectory("original");
        var replacementKeyRingPath = CreateKeyRingDirectory("replacement");

        try
        {
            setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSLostKeyRing");
            connectionString = setupContext.Database.GetConnectionString();
            var clientId = $"lost-ring-{Guid.NewGuid():N}"[..30];
            var redirectUri = $"https://client.example.test/{clientId}/callback";
            var options = CreateOptions(clientId, redirectUri);
            var setupStack = BuildStack(
                setupContext,
                options,
                CreateFileSystemProvider(originalKeyRingPath));
            var flow = await PrepareAuthorizationCodeAsync(setupStack, clientId, redirectUri);
            var originalKey = await setupContext.Set<SqlOSSigningKey>().SingleAsync(key => key.IsActive);

            await setupContext.DisposeAsync();
            setupContext = null;
            await using var replacementContext = CreateContext(connectionString!);
            var replacementStack = BuildStack(
                replacementContext,
                options,
                CreateFileSystemProvider(replacementKeyRingPath));

            var act = async () => await ExchangeAuthorizationCodeAsync(
                replacementStack,
                flow.Code,
                flow.CodeVerifier,
                clientId,
                redirectUri);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Refusing to rotate or issue tokens*");
            var keys = await replacementContext.Set<SqlOSSigningKey>().ToListAsync();
            keys.Should().ContainSingle();
            keys[0].Id.Should().Be(originalKey.Id);
            keys[0].IsActive.Should().BeTrue();
            keys[0].RetiredAt.Should().BeNull();
        }
        finally
        {
            if (setupContext != null)
            {
                await setupContext.DisposeAsync();
            }
            await DeleteDatabaseAsync(connectionString);
            DeleteKeyRingDirectory(originalKeyRingPath);
            DeleteKeyRingDirectory(replacementKeyRingPath);
        }
    }

    [TestMethod]
    public async Task EnsureAndRotateSigningKey_MultipleSqlInstances_MaintainsSingleActiveKey()
    {
        TestSqlOSDbContext? setupContext = null;
        string? connectionString = null;
        var keyRingPath = CreateKeyRingDirectory("concurrent");

        try
        {
            setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSMultiKey");
            connectionString = setupContext.Database.GetConnectionString();
            await setupContext.DisposeAsync();
            setupContext = null;
            var options = CreateOptions("multi-instance", "https://client.example.test/multi/callback");

            await using (var firstContext = CreateContext(connectionString!))
            await using (var secondContext = CreateContext(connectionString!))
            {
                var first = BuildStack(firstContext, options, CreateFileSystemProvider(keyRingPath));
                var second = BuildStack(secondContext, options, CreateFileSystemProvider(keyRingPath));
                var ensured = await Task.WhenAll(
                    first.Crypto.EnsureActiveSigningKeyAsync(),
                    second.Crypto.EnsureActiveSigningKeyAsync());
                ensured.Select(key => key.Id).Distinct().Should().ContainSingle();
            }

            await using (var firstRotationContext = CreateContext(connectionString!))
            await using (var secondRotationContext = CreateContext(connectionString!))
            {
                var first = BuildStack(firstRotationContext, options, CreateFileSystemProvider(keyRingPath));
                var second = BuildStack(secondRotationContext, options, CreateFileSystemProvider(keyRingPath));
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var arrivals = 0;

                async Task<SqlOSSigningKey> RotateAfterBarrierAsync(SqlOSCryptoService crypto)
                {
                    if (Interlocked.Increment(ref arrivals) == 2)
                    {
                        release.TrySetResult();
                    }

                    await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    return await crypto.RotateSigningKeyAsync();
                }

                var rotated = await Task.WhenAll(
                    RotateAfterBarrierAsync(first.Crypto),
                    RotateAfterBarrierAsync(second.Crypto));
                rotated.Select(key => key.Id).Distinct().Should().HaveCount(2);
            }

            await using var verifyContext = CreateContext(connectionString!);
            var allKeys = await verifyContext.Set<SqlOSSigningKey>().OrderBy(key => key.ActivatedAt).ToListAsync();
            allKeys.Should().HaveCount(3);
            allKeys.Count(key => key.IsActive).Should().Be(1);
            allKeys.Where(key => !key.IsActive).Should().OnlyContain(key => key.RetiredAt != null);
            allKeys.Should().OnlyContain(key => !key.KeyReference.Contains("PRIVATE KEY", StringComparison.Ordinal));
        }
        finally
        {
            if (setupContext != null)
            {
                await setupContext.DisposeAsync();
            }
            await DeleteDatabaseAsync(connectionString);
            DeleteKeyRingDirectory(keyRingPath);
        }
    }

    [TestMethod]
    public async Task ValidationCache_RotationOnReplicaA_ImmediatelyValidatesConcurrentlyOnReplicaB()
    {
        TestSqlOSDbContext? setupContext = null;
        string? connectionString = null;
        var keyRingPath = CreateKeyRingDirectory("replica-cache-refresh");
        var validationContexts = new List<TestSqlOSDbContext>();

        try
        {
            setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSReplicaKeyCache");
            connectionString = setupContext.Database.GetConnectionString();
            var options = CreateOptions("replica-cache", "https://client.example.test/replica-cache/callback");
            options.AccessTokenValidationSigningKeyCacheTtl = TimeSpan.FromHours(1);
            var provider = CreateFileSystemProvider(keyRingPath);
            var replicaA = BuildStack(setupContext, options, provider);
            var principal = await SeedTokenContextAsync(setupContext, replicaA, "replica-cache");
            var originalKey = await replicaA.Crypto.EnsureActiveSigningKeyAsync();
            var replicaBCache = new SqlOSValidationSigningKeyCache();

            await using (var primingContext = CreateContext(connectionString!))
            {
                var replicaB = BuildStack(primingContext, options, provider, replicaBCache);
                var primedKeys = await replicaB.Crypto.GetValidationSigningKeysAsync();
                primedKeys.Select(key => key.Kid).Should().Equal(originalKey.Kid);
            }

            var rotatedKey = await replicaA.Crypto.RotateSigningKeyAsync();
            var token = await replicaA.Crypto.CreateAccessTokenAsync(
                principal.User,
                principal.Session,
                principal.Client,
                principal.OrganizationId);
            new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Kid.Should().Be(rotatedKey.Kid);

            validationContexts.AddRange(Enumerable.Range(0, 12).Select(_ => CreateContext(connectionString!)));
            var validations = validationContexts
                .Select(context => BuildStack(context, options, provider, replicaBCache).Crypto
                    .ValidateAccessTokenAsync(token, principal.Client.Audience))
                .ToArray();

            var validated = await Task.WhenAll(validations);
            validated.Should().OnlyContain(result => result != null);
            var replicaBKeys = await BuildStack(validationContexts[0], options, provider, replicaBCache)
                .Crypto.GetValidationSigningKeysAsync();
            replicaBKeys.Select(key => key.Kid).Should().BeEquivalentTo([originalKey.Kid, rotatedKey.Kid]);
        }
        finally
        {
            foreach (var context in validationContexts)
            {
                await context.DisposeAsync();
            }
            if (setupContext != null)
            {
                await setupContext.DisposeAsync();
            }
            await DeleteDatabaseAsync(connectionString);
            DeleteKeyRingDirectory(keyRingPath);
        }
    }

    [TestMethod]
    public async Task StartupSigningKeyGate_ExistingPlaintextSqlRow_IsRejected()
    {
        TestSqlOSDbContext? context = null;
        string? connectionString = null;

        try
        {
            context = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSPlaintextKey");
            connectionString = context.Database.GetConnectionString();
            using var rsa = RSA.Create(2048);
            context.Set<SqlOSSigningKey>().Add(new SqlOSSigningKey
            {
                Id = "key_existing_plaintext",
                Kid = "existing-plaintext-kid",
                Algorithm = SecurityAlgorithms.RsaSha256,
                PublicKeyPem = rsa.ExportRSAPublicKeyPem(),
                CustodyProvider = "legacy-unprotected",
                KeyReference = rsa.ExportPkcs8PrivateKeyPem(),
                IsActive = true,
                ActivatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            var stack = BuildStack(
                context,
                CreateOptions("plaintext-client", "https://client.example.test/plaintext/callback"),
                new EphemeralDataProtectionProvider());

            var act = async () => await stack.Crypto.EnsureActiveSigningKeyAsync();

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*contains plaintext private key material*");
            (await context.Set<SqlOSSigningKey>().CountAsync()).Should().Be(1);
        }
        finally
        {
            if (context != null)
            {
                await context.DisposeAsync();
            }
            await DeleteDatabaseAsync(connectionString);
        }
    }

    [TestMethod]
    public async Task DbCompromiseSimulation_SqlRowCannotMintTokenAcceptedByLiveValidator()
    {
        TestSqlOSDbContext? context = null;
        string? connectionString = null;

        try
        {
            context = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSDbCompromise");
            connectionString = context.Database.GetConnectionString();
            var options = CreateOptions("db-compromise", "https://client.example.test/db/callback");
            var provider = new EphemeralDataProtectionProvider();
            var stack = BuildStack(context, options, provider);
            var principal = await SeedTokenContextAsync(context, stack, "db-compromise");
            var legitimateToken = await stack.Crypto.CreateAccessTokenAsync(
                principal.User,
                principal.Session,
                principal.Client,
                principal.OrganizationId);
            (await stack.Crypto.ValidateAccessTokenAsync(legitimateToken, principal.Client.Audience)).Should().NotBeNull();

            await using var attackerReadContext = CreateContext(connectionString!);
            var stolenRow = await attackerReadContext.Set<SqlOSSigningKey>().AsNoTracking().SingleAsync();
            stolenRow.KeyReference.Should().NotContain("PRIVATE KEY");
            var attackerCustody = new SqlOSDataProtectionSigningKeyCustody(
                new EphemeralDataProtectionProvider());
            var signAct = async () => await attackerCustody.SignAsync(ToDescriptor(stolenRow), "forge"u8.ToArray());
            await signAct.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*cannot be opened by this application instance*");

            using var attackerRsa = RSA.Create(2048);
            var forged = CreateForgedToken(
                attackerRsa,
                stolenRow.Kid,
                options.Issuer,
                principal.User.Id,
                principal.Session.Id,
                principal.Client);
            (await stack.Crypto.ValidateAccessTokenAsync(forged, principal.Client.Audience)).Should().BeNull();
        }
        finally
        {
            if (context != null)
            {
                await context.DisposeAsync();
            }
            await DeleteDatabaseAsync(connectionString);
        }
    }

    [TestMethod]
    public async Task RotationGraceAndCleanup_SqlJwksRetainsThenRemovesRetiredKey()
    {
        TestSqlOSDbContext? context = null;
        string? connectionString = null;

        try
        {
            context = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSKeyGrace");
            connectionString = context.Database.GetConnectionString();
            var options = CreateOptions("key-grace", "https://client.example.test/grace/callback");
            var stack = BuildStack(context, options, new EphemeralDataProtectionProvider());
            var principal = await SeedTokenContextAsync(context, stack, "key-grace");
            var oldToken = await stack.Crypto.CreateAccessTokenAsync(principal.User, principal.Session, principal.Client, null);
            var oldKey = await context.Set<SqlOSSigningKey>().SingleAsync(key => key.IsActive);

            var newKey = await stack.Crypto.RotateSigningKeyAsync();
            var newToken = await stack.Crypto.CreateAccessTokenAsync(principal.User, principal.Session, principal.Client, null);

            (await stack.Crypto.ValidateAccessTokenAsync(oldToken, principal.Client.Audience)).Should().NotBeNull();
            (await stack.Crypto.ValidateAccessTokenAsync(newToken, principal.Client.Audience)).Should().NotBeNull();
            var graceJwks = System.Text.Json.JsonSerializer.Serialize(
                stack.Crypto.GetJwksDocument(await stack.Crypto.GetValidationSigningKeysAsync()));
            graceJwks.Should().Contain(oldKey.Kid).And.Contain(newKey.Kid);

            oldKey.RetiredAt = DateTime.UtcNow.AddDays(-8);
            await context.SaveChangesAsync();
            (await stack.Crypto.CleanupRetiredSigningKeysAsync(TimeSpan.FromDays(7))).Should().Be(1);
            (await stack.Crypto.ValidateAccessTokenAsync(oldToken, principal.Client.Audience)).Should().BeNull();
            var cleanedJwks = System.Text.Json.JsonSerializer.Serialize(
                stack.Crypto.GetJwksDocument(await stack.Crypto.GetValidationSigningKeysAsync()));
            cleanedJwks.Should().NotContain(oldKey.Kid).And.Contain(newKey.Kid);
        }
        finally
        {
            if (context != null)
            {
                await context.DisposeAsync();
            }
            await DeleteDatabaseAsync(connectionString);
        }
    }

    [TestMethod]
    public async Task PersistedGraceWindow_SqlJwksAndStatefulValidationUseSameTrustBoundary()
    {
        TestSqlOSDbContext? context = null;
        string? connectionString = null;

        try
        {
            context = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSPersistedKeyGrace");
            connectionString = context.Database.GetConnectionString();
            var options = CreateOptions("persisted-key-grace", "https://client.example.test/persisted-grace/callback");
            var stack = BuildStack(context, options, new EphemeralDataProtectionProvider());
            var principal = await SeedTokenContextAsync(context, stack, "persisted-key-grace");
            var oldToken = await stack.Crypto.CreateAccessTokenAsync(
                principal.User,
                principal.Session,
                principal.Client,
                null);
            var oldKey = await context.Set<SqlOSSigningKey>().SingleAsync(key => key.IsActive);
            var activeKey = await stack.Crypto.RotateSigningKeyAsync();
            oldKey.RetiredAt = DateTime.UtcNow.AddDays(-2);
            context.Set<SqlOSSettings>().Add(new SqlOSSettings
            {
                Id = "default",
                SigningKeyGraceWindowDays = 1,
                SigningKeyRotationIntervalDays = 90,
                SigningKeyRetiredCleanupDays = 30,
                UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();

            var validationKeys = await stack.Crypto.GetValidationSigningKeysAsync();
            var jwks = System.Text.Json.JsonSerializer.Serialize(
                stack.Crypto.GetJwksDocument(validationKeys));

            validationKeys.Select(key => key.Kid).Should().Equal(activeKey.Kid);
            jwks.Should().Contain(activeKey.Kid).And.NotContain(oldKey.Kid);
            (await stack.Crypto.ValidateAccessTokenAsync(oldToken, principal.Client.Audience)).Should().BeNull();
        }
        finally
        {
            if (context != null)
            {
                await context.DisposeAsync();
            }
            await DeleteDatabaseAsync(connectionString);
        }
    }

    [TestMethod]
    public async Task SigningKeyLifecycleInvariant_SqlConstraintAndServiceRejectInactiveKeyWithoutRetiredAt()
    {
        TestSqlOSDbContext? context = null;
        string? connectionString = null;
        var keyRingPath = CreateKeyRingDirectory("lifecycle-invariant");

        try
        {
            context = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSKeyLifecycleInvariant");
            connectionString = context.Database.GetConnectionString();
            var options = CreateOptions("key-lifecycle", "https://client.example.test/key-lifecycle/callback");
            var provider = CreateFileSystemProvider(keyRingPath);
            var stack = BuildStack(context, options, provider);
            var key = await stack.Crypto.EnsureActiveSigningKeyAsync();

            var constrainedWrite = async () => await context.Database.ExecuteSqlRawAsync(
                "UPDATE [dbo].[SqlOSSigningKeys] SET [IsActive] = 0, [RetiredAt] = NULL WHERE [Id] = {0}",
                key.Id);
            await constrainedWrite.Should().ThrowAsync<Exception>()
                .WithMessage("*CK_SqlOSSigningKeys_Lifecycle*");

            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE [dbo].[SqlOSSigningKeys] NOCHECK CONSTRAINT [CK_SqlOSSigningKeys_Lifecycle]");
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE [dbo].[SqlOSSigningKeys] SET [IsActive] = 0, [RetiredAt] = NULL WHERE [Id] = {0}",
                key.Id);
            await context.DisposeAsync();
            context = null;

            await using var corruptedContext = CreateContext(connectionString!);
            var corruptedStack = BuildStack(
                corruptedContext,
                options,
                CreateFileSystemProvider(keyRingPath));
            var act = async () => await corruptedStack.Crypto.EnsureActiveSigningKeyAsync();

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*inactive*retirement timestamp*");
            (await corruptedContext.Set<SqlOSSigningKey>().CountAsync()).Should().Be(1);
        }
        finally
        {
            if (context != null)
            {
                await context.DisposeAsync();
            }
            await DeleteDatabaseAsync(connectionString);
            DeleteKeyRingDirectory(keyRingPath);
        }
    }

    [TestMethod]
    public async Task CleanupRetiredSigningKey_SqlReferenceSubstitution_DoesNotDeleteActiveMockProviderKey()
    {
        TestSqlOSDbContext? context = null;
        string? connectionString = null;

        try
        {
            context = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSKeyReferenceSubstitution");
            connectionString = context.Database.GetConnectionString();
            var options = CreateOptions("key-reference-substitution", "https://client.example.test/key-reference/callback");
            using var custody = new TrackingTestSigningKeyCustody();
            var stack = BuildStack(context, options, custody);
            var principal = await SeedTokenContextAsync(context, stack, "key-reference-substitution");
            await stack.Crypto.CreateAccessTokenAsync(
                principal.User,
                principal.Session,
                principal.Client,
                null);
            var retiredKey = await context.Set<SqlOSSigningKey>().SingleAsync(key => key.IsActive);
            var activeKey = await stack.Crypto.RotateSigningKeyAsync();
            retiredKey.RetiredAt = DateTime.UtcNow.AddDays(-40);
            await context.SaveChangesAsync();
            var retiredReference = retiredKey.KeyReference;

            await context.Database.ExecuteSqlRawAsync(
                "UPDATE [dbo].[SqlOSSigningKeys] SET [KeyReference] = {0} WHERE [Id] = {1}",
                activeKey.KeyReference,
                retiredKey.Id);
            context.ChangeTracker.Clear();

            var act = async () => await stack.Crypto.CleanupRetiredSigningKeysAsync(TimeSpan.FromDays(30));

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*share a custody reference*ambiguous provider ownership*");
            custody.DeleteCount.Should().Be(0);

            await context.Database.ExecuteSqlRawAsync(
                "UPDATE [dbo].[SqlOSSigningKeys] SET [KeyReference] = {0} WHERE [Id] = {1}",
                retiredReference,
                retiredKey.Id);
            context.ChangeTracker.Clear();
            var token = await stack.Crypto.CreateAccessTokenAsync(
                principal.User,
                principal.Session,
                principal.Client,
                null);
            (await stack.Crypto.ValidateAccessTokenAsync(token, principal.Client.Audience)).Should().NotBeNull();
        }
        finally
        {
            if (context != null)
            {
                await context.DisposeAsync();
            }
            await DeleteDatabaseAsync(connectionString);
        }
    }

    private static SqlOSAuthServerOptions CreateOptions(string clientId, string redirectUri)
    {
        var options = new SqlOSAuthServerOptions
        {
            Issuer = AspireFixture.Options.Issuer,
            BasePath = AspireFixture.Options.BasePath,
            DefaultSigningKeyGraceWindowDays = 7
        };
        options.SeedBrowserClient(clientId, $"Key Custody Client {clientId}", redirectUri);
        options.SeedAuthPage(page =>
        {
            page.EnabledCredentialTypes = ["password", "email_otp"];
            page.EnablePasswordSignup = true;
        });
        return options;
    }

    private static ServiceStack BuildStack(
        TestSqlOSDbContext context,
        SqlOSAuthServerOptions optionsValue,
        IDataProtectionProvider dataProtectionProvider)
    {
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options, dataProtectionProvider);
        return BuildStack(context, options, crypto);
    }

    private static ServiceStack BuildStack(
        TestSqlOSDbContext context,
        SqlOSAuthServerOptions optionsValue,
        IDataProtectionProvider dataProtectionProvider,
        SqlOSValidationSigningKeyCache validationSigningKeyCache)
    {
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(
            context,
            options,
            new SqlOSDataProtectionSigningKeyCustody(dataProtectionProvider),
            dataProtectionProvider,
            validationSigningKeyCache);
        return BuildStack(context, options, crypto);
    }

    private static ServiceStack BuildStack(
        TestSqlOSDbContext context,
        SqlOSAuthServerOptions optionsValue,
        ISqlOSSigningKeyCustody signingKeyCustody)
    {
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(
            context,
            options,
            dataProtectionProvider: null,
            signingKeyCustody: signingKeyCustody);
        return BuildStack(context, options, crypto);
    }

    private static ServiceStack BuildStack(
        TestSqlOSDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSCryptoService crypto)
    {
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender { IsConfigured = true };
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
        var authorizationServer = new SqlOSAuthorizationServerService(
            context,
            admin,
            auth,
            crypto,
            settings,
            authPageSession,
            options);
        return new ServiceStack(crypto, admin, authorizationServer);
    }

    private static async Task<AuthorizationCodeFlow> PrepareAuthorizationCodeAsync(
        ServiceStack stack,
        string clientId,
        string redirectUri)
    {
        await stack.Admin.UpsertSeededClientsAsync();
        await stack.Crypto.EnsureActiveSigningKeyAsync();
        var user = await stack.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Key Custody User",
            $"key-custody-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var organization = await stack.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"Key Custody {Guid.NewGuid():N}", null));
        await stack.Admin.CreateMembershipAsync(
            organization.Id,
            new SqlOSCreateMembershipRequest(user.Id, "owner"));
        var codeVerifier = stack.Crypto.GenerateOpaqueToken();
        var request = await stack.AuthorizationServer.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                clientId,
                redirectUri,
                "key-custody-state",
                "openid profile email offline_access",
                stack.Crypto.CreatePkceCodeChallenge(codeVerifier),
                "S256",
                null,
                user.DefaultEmail,
                null,
                null,
                "hosted",
                null));
        var redirect = await stack.AuthorizationServer.IssueAuthorizationRedirectAsync(
            request,
            user,
            organization.Id,
            "password",
            CreateHttpContext());
        return new AuthorizationCodeFlow(
            QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString(),
            codeVerifier,
            organization.Id);
    }

    private static Task<SqlOSTokenEndpointResult> ExchangeAuthorizationCodeAsync(
        ServiceStack stack,
        string code,
        string codeVerifier,
        string clientId,
        string redirectUri)
        => stack.AuthorizationServer.ExchangeAuthorizationCodeAsync(
            new SqlOSTokenRequest(
                "authorization_code",
                code,
                redirectUri,
                clientId,
                codeVerifier,
                null,
                null),
            CreateHttpContext());

    private static async Task<TokenPrincipal> SeedTokenContextAsync(
        TestSqlOSDbContext context,
        ServiceStack stack,
        string suffix)
    {
        await stack.Admin.UpsertSeededClientsAsync();
        var user = await stack.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            $"Token User {suffix}",
            $"token-{suffix}-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var organization = await stack.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"Token Org {suffix} {Guid.NewGuid():N}", null));
        await stack.Admin.CreateMembershipAsync(
            organization.Id,
            new SqlOSCreateMembershipRequest(user.Id, "owner"));
        var client = await context.Set<SqlOSClientApplication>().SingleAsync(app => app.ClientId == suffix);
        var session = new SqlOSSession
        {
            Id = stack.Crypto.GenerateId("ses"),
            UserId = user.Id,
            ClientApplicationId = client.Id,
            OrganizationId = organization.Id,
            AuthenticationMethod = "password mfa",
            EffectiveAudience = client.Audience,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            IdleExpiresAt = DateTime.UtcNow.AddHours(1),
            AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        context.Set<SqlOSSession>().Add(session);
        await context.SaveChangesAsync();
        return new TokenPrincipal(user, session, client, organization.Id);
    }

    private static string CreateForgedToken(
        RSA attackerRsa,
        string kid,
        string issuer,
        string userId,
        string sessionId,
        SqlOSClientApplication client)
    {
        var token = new JwtSecurityToken(
            issuer,
            client.Audience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                new Claim("sid", sessionId),
                new Claim("client_id", client.ClientId)
            ],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(
                new RsaSecurityKey(attackerRsa) { KeyId = kid },
                SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static SqlOSSigningKeyDescriptor ToDescriptor(SqlOSSigningKey key)
        => new(key.Kid, key.Algorithm, key.PublicKeyPem, key.KeyReference, key.CustodyProvider);

    private static TestSqlOSDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<TestSqlOSDbContext>().UseSqlServer(connectionString).Options);

    private static string CreateKeyRingDirectory(string suffix)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"sqlos-signing-key-tests-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static IDataProtectionProvider CreateFileSystemProvider(string keyRingPath)
        => DataProtectionProvider.Create(
            new DirectoryInfo(keyRingPath),
            builder => builder.SetApplicationName("SqlOS.IntegrationTests.SigningKeyCustody"));

    private static void DeleteKeyRingDirectory(string keyRingPath)
    {
        if (Directory.Exists(keyRingPath))
        {
            Directory.Delete(keyRingPath, recursive: true);
        }
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("auth.example.test");
        context.Request.Headers.UserAgent = "SqlOS signing-key custody integration test";
        return context;
    }

    private static async Task DeleteDatabaseAsync(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            await using var cleanup = CreateContext(connectionString);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private sealed record ServiceStack(
        SqlOSCryptoService Crypto,
        SqlOSAdminService Admin,
        SqlOSAuthorizationServerService AuthorizationServer);

    private sealed record AuthorizationCodeFlow(string Code, string CodeVerifier, string OrganizationId);

    private sealed record TokenPrincipal(
        SqlOSUser User,
        SqlOSSession Session,
        SqlOSClientApplication Client,
        string OrganizationId);

    private sealed class TrackingTestSigningKeyCustody : ISqlOSSigningKeyCustody, IDisposable
    {
        private readonly Dictionary<string, RSA> _keys = new(StringComparer.Ordinal);

        public string ProviderId => "mock-kms:integration:v1";
        public int DeleteCount { get; private set; }

        public Task<SqlOSSigningKeyCreationResult> CreateKeyAsync(
            string kid,
            string algorithm,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rsa = RSA.Create(2048);
            var reference = $"mock-kms:key:{kid}";
            _keys.Add(reference, rsa);
            return Task.FromResult(new SqlOSSigningKeyCreationResult(
                algorithm,
                rsa.ExportRSAPublicKeyPem(),
                reference));
        }

        public Task<byte[]> SignAsync(
            SqlOSSigningKeyDescriptor key,
            ReadOnlyMemory<byte> signingInput,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            key.CustodyProvider.Should().Be(ProviderId);
            return Task.FromResult(_keys[key.KeyReference].SignData(
                signingInput.Span,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));
        }

        public Task DeleteKeyAsync(
            SqlOSSigningKeyDescriptor key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_keys.Remove(key.KeyReference, out var rsa))
            {
                rsa.Dispose();
            }
            DeleteCount++;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            foreach (var rsa in _keys.Values)
            {
                rsa.Dispose();
            }
            _keys.Clear();
        }
    }
}
