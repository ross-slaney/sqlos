using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Services;

namespace SqlOS.AuthServer.Authentication;

internal sealed class SqlOSJwtHandler : AuthenticationHandler<SqlOSJwtOptions>
{
    public SqlOSJwtHandler(
        IOptionsMonitor<SqlOSJwtOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var validation = ToValidationOptions();
        if (string.IsNullOrWhiteSpace(validation.ExpectedAudience))
        {
            return AuthenticateResult.Fail("SqlOS JWT authentication requires an expected audience.");
        }

        var authService = Context.RequestServices.GetRequiredService<SqlOSAuthService>();
        var ticket = await SqlOSBearerAuthentication.AuthenticateAsync(
            Context,
            validation,
            authService,
            Context.RequestAborted);

        if (ticket.Kind == SqlOSBearerTicketKind.Missing)
        {
            return AuthenticateResult.NoResult();
        }

        if (ticket.Kind == SqlOSBearerTicketKind.InsufficientScope)
        {
            Context.Items[SqlOSJwtDefaults.ChallengeErrorItemKey] = "insufficient_scope";
            Context.Items[SqlOSJwtDefaults.ChallengeDescriptionItemKey] = ticket.Failure;
            return AuthenticateResult.Fail(ticket.Failure);
        }

        if (ticket.Kind != SqlOSBearerTicketKind.Success || ticket.Token == null)
        {
            Context.Items[SqlOSJwtDefaults.ChallengeErrorItemKey] = "invalid_token";
            Context.Items[SqlOSJwtDefaults.ChallengeDescriptionItemKey] = ticket.Failure;
            return AuthenticateResult.Fail(ticket.Failure);
        }

        return AuthenticateResult.Success(new AuthenticationTicket(ticket.Token.Principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var error = Context.Items.TryGetValue(SqlOSJwtDefaults.ChallengeErrorItemKey, out var storedError)
            ? storedError as string
            : "invalid_token";
        var description = Context.Items.TryGetValue(SqlOSJwtDefaults.ChallengeDescriptionItemKey, out var storedDescription)
            ? storedDescription as string
            : "A bearer access token is required.";

        if (string.Equals(error, "insufficient_scope", StringComparison.Ordinal))
        {
            return SqlOSBearerAuthentication.WriteInsufficientScopeAsync(
                Response,
                ToValidationOptions(),
                description ?? "The access token's granted scope is insufficient.");
        }

        return SqlOSBearerAuthentication.WriteUnauthorizedAsync(
            Response,
            ToValidationOptions(),
            description ?? "A bearer access token is required.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        var description = Context.Items.TryGetValue(SqlOSJwtDefaults.ChallengeDescriptionItemKey, out var storedDescription)
            ? storedDescription as string
            : "The access token's granted scope is insufficient.";
        return SqlOSBearerAuthentication.WriteInsufficientScopeAsync(
            Response,
            ToValidationOptions(),
            description ?? "The access token's granted scope is insufficient.");
    }

    private SqlOSAccessTokenValidationOptions ToValidationOptions()
        => new()
        {
            ExpectedAudience = Options.ExpectedAudience ?? string.Empty,
            RequiredScopes = Options.RequiredScopes,
            Realm = Options.Realm,
            ResourceMetadataUrl = Options.ResourceMetadataUrl
        };
}
