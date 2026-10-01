using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.Domain;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class SqlOSIdsTests
{
    [DataTestMethod]
    [DataRow("usr")]
    [DataRow("org")]
    [DataRow("evt")]
    [DataRow("grant")]
    [DataRow("clcred")]
    [DataRow("a_b")]
    public void New_writes_the_prefix_an_underscore_and_24_lowercase_hex_characters(string prefix)
    {
        var id = SqlOSIds.New(prefix);

        id.Should().MatchRegex($"^{Regex.Escape(prefix)}_[0-9a-f]{{24}}$");
        id.Length.Should().Be(prefix.Length + 1 + SqlOSIds.RandomLength);
        id[prefix.Length + 1 + 12].Should().Be('4', "the random part is a version 4 GUID");
    }

    [TestMethod]
    public void New_matches_the_7x_expression_length_for_every_prefix()
    {
        foreach (var prefix in new[] { "usr", "x", "averyveryverylongprefix", string.Empty })
        {
            var legacy = $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 1 + 24, prefix.Length + 1 + 32)];

            SqlOSIds.New(prefix).Length.Should().Be(legacy.Length);
        }
    }

    [TestMethod]
    public void An_empty_prefix_keeps_the_7x_shape()
    {
        SqlOSIds.New(string.Empty).Should().MatchRegex("^_[0-9a-f]{24}$");
    }

    [TestMethod]
    public void New_never_repeats()
    {
        Enumerable.Range(0, 10_000).Select(_ => SqlOSIds.New("usr")).Distinct().Should().HaveCount(10_000);
    }

    [TestMethod]
    public void A_null_prefix_is_rejected()
    {
        FluentActions.Invoking(() => SqlOSIds.New(null!)).Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void SqlOSCryptoService_GenerateId_delegates_to_New()
    {
        using var context = new TestSqlOSInMemoryDbContext(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(nameof(SqlOSIdsTests))
            .Options);
        var crypto = TestCryptoService.Create(context, Options.Create(new SqlOSAuthServerOptions()));

        crypto.GenerateId("usr").Should().MatchRegex("^usr_[0-9a-f]{24}$");
        crypto.GenerateId("tmp").Should().MatchRegex("^tmp_[0-9a-f]{24}$");
    }
}
