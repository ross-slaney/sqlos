using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Example.Api.Configuration;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.Services;

namespace SqlOS.Example.Api.Endpoints;

public static class ExampleAuthEndpoints
{
    private const string SsoStateCookie = "sqlos_example_sso_state";
    private const string SsoVerifierCookie = "sqlos_example_sso_verifier";
    private const string SsoRedirectUriCookie = "sqlos_example_sso_redirect_uri";
    private const string SsoClientIdCookie = "sqlos_example_sso_client_id";

    public static void MapExampleAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth");

        auth.MapPost("/discover", (SqlOSHomeRealmDiscoveryRequest request, SqlOSHomeRealmDiscoveryService discoveryService, CancellationToken cancellationToken) =>
            TryAsync(async () => Results.Ok(await discoveryService.DiscoverAsync(request, cancellationToken))));

        auth.MapPost("/login", (ExamplePasswordLoginRequest request, SqlOSAuthService authService, ExampleFgaService fgaService, ExampleAppDbContext context, IOptions<ExampleWebOptions> webOptions, HttpContext httpContext, CancellationToken cancellationToken) =>
            TryAsync(async () =>
            {
                var result = await authService.LoginWithPasswordAsync(
                    new SqlOSPasswordLoginRequest(request.Email, request.Password, webOptions.Value.ClientId, request.OrganizationId),
                    httpContext,
                    cancellationToken);

                return Results.Ok(await ToLoginResponseAsync(result, request.Email, context, authService, fgaService, cancellationToken));
            }));

        auth.MapPost("/select-organization", (ExampleSelectOrganizationRequest request, SqlOSAuthService authService, ExampleFgaService fgaService, ExampleAppDbContext context, HttpContext httpContext, CancellationToken cancellationToken) =>
            TryAsync(async () =>
            {
                var tokens = await authService.SelectOrganizationAsync(
                    new SqlOSSelectOrganizationRequest(request.PendingAuthToken, request.OrganizationId),
                    httpContext,
                    cancellationToken);

                return Results.Ok(await ToTokenResponseAsync(tokens, context, authService, fgaService, cancellationToken));
            }));

        auth.MapPost("/sso/start", (ExampleSsoStartRequest request, SqlOSSsoAuthorizationService ssoAuthorizationService, SqlOSCryptoService cryptoService, IOptions<ExampleWebOptions> webOptions, HttpContext httpContext, CancellationToken cancellationToken) =>
            TryAsync(async () =>
            {
                var state = cryptoService.GenerateOpaqueToken();
                var codeVerifier = cryptoService.GenerateOpaqueToken();
                var codeChallenge = cryptoService.CreatePkceCodeChallenge(codeVerifier);
                var options = webOptions.Value;

                var result = await ssoAuthorizationService.StartAuthorizationAsync(
                    new SqlOSSsoAuthorizationStartRequest(
                        request.Email,
                        options.ClientId,
                        options.CallbackUrl,
                        state,
                        codeChallenge,
                        "S256"),
                    cancellationToken);

                SetSsoCookie(httpContext.Response, SsoStateCookie, state, httpContext);
                SetSsoCookie(httpContext.Response, SsoVerifierCookie, codeVerifier, httpContext);
                SetSsoCookie(httpContext.Response, SsoRedirectUriCookie, options.CallbackUrl, httpContext);
                SetSsoCookie(httpContext.Response, SsoClientIdCookie, options.ClientId, httpContext);

                return Results.Ok(result);
            }));

        auth.MapPost("/sso/exchange", (ExampleSsoExchangeRequest request, SqlOSSsoAuthorizationService ssoAuthorizationService, ExampleFgaService fgaService, ExampleAppDbContext context, SqlOSAuthService authService, IOptions<ExampleWebOptions> webOptions, HttpContext httpContext, CancellationToken cancellationToken) =>
            TryAsync(async () =>
            {
                var storedState = httpContext.Request.Cookies[SsoStateCookie];
                var codeVerifier = httpContext.Request.Cookies[SsoVerifierCookie];
                var redirectUri = httpContext.Request.Cookies[SsoRedirectUriCookie];
                var clientId = httpContext.Request.Cookies[SsoClientIdCookie] ?? webOptions.Value.ClientId;

                if (string.IsNullOrWhiteSpace(storedState) || string.IsNullOrWhiteSpace(codeVerifier) || string.IsNullOrWhiteSpace(redirectUri))
                {
                    throw new InvalidOperationException("SSO exchange cookies are missing or expired.");
                }

                if (!string.Equals(storedState, request.State, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("SSO state validation failed.");
                }

                var tokens = await ssoAuthorizationService.ExchangeCodeAsync(
                    new SqlOSPkceExchangeRequest(request.Code, clientId, redirectUri, codeVerifier),
                    httpContext,
                    cancellationToken);

                ClearSsoCookies(httpContext.Response, httpContext);
                return Results.Ok(await ToTokenResponseAsync(tokens, context, authService, fgaService, cancellationToken));
            }));

        auth.MapPost("/refresh", (ExampleRefreshRequest request, SqlOSAuthService authService, ExampleFgaService fgaService, ExampleAppDbContext context, CancellationToken cancellationToken) =>
            TryAsync(async () =>
            {
                var tokens = await authService.RefreshAsync(new SqlOSRefreshRequest(request.RefreshToken, request.OrganizationId), cancellationToken);
                return Results.Ok(await ToTokenResponseAsync(tokens, context, authService, fgaService, cancellationToken));
            }));

        auth.MapPost("/logout", (ExampleLogoutRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
            TryAsync(async () =>
            {
                await authService.LogoutAsync(request.RefreshToken, request.SessionId, cancellationToken);
                return Results.NoContent();
            }));

        auth.MapGet("/session", async (HttpContext httpContext, SqlOSAuthService authService, ExampleAppDbContext context, CancellationToken cancellationToken) =>
        {
            var bearerToken = httpContext.Request.Headers.Authorization.ToString();
            if (!bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Unauthorized();
            }

            var validated = await authService.ValidateAccessTokenAsync(bearerToken["Bearer ".Length..].Trim(), cancellationToken);
            if (validated == null)
            {
                return Results.Unauthorized();
            }

            var session = await context.Set<SqlOSSession>()
                .Include(x => x.User)
                .Include(x => x.ClientApplication)
                .FirstOrDefaultAsync(x => x.Id == validated.SessionId, cancellationToken);

            return Results.Ok(new
            {
                user = session?.User == null ? null : new
                {
                    session.User.Id,
                    session.User.DisplayName,
                    Email = session.User.DefaultEmail
                },
                session = session == null ? null : new
                {
                    session.Id,
                    session.AuthenticationMethod,
                    ClientId = session.ClientApplication?.ClientId,
                    session.CreatedAt,
                    session.LastSeenAt,
                    session.IdleExpiresAt,
                    session.AbsoluteExpiresAt,
                    session.RevokedAt
                },
                token = new
                {
                    validated.UserId,
                    validated.OrganizationId,
                    validated.ClientId,
                    claims = validated.Principal.Claims.Select(x => new { x.Type, x.Value })
                }
            });
        });
    }

    private static async Task<object> ToLoginResponseAsync(
        SqlOSLoginResult result,
        string email,
        ExampleAppDbContext context,
        SqlOSAuthService authService,
        ExampleFgaService fgaService,
        CancellationToken cancellationToken)
    {
        if (result.Tokens == null)
        {
            return new
            {
                result.RequiresOrganizationSelection,
                result.PendingAuthToken,
                result.Organizations
            };
        }

        var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
        var user = await context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .Where(x => x.NormalizedEmail == normalizedEmail)
            .Select(x => x.User!)
            .FirstAsync(cancellationToken);

        return await ToTokenResponseAsync(result.Tokens, user, authService, fgaService, cancellationToken);
    }

    private static async Task<object> ToTokenResponseAsync(
        SqlOSTokenResponse tokens,
        ExampleAppDbContext context,
        SqlOSAuthService authService,
        ExampleFgaService fgaService,
        CancellationToken cancellationToken)
    {
        var validated = await authService.ValidateAccessTokenAsync(tokens.AccessToken, cancellationToken)
            ?? throw new InvalidOperationException("Access token validation failed.");

        var user = await context.Set<SqlOSUser>().FirstAsync(x => x.Id == validated.UserId, cancellationToken);
        return await ToTokenResponseAsync(tokens, user, authService, fgaService, cancellationToken);
    }

    private static async Task<object> ToTokenResponseAsync(
        SqlOSTokenResponse tokens,
        SqlOSUser user,
        SqlOSAuthService authService,
        ExampleFgaService fgaService,
        CancellationToken cancellationToken)
    {
        var validated = await authService.ValidateAccessTokenAsync(tokens.AccessToken, cancellationToken)
            ?? throw new InvalidOperationException("Access token validation failed.");

        if (!string.IsNullOrWhiteSpace(tokens.OrganizationId))
        {
            await fgaService.EnsureUserAccessAsync(user.Id, tokens.OrganizationId, cancellationToken);
        }

        return new
        {
            user = new
            {
                user.Id,
                Email = user.DefaultEmail,
                user.DisplayName
            },
            accessToken = tokens.AccessToken,
            refreshToken = tokens.RefreshToken,
            sessionId = tokens.SessionId,
            clientId = tokens.ClientId,
            organizationId = tokens.OrganizationId,
            accessTokenExpiresAt = tokens.AccessTokenExpiresAt,
            refreshTokenExpiresAt = tokens.RefreshTokenExpiresAt,
            claims = validated.Principal.Claims.Select(x => new { x.Type, x.Value })
        };
    }

    private static void SetSsoCookie(HttpResponse response, string name, string value, HttpContext httpContext)
    {
        response.Cookies.Append(name, value, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = httpContext.Request.IsHttps,
            MaxAge = TimeSpan.FromMinutes(10)
        });
    }

    private static void ClearSsoCookies(HttpResponse response, HttpContext httpContext)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = httpContext.Request.IsHttps
        };

        response.Cookies.Delete(SsoStateCookie, options);
        response.Cookies.Delete(SsoVerifierCookie, options);
        response.Cookies.Delete(SsoRedirectUriCookie, options);
        response.Cookies.Delete(SsoClientIdCookie, options);
    }

    private static async Task<IResult> TryAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private sealed record ExamplePasswordLoginRequest(string Email, string Password, string? OrganizationId);
    private sealed record ExampleSelectOrganizationRequest(string PendingAuthToken, string OrganizationId);
    private sealed record ExampleSsoStartRequest(string Email);
    private sealed record ExampleSsoExchangeRequest(string Code, string State);
    private sealed record ExampleRefreshRequest(string RefreshToken, string? OrganizationId);
    private sealed record ExampleLogoutRequest(string? RefreshToken, string? SessionId);
}
