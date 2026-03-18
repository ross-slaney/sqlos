using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSOidcBrowserAuthService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSAuthService _authService;
    private readonly SqlOSAuthorizationServerService _authorizationServerService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSOidcAuthService _oidcAuthService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSOidcBrowserAuthService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSAuthService authService,
        SqlOSAuthorizationServerService authorizationServerService,
        SqlOSCryptoService cryptoService,
        SqlOSOidcAuthService oidcAuthService,
        SqlOSSettingsService settingsService,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _adminService = adminService;
        _authService = authService;
        _authorizationServerService = authorizationServerService;
        _cryptoService = cryptoService;
        _oidcAuthService = oidcAuthService;
        _settingsService = settingsService;
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

        var client = await _adminService.RequireClientAsync(request.ClientId, request.RedirectUri, cancellationToken);
        var callbackUri = GetProviderCallbackUri(httpContext);
        var providerNonce = _cryptoService.GenerateOpaqueToken();
        var providerCodeVerifier = _cryptoService.GenerateOpaqueToken();
        var providerState = await _cryptoService.CreateTemporaryTokenAsync(
            "oidc_browser_request",
            null,
            client.Id,
            null,
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
            _options.TemporaryTokenLifetime,
            cancellationToken);

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
        var providerState = await _cryptoService.CreateTemporaryTokenAsync(
            "oidc_authorization_request",
            null,
            client.Id,
            authorizationRequest.OrganizationId,
            new OidcAuthorizationRequestPayload(
                authorizationRequest.Id,
                connectionId,
                providerNonce,
                providerCodeVerifier,
                callbackUri,
                email),
            _options.TemporaryTokenLifetime,
            cancellationToken);

        var providerResult = await _oidcAuthService.StartAuthorizationAsync(
            new SqlOSStartOidcAuthorizationRequest(
                connectionId,
                email ?? authorizationRequest.LoginHintEmail ?? string.Empty,
                client.ClientId,
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

    public async Task<IResult> HandleCallbackAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var callbackInput = await ReadCallbackInputAsync(httpContext, cancellationToken);
        if (string.IsNullOrWhiteSpace(callbackInput.State))
        {
            return RenderCallbackError("The OIDC callback was missing the provider state.");
        }

        var requestToken = await _cryptoService.ConsumeTemporaryTokenAsync("oidc_browser_request", callbackInput.State, cancellationToken);
        if (requestToken == null)
        {
            var authorizationRequestToken = await _cryptoService.ConsumeTemporaryTokenAsync("oidc_authorization_request", callbackInput.State, cancellationToken);
            if (authorizationRequestToken != null)
            {
                return await HandleAuthorizationRequestCallbackAsync(httpContext, callbackInput, authorizationRequestToken, cancellationToken);
            }
        }

        if (requestToken == null)
        {
            return RenderCallbackError("The OIDC browser login request is invalid or expired.");
        }

        var payload = _cryptoService.DeserializePayload<OidcBrowserRequestPayload>(requestToken);
        if (payload == null)
        {
            return RenderCallbackError("The OIDC browser login request payload is invalid.");
        }

        if (!string.IsNullOrWhiteSpace(callbackInput.Error))
        {
            return Results.Redirect(BuildAppRedirectUri(
                payload.RedirectUri,
                new Dictionary<string, string?>
                {
                    ["state"] = payload.State,
                    ["error"] = callbackInput.ErrorDescription ?? callbackInput.Error
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

            var code = await _cryptoService.CreateTemporaryTokenAsync(
                "oidc_browser_code",
                result.UserId,
                requestToken.ClientApplicationId,
                null,
                new OidcBrowserCodePayload(
                    payload.ClientId,
                    payload.RedirectUri,
                    payload.CodeChallenge,
                    payload.CodeChallengeMethod,
                    result.AuthenticationMethod),
                TimeSpan.FromMinutes(5),
                cancellationToken);

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
            return Results.Redirect(BuildAppRedirectUri(
                payload.RedirectUri,
                new Dictionary<string, string?>
                {
                    ["state"] = payload.State,
                    ["error"] = ex.Message
                }));
        }
    }

    private async Task<IResult> HandleAuthorizationRequestCallbackAsync(
        HttpContext httpContext,
        OidcCallbackInput callbackInput,
        SqlOSTemporaryToken authorizationRequestToken,
        CancellationToken cancellationToken)
    {
        var payload = _cryptoService.DeserializePayload<OidcAuthorizationRequestPayload>(authorizationRequestToken);
        if (payload == null)
        {
            return RenderCallbackError("The OIDC authorization request payload is invalid.");
        }

        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(payload.AuthorizationRequestId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(callbackInput.Error))
        {
            var headlessErrorRedirect = await TryBuildHeadlessUiUrlForAuthorizationRequestAsync(
                httpContext,
                authorizationRequest.Id,
                "login",
                callbackInput.ErrorDescription ?? callbackInput.Error,
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
                    ["error"] = callbackInput.ErrorDescription ?? callbackInput.Error
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

            var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == result.UserId, cancellationToken);
            var redirectUrl = await _authorizationServerService.IssueAuthorizationRedirectAsync(
                authorizationRequest,
                user,
                result.OrganizationId ?? authorizationRequest.OrganizationId,
                result.AuthenticationMethod,
                httpContext,
                cancellationToken);

            return Results.Redirect(redirectUrl);
        }
        catch (InvalidOperationException ex)
        {
            var headlessErrorRedirect = await TryBuildHeadlessUiUrlForAuthorizationRequestAsync(
                httpContext,
                authorizationRequest.Id,
                "login",
                ex.Message,
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
                    ["error"] = ex.Message
                }));
        }
    }

    public async Task<SqlOSLoginResult> ExchangeCodeAsync(
        SqlOSPkceExchangeRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var token = await _cryptoService.ConsumeTemporaryTokenAsync("oidc_browser_code", request.Code, cancellationToken)
            ?? throw new InvalidOperationException("Authorization code is invalid or expired.");
        var payload = _cryptoService.DeserializePayload<OidcBrowserCodePayload>(token)
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
        var origin = string.IsNullOrWhiteSpace(_options.PublicOrigin)
            ? $"{httpContext.Request.Scheme}://{httpContext.Request.Host}".TrimEnd('/')
            : _options.PublicOrigin!.TrimEnd('/');

        return $"{origin}{_options.BasePath.TrimEnd('/')}/oidc/callback";
    }

    private static string BuildAppRedirectUri(string redirectUri, IDictionary<string, string?> parameters)
        => QueryHelpers.AddQueryString(redirectUri, parameters);

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
        if (!string.Equals(_settingsService.CurrentPresentationMode, "headless", StringComparison.OrdinalIgnoreCase) || _options.Headless.BuildUiUrl == null)
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
        => Results.Content(
            $"""
            <html>
              <head><title>SqlOS social login error</title></head>
              <body style="font-family: ui-sans-serif, system-ui, sans-serif; padding: 32px;">
                <h1>SqlOS social login error</h1>
                <p>{System.Net.WebUtility.HtmlEncode(message)}</p>
              </body>
            </html>
            """,
            "text/html");

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

    private sealed record OidcBrowserRequestPayload(
        string ClientId,
        string RedirectUri,
        string State,
        string CodeChallenge,
        string CodeChallengeMethod,
        string ConnectionId,
        string? Email,
        string ProviderNonce,
        string ProviderCodeVerifier,
        string CallbackUri);

    private sealed record OidcAuthorizationRequestPayload(
        string AuthorizationRequestId,
        string ConnectionId,
        string ProviderNonce,
        string ProviderCodeVerifier,
        string CallbackUri,
        string? Email);

    private sealed record OidcBrowserCodePayload(
        string ClientId,
        string RedirectUri,
        string CodeChallenge,
        string CodeChallengeMethod,
        string AuthenticationMethod);

    private sealed record OidcCallbackInput(
        string Code,
        string State,
        string Error,
        string ErrorDescription,
        string? UserPayload);
}
