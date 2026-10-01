using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Domain;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class TemporaryTokenKindTests
{
    private sealed record Payload(string Name, int Count = 0);

    [TestMethod]
    public void A_fixed_lifetime_belongs_to_the_kind_and_takes_no_value()
    {
        var lifetime = TemporaryTokenLifetime.Fixed(TimeSpan.FromMinutes(10));

        lifetime.IsConfigured.Should().BeFalse();
        lifetime.Resolve(null).Should().Be(TimeSpan.FromMinutes(10));
        FluentActions.Invoking(() => lifetime.Resolve(TimeSpan.FromMinutes(5)))
            .Should().Throw<ArgumentException>("a constant cannot be overridden at one call site");
        FluentActions.Invoking(() => TemporaryTokenLifetime.Fixed(TimeSpan.Zero)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void A_configured_lifetime_needs_its_value_and_uses_it_as_the_host_set_it()
    {
        var lifetime = TemporaryTokenLifetime.Configured("SqlOSMagicLinkOptions.TokenLifetime");

        lifetime.IsConfigured.Should().BeTrue();
        lifetime.Setting.Should().Be("SqlOSMagicLinkOptions.TokenLifetime");
        lifetime.Resolve(TimeSpan.FromMinutes(3)).Should().Be(TimeSpan.FromMinutes(3));
        lifetime.Resolve(TimeSpan.Zero).Should().Be(TimeSpan.Zero, "7.x issued an already-expired token for a zero lifetime rather than failing");
        FluentActions.Invoking(() => lifetime.Resolve(null))
            .Should().Throw<ArgumentException>("a setting cannot be forgotten at one call site")
            .WithMessage("*SqlOSMagicLinkOptions.TokenLifetime*");
        default(TemporaryTokenLifetime).IsConfigured.Should().BeTrue();
        default(TemporaryTokenLifetime).ToString().Should().Be("configured");
    }

    [TestMethod]
    public void A_kind_allows_only_the_bindings_its_purpose_defines()
    {
        var kind = new TemporaryTokenKind<Payload>("test", TemporaryTokenLifetime.Fixed(TimeSpan.FromMinutes(1)), TemporaryTokenBindings.User | TemporaryTokenBindings.ClientApplication);

        FluentActions.Invoking(() => kind.EnsureAllows(new TemporaryTokenBinding(UserId: "usr_1", ClientApplicationId: "cli_1"))).Should().NotThrow();
        FluentActions.Invoking(() => kind.EnsureAllows(new TemporaryTokenBinding(UserId: "usr_1"))).Should().NotThrow("a declared binding may be empty");
        FluentActions.Invoking(() => kind.EnsureAllows(TemporaryTokenBinding.None)).Should().NotThrow();
        FluentActions.Invoking(() => kind.EnsureAllows(new TemporaryTokenBinding(OrganizationId: "org_1")))
            .Should().Throw<ArgumentException>().WithMessage("*'test'*Organization*");
        FluentActions.Invoking(() => kind.EnsureAllows(new TemporaryTokenBinding(IssuerSessionFamilyId: "aps_1")))
            .Should().Throw<ArgumentException>().WithMessage("*IssuerSession*");
    }

    [TestMethod]
    public void A_payload_round_trips_as_system_text_json_defaults_write_it()
    {
        var kind = new TemporaryTokenKind<Payload>("test", TemporaryTokenLifetime.Fixed(TimeSpan.FromMinutes(1)), TemporaryTokenBindings.None);

        var json = kind.Serialize(new Payload("x", 2));

        json.Should().Be("{\"Name\":\"x\",\"Count\":2}", "member names as declared, in declaration order");
        kind.Deserialize(json).Should().Be(new Payload("x", 2));
        kind.Serialize(null).Should().BeNull();
        kind.Deserialize(null).Should().BeNull();
        kind.Deserialize("  ").Should().BeNull();
        kind.PayloadType.Should().Be(typeof(Payload));
    }

    [TestMethod]
    public void An_object_kind_serializes_the_runtime_type_as_the_7x_api_did()
    {
        var kind = new TemporaryTokenKind<object>("host", TemporaryTokenLifetime.Configured("host"), TemporaryTokenBindings.All);

        kind.Serialize(new Payload("y", 1)).Should().Be("{\"Name\":\"y\",\"Count\":1}");
        kind.Serialize(new { a = 1 }).Should().Be("{\"a\":1}");
    }

    [TestMethod]
    public void A_kind_names_its_purpose_and_rejects_a_purpose_the_column_cannot_hold()
    {
        var kind = new TemporaryTokenKind<Payload>("purpose", TemporaryTokenLifetime.Fixed(TimeSpan.FromMinutes(1)), TemporaryTokenBindings.None);

        kind.ToString().Should().Be("purpose");
        kind.IsSingleUse.Should().BeTrue("single use is the default");
        FluentActions.Invoking(() => new TemporaryTokenKind<Payload>(" ", TemporaryTokenLifetime.Fixed(TimeSpan.FromMinutes(1)), TemporaryTokenBindings.None))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new TemporaryTokenKind<Payload>(new string('p', 81), TemporaryTokenLifetime.Fixed(TimeSpan.FromMinutes(1)), TemporaryTokenBindings.None))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void A_kind_may_declare_the_event_issuing_one_of_its_tokens_raises()
    {
        var plain = new TemporaryTokenKind<Payload>("plain", TemporaryTokenLifetime.Fixed(TimeSpan.FromMinutes(1)), TemporaryTokenBindings.User);
        var audited = new TemporaryTokenKind<Payload>(
            "audited",
            TemporaryTokenLifetime.Fixed(TimeSpan.FromMinutes(1)),
            TemporaryTokenBindings.User,
            issued: static (tokenId, binding) => new Issued(tokenId, binding.UserId));

        plain.IssuedEvent("tmp_1", new TemporaryTokenBinding(UserId: "usr_1")).Should().BeNull();
        audited.IssuedEvent("tmp_1", new TemporaryTokenBinding(UserId: "usr_1")).Should().Be(new Issued("tmp_1", "usr_1"));
    }

    private sealed record Issued(string TokenId, string? UserId) : ISqlOSDomainEvent;
}
