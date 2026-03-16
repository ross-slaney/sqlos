using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
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
    private const string OidcStateCookie = "sqlos_example_oidc_state";
    private const string OidcVerifierCookie = "sqlos_example_oidc_verifier";
    private const string OidcCallbackUriCookie = "sqlos_example_oidc_callback_uri";
    private const string OidcClientIdCookie = "sqlos_example_oidc_client_id";
    private const string OidcConnectionIdCookie = "sqlos_example_oidc_connection_id";
    private const string OidcNonceCookie = "sqlos_example_oidc_nonce";

    public static void MapExampleAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth");
        auth.ExcludeFromDescription();

        auth.MapPost("/discover", (SqlOSHomeRealmDiscoveryRequest request, SqlOSHomeRealmDiscoveryService discoveryService, CancellationToken cancellationToken) =>
            TryAsync(async () => Results.Ok(await discoveryService.DiscoverAsync(request, cancellationToken))));

        auth.MapGet("/oidc/providers", (SqlOSOidcAuthService oidcAuthService, CancellationToken cancellationToken) =>
            TryAsync(async () => Results.Ok(await oidcAuthService.ListEnabledProvidersAsync(cancellationToken))));

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
                var result = await StartSsoAsync(
                    request.Email,
                    httpContext,
                    webOptions.Value,
                    cryptoService,
                    ssoAuthorizationService,
                    cancellationToken);

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

        auth.MapPost("/oidc/start", (ExampleOidcStartRequest request, SqlOSHomeRealmDiscoveryService discoveryService, SqlOSOidcAuthService oidcAuthService, SqlOSCryptoService cryptoService, IOptions<ExampleWebOptions> webOptions, HttpContext httpContext, CancellationToken cancellationToken) =>
            TryAsync(async () =>
            {
                var discovery = await discoveryService.DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest(request.Email), cancellationToken);
                if (string.Equals(discovery.Mode, "sso", StringComparison.Ordinal))
                {
                    var ssoResult = await StartSsoAsync(
                        request.Email,
                        httpContext,
                        webOptions.Value,
                        cryptoService,
                        httpContext.RequestServices.GetRequiredService<SqlOSSsoAuthorizationService>(),
                        cancellationToken);

                    return Results.Ok(new
                    {
                        mode = "sso",
                        ssoResult.AuthorizationUrl,
                        ssoResult.OrganizationId,
                        ssoResult.OrganizationName,
                        ssoResult.PrimaryDomain
                    });
                }

                var state = cryptoService.GenerateOpaqueToken();
                var nonce = cryptoService.GenerateOpaqueToken();
                var codeVerifier = cryptoService.GenerateOpaqueToken();
                var callbackUri = BuildOidcCallbackUri(httpContext, request.ConnectionId);
                var oidcResult = await oidcAuthService.StartAuthorizationAsync(
                    new SqlOSStartOidcAuthorizationRequest(
                        request.ConnectionId,
                        request.Email,
                        webOptions.Value.ClientId,
                        callbackUri,
                        state,
                        nonce,
                        cryptoService.CreatePkceCodeChallenge(codeVerifier),
                        "S256"),
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    cancellationToken);

                SetSsoCookie(httpContext.Response, OidcStateCookie, state, httpContext);
                SetSsoCookie(httpContext.Response, OidcVerifierCookie, codeVerifier, httpContext);
                SetSsoCookie(httpContext.Response, OidcCallbackUriCookie, callbackUri, httpContext);
                SetSsoCookie(httpContext.Response, OidcClientIdCookie, webOptions.Value.ClientId, httpContext);
                SetSsoCookie(httpContext.Response, OidcConnectionIdCookie, request.ConnectionId, httpContext);
                SetSsoCookie(httpContext.Response, OidcNonceCookie, nonce, httpContext);

                return Results.Ok(new
                {
                    mode = "oidc",
                    oidcResult.AuthorizationUrl,
                    connectionId = oidcResult.ConnectionId,
                    providerType = oidcResult.ProviderType,
                    oidcResult.DisplayName
                });
            }));

        auth.MapMethods("/oidc/callback/{connectionId}", ["GET", "POST"], (string connectionId, IOptions<ExampleWebOptions> webOptions, SqlOSOidcAuthService oidcAuthService, SqlOSCryptoService cryptoService, HttpContext httpContext, CancellationToken cancellationToken) =>
            TryAsync(async () =>
            {
                var callbackUrl = webOptions.Value.CallbackUrl;
                var callbackInput = await ReadOidcCallbackInputAsync(httpContext, cancellationToken);
                var code = callbackInput.Code;
                var state = callbackInput.State;
                var error = callbackInput.Error;
                var userPayload = callbackInput.UserPayload;

                if (!string.IsNullOrWhiteSpace(error))
                {
                    ClearOidcCookies(httpContext.Response, httpContext);
                    return Results.Redirect(QueryHelpers.AddQueryString(callbackUrl, "error", error));
                }

                var storedState = httpContext.Request.Cookies[OidcStateCookie];
                var codeVerifier = httpContext.Request.Cookies[OidcVerifierCookie];
                var callbackUri = httpContext.Request.Cookies[OidcCallbackUriCookie];
                var clientId = httpContext.Request.Cookies[OidcClientIdCookie] ?? webOptions.Value.ClientId;
                var storedConnectionId = httpContext.Request.Cookies[OidcConnectionIdCookie];
                var nonce = httpContext.Request.Cookies[OidcNonceCookie];

                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
                {
                    ClearOidcCookies(httpContext.Response, httpContext);
                    return Results.Redirect(QueryHelpers.AddQueryString(callbackUrl, "error", "The OIDC callback was missing the code or state."));
                }

                if (string.IsNullOrWhiteSpace(storedState) || string.IsNullOrWhiteSpace(codeVerifier) || string.IsNullOrWhiteSpace(callbackUri) || string.IsNullOrWhiteSpace(storedConnectionId) || string.IsNullOrWhiteSpace(nonce))
                {
                    ClearOidcCookies(httpContext.Response, httpContext);
                    return Results.Redirect(QueryHelpers.AddQueryString(callbackUrl, "error", "The OIDC callback cookies are missing or expired."));
                }

                if (!string.Equals(storedState, state, StringComparison.Ordinal))
                {
                    ClearOidcCookies(httpContext.Response, httpContext);
                    return Results.Redirect(QueryHelpers.AddQueryString(callbackUrl, "error", "OIDC state validation failed."));
                }

                if (!string.Equals(storedConnectionId, connectionId, StringComparison.Ordinal))
                {
                    ClearOidcCookies(httpContext.Response, httpContext);
                    return Results.Redirect(QueryHelpers.AddQueryString(callbackUrl, "error", "The OIDC callback did not match the original login request."));
                }

                try
                {
                    var result = await oidcAuthService.CompleteAuthorizationAsync(
                        new SqlOSCompleteOidcAuthorizationRequest(connectionId, clientId, callbackUri, code, codeVerifier, nonce, userPayload),
                        httpContext.Connection.RemoteIpAddress?.ToString(),
                        cancellationToken);

                    if (result.OrganizationCount > 1)
                    {
                        throw new InvalidOperationException("This application requires zero or one active organization membership for OIDC login.");
                    }

                    var handoff = await cryptoService.CreateTemporaryTokenAsync(
                        "oidc_handoff",
                        result.UserId,
                        null,
                        result.OrganizationId,
                        new OidcHandoffPayload(result.UserId, result.OrganizationId, result.AuthenticationMethod, clientId, result.Email, result.DisplayName),
                        TimeSpan.FromMinutes(5),
                        cancellationToken);

                    ClearOidcCookies(httpContext.Response, httpContext);
                    return Results.Redirect(QueryHelpers.AddQueryString(callbackUrl, "handoff", handoff));
                }
                catch (InvalidOperationException ex)
                {
                    ClearOidcCookies(httpContext.Response, httpContext);
                    return Results.Redirect(QueryHelpers.AddQueryString(callbackUrl, "error", ex.Message));
                }
            }));

        auth.MapPost("/oidc/complete", (ExampleOidcCompleteRequest request, SqlOSCryptoService cryptoService, SqlOSAuthService authService, ExampleFgaService fgaService, ExampleAppDbContext context, HttpContext httpContext, CancellationToken cancellationToken) =>
            TryAsync(async () =>
            {
                var token = await cryptoService.ConsumeTemporaryTokenAsync("oidc_handoff", request.Handoff, cancellationToken)
                    ?? throw new InvalidOperationException("The OIDC handoff is invalid or expired.");

                var payload = cryptoService.DeserializePayload<OidcHandoffPayload>(token)
                    ?? throw new InvalidOperationException("The OIDC handoff payload is invalid.");

                var user = await context.Set<SqlOSUser>().FirstAsync(x => x.Id == payload.UserId, cancellationToken);
                var client = await context.Set<SqlOSClientApplication>().FirstAsync(x => x.ClientId == payload.ClientId, cancellationToken);
                var tokens = await authService.CreateSessionTokensForUserAsync(
                    user,
                    client,
                    payload.OrganizationId,
                    payload.AuthenticationMethod,
                    httpContext.Request.Headers.UserAgent.ToString(),
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    cancellationToken);

                return Results.Ok(await ToTokenResponseAsync(tokens, user, authService, fgaService, cancellationToken));
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

    private static void ClearOidcCookies(HttpResponse response, HttpContext httpContext)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = httpContext.Request.IsHttps
        };

        response.Cookies.Delete(OidcStateCookie, options);
        response.Cookies.Delete(OidcVerifierCookie, options);
        response.Cookies.Delete(OidcCallbackUriCookie, options);
        response.Cookies.Delete(OidcClientIdCookie, options);
        response.Cookies.Delete(OidcConnectionIdCookie, options);
        response.Cookies.Delete(OidcNonceCookie, options);
    }

    private static async Task<SqlOSSsoAuthorizationStartResult> StartSsoAsync(
        string email,
        HttpContext httpContext,
        ExampleWebOptions options,
        SqlOSCryptoService cryptoService,
        SqlOSSsoAuthorizationService ssoAuthorizationService,
        CancellationToken cancellationToken)
    {
        var state = cryptoService.GenerateOpaqueToken();
        var codeVerifier = cryptoService.GenerateOpaqueToken();
        var codeChallenge = cryptoService.CreatePkceCodeChallenge(codeVerifier);

        var result = await ssoAuthorizationService.StartAuthorizationAsync(
            new SqlOSSsoAuthorizationStartRequest(
                email,
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

        return result;
    }

    private static string BuildOidcCallbackUri(HttpContext httpContext, string connectionId)
        => $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/api/v1/auth/oidc/callback/{connectionId}";

    private static async Task<OidcCallbackInput> ReadOidcCallbackInputAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        string? ReadQuery(string key) => httpContext.Request.Query.TryGetValue(key, out var value) ? value.ToString() : null;

        if (HttpMethods.IsGet(httpContext.Request.Method))
        {
            return new OidcCallbackInput(
                ReadQuery("code"),
                ReadQuery("state"),
                ReadQuery("error") ?? ReadQuery("error_description"),
                ReadQuery("user"));
        }

        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        return new OidcCallbackInput(
            form.TryGetValue("code", out var code) ? code.ToString() : null,
            form.TryGetValue("state", out var state) ? state.ToString() : null,
            form.TryGetValue("error", out var error) ? error.ToString() : form.TryGetValue("error_description", out var errorDescription) ? errorDescription.ToString() : null,
            form.TryGetValue("user", out var userPayload) ? userPayload.ToString() : null);
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
    private sealed record ExampleOidcStartRequest(string Email, string ConnectionId);
    private sealed record ExampleOidcCompleteRequest(string Handoff);
    private sealed record ExampleRefreshRequest(string RefreshToken, string? OrganizationId);
    private sealed record ExampleLogoutRequest(string? RefreshToken, string? SessionId);
    private sealed record OidcHandoffPayload(
        string UserId,
        string? OrganizationId,
        string AuthenticationMethod,
        string ClientId,
        string Email,
        string DisplayName);
    private sealed record OidcCallbackInput(string? Code, string? State, string? Error, string? UserPayload);
}
