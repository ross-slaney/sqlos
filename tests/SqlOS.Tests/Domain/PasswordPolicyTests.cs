using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Policies;
using SqlOS.Domain;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class PasswordPolicyTests
{
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t\r\n")]
    [DataRow("  ")]
    public void The_80_policy_refuses_a_blank_password_with_the_7x_message(string? password)
    {
        var refusal = PasswordPolicy.Default.Check(password);

        refusal.Should().Be(new PasswordRefusal("required", "Password is required."));
        FluentActions.Invoking(() => PasswordPolicy.Default.Enforce(password))
            .Should().Throw<SqlOSDomainException>()
            .Which.Should().Match<SqlOSDomainException>(exception =>
                exception.Error == SqlOSDomainError.PasswordRejected && exception.Message == "Password is required.");
    }

    [DataTestMethod]
    [DataRow("x")]
    [DataRow(" padded ")]
    [DataRow("P@ssword123!")]
    public void The_80_policy_accepts_any_password_that_is_not_blank(string password)
    {
        PasswordPolicy.Default.Check(password).Should().BeNull("8.0 keeps 7.2.1's only rule; #417 adds more");
        FluentActions.Invoking(() => PasswordPolicy.Default.Enforce(password)).Should().NotThrow();
    }

    [TestMethod]
    public void A_new_rule_extends_the_policy_and_the_first_broken_rule_decides()
    {
        var policy = new PasswordPolicy([new PasswordIsNotBlank(), new MinimumLength(12), new MinimumLength(20)]);

        policy.Check(" ")!.Rule.Should().Be("required");
        policy.Check("short")!.Message.Should().Be("At least 12 characters.");
        policy.Check("exactly-12-c")!.Message.Should().Be("At least 20 characters.");
        policy.Check("a-password-of-twenty").Should().BeNull();
    }

    [TestMethod]
    public void A_policy_has_no_null_rules()
    {
        FluentActions.Invoking(() => new PasswordPolicy([new PasswordIsNotBlank(), null!]))
            .Should().Throw<ArgumentException>();
    }

    private sealed class MinimumLength(int length) : IPasswordRule
    {
        public PasswordRefusal? Check(string? password)
            => password is { Length: var actual } && actual >= length ? null : new PasswordRefusal("length", $"At least {length} characters.");
    }
}
