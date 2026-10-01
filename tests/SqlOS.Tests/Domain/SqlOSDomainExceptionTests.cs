using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Errors;
using SqlOS.Domain;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class SqlOSDomainExceptionTests
{
    [TestMethod]
    public void Every_error_code_has_a_default_message()
    {
        foreach (var error in Enum.GetValues<SqlOSDomainError>())
        {
            var exception = SqlOSDomainException.Of(error);

            exception.Error.Should().Be(error);
            exception.Message.Should().NotBeNullOrWhiteSpace();
        }
    }

    [TestMethod]
    public void Error_codes_are_stable()
    {
        // Codes appear in logs: never renumber one.
        ((int)SqlOSDomainError.Expired).Should().Be(1);
        ((int)SqlOSDomainError.AlreadyConsumed).Should().Be(2);
        ((int)SqlOSDomainError.Revoked).Should().Be(3);
        ((int)SqlOSDomainError.AttemptsExhausted).Should().Be(4);
        ((int)SqlOSDomainError.Disabled).Should().Be(5);
        ((int)SqlOSDomainError.InvalidEmailAddress).Should().Be(6);
        ((int)SqlOSDomainError.InvalidDomainName).Should().Be(7);
        ((int)SqlOSDomainError.InvalidPhoneNumber).Should().Be(8);
        ((int)SqlOSDomainError.InvalidRedirectUri).Should().Be(9);
        ((int)SqlOSDomainError.PasswordRejected).Should().Be(10);
        ((int)SqlOSDomainError.OwnershipProofMismatch).Should().Be(11);
        ((int)SqlOSDomainError.AggregatePartNotLoaded).Should().Be(12);
        ((int)SqlOSDomainError.UnknownMember).Should().Be(13);
        ((int)SqlOSDomainError.InvalidMemberState).Should().Be(14);
        Enum.GetValues<SqlOSDomainError>().Should().HaveCount(14, "a new code needs its own line above");
    }

    [TestMethod]
    public void Value_object_codes_default_to_the_7x_messages()
    {
        SqlOSDomainException.Of(SqlOSDomainError.InvalidEmailAddress).Message.Should().Be("Enter a valid email address.");
        SqlOSDomainException.Of(SqlOSDomainError.InvalidDomainName).Message.Should().Be("Domain is not a valid DNS name.");
        SqlOSDomainException.Of(SqlOSDomainError.InvalidPhoneNumber).Message.Should().Be("Phone number is invalid.");
    }

    [TestMethod]
    public void A_specific_message_replaces_the_default()
    {
        var exception = SqlOSDomainException.Of(SqlOSDomainError.InvalidDomainName, "Wildcard domains cannot be verified.");

        exception.Error.Should().Be(SqlOSDomainError.InvalidDomainName);
        exception.Message.Should().Be("Wildcard domains cannot be verified.");
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void A_specific_message_cannot_be_blank(string? message)
    {
        FluentActions.Invoking(() => SqlOSDomainException.Of(SqlOSDomainError.Expired, message!))
            .Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void An_undefined_code_is_rejected()
    {
        FluentActions.Invoking(() => SqlOSDomainException.Of((SqlOSDomainError)0))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void Code_that_handled_7x_validation_failures_still_catches_domain_errors()
    {
        Action act = () => throw SqlOSDomainException.Of(SqlOSDomainError.InvalidPhoneNumber);

        act.Should().Throw<InvalidOperationException>().WithMessage("Phone number is invalid.");
    }

    [TestMethod]
    public void The_public_error_mapper_maps_a_7x_message_exactly_as_before()
    {
        var mapped = SqlOSPublicAuthErrorMapper.Map(
            SqlOSDomainException.Of(SqlOSDomainError.InvalidPhoneNumber),
            SqlOSPublicAuthErrorSurface.HeadlessApi);
        var legacy = SqlOSPublicAuthErrorMapper.Map(
            new InvalidOperationException("Phone number is invalid."),
            SqlOSPublicAuthErrorSurface.HeadlessApi);

        mapped.Should().Be(legacy);
    }
}
