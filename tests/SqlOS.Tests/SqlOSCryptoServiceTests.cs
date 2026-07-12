using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSCryptoServiceTests
{
    [TestMethod]
    public void HashPassword_VerifyPassword_Succeeds()
    {
        using var context = CreateContext();
        var service = new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions()));

        var hash = service.HashPassword("P@ssword123!");

        service.VerifyPassword(hash, "P@ssword123!").Should().BeTrue();
        service.VerifyPassword(hash, "bad-password").Should().BeFalse();
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_DefaultConfiguration_FailsClosedEvenWhenDataProtectionExists()
    {
        using var context = CreateContext();
        var service = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            new EphemeralDataProtectionProvider());

        var act = async () => await service.EnsureActiveSigningKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*DataProtectionKeyRingIsPersistedAndShared*");
        context.Set<SqlOSSigningKey>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_WithoutDataProtectionProvider_FailsClosed()
    {
        using var context = CreateContext();
        var service = new SqlOSCryptoService(context, ConfiguredDataProtectionOptions());

        var act = async () => await service.EnsureActiveSigningKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires an ASP.NET Core Data Protection provider*");
        context.Set<SqlOSSigningKey>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_ConfiguredDataProtection_PersistsOnlyOpaqueCustodyReference()
    {
        using var context = CreateContext();
        var service = CreateDataProtectionService(context, new EphemeralDataProtectionProvider());

        var first = await service.EnsureActiveSigningKeyAsync();
        var second = await service.EnsureActiveSigningKeyAsync();

        second.Id.Should().Be(first.Id);
        first.CustodyProvider.Should().Be(SqlOSDataProtectionSigningKeyCustody.DataProtectionProviderId);
        first.KeyReference.Should().StartWith("sqlos-dp-signing:v1:");
        Base64UrlEncoder.DecodeBytes(first.KeyReference["sqlos-dp-signing:v1:".Length..])
            .Should().NotBeEmpty();
        first.KeyReference.Should().NotContain("BEGIN PRIVATE KEY");
        first.PublicKeyPem.Should().Contain("BEGIN RSA PUBLIC KEY");
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_RejectsExistingPlaintextPrivateKeyRow()
    {
        using var context = CreateContext();
        using var rsa = RSA.Create(2048);
        context.Set<SqlOSSigningKey>().Add(new SqlOSSigningKey
        {
            Id = "key_legacy",
            Kid = "legacy-kid",
            Algorithm = SecurityAlgorithms.RsaSha256,
            PublicKeyPem = rsa.ExportRSAPublicKeyPem(),
            CustodyProvider = "legacy-unprotected",
            KeyReference = rsa.ExportPkcs8PrivateKeyPem(),
            IsActive = true,
            ActivatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var service = CreateDataProtectionService(context, new EphemeralDataProtectionProvider());

        var act = async () => await service.EnsureActiveSigningKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*contains plaintext private key material*");
        context.Set<SqlOSSigningKey>().Should().ContainSingle();
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_AmbiguousPublicKeyPem_FailsWithClearCustodyError()
    {
        using var context = CreateContext();
        using var rsa = RSA.Create(2048);
        var publicKeyPem = rsa.ExportRSAPublicKeyPem();
        context.Set<SqlOSSigningKey>().Add(new SqlOSSigningKey
        {
            Id = "key_ambiguous_public",
            Kid = "ambiguous-public-kid",
            Algorithm = SecurityAlgorithms.RsaSha256,
            PublicKeyPem = $"{publicKeyPem}\n{publicKeyPem}",
            CustodyProvider = SqlOSDataProtectionSigningKeyCustody.DataProtectionProviderId,
            KeyReference = "sqlos-dp-signing:v1:not-used",
            IsActive = true,
            ActivatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var service = CreateDataProtectionService(context, new EphemeralDataProtectionProvider());

        var act = async () => await service.EnsureActiveSigningKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not contain a valid RSA public key*");
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_WithLostDataProtectionRing_FailsWithoutRotating()
    {
        using var context = CreateContext();
        var originalService = CreateDataProtectionService(context, new EphemeralDataProtectionProvider());
        var originalKey = await originalService.EnsureActiveSigningKeyAsync();
        var replacementService = CreateDataProtectionService(context, new EphemeralDataProtectionProvider());

        var act = async () => await replacementService.EnsureActiveSigningKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Refusing to rotate or issue tokens*");
        var stored = context.Set<SqlOSSigningKey>().Should().ContainSingle().Subject;
        stored.Id.Should().Be(originalKey.Id);
        stored.IsActive.Should().BeTrue();
        stored.RetiredAt.Should().BeNull();
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_SwappedCustodyReferenceAndPublicKey_FailsClosed()
    {
        using var context = CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var service = CreateDataProtectionService(context, provider);
        var retired = await service.EnsureActiveSigningKeyAsync();
        var active = await service.RotateSigningKeyAsync();

        active.KeyReference = retired.KeyReference;
        active.PublicKeyPem = retired.PublicKeyPem;
        await context.SaveChangesAsync();

        var act = async () => await service.EnsureActiveSigningKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ambiguous provider ownership*");
        context.Set<SqlOSSigningKey>().Count(key => key.IsActive).Should().Be(1);
        context.Set<SqlOSSigningKey>().Should().HaveCount(2);
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_InactiveRowWithoutRetiredAt_FailsClosed()
    {
        using var context = CreateContext();
        var service = CreateDataProtectionService(context, new EphemeralDataProtectionProvider());
        var key = await service.EnsureActiveSigningKeyAsync();
        key.IsActive = false;
        key.RetiredAt = null;
        await context.SaveChangesAsync();

        var act = async () => await service.EnsureActiveSigningKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*inactive*retirement timestamp*");
        context.Set<SqlOSSigningKey>().Should().ContainSingle();
    }

    [TestMethod]
    public async Task CreateAccessToken_DataProtectionCustody_PublishesMatchingJwksAndValidates()
    {
        using var context = CreateContext();
        var service = CreateDataProtectionService(context, new EphemeralDataProtectionProvider());
        var (user, session, client) = await SeedTokenContextAsync(context);

        var rawToken = await service.CreateAccessTokenAsync(user, session, client, "org_test");
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(rawToken);
        var key = await context.Set<SqlOSSigningKey>().SingleAsync();
        var validated = await service.ValidateAccessTokenAsync(rawToken, client.Audience);
        var jwksJson = System.Text.Json.JsonSerializer.Serialize(
            service.GetJwksDocument(await service.GetValidationSigningKeysAsync()));

        parsed.Header.Alg.Should().Be(SecurityAlgorithms.RsaSha256);
        parsed.Header.Typ.Should().Be("JWT");
        parsed.Header.Kid.Should().Be(key.Kid);
        validated.Should().NotBeNull();
        validated!.UserId.Should().Be(user.Id);
        jwksJson.Should().Contain(key.Kid);
        key.KeyReference.Should().NotContain("BEGIN PRIVATE KEY");
    }

    [TestMethod]
    public async Task CreateAccessToken_InjectedExternalCustody_NeverPersistsPrivateMaterial()
    {
        using var context = CreateContext();
        using var custody = new TestExternalSigningKeyCustody();
        var service = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            dataProtectionProvider: null,
            signingKeyCustody: custody);
        var (user, session, client) = await SeedTokenContextAsync(context);

        var rawToken = await service.CreateAccessTokenAsync(user, session, client, "org_test");
        var key = await context.Set<SqlOSSigningKey>().SingleAsync();

        key.CustodyProvider.Should().Be(custody.ProviderId);
        key.KeyReference.Should().StartWith("fake-kms:v1:");
        key.KeyReference.Should().NotContain("PRIVATE KEY");
        (await service.ValidateAccessTokenAsync(rawToken, client.Audience)).Should().NotBeNull();
        custody.SignCount.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void AddSqlOS_PreRegisteredExternalCustody_IsNotReplacedByBuiltInProvider()
    {
        var services = new ServiceCollection();
        var custody = new TestExternalSigningKeyCustody();
        services.AddSingleton<ISqlOSSigningKeyCustody>(custody);

        services.AddSqlOS<TestSqlOSInMemoryDbContext>();
        using var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetRequiredService<ISqlOSSigningKeyCustody>().Should().BeSameAs(custody);
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_ProviderSubstitution_FailsClosed()
    {
        using var context = CreateContext();
        using var originalCustody = new TestExternalSigningKeyCustody("fake-kms:original");
        var originalService = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            signingKeyCustody: originalCustody);
        await originalService.EnsureActiveSigningKeyAsync();
        using var substitutedCustody = new TestExternalSigningKeyCustody("fake-kms:substituted");
        var substitutedService = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            signingKeyCustody: substitutedCustody);

        var act = async () => await substitutedService.EnsureActiveSigningKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*bound to custody provider 'fake-kms:original'*");
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_CustodyPublicPrivateMismatch_FailsBeforePersistence()
    {
        using var context = CreateContext();
        using var custody = new TestExternalSigningKeyCustody { ReturnMismatchedSignature = true };
        var service = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            signingKeyCustody: custody);

        var act = async () => await service.EnsureActiveSigningKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*produced a signature that does not match*");
        context.Set<SqlOSSigningKey>().Should().BeEmpty();
        custody.DeleteCount.Should().Be(1);
    }

    [TestMethod]
    public async Task RotateSigningKey_ProviderReusesExistingReference_FailsWithoutDeletingLiveKey()
    {
        using var context = CreateContext();
        using var custody = new TestExternalSigningKeyCustody();
        var service = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            signingKeyCustody: custody);
        var (user, session, client) = await SeedTokenContextAsync(context);
        await service.EnsureActiveSigningKeyAsync();
        custody.ReuseExistingKeyReference = true;

        var act = async () => await service.RotateSigningKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reused existing key material*");
        custody.DeleteCount.Should().Be(0);
        context.Set<SqlOSSigningKey>().Should().ContainSingle(key => key.IsActive);
        var token = await service.CreateAccessTokenAsync(user, session, client, null);
        (await service.ValidateAccessTokenAsync(token, client.Audience)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task RotateSigningKey_ProviderReusesReferenceWithDifferentPublicKey_DoesNotDeleteLiveKey()
    {
        using var context = CreateContext();
        using var custody = new TestExternalSigningKeyCustody();
        var service = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            signingKeyCustody: custody);
        var (user, session, client) = await SeedTokenContextAsync(context);
        await service.EnsureActiveSigningKeyAsync();
        custody.ReuseExistingKeyReference = true;
        custody.ReturnDifferentPublicKeyForReusedReference = true;

        var act = async () => await service.RotateSigningKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reused existing key material*");
        custody.DeleteCount.Should().Be(0);
        var token = await service.CreateAccessTokenAsync(user, session, client, null);
        (await service.ValidateAccessTokenAsync(token, client.Audience)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task RotateSigningKey_ProviderAliasesExistingPublicKey_FailsWithoutDeletingEitherReference()
    {
        using var context = CreateContext();
        using var custody = new TestExternalSigningKeyCustody();
        var service = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            signingKeyCustody: custody);
        await service.EnsureActiveSigningKeyAsync();
        custody.AliasExistingPublicKey = true;

        var act = async () => await service.RotateSigningKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reused existing key material*");
        custody.DeleteCount.Should().Be(0);
        context.Set<SqlOSSigningKey>().Should().ContainSingle(key => key.IsActive);
    }

    [TestMethod]
    public async Task DbCompromiseSimulation_StoredMaterialAloneCannotMintAcceptedToken()
    {
        using var context = CreateContext();
        var legitimateProvider = new EphemeralDataProtectionProvider();
        var service = CreateDataProtectionService(context, legitimateProvider);
        var (user, session, client) = await SeedTokenContextAsync(context);
        var legitimateToken = await service.CreateAccessTokenAsync(user, session, client, "org_test");
        (await service.ValidateAccessTokenAsync(legitimateToken, client.Audience)).Should().NotBeNull();
        var stolenRow = await context.Set<SqlOSSigningKey>().AsNoTracking().SingleAsync();

        var attackerCustody = new SqlOSDataProtectionSigningKeyCustody(
            ConfiguredDataProtectionOptions(),
            new EphemeralDataProtectionProvider());
        var signAct = async () => await attackerCustody.SignAsync(
            ToDescriptor(stolenRow),
            "attacker-input"u8.ToArray());
        await signAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be opened by this application instance*");

        using var attackerRsa = RSA.Create(2048);
        var attackerToken = CreateForgedToken(attackerRsa, stolenRow.Kid, user.Id, session.Id, client);
        (await service.ValidateAccessTokenAsync(attackerToken, client.Audience)).Should().BeNull();
    }

    [TestMethod]
    public async Task ValidateAccessToken_AlgorithmConfusionUsingPublishedRsaKeyAsHmacSecret_IsRejected()
    {
        using var context = CreateContext();
        var service = CreateDataProtectionService(context, new EphemeralDataProtectionProvider());
        var (user, session, client) = await SeedTokenContextAsync(context);
        await service.EnsureActiveSigningKeyAsync();
        var key = await context.Set<SqlOSSigningKey>().SingleAsync();
        var forged = new JwtSecurityToken(
            issuer: "https://localhost/sqlos/auth",
            audience: client.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id),
                new Claim("sid", session.Id),
                new Claim("client_id", client.ClientId)
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key.PublicKeyPem)) { KeyId = key.Kid },
                SecurityAlgorithms.HmacSha256));
        var rawToken = new JwtSecurityTokenHandler().WriteToken(forged);

        (await service.ValidateAccessTokenAsync(rawToken, client.Audience)).Should().BeNull();
    }

    [TestMethod]
    public async Task ValidateAccessToken_UsesPersistedGraceWindowConsistentWithJwks()
    {
        using var context = CreateContext();
        var service = CreateDataProtectionService(context, new EphemeralDataProtectionProvider());
        var (user, session, client) = await SeedTokenContextAsync(context);
        var oldToken = await service.CreateAccessTokenAsync(user, session, client, null);
        var oldKey = await context.Set<SqlOSSigningKey>().SingleAsync();
        var activeKey = await service.RotateSigningKeyAsync();
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

        var jwksKeys = await service.GetValidationSigningKeysAsync();

        jwksKeys.Select(key => key.Kid).Should().Equal(activeKey.Kid);
        (await service.ValidateAccessTokenAsync(oldToken, client.Audience)).Should().BeNull();
    }

    [TestMethod]
    public async Task RotateAndCleanupSigningKey_PreservesGraceThenRemovesRetiredJwksKey()
    {
        using var context = CreateContext();
        using var custody = new TestExternalSigningKeyCustody();
        var options = new SqlOSAuthServerOptions { DefaultSigningKeyGraceWindowDays = 7 };
        var service = new SqlOSCryptoService(
            context,
            Options.Create(options),
            signingKeyCustody: custody);
        var (user, session, client) = await SeedTokenContextAsync(context);
        var oldToken = await service.CreateAccessTokenAsync(user, session, client, null);
        var oldKey = await context.Set<SqlOSSigningKey>().SingleAsync();

        var newKey = await service.RotateSigningKeyAsync();
        var newToken = await service.CreateAccessTokenAsync(user, session, client, null);

        newKey.Kid.Should().NotBe(oldKey.Kid);
        (await service.ValidateAccessTokenAsync(oldToken, client.Audience)).Should().NotBeNull();
        (await service.ValidateAccessTokenAsync(newToken, client.Audience)).Should().NotBeNull();
        (await service.GetValidationSigningKeysAsync()).Select(key => key.Kid).Should().Contain([oldKey.Kid, newKey.Kid]);

        oldKey.RetiredAt = DateTime.UtcNow.AddDays(-8);
        await context.SaveChangesAsync();
        (await service.ValidateAccessTokenAsync(oldToken, client.Audience)).Should().BeNull();
        (await service.CleanupRetiredSigningKeysAsync(TimeSpan.FromDays(7))).Should().Be(1);
        (await service.GetValidationSigningKeysAsync()).Select(key => key.Kid).Should().Equal(newKey.Kid);
        custody.DeleteCount.Should().Be(1);
    }

    [TestMethod]
    public async Task CleanupRetiredSigningKey_DuplicateActiveReference_FailsWithoutDeletingLiveKey()
    {
        using var context = CreateContext();
        using var custody = new TestExternalSigningKeyCustody();
        var service = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            signingKeyCustody: custody);
        var (user, session, client) = await SeedTokenContextAsync(context);
        var retiredKey = await service.EnsureActiveSigningKeyAsync();
        var activeKey = await service.RotateSigningKeyAsync();
        retiredKey.RetiredAt = DateTime.UtcNow.AddDays(-40);
        var retiredReference = retiredKey.KeyReference;
        retiredKey.KeyReference = activeKey.KeyReference;
        await context.SaveChangesAsync();

        var act = async () => await service.CleanupRetiredSigningKeysAsync(TimeSpan.FromDays(30));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*share a custody reference*ambiguous provider ownership*");
        custody.DeleteCount.Should().Be(0);
        retiredKey.KeyReference = retiredReference;
        await context.SaveChangesAsync();
        var token = await service.CreateAccessTokenAsync(user, session, client, null);
        (await service.ValidateAccessTokenAsync(token, client.Audience)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_DuplicateStoredPublicKey_FailsClosed()
    {
        using var context = CreateContext();
        using var custody = new TestExternalSigningKeyCustody();
        var service = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            signingKeyCustody: custody);
        var retiredKey = await service.EnsureActiveSigningKeyAsync();
        var activeKey = await service.RotateSigningKeyAsync();
        retiredKey.PublicKeyPem = activeKey.PublicKeyPem;
        await context.SaveChangesAsync();

        var act = async () => await service.EnsureActiveSigningKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*share the same canonical RSA public key*ambiguous provider ownership*");
    }

    [TestMethod]
    public void ProtectSecret_UnprotectSecret_RoundTrips()
    {
        using var context = CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var service = new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions()), provider);

        var protectedSecret = service.ProtectSecret("super-secret-value");

        protectedSecret.Should().NotBe("super-secret-value");
        service.UnprotectSecret(protectedSecret).Should().Be("super-secret-value");
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }

    private static IOptions<SqlOSAuthServerOptions> ConfiguredDataProtectionOptions()
    {
        var options = new SqlOSAuthServerOptions();
        options.SigningKeyCustody.DataProtectionKeyRingIsPersistedAndShared = true;
        return Options.Create(options);
    }

    private static SqlOSCryptoService CreateDataProtectionService(
        TestSqlOSInMemoryDbContext context,
        IDataProtectionProvider provider)
        => new(context, ConfiguredDataProtectionOptions(), provider);

    private static async Task<(SqlOSUser User, SqlOSSession Session, SqlOSClientApplication Client)> SeedTokenContextAsync(
        TestSqlOSInMemoryDbContext context)
    {
        var user = new SqlOSUser
        {
            Id = $"usr_{Guid.NewGuid():N}"[..28],
            DisplayName = "Test User",
            DefaultEmail = "test@example.com",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var client = new SqlOSClientApplication
        {
            Id = $"cli_{Guid.NewGuid():N}"[..28],
            ClientId = $"test-client-{Guid.NewGuid():N}"[..30],
            Name = "Test Client",
            Audience = "test-api",
            CreatedAt = DateTime.UtcNow
        };
        var session = new SqlOSSession
        {
            Id = $"ses_{Guid.NewGuid():N}"[..28],
            UserId = user.Id,
            ClientApplicationId = client.Id,
            AuthenticationMethod = "password mfa",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            IdleExpiresAt = DateTime.UtcNow.AddHours(1),
            AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1),
            EffectiveAudience = client.Audience
        };
        context.Set<SqlOSUser>().Add(user);
        context.Set<SqlOSClientApplication>().Add(client);
        context.Set<SqlOSSession>().Add(session);
        await context.SaveChangesAsync();
        return (user, session, client);
    }

    private static string CreateForgedToken(
        RSA attackerRsa,
        string kid,
        string userId,
        string sessionId,
        SqlOSClientApplication client)
    {
        var token = new JwtSecurityToken(
            issuer: "https://localhost/sqlos/auth",
            audience: client.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                new Claim("sid", sessionId),
                new Claim("client_id", client.ClientId)
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new RsaSecurityKey(attackerRsa) { KeyId = kid },
                SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static SqlOSSigningKeyDescriptor ToDescriptor(SqlOSSigningKey key)
        => new(key.Kid, key.Algorithm, key.PublicKeyPem, key.KeyReference, key.CustodyProvider);

    private sealed class TestExternalSigningKeyCustody : ISqlOSSigningKeyCustody, IDisposable
    {
        private readonly ConcurrentDictionary<string, RSA> _keys = new(StringComparer.Ordinal);

        public TestExternalSigningKeyCustody(string providerId = "fake-kms:v1")
        {
            ProviderId = providerId;
        }

        public string ProviderId { get; }
        public int SignCount { get; private set; }
        public int DeleteCount { get; private set; }
        public bool ReturnMismatchedSignature { get; set; }
        public bool ReuseExistingKeyReference { get; set; }
        public bool ReturnDifferentPublicKeyForReusedReference { get; set; }
        public bool AliasExistingPublicKey { get; set; }

        public Task<SqlOSSigningKeyCreationResult> CreateKeyAsync(
            string kid,
            string algorithm,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            algorithm.Should().Be(SecurityAlgorithms.RsaSha256);

            if (_keys.FirstOrDefault() is { Key: not null } existing
                && (ReuseExistingKeyReference || AliasExistingPublicKey))
            {
                if (ReuseExistingKeyReference)
                {
                    var publicKeyPem = existing.Value.ExportRSAPublicKeyPem();
                    if (ReturnDifferentPublicKeyForReusedReference)
                    {
                        using var differentKey = RSA.Create(2048);
                        publicKeyPem = differentKey.ExportRSAPublicKeyPem();
                    }

                    return Task.FromResult(new SqlOSSigningKeyCreationResult(
                        algorithm,
                        publicKeyPem,
                        existing.Key));
                }

                var aliasReference = $"fake-kms:v1:alias:{kid}";
                _keys[aliasReference] = existing.Value;
                return Task.FromResult(new SqlOSSigningKeyCreationResult(
                    algorithm,
                    existing.Value.ExportRSAPublicKeyPem(),
                    aliasReference));
            }

            var rsa = RSA.Create(2048);
            var keyReference = $"fake-kms:v1:{kid}";
            _keys[keyReference] = rsa;
            return Task.FromResult(new SqlOSSigningKeyCreationResult(
                algorithm,
                rsa.ExportRSAPublicKeyPem(),
                keyReference));
        }

        public Task<byte[]> SignAsync(
            SqlOSSigningKeyDescriptor key,
            ReadOnlyMemory<byte> signingInput,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            key.CustodyProvider.Should().Be(ProviderId);
            SignCount++;
            var rsa = _keys[key.KeyReference];
            var signer = ReturnMismatchedSignature ? RSA.Create(2048) : rsa;
            try
            {
                return Task.FromResult(signer.SignData(
                    signingInput.Span,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1));
            }
            finally
            {
                if (!ReferenceEquals(signer, rsa))
                {
                    signer.Dispose();
                }
            }
        }

        public Task DeleteKeyAsync(
            SqlOSSigningKeyDescriptor key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_keys.TryRemove(key.KeyReference, out var rsa))
            {
                rsa.Dispose();
            }
            DeleteCount++;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            var disposed = new HashSet<RSA>(ReferenceEqualityComparer.Instance);
            foreach (var rsa in _keys.Values)
            {
                if (disposed.Add(rsa))
                {
                    rsa.Dispose();
                }
            }
            _keys.Clear();
        }
    }
}
