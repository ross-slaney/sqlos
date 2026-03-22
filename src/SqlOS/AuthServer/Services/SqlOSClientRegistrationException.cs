using Microsoft.AspNetCore.Http;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSClientRegistrationException : InvalidOperationException
{
    public SqlOSClientRegistrationException(
        string error,
        string message,
        int statusCode = StatusCodes.Status400BadRequest)
        : base(message)
    {
        Error = error;
        StatusCode = statusCode;
    }

    public string Error { get; }

    public int StatusCode { get; }
}
