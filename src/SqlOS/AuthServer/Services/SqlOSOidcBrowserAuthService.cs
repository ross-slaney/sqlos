using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Security;
using SqlOS.AuthServer.Configuration;
using SqlOS.Security;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSOidcBrowserAuthService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSAuthService _authService;
    private readonly SqlOSAuthorizationServerService _authorizationServerService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSOidcAuthService _oidcAuthService;
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSOidcBrowserAuthService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSAuthService authService,
        SqlOSAuthorizationServerService authorizationServerService,
        SqlOSCryptoService cryptoService,
        SqlOSOidcAuthService oidcAuthService,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _adminService = adminService;
        _authService = authService;
        _authorizationServerService = authorizationServerService;
        _cryptoService = cryptoService;
        _oidcAuthService = oidcAuthService;
        _options = options.Value;
    }

    public async Task<SqlOSOidcAuthorizationUrlResult> CreateAuthorizationUrlAsync(
        SqlOSOidcAuthorizationUrlRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.CodeChallenge))
        {
            throw new InvalidOperationException("A PKCE code challenge is required.");
        }

        if (!string.Equals(request.CodeChallengeMethod, "S256", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only S256 PKCE is supported for OIDC browser login.");
        }

        if (!_cryptoService.IsValidS256PkceCodeChallenge(request.CodeChallenge))
        {
            throw new InvalidOperationException(
                "PKCE code challenge must be a 43-character RFC 7636 S256 value.");
        }

        var client = await _adminService.RequireClientAsync(request.ClientId, request.RedirectUri, cancellationToken);
        // This flow ends in /oidc/exchange, which returns tokens with no consent screen. Refuse a
        // third-party client before the provider state (a bearer handle) exists; it uses /authorize.
        await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(_adminService, client, httpContext, userId: null, cancellationToken);
        var callbackUri = GetProviderCallbackUri(httpContext);
        var providerNonce = _cryptoService.GenerateOpaqueToken();
        var providerCodeVerifier = _cryptoService.GenerateOpaqueToken();
        var providerState = (await _cryptoService.CreateTemporaryTokenAsync(
            SqlOSTemporaryTokenKinds.OidcBrowserRequest,
            new OidcBrowserRequestPayload(
                request.ClientId,
                request.RedirectUri,
                request.State,
                request.CodeChallenge,
                request.CodeChallengeMethod,
                request.ConnectionId,
                request.Email,
                providerNonce,
                providerCodeVerifier,
                callbackUri),
            new TemporaryTokenBinding(ClientApplicationId: client.Id),
            _options.TemporaryTokenLifetime,
            cancellationToken)).RawToken;

        var providerResult = await _oidcAuthService.StartAuthorizationAsync(
            new SqlOSStartOidcAuthorizationRequest(
                request.ConnectionId,
                request.Email ?? string.Empty,
                request.ClientId,
                callbackUri,
                providerState,
                providerNonce,
                _cryptoService.CreatePkceCodeChallenge(providerCodeVerifier),
                "S256"),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return new SqlOSOidcAuthorizationUrlResult(
            providerResult.AuthorizationUrl,
            providerResult.ConnectionId,
            providerResult.ProviderType.ToString(),
            providerResult.DisplayName);
    }

    public async Task<SqlOSOidcAuthorizationUrlResult> CreateAuthorizationUrlForAuthRequestAsync(
        string authorizationRequestId,
        string connectionId,
        string? email,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(authorizationRequestId, cancellationToken);
        var client = await _context.Set<SqlOSClientApplication>()
            .FirstAsync(x => x.Id == authorizationRequest.ClientApplicationId, cancellationToken);
        var callbackUri = GetProviderCallbackUri(httpContext);
        var providerNonce = _cryptoService.GenerateOpaqueToken();
        var providerCodeVerifier = _cryptoService.GenerateOpaqueToken();
        var providerState = (await _cryptoService.CreateTemporaryTokenAsync(
            SqlOSTemporaryTokenKinds.OidcAuthorizationRequest,
            new OidcAuthorizationRequestPayload(
                authorizationRequest.Id,
                connectionId,
                providerNonce,
                providerCodeVerifier,
                callbackUri,
                email),
            new TemporaryTokenBinding(ClientApplicationId: client.Id, OrganizationId: authorizationRequest.OrganizationId),
            _options.TemporaryTokenLifetime,
            cancellationToken)).RawToken;

        var providerResult = await _oidcAuthService.StartAuthorizationAsync(
            new SqlOSStartOidcAuthorizationRequest(
                connectionId,
                email ?? authorizationRequest.LoginHintEmail ?? string.Empty,
                client.ClientId,
                callbackUri,
                providerState,
                providerNonce,
                _cryptoService.CreatePkceCodeChallenge(providerCodeVerifier),
                "S256")
            {
                ForceFreshAuthentication = SqlOSAuthorizationServerService.RequiresFreshAuthentication(authorizationRequest),
                PropagateMaxAgeZero = authorizationRequest.MaxAgeSeconds == 0
            },
            httpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return new SqlOSOidcAuthorizationUrlResult(
            providerResult.AuthorizationUrl,
            providerResult.ConnectionId,
            providerResult.ProviderType.ToString(),
            providerResult.DisplayName);
    }

    public async Task<IResult> HandleCallbackAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var callbackInput = await ReadCallbackInputAsync(httpContext, cancellationToken);
        if (string.IsNullOrWhiteSpace(callbackInput.State))
        {
            return RenderCallbackError("The OIDC callback was missing the provider state.");
        }

        var requestToken = await _cryptoService.ConsumeTemporaryTokenAsync(SqlOSTemporaryTokenKinds.OidcBrowserRequest, callbackInput.State, cancellationToken);
        if (requestToken == null)
        {
            var authorizationRequestToken = await _cryptoService.ConsumeTemporaryTokenAsync(SqlOSTemporaryTokenKinds.OidcAuthorizationRequest, callbackInput.State, cancellationToken);
            if (authorizationRequestToken != null)
            {
                return await HandleAuthorizationRequestCallbackAsync(httpContext, callbackInput, authorizationRequestToken, cancellationToken);
            }
        }

        if (requestToken == null)
        {
            return RenderCallbackError("The OIDC browser login request is invalid or expired.");
        }

        var payload = requestToken.ReadPayload(SqlOSTemporaryTokenKinds.OidcBrowserRequest);
        if (payload == null)
        {
            return RenderCallbackError("The OIDC browser login request payload is invalid.");
        }

        if (!string.IsNullOrWhiteSpace(callbackInput.Error))
        {
            var error = await MapCallbackErrorAsync(
                httpContext,
                SqlOSPublicAuthErrorMapper.ProviderCallbackError(callbackInput.Error, callbackInput.ErrorDescription),
                cancellationToken);
            return Results.Redirect(BuildAppRedirectUri(
                payload.RedirectUri,
                new Dictionary<string, string?>
                {
                    ["state"] = payload.State,
                    ["error"] = error.PublicMessage
                }));
        }

        if (string.IsNullOrWhiteSpace(callbackInput.Code))
        {
            return Results.Redirect(BuildAppRedirectUri(
                payload.RedirectUri,
                new Dictionary<string, string?>
                {
                    ["state"] = payload.State,
                    ["error"] = "The OIDC callback was missing the provider code."
                }));
        }

        try
        {
            // Provider state issued before the direct-login gate (or before the client lost its
            // first-party status) must not complete the upstream login or mint a code for the client.
            var client = await _context.Set<SqlOSClientApplication>()
                .FirstAsync(x => x.Id == requestToken.ClientApplicationId, cancellationToken);
            await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(_adminService, client, httpContext, userId: null, cancellationToken);

            var result = await _oidcAuthService.CompleteAuthorizationAsync(
                new SqlOSCompleteOidcAuthorizationRequest(
                    payload.ConnectionId,
                    payload.ClientId,
                    payload.CallbackUri,
                    callbackInput.Code,
                    payload.ProviderCodeVerifier,
                    payload.ProviderNonce,
                    callbackInput.UserPayload),
                httpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            await InvokeSocialSignupHookAsync(httpContext, authorizationRequest: null, result, cancellationToken);

            var code = (await _cryptoService.CreateTemporaryTokenAsync(
                SqlOSTemporaryTokenKinds.OidcBrowserCode,
                new OidcBrowserCodePayload(
                    payload.ClientId,
                    payload.RedirectUri,
                    payload.CodeChallenge,
                    payload.CodeChallengeMethod,
                    result.AuthenticationMethod),
                new TemporaryTokenBinding(UserId: result.UserId, ClientApplicationId: requestToken.ClientApplicationId),
                configuredLifetime: null,
                cancellationToken)).RawToken;

            return Results.Redirect(BuildAppRedirectUri(
                payload.RedirectUri,
                new Dictionary<string, string?>
                {
                    ["code"] = code,
                    ["state"] = payload.State
                }));
        }
        catch (InvalidOperationException ex)
        {
            var error = await MapCallbackErrorAsync(httpContext, ex, cancellationToken);
            return Results.Redirect(BuildAppRedirectUri(
                payload.RedirectUri,
                new Dictionary<string, string?>
                {
                    ["state"] = payload.State,
                    ["error"] = error.PublicMessage
                }));
        }
    }

    private async Task<IResult> HandleAuthorizationRequestCallbackAsync(
        HttpContext httpContext,
        OidcCallbackInput callbackInput,
        SqlOSTemporaryToken authorizationRequestToken,
        CancellationToken cancellationToken)
    {
        var payload = authorizationRequestToken.ReadPayload(SqlOSTemporaryTokenKinds.OidcAuthorizationRequest);
        if (payload == null)
        {
            return RenderCallbackError("The OIDC authorization request payload is invalid.");
        }

        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(payload.AuthorizationRequestId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(callbackInput.Error))
        {
            var error = await MapCallbackErrorAsync(
                httpContext,
                SqlOSPublicAuthErrorMapper.ProviderCallbackError(callbackInput.Error, callbackInput.ErrorDescription),
                cancellationToken);
            var headlessErrorRedirect = await TryBuildHeadlessUiUrlForAuthorizationRequestAsync(
                httpContext,
                authorizationRequest.Id,
                "login",
                error.PublicMessage,
                pendingToken: null,
                email: authorizationRequest.LoginHintEmail,
                displayName: null,
                cancellationToken);
            if (headlessErrorRedirect != null)
            {
                return Results.Redirect(headlessErrorRedirect);
            }

            return Results.Redirect(BuildAppRedirectUri(
                authorizationRequest.RedirectUri,
                new Dictionary<string, string?>
                {
                    ["state"] = authorizationRequest.State,
                    ["error"] = error.PublicMessage
                }));
        }

        if (string.IsNullOrWhiteSpace(callbackInput.Code))
        {
            var headlessErrorRedirect = await TryBuildHeadlessUiUrlForAuthorizationRequestAsync(
                httpContext,
                authorizationRequest.Id,
                "login",
                "The OIDC callback was missing the provider code.",
                pendingToken: null,
                email: authorizationRequest.LoginHintEmail,
                displayName: null,
                cancellationToken);
            if (headlessErrorRedirect != null)
            {
                return Results.Redirect(headlessErrorRedirect);
            }

            return Results.Redirect(BuildAppRedirectUri(
                authorizationRequest.RedirectUri,
                new Dictionary<string, string?>
                {
                    ["state"] = authorizationRequest.State,
                    ["error"] = "The OIDC callback was missing the provider code."
                }));
        }

        try
        {
            var result = await _oidcAuthService.CompleteAuthorizationAsync(
                new SqlOSCompleteOidcAuthorizationRequest(
                    payload.ConnectionId,
                    authorizationRequest.ClientApplication?.ClientId ?? string.Empty,
                    payload.CallbackUri,
                    callbackInput.Code,
                    payload.ProviderCodeVerifier,
                    payload.ProviderNonce,
                    callbackInput.UserPayload),
                httpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            var organizationId = await InvokeSocialSignupHookAsync(httpContext, authorizationRequest, result, cancellationToken);

            var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == result.UserId, cancellationToken);
            authorizationRequest.OrganizationId ??= organizationId;
            // A silently reused upstream session must stamp the upstream auth_time
            // as the authentication moment, not the callback time. When the provider
            // did not assert auth_time this is null and local resolution applies.
            var completion = await _authorizationServerService.CompleteCredentialSignInAsync(
                authorizationRequest,
                user,
                result.AuthenticationMethod,
                httpContext,
                cancellationToken,
                knownAuthenticatedAt: result.UpstreamAuthenticatedAt);
            var redirectUrl = completion.RedirectUrl
                ?? await _authorizationServerService.CreateAuthorizationContinuationRedirectAsync(
                    completion,
                    httpContext,
                    cancellationToken);

            return Results.Redirect(redirectUrl);
        }
        catch (InvalidOperationException ex)
        {
            var error = await MapCallbackErrorAsync(httpContext, ex, cancellationToken);
            var headlessErrorRedirect = await TryBuildHeadlessUiUrlForAuthorizationRequestAsync(
                httpContext,
                authorizationRequest.Id,
                "login",
                error.PublicMessage,
                pendingToken: null,
                email: authorizationRequest.LoginHintEmail,
                displayName: null,
                cancellationToken);
            if (headlessErrorRedirect != null)
            {
                return Results.Redirect(headlessErrorRedirect);
            }

            return Results.Redirect(BuildAppRedirectUri(
                authorizationRequest.RedirectUri,
                new Dictionary<string, string?>
                {
                    ["state"] = authorizationRequest.State,
                    ["error"] = error.PublicMessage
                }));
        }
    }

    public async Task<SqlOSLoginResult> ExchangeCodeAsync(
        SqlOSPkceExchangeRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var token = await _cryptoService.ConsumeTemporaryTokenAsync(SqlOSTemporaryTokenKinds.OidcBrowserCode, request.Code, cancellationToken)
            ?? throw new InvalidOperationException("Authorization code is invalid or expired.");
        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.OidcBrowserCode)
            ?? throw new InvalidOperationException("Authorization code payload is invalid.");

        if (token.UserId == null)
        {
            throw new InvalidOperationException("Authorization code user is missing.");
        }

        if (!string.Equals(payload.ClientId, request.ClientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Authorization code was not issued for this client.");
        }

        if (!string.Equals(payload.RedirectUri, request.RedirectUri, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Redirect URI does not match the OIDC browser login request.");
        }

        if (!_cryptoService.VerifyPkceCodeVerifier(request.CodeVerifier, payload.CodeChallenge, payload.CodeChallengeMethod))
        {
            throw new InvalidOperationException("PKCE verification failed.");
        }

        var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == token.UserId, cancellationToken);
        var client = await _adminService.RequireClientAsync(request.ClientId, request.RedirectUri, cancellationToken);
        return await _authService.CompleteExternalLoginAsync(user, client, payload.AuthenticationMethod, httpContext, cancellationToken);
    }

    private string GetProviderCallbackUri(HttpContext httpContext)
    {
        var origin = SqlOSPublicOriginResolver.Resolve(_options);

        return $"{origin}{_options.BasePath.TrimEnd('/')}/oidc/callback";
    }

    private async Task<string?> InvokeSocialSignupHookAsync(
        HttpContext httpContext,
        SqlOSAuthorizationRequest? authorizationRequest,
        SqlOSCompleteOidcAuthorizationResult result,
        CancellationToken cancellationToken)
    {
        if (!result.UserCreated || _options.Headless.OnHeadlessSignupAsync == null)
        {
            return result.OrganizationId;
        }

        var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == result.UserId, cancellationToken);
        SqlOSOrganization? organization = null;
        if (!string.IsNullOrWhiteSpace(result.OrganizationId))
        {
            organization = await _context.Set<SqlOSOrganization>()
                .FirstOrDefaultAsync(x => x.Id == result.OrganizationId, cancellationToken);
        }

        await _options.Headless.OnHeadlessSignupAsync(
            new SqlOSHeadlessSignupHookContext(
                httpContext,
                authorizationRequest,
                user,
                organization,
                new JsonObject()),
            cancellationToken);

        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
        return organizations.Count == 1 ? organizations[0].Id : result.OrganizationId;
    }

    private static string BuildAppRedirectUri(string redirectUri, IDictionary<string, string?> parameters)
        => QueryHelpers.AddQueryString(redirectUri, parameters);

    private async Task<SqlOSPublicAuthError> MapCallbackErrorAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var error = SqlOSPublicAuthErrorMapper.Map(exception, SqlOSPublicAuthErrorSurface.OidcCallback);
        await SqlOSPublicAuthErrorAudit.RecordIfDiagnosticAsync(
            _adminService,
            httpContext,
            SqlOSPublicAuthErrorSurface.OidcCallback,
            exception,
            error,
            cancellationToken);
        return error;
    }

    private async Task<string?> TryBuildHeadlessUiUrlForAuthorizationRequestAsync(
        HttpContext httpContext,
        string authorizationRequestId,
        string view,
        string? error,
        string? pendingToken,
        string? email,
        string? displayName,
        CancellationToken cancellationToken)
    {
        if (_options.Headless.BuildUiUrl == null)
        {
            return null;
        }

        var authorizationRequest = await _authorizationServerService.TryGetActiveAuthorizationRequestAsync(authorizationRequestId, cancellationToken);
        if (authorizationRequest == null || !SqlOSHeadlessAuthService.IsHeadlessRequest(authorizationRequest))
        {
            return null;
        }

        return _options.Headless.BuildUiUrl(
            new SqlOSHeadlessUiRouteContext(
                httpContext,
                authorizationRequest.Id,
                SqlOSHeadlessAuthService.NormalizeView(view),
                error,
                pendingToken,
                email ?? authorizationRequest.LoginHintEmail,
                displayName,
                SqlOSHeadlessAuthService.ParseUiContext(authorizationRequest.UiContextJson)));
    }

    private static IResult RenderCallbackError(string message)
        => new SqlOSHostedHtmlResult(
            $$"""
            <html>
              <head>
                <title>SqlOS social login error</title>
                <style {{SqlOSCspNonce.Attribute}}>body { font-family: ui-sans-serif, system-ui, sans-serif; padding: 32px; }</style>
              </head>
              <body>
                <h1>SqlOS social login error</h1>
                <p>{{System.Net.WebUtility.HtmlEncode(message)}}</p>
              </body>
            </html>
            """,
            StatusCodes.Status400BadRequest);

    private static async Task<OidcCallbackInput> ReadCallbackInputAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (HttpMethods.IsPost(httpContext.Request.Method))
        {
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            return new OidcCallbackInput(
                form["code"].ToString(),
                form["state"].ToString(),
                form["error"].ToString(),
                form["error_description"].ToString(),
                form["user"].ToString());
        }

        var query = httpContext.Request.Query;
        return new OidcCallbackInput(
            query["code"].ToString(),
            query["state"].ToString(),
            query["error"].ToString(),
            query["error_description"].ToString(),
            query["user"].ToString());
    }

    private sealed record OidcCallbackInput(
        string Code,
        string State,
        string Error,
        string ErrorDescription,
        string? UserPayload);
}
