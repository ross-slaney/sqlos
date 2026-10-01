using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Domain;
using static SqlOS.Tests.Domain.DomainTime;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class RevocationTests
{
    [TestMethod]
    public void Revoking_records_the_time_and_reason()
    {
        var revoked = new Revocation(null, null).Revoke("user_logout", Now);

        revoked.IsRevoked.Should().BeTrue();
        revoked.RevokedAt.Should().Be(Now);
        revoked.Reason.Should().Be("user_logout");
    }

    [TestMethod]
    public void Revoking_again_is_idempotent_and_keeps_the_first_time_and_reason()
    {
        var first = new Revocation(null, null).Revoke("email_claimed", Now);

        var second = first.Revoke("admin_revoked", Now.AddHours(1));

        second.Should().Be(first);
        second.RevokedAt.Should().Be(Now);
        second.Reason.Should().Be("email_claimed");
    }

    [TestMethod]
    public void A_stored_revocation_without_a_reason_column_stays_revoked()
    {
        var stored = new Revocation(Now, null);

        stored.IsRevoked.Should().BeTrue();
        stored.Revoke("later", Now.AddDays(1)).Should().Be(stored);
    }

    [TestMethod]
    public void EnsureNotRevoked_throws_only_once_revoked()
    {
        FluentActions.Invoking(() => new Revocation(null, null).EnsureNotRevoked()).Should().NotThrow();
        FluentActions.Invoking(() => new Revocation(Now, "x").EnsureNotRevoked())
            .Should().Throw<SqlOSDomainException>()
            .Which.Error.Should().Be(SqlOSDomainError.Revoked);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void A_revocation_needs_a_reason(string? reason)
    {
        FluentActions.Invoking(() => new Revocation(null, null).Revoke(reason!, Now))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new Revocation(Now, "x").Revoke(reason!, Now))
            .Should().Throw<ArgumentException>("the reason is validated even when the call is a no-op");
    }

    [TestMethod]
    public void The_default_revocation_is_not_revoked()
    {
        default(Revocation).IsRevoked.Should().BeFalse();
        default(Revocation).Should().Be(new Revocation(null, null));
    }

    [TestMethod]
    public void Revocations_are_equal_by_time_and_reason()
    {
        new Revocation(Now, "a").Should().Be(new Revocation(Now, "a"));
        new Revocation(Now, "a").Should().NotBe(new Revocation(Now, "b"));
        new Revocation(Now, "a").Should().NotBe(new Revocation(Now.Tick(1), "a"));
    }
}
