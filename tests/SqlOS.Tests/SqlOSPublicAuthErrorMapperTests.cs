using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Services;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSPublicAuthErrorMapperTests
{
    [TestMethod]
    public void PublicError_DefaultJsonSerialization_ExcludesDiagnosticFields()
    {
        var error = SqlOSPublicAuthErrorMapper.Map(
            new InvalidOperationException("provider_secret=do-not-return"),
            SqlOSPublicAuthErrorSurface.HeadlessApi);

        var json = JsonSerializer.Serialize(error);
        var lowerJson = json.ToLowerInvariant();

        json.Should().Contain("The request could not be completed.");
        json.Should().NotContain("provider_secret");
        lowerJson.Should().NotContain("diagnosticmessage");
        lowerJson.Should().NotContain("auditreason");
    }

    [TestMethod]
    public void PublicErrorMapper_RejectsUnsafeInvalidOperationMessages()
    {
        var mapped = SqlOSPublicAuthErrorMapper.Map(
            new InvalidOperationException("server=db01;secret=super-secret;tenant=tenant-123"),
            SqlOSPublicAuthErrorSurface.HeadlessApi);

        mapped.Error.Should().Be("invalid_request");
        mapped.PublicMessage.Should().Be(SqlOSPublicAuthErrorMapper.DefaultRequestMessage);
        mapped.PublicMessage.Should().NotContain("server=db01");
        mapped.PublicMessage.Should().NotContain("secret");
        mapped.DiagnosticMessage.Should().Contain("server=db01");
        mapped.DiagnosticMessage.Should().Contain("super-secret");
        mapped.HasDiagnosticDetail.Should().BeTrue();
    }

    [TestMethod]
    public void PublicErrorMapper_PreservesTypedPublicAuthException()
    {
        var mapped = SqlOSPublicAuthErrorMapper.Map(
            new SqlOSPublicAuthException(
                "invalid_client",
                "The client is not allowed to start this flow.",
                StatusCodes.Status401Unauthorized,
                "client_policy",
                "client_id=server=db01;secret=super-secret"),
            SqlOSPublicAuthErrorSurface.HeadlessApi);

        mapped.Error.Should().Be("invalid_client");
        mapped.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        mapped.PublicMessage.Should().Be("The client is not allowed to start this flow.");
        mapped.DiagnosticMessage.Should().Contain("server=db01");
        mapped.HasDiagnosticDetail.Should().BeTrue();
    }

    [TestMethod]
    public void PublicErrorMapper_PreservesKnownSafeHostedMessages()
    {
        var mapped = SqlOSPublicAuthErrorMapper.Map(
            new InvalidOperationException(SqlOSPasswordLoginAbuseService.PublicFailureMessage),
            SqlOSPublicAuthErrorSurface.HostedPage);

        mapped.Error.Should().Be("invalid_request");
        mapped.PublicMessage.Should().Be(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        mapped.HasDiagnosticDetail.Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow("The sign-in link is invalid or expired.")]
    [DataRow("Magic-link sign-in is unavailable.")]
    [DataRow("Too many sign-in link requests. Try again later.")]
    [DataRow("We couldn't send a sign-in link right now.")]
    [DataRow("Wait 30 seconds before requesting another sign-in link.")]
    public void PublicErrorMapper_PreservesReviewedMagicLinkMessages(string message)
    {
        var mapped = SqlOSPublicAuthErrorMapper.Map(
            new InvalidOperationException(message),
            SqlOSPublicAuthErrorSurface.HeadlessApi);

        mapped.PublicMessage.Should().Be(message);
        mapped.HasDiagnosticDetail.Should().BeFalse();
    }
}
