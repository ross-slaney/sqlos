using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.Domain;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class HashedSecretTests
{
    private const string Password = "correct horse battery staple";

    // Stored 7.2.1 values. PasswordHasher<object> on .NET 9 writes Identity v3 payloads
    // (PBKDF2-HMAC-SHA512, 100,000 iterations); the two older payloads are what earlier ASP.NET Core
    // Identity versions wrote, and 7.x accepts them without rehashing.
    private const string Pbkdf2V3 = "AQAAAAIAAYagAAAAEEgEoe3AxG+IjuQnLiqD0QgXgoznu19m5255UQYUvwlS9I//F2TfLQex/3ZPoKorkg==";
    private const string Pbkdf2V3Sha256TenThousand = "AQAAAAEAACcQAAAAEAMKERgfJi00O0JJUFdeZWx2joHlVjd4vMxoYCUWfBfF7fOClowlIy2U2tEvqfGFYQ==";
    private const string Pbkdf2V2 = "ACGNt6yKLcj0xYGice2e24mdF7wIymHsdqi+uYGZxZd1b03I71n+UUEhYfBkNgWO1A==";

    [DataTestMethod]
    [DataRow("raw-token-value", "43A2860580227E27192A056EEC8CCEABF9C499D5C20C755F4B75434330253DE4")]
    [DataRow("tok:123456", "A0150B2D95583F861AC0DE8A19B2A27F72C0C092D797B6BA675425A911A89DFD")]
    [DataRow("üñí©ødé", "7FA1F07DC5C3CEE557EAFAAC5625609472584924BC23AE5F1784A61C347DB3CB")]
    [DataRow("", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    public void Sha256_writes_the_7x_uppercase_hex_of_the_utf8_secret(string secret, string stored)
    {
        var hashed = HashedSecret.Sha256(secret);

        hashed.Scheme.Should().Be(HashedSecretScheme.Sha256);
        hashed.Hash.Should().Be(stored);
        CreateCrypto().HashToken(secret).Should().Be(stored, "SqlOSCryptoService.HashToken delegates to the part");
    }

    [DataTestMethod]
    [DataRow("raw-token-value", "43A2860580227E27192A056EEC8CCEABF9C499D5C20C755F4B75434330253DE4")]
    [DataRow("tok:123456", "A0150B2D95583F861AC0DE8A19B2A27F72C0C092D797B6BA675425A911A89DFD")]
    public void A_stored_sha256_hash_matches_only_its_secret(string secret, string stored)
    {
        var hashed = HashedSecret.FromStored(HashedSecretScheme.Sha256, stored);

        hashed.Matches(secret).Should().BeTrue();
        hashed.Matches(secret + " ").Should().BeFalse();
        hashed.Matches(secret.ToUpperInvariant() + "x").Should().BeFalse();
        hashed.Matches(string.Empty).Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow("43a2860580227e27192a056eec8cceabf9c499d5c20c755f4b75434330253de4", DisplayName = "lower-case hex")]
    [DataRow("43A2860580227E27192A056EEC8CCEABF9C499D5C20C755F4B75434330253DE", DisplayName = "truncated")]
    [DataRow("43A2860580227E27192A056EEC8CCEABF9C499D5C20C755F4B75434330253DE40", DisplayName = "too long")]
    [DataRow("", DisplayName = "empty")]
    [DataRow("not hex at all", DisplayName = "not hex")]
    [DataRow("43A2860580227E27192A056EEC8CCEABF9C499D5C20C755F4B75434330253DÉ4", DisplayName = "non-ASCII")]
    public void A_malformed_or_differently_cased_sha256_value_never_matches_and_never_throws(string stored)
    {
        // 7.x compares SHA-256 hashes ordinally, so only the exact stored form matches.
        HashedSecret.FromStored(HashedSecretScheme.Sha256, stored).Matches("raw-token-value").Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow(Pbkdf2V3, DisplayName = "Identity v3, HMAC-SHA512, 100,000 iterations")]
    [DataRow(Pbkdf2V3Sha256TenThousand, DisplayName = "Identity v3, HMAC-SHA256, 10,000 iterations")]
    [DataRow(Pbkdf2V2, DisplayName = "Identity v2")]
    public void A_stored_7x_password_hash_matches_its_password_without_rehashing(string stored)
    {
        var hashed = HashedSecret.FromStored(HashedSecretScheme.Pbkdf2, stored);

        hashed.Matches(Password).Should().BeTrue();
        hashed.Matches("Correct horse battery staple").Should().BeFalse();
        hashed.Matches(Password + " ").Should().BeFalse();
        hashed.Matches(string.Empty).Should().BeFalse();
        hashed.Hash.Should().Be(stored, "wrapping a stored value never changes it");
        CreateCrypto().VerifyPassword(stored, Password).Should().BeTrue("SqlOSCryptoService.VerifyPassword delegates to the part");
        CreateCrypto().VerifyPassword(stored, "wrong").Should().BeFalse();
    }

    [TestMethod]
    public void Pbkdf2_writes_a_salted_identity_v3_payload_that_round_trips()
    {
        var first = HashedSecret.Pbkdf2(Password);
        var second = HashedSecret.Pbkdf2(Password);

        first.Scheme.Should().Be(HashedSecretScheme.Pbkdf2);
        Convert.FromBase64String(first.Hash)[0].Should().Be(0x01, "Identity v3 header");
        Convert.FromBase64String(first.Hash).Length.Should().Be(Convert.FromBase64String(Pbkdf2V3).Length);
        HashedSecret.IsPbkdf2Payload(first.Hash).Should().BeTrue();
        first.Hash.Should().NotBe(second.Hash, "every hash has its own salt");
        HashedSecret.FromStored(HashedSecretScheme.Pbkdf2, first.Hash).Matches(Password).Should().BeTrue();
        HashedSecret.FromStored(HashedSecretScheme.Pbkdf2, second.Hash).Matches(Password).Should().BeTrue();
        CreateCrypto().VerifyPassword(CreateCrypto().HashPassword(Password), Password).Should().BeTrue();
    }

    [TestMethod]
    public void A_malformed_pbkdf2_value_behaves_exactly_like_the_password_hasher()
    {
        HashedSecret.FromStored(HashedSecretScheme.Pbkdf2, string.Empty).Matches(Password).Should().BeFalse();
        HashedSecret.FromStored(HashedSecretScheme.Pbkdf2, Convert.ToBase64String([1, 2, 3])).Matches(Password).Should().BeFalse();
        HashedSecret.FromStored(HashedSecretScheme.Pbkdf2, Convert.ToBase64String([9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])).Matches(Password).Should().BeFalse();
        FluentActions.Invoking(() => HashedSecret.FromStored(HashedSecretScheme.Pbkdf2, "not base64!").Matches(Password))
            .Should().Throw<FormatException>("7.x VerifyPassword throws on a stored value that is not base64");
    }

    [DataTestMethod]
    [DataRow(Pbkdf2V3, true)]
    [DataRow(Pbkdf2V2, true)]
    [DataRow(Pbkdf2V3Sha256TenThousand, true)]
    [DataRow("AQAAAAEAACcQAAAA", false, DisplayName = "header shorter than 13 bytes")]
    [DataRow("AgAAAAEAACcQAAAAEAMKERgfJi00O0JJUFdeZWx2joHl", false, DisplayName = "unknown version marker")]
    [DataRow("43A2860580227E27192A056EEC8CCEABF9C499D5C20C755F4B75434330253DE4", false, DisplayName = "a SHA-256 hash")]
    [DataRow("not base64!", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void IsPbkdf2Payload_accepts_only_password_hasher_payloads(string? hash, bool expected)
    {
        HashedSecret.IsPbkdf2Payload(hash).Should().Be(expected);
    }

    [TestMethod]
    public void The_default_value_never_matches()
    {
        default(HashedSecret).Matches(Password).Should().BeFalse();
        default(HashedSecret).Matches(string.Empty).Should().BeFalse();
    }

    [TestMethod]
    public void ToString_never_shows_the_hash()
    {
        var hashed = HashedSecret.FromStored(HashedSecretScheme.Pbkdf2, Pbkdf2V3);

        hashed.ToString().Should().Be("HashedSecret(Pbkdf2)");
        HashedSecret.Sha256("raw-token-value").ToString().Should().Be("HashedSecret(Sha256)");
        $"{hashed}".Should().NotContain(Pbkdf2V3);
    }

    [TestMethod]
    public void Hashed_secrets_are_equal_by_scheme_and_stored_hash()
    {
        HashedSecret.Sha256("a").Should().Be(HashedSecret.FromStored(HashedSecretScheme.Sha256, HashedSecret.Sha256("a").Hash));
        HashedSecret.Sha256("a").Should().NotBe(HashedSecret.Sha256("b"));
        HashedSecret.FromStored(HashedSecretScheme.Sha256, Pbkdf2V3).Should().NotBe(HashedSecret.FromStored(HashedSecretScheme.Pbkdf2, Pbkdf2V3));
    }

    [TestMethod]
    public void Nulls_throw_the_7x_argument_exceptions_of_the_underlying_hash_functions()
    {
        // The behavior lock records these messages: the public refresh API without a refresh token
        // fails inside HashToken with Encoding.GetBytes's "Parameter 's'".
        FluentActions.Invoking(() => HashedSecret.Sha256(null!)).Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("s");
        FluentActions.Invoking(() => CreateCrypto().HashToken(null!)).Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("s");
        FluentActions.Invoking(() => HashedSecret.Pbkdf2(null!)).Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("password");
        FluentActions.Invoking(() => CreateCrypto().HashPassword(null!)).Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("password");
        FluentActions.Invoking(() => CreateCrypto().VerifyPassword(null!, Password)).Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("hashedPassword");
        FluentActions.Invoking(() => CreateCrypto().VerifyPassword(Pbkdf2V3, null!)).Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("providedPassword");
        FluentActions.Invoking(() => HashedSecret.Sha256("a").Matches(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => HashedSecret.FromStored(HashedSecretScheme.Sha256, null!).Matches("a")).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => HashedSecret.FromStored((HashedSecretScheme)0, "x")).Should().Throw<ArgumentOutOfRangeException>();
    }

    // Hashing never touches the database; the context only satisfies the constructor.
    private static readonly AuthServer.Services.SqlOSCryptoService Crypto = TestCryptoService.Create(
        new TestSqlOSInMemoryDbContext(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(nameof(HashedSecretTests))
            .Options),
        Options.Create(new SqlOSAuthServerOptions()));

    private static AuthServer.Services.SqlOSCryptoService CreateCrypto() => Crypto;
}
