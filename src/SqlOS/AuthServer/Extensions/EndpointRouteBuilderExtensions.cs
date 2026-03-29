using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Configuration;
using SqlOS.Dashboard;

namespace SqlOS.AuthServer.Extensions;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapAuthServer(this IEndpointRouteBuilder endpoints, string? pathPrefix = null)
    {
        var authPrefix = (pathPrefix ?? "/sqlos/auth").TrimEnd('/');
        var authOptions = endpoints.ServiceProvider.GetService<IOptions<SqlOSAuthServerOptions>>()?.Value ?? new SqlOSAuthServerOptions();
        var resolvedHeadlessPath = authOptions.Headless.ResolveApiBasePath(authPrefix);
        var adminPrefix = authPrefix.EndsWith("/auth", StringComparison.OrdinalIgnoreCase)
            ? $"{authPrefix[..^5]}/admin/auth"
            : $"{authPrefix}/admin";

        var auth = endpoints.MapGroup(authPrefix);
        auth.ExcludeFromDescription();

        var adminRoot = endpoints.MapGroup(adminPrefix);
        adminRoot.ExcludeFromDescription();

        var adminApi = adminRoot.MapGroup("/api");

        auth.MapGet("/.well-known/oauth-authorization-server", async (HttpContext context, SqlOSAuthorizationServerService authorizationServerService, CancellationToken cancellationToken) =>
            Results.Ok(await authorizationServerService.GetMetadataAsync(context, cancellationToken)));

        auth.MapGet("/.well-known/jwks.json", async (SqlOSCryptoService cryptoService, SqlOSSettingsService settingsService, CancellationToken cancellationToken) =>
        {
            var rotationSettings = await settingsService.GetKeyRotationSettingsAsync(cancellationToken);
            var keys = await cryptoService.GetValidationSigningKeysAsync(rotationSettings.GraceWindow, cancellationToken);
            return Results.Ok(cryptoService.GetJwksDocument(keys));
        });

        auth.MapGet("/authorize", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSHeadlessAuthService headlessAuthService,
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (string.Equals(context.Request.Query["prompt"], "login", StringComparison.Ordinal))
                {
                    await authPageSessionService.SignOutAsync(context, cancellationToken);
                }

                var authorizationRequest = await authorizationServerService.CreateAuthorizationRequestAsync(
                    new SqlOSAuthorizeRequestInput(
                        context.Request.Query["response_type"].ToString(),
                        context.Request.Query["client_id"].ToString(),
                        context.Request.Query["redirect_uri"].ToString(),
                        context.Request.Query["state"].ToString(),
                        context.Request.Query["scope"].ToString(),
                        context.Request.Query["code_challenge"].ToString(),
                        context.Request.Query["code_challenge_method"].ToString(),
                        context.Request.Query["resource"].ToString(),
                        context.Request.Query["login_hint"].ToString(),
                        context.Request.Query["prompt"].ToString(),
                        context.Request.Query["nonce"].ToString(),
                        headlessAuthService.IsEnabled ? "headless" : "hosted",
                        context.Request.Query["ui_context"].ToString()),
                    cancellationToken);
                var requestedView = string.Equals(context.Request.Query["view"], "signup", StringComparison.OrdinalIgnoreCase)
                    ? "signup"
                    : "login";

                var existingSession = await authPageSessionService.TryGetSessionAsync(context, cancellationToken);
                if (existingSession != null && !string.Equals(context.Request.Query["prompt"], "login", StringComparison.Ordinal))
                {
                    var redirectUrl = await authorizationServerService.IssueAuthorizationRedirectAsync(
                        authorizationRequest,
                        existingSession.User,
                        existingSession.OrganizationId,
                        existingSession.AuthenticationMethod,
                        context,
                        cancellationToken);
                    return Results.Redirect(redirectUrl);
                }

                if (headlessAuthService.IsEnabled)
                {
                    return Results.Redirect(headlessAuthService.BuildUiUrl(
                        context,
                        authorizationRequest.Id,
                        requestedView,
                        error: null,
                        pendingToken: null,
                        email: authorizationRequest.LoginHintEmail,
                        displayName: null,
                        uiContext: SqlOSHeadlessAuthService.ParseUiContext(authorizationRequest.UiContextJson)));
                }

                var page = await BuildAuthPageViewModelAsync(
                    requestedView,
                    authorizationRequest.Id,
                    authorizationRequest.LoginHintEmail,
                    null,
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken);

                return Html(page);
            }
            catch (InvalidOperationException ex)
            {
                if (headlessAuthService.IsEnabled)
                {
                    return Results.Redirect(headlessAuthService.BuildStandaloneUiUrl(
                        context,
                        "login",
                        requestId: null,
                        email: context.Request.Query["login_hint"].ToString(),
                        uiContext: SqlOSHeadlessAuthService.ParseUiContext(context.Request.Query["ui_context"].ToString()))
                        + $"&error={Uri.EscapeDataString(ex.Message)}");
                }

                var page = await BuildAuthPageViewModelAsync(
                    "login",
                    null,
                    context.Request.Query["login_hint"].ToString(),
                    ex.Message,
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });

        auth.MapGet("/login", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (headlessAuthService.IsEnabled)
            {
                return Results.Redirect(headlessAuthService.BuildStandaloneUiUrl(
                    context,
                    "login",
                    context.Request.Query["request"].ToString(),
                    context.Request.Query["email"].ToString(),
                    SqlOSHeadlessAuthService.ParseUiContext(context.Request.Query["ui_context"].ToString())));
            }

            var page = await BuildAuthPageViewModelAsync(
                "login",
                context.Request.Query["request"].ToString(),
                context.Request.Query["email"].ToString(),
                null,
                null,
                null,
                authPrefix,
                authorizationServerService,
                cancellationToken);
            return Html(page);
        });

        auth.MapPost("/login/identify", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSHomeRealmDiscoveryService discoveryService,
            SqlOSSamlService samlService,
            ISqlOSAuthServerDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var email = form["email"].ToString();

            var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
            var discovery = await discoveryService.DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest(email), cancellationToken);
            if (authorizationRequest != null)
            {
                authorizationRequest.LoginHintEmail = email;
                if (!string.IsNullOrWhiteSpace(discovery.OrganizationId))
                {
                    authorizationRequest.OrganizationId = discovery.OrganizationId;
                    authorizationRequest.ResolvedOrganizationId = discovery.OrganizationId;
                }

                if (!string.IsNullOrWhiteSpace(discovery.ConnectionId))
                {
                    authorizationRequest.ConnectionId = discovery.ConnectionId;
                    authorizationRequest.ResolvedConnectionId = discovery.ConnectionId;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (authorizationRequest != null
                && string.Equals(discovery.Mode, "sso", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(discovery.ConnectionId))
            {
                return Results.Redirect(await samlService.BuildIdentityProviderRedirectForAuthorizationRequestAsync(authorizationRequest.Id, cancellationToken));
            }

            var page = await BuildAuthPageViewModelAsync(
                "password",
                requestId,
                email,
                null,
                null,
                null,
                authPrefix,
                authorizationServerService,
                cancellationToken);
            return Html(page);
        });

        auth.MapPost("/login/password", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var email = form["email"].ToString();
            var password = form["password"].ToString();

            try
            {
                var authentication = await authorizationServerService.AuthenticatePasswordAsync(email, password, cancellationToken);
                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                if (authorizationRequest == null)
                {
                    await authPageSessionService.SignInAsync(context, authentication.User, authentication.Organizations.FirstOrDefault()?.Id, authentication.AuthenticationMethod, cancellationToken);
                    return Results.Redirect($"{authPrefix}/login?status=signed-in");
                }

                if (!string.IsNullOrWhiteSpace(authorizationRequest.OrganizationId))
                {
                    if (authentication.Organizations.All(x => x.Id != authorizationRequest.OrganizationId))
                    {
                        throw new InvalidOperationException("The selected organization is not available to this user.");
                    }

                    return Results.Redirect(await authorizationServerService.IssueAuthorizationRedirectAsync(
                        authorizationRequest,
                        authentication.User,
                        authorizationRequest.OrganizationId,
                        authentication.AuthenticationMethod,
                        context,
                        cancellationToken));
                }

                if (authentication.Organizations.Count > 1)
                {
                    var pendingToken = await authorizationServerService.CreatePendingOrganizationSelectionAsync(
                        authentication.User,
                        authorizationRequest,
                        authentication.AuthenticationMethod,
                        cancellationToken);
                    var organizationPage = await BuildAuthPageViewModelAsync(
                        "organization",
                        requestId,
                        email,
                        null,
                        null,
                        pendingToken,
                        authPrefix,
                        authorizationServerService,
                        cancellationToken,
                        authentication.Organizations);
                    return Html(organizationPage);
                }

                return Results.Redirect(await authorizationServerService.IssueAuthorizationRedirectAsync(
                    authorizationRequest,
                    authentication.User,
                    authentication.Organizations.FirstOrDefault()?.Id,
                    authentication.AuthenticationMethod,
                    context,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                var page = await BuildAuthPageViewModelAsync(
                    "password",
                    requestId,
                    email,
                    ex.Message,
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });

        auth.MapPost("/login/select-organization", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var pendingToken = form["pendingToken"].ToString();
            var organizationId = form["organizationId"].ToString();
            var redirectUrl = await authorizationServerService.CompletePendingOrganizationSelectionAsync(
                pendingToken,
                organizationId,
                context,
                cancellationToken);
            return Results.Redirect(redirectUrl);
        });

        auth.MapGet("/login/oidc/{connectionId}", async (
            string connectionId,
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSOidcBrowserAuthService oidcBrowserAuthService,
            CancellationToken cancellationToken) =>
        {
            var requestId = context.Request.Query["request"].ToString();
            var email = context.Request.Query["email"].ToString();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                var page = await BuildAuthPageViewModelAsync(
                    "login",
                    null,
                    email,
                    "OIDC sign-in requires an active authorization request.",
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken);
                return Html(page, StatusCodes.Status400BadRequest);
            }

            var result = await oidcBrowserAuthService.CreateAuthorizationUrlForAuthRequestAsync(requestId, connectionId, email, context, cancellationToken);
            return Results.Redirect(result.AuthorizationUrl);
        });

        auth.MapGet("/signup", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (headlessAuthService.IsEnabled)
            {
                return Results.Redirect(headlessAuthService.BuildStandaloneUiUrl(
                    context,
                    "signup",
                    context.Request.Query["request"].ToString(),
                    context.Request.Query["email"].ToString(),
                    SqlOSHeadlessAuthService.ParseUiContext(context.Request.Query["ui_context"].ToString())));
            }

            var page = await BuildAuthPageViewModelAsync(
                "signup",
                context.Request.Query["request"].ToString(),
                context.Request.Query["email"].ToString(),
                null,
                null,
                null,
                authPrefix,
                authorizationServerService,
                cancellationToken);
            return Html(page);
        });

        auth.MapPost("/signup/submit", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var displayName = form["displayName"].ToString();
            var email = form["email"].ToString();
            var password = form["password"].ToString();
            var organizationName = form["organizationName"].ToString();

            try
            {
                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var signup = await authorizationServerService.SignUpAsync(
                    displayName,
                    email,
                    password,
                    organizationName,
                    authorizationRequest?.OrganizationId,
                    cancellationToken);

                if (authorizationRequest == null)
                {
                    await authPageSessionService.SignInAsync(context, signup.User, signup.Organizations.FirstOrDefault()?.Id, signup.AuthenticationMethod, cancellationToken);
                    return Results.Redirect($"{authPrefix}/login?status=signed-up");
                }

                return Results.Redirect(await authorizationServerService.IssueAuthorizationRedirectAsync(
                    authorizationRequest,
                    signup.User,
                    authorizationRequest.OrganizationId ?? signup.Organizations.FirstOrDefault()?.Id,
                    signup.AuthenticationMethod,
                    context,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                var page = await BuildAuthPageViewModelAsync(
                    "signup",
                    requestId,
                    email,
                    ex.Message,
                    displayName,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });

        var headless = endpoints.MapGroup(resolvedHeadlessPath);
        headless.ExcludeFromDescription();

        headless.MapGet("/requests/{requestId}", async (
            string requestId,
            string? view,
            string? error,
            string? pendingToken,
            string? email,
            string? displayName,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.GetRequestAsync(
                    requestId,
                    view,
                    error,
                    pendingToken,
                    email,
                    displayName,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        headless.MapPost("/identify", async (
            SqlOSHeadlessIdentifyRequest request,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.IdentifyAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        headless.MapPost("/password/login", async (
            SqlOSHeadlessPasswordLoginRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.PasswordLoginAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        headless.MapPost("/signup", async (
            SqlOSHeadlessSignupRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.SignUpAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        headless.MapPost("/organization/select", async (
            SqlOSHeadlessOrganizationSelectionRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.SelectOrganizationAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        headless.MapPost("/provider/start", async (
            SqlOSHeadlessProviderStartRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.StartProviderAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        auth.MapGet("/logged-out", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            await authPageSessionService.SignOutAsync(context, cancellationToken);
            var page = await BuildAuthPageViewModelAsync(
                "logged-out",
                null,
                null,
                null,
                null,
                null,
                authPrefix,
                authorizationServerService,
                cancellationToken);
            return Html(page);
        });

        auth.MapGet("/logout", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            await authPageSessionService.SignOutAsync(context, cancellationToken);

            var requestedReturnUrl = context.Request.Query["returnTo"].ToString();
            if (string.IsNullOrWhiteSpace(requestedReturnUrl))
            {
                requestedReturnUrl = context.Request.Query["post_logout_redirect_uri"].ToString();
            }

            var redirectTarget = await authorizationServerService.ResolvePostLogoutRedirectAsync(
                context,
                requestedReturnUrl,
                cancellationToken);

            return redirectTarget == null
                ? Results.Redirect($"{authPrefix}/logged-out")
                : Results.Redirect(redirectTarget);
        });

        auth.MapPost("/token", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);

            try
            {
                var result = await authorizationServerService.ExchangeAuthorizationCodeAsync(
                    new SqlOSTokenRequest(
                        form["grant_type"].ToString(),
                        form["code"].ToString(),
                        form["redirect_uri"].ToString(),
                        form["client_id"].ToString(),
                        form["code_verifier"].ToString(),
                        form["refresh_token"].ToString(),
                        form["resource"].ToString()),
                    context,
                    cancellationToken);

                return Results.Ok(new
                {
                    access_token = result.Tokens.AccessToken,
                    refresh_token = result.Tokens.RefreshToken,
                    token_type = "Bearer",
                    expires_in = Math.Max(1, (int)(result.Tokens.AccessTokenExpiresAt - DateTime.UtcNow).TotalSeconds),
                    scope = result.Scope ?? string.Empty
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_grant",
                    error_description = ex.Message
                });
            }
        });

        if (authOptions.ClientRegistration.Dcr.Enabled)
        {
            auth.MapPost("/register", async (
                SqlOSDynamicClientRegistrationRequest request,
                SqlOSDynamicClientRegistrationService registrationService,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await registrationService.RegisterAsync(request, context, cancellationToken);
                    return Results.Json(result, statusCode: StatusCodes.Status201Created);
                }
                catch (SqlOSClientRegistrationException ex)
                {
                    return Results.Json(new
                    {
                        error = ex.Error,
                        error_description = ex.Message
                    }, statusCode: ex.StatusCode);
                }
            });
        }

        auth.MapPost("/signup", async (SqlOSSignupRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.SignUpAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/password/login", async (SqlOSPasswordLoginRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.LoginWithPasswordAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/select-organization", async (SqlOSSelectOrganizationRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.SelectOrganizationAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/token/exchange", async (SqlOSExchangeCodeRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.ExchangeCodeAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/token/refresh", async (SqlOSRefreshRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
            Results.Ok(await authService.RefreshAsync(request, cancellationToken)));

        auth.MapPost("/logout", async (HttpContext context, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            var request = await context.Request.ReadFromJsonAsync<LogoutRequest>(cancellationToken: cancellationToken) ?? new LogoutRequest(null, null);
            await authService.LogoutAsync(request.RefreshToken, request.SessionId, cancellationToken);
            return Results.NoContent();
        });

        auth.MapPost("/logout-all", async (LogoutAllRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            await authService.LogoutAllAsync(request.UserId, cancellationToken);
            return Results.NoContent();
        });

        auth.MapPost("/password/forgot", async (SqlOSForgotPasswordRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
            Results.Ok(new { token = await authService.CreatePasswordResetTokenAsync(request, cancellationToken) }));

        auth.MapPost("/password/reset", async (SqlOSResetPasswordRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            await authService.ResetPasswordAsync(request, cancellationToken);
            return Results.NoContent();
        });

        auth.MapPost("/email/verification-token", async (SqlOSCreateVerificationTokenRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
            Results.Ok(new { token = await authService.CreateEmailVerificationTokenAsync(request, cancellationToken) }));

        auth.MapPost("/email/verify", async (SqlOSVerifyEmailRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            await authService.VerifyEmailAsync(request, cancellationToken);
            return Results.NoContent();
        });

        auth.MapGet("/oidc/providers", async (SqlOSOidcAuthService oidcAuthService, CancellationToken cancellationToken) =>
            Results.Ok(await oidcAuthService.ListEnabledProvidersAsync(cancellationToken)));

        auth.MapPost("/oidc/authorization-url", async (SqlOSOidcAuthorizationUrlRequest request, SqlOSOidcBrowserAuthService oidcBrowserAuthService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await oidcBrowserAuthService.CreateAuthorizationUrlAsync(request, httpContext, cancellationToken)));

        auth.MapMethods("/oidc/callback", ["GET", "POST"], async (SqlOSOidcBrowserAuthService oidcBrowserAuthService, HttpContext httpContext, CancellationToken cancellationToken) =>
            await oidcBrowserAuthService.HandleCallbackAsync(httpContext, cancellationToken));

        auth.MapPost("/oidc/exchange", async (SqlOSPkceExchangeRequest request, SqlOSOidcBrowserAuthService oidcBrowserAuthService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await oidcBrowserAuthService.ExchangeCodeAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/sso/authorization-url", async (SqlOSAuthorizationUrlRequest request, SqlOSSamlService samlService, CancellationToken cancellationToken) =>
            Results.Ok(new { authorizationUrl = await samlService.CreateAuthorizationUrlAsync(request, cancellationToken) }));

        auth.MapGet("/saml/login/{connectionId}", async (string connectionId, string requestToken, SqlOSSamlService samlService, CancellationToken cancellationToken) =>
            Results.Redirect(await samlService.BuildIdentityProviderRedirectAsync(connectionId, requestToken, cancellationToken)));

        static async Task<IResult> HandleSamlAcsAsync(
            string connectionId,
            HttpContext httpContext,
            SqlOSSamlService samlService,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken)
        {
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            var samlResponse = form["SAMLResponse"].ToString();
            var relayState = form["RelayState"].ToString();
            if (string.IsNullOrWhiteSpace(samlResponse) || string.IsNullOrWhiteSpace(relayState))
            {
                return Results.BadRequest(new { error = "SAMLResponse and RelayState are required." });
            }

            try
            {
                var redirectUrl = await samlService.HandleAcsAsync(connectionId, samlResponse, relayState, cancellationToken);
                return Results.Redirect(redirectUrl);
            }
            catch (InvalidOperationException ex)
            {
                var headlessErrorRedirect = await headlessAuthService.TryBuildUiUrlForAuthorizationRequestAsync(
                    httpContext,
                    relayState,
                    "login",
                    ex.Message,
                    pendingToken: null,
                    email: null,
                    displayName: null,
                    cancellationToken);
                if (headlessErrorRedirect != null)
                {
                    return Results.Redirect(headlessErrorRedirect);
                }

                return Results.BadRequest(new { error = ex.Message });
            }
        }

        auth.MapPost("/saml/acs/{connectionId}", HandleSamlAcsAsync);

        adminApi.MapGet("/stats", async (HttpContext context, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            var authorized = await IsAdminAuthorizedAsync(context, options.Value, environment);
            if (!authorized)
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.GetDashboardSummaryAsync(cancellationToken));
        });

        MapAdminEndpoints(adminApi);
        return endpoints;
    }

    private static void MapAdminEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/users", async (HttpContext context, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListUsersAsync(page, pageSize, cancellationToken));
        });

        api.MapGet("/users/{userId}", async (HttpContext context, string userId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.GetUserAsync(userId, cancellationToken));
        });

        api.MapGet("/users/{userId}/memberships", async (HttpContext context, string userId, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListUserMembershipsAsync(userId, page, pageSize, cancellationToken));
        });

        api.MapGet("/users/{userId}/sessions", async (HttpContext context, string userId, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListUserSessionsAsync(userId, page, pageSize, cancellationToken));
        });

        api.MapPost("/users", async (HttpContext context, SqlOSCreateUserRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var user = await adminService.CreateUserAsync(request, cancellationToken);
            return Results.Ok(new
            {
                user.Id,
                user.DisplayName,
                user.DefaultEmail,
                user.IsActive,
                user.CreatedAt,
                user.UpdatedAt
            });
        });

        api.MapGet("/organizations", async (HttpContext context, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListOrganizationsAsync(page, pageSize, cancellationToken));
        });

        api.MapGet("/organizations/{organizationId}", async (HttpContext context, string organizationId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.GetOrganizationAsync(organizationId, cancellationToken));
        });

        api.MapPost("/organizations", async (HttpContext context, SqlOSCreateOrganizationRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var organization = await adminService.CreateOrganizationAsync(request, cancellationToken);
            return Results.Ok(new
            {
                organization.Id,
                organization.Name,
                organization.Slug,
                organization.PrimaryDomain,
                organization.IsActive,
                organization.CreatedAt
            });
        });

        api.MapPut("/organizations/{organizationId}", async (HttpContext context, string organizationId, SqlOSUpdateOrganizationRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var organization = await adminService.UpdateOrganizationAsync(organizationId, request, cancellationToken);
            return Results.Ok(new
            {
                organization.Id,
                organization.Name,
                organization.Slug,
                organization.PrimaryDomain,
                organization.IsActive,
                organization.CreatedAt
            });
        });

        api.MapGet("/memberships", async (HttpContext context, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListMembershipsAsync(page, pageSize, cancellationToken));
        });

        api.MapGet("/organizations/{organizationId}/memberships", async (HttpContext context, string organizationId, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListOrganizationMembershipsAsync(organizationId, page, pageSize, cancellationToken));
        });

        api.MapPost("/memberships", async (HttpContext context, CreateMembershipRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var membership = await adminService.CreateMembershipAsync(request.OrganizationId, new SqlOSCreateMembershipRequest(request.UserId, request.Role), cancellationToken);
            return Results.Ok(new
            {
                membership.OrganizationId,
                membership.UserId,
                membership.Role,
                membership.IsActive,
                membership.CreatedAt
            });
        });

        api.MapPost("/organizations/{organizationId}/memberships", async (HttpContext context, string organizationId, SqlOSCreateMembershipRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var membership = await adminService.CreateMembershipAsync(organizationId, request, cancellationToken);
            return Results.Ok(new
            {
                membership.OrganizationId,
                membership.UserId,
                membership.Role,
                membership.IsActive,
                membership.CreatedAt
            });
        });

        api.MapGet("/clients", async (HttpContext context, string? source, string? status, string? search, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListClientsAsync(source, status, search, page, pageSize, cancellationToken));
        });

        api.MapGet("/clients/{clientId}", async (HttpContext context, string clientId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await adminService.GetClientDetailAsync(clientId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/clients", async (HttpContext context, SqlOSCreateClientRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var client = await adminService.CreateClientAsync(request, cancellationToken);
                return Results.Ok(new
                {
                    client.Id,
                    client.ClientId,
                    client.Name,
                    client.Audience,
                    RedirectUris = SqlOSAdminService.DeserializeJsonList(client.RedirectUrisJson),
                    client.IsActive,
                    client.CreatedAt
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/clients/{clientId}/disable", async (HttpContext context, string clientId, ClientLifecycleRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var client = await adminService.DisableClientAsync(clientId, request.Reason, cancellationToken);
                return Results.Ok(new
                {
                    client.Id,
                    client.ClientId,
                    client.IsActive,
                    client.DisabledAt,
                    client.DisabledReason
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/clients/{clientId}/enable", async (HttpContext context, string clientId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var client = await adminService.EnableClientAsync(clientId, cancellationToken);
                return Results.Ok(new
                {
                    client.Id,
                    client.ClientId,
                    client.IsActive,
                    client.DisabledAt,
                    client.DisabledReason
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/clients/{clientId}/revoke", async (HttpContext context, string clientId, ClientLifecycleRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var revokedSessions = await adminService.RevokeClientSessionsAsync(clientId, string.IsNullOrWhiteSpace(request.Reason) ? "client_revoked" : request.Reason.Trim(), cancellationToken);
                return Results.Ok(new { revokedSessions });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapGet("/oidc-connections", async (HttpContext context, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListOidcConnectionsAsync(cancellationToken));
        });

        api.MapPost("/oidc-connections", async (HttpContext context, CreateOidcConnectionRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            if (!Enum.TryParse<SqlOSOidcProviderType>(request.ProviderType, ignoreCase: true, out var providerType))
            {
                return Results.BadRequest(new { message = $"Unsupported OIDC provider '{request.ProviderType}'." });
            }

            if (!TryParseClientAuthMethod(request.ClientAuthMethod, out var clientAuthMethod))
            {
                return Results.BadRequest(new { message = $"Unsupported OIDC client auth method '{request.ClientAuthMethod}'." });
            }

            var connection = await adminService.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
                providerType,
                request.DisplayName,
                request.ClientId,
                request.ClientSecret,
                request.AllowedCallbackUris,
                request.UseDiscovery,
                request.DiscoveryUrl,
                request.Issuer,
                request.AuthorizationEndpoint,
                request.TokenEndpoint,
                request.UserInfoEndpoint,
                request.JwksUri,
                request.MicrosoftTenant,
                request.Scopes,
                request.ClaimMapping,
                clientAuthMethod,
                request.UseUserInfo,
                request.AppleTeamId,
                request.AppleKeyId,
                request.ApplePrivateKeyPem,
                request.LogoDataUrl), cancellationToken);
            return Results.Ok(ToOidcConnectionResponse(connection));
        });

        api.MapPut("/oidc-connections/{connectionId}", async (HttpContext context, string connectionId, UpdateOidcConnectionRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            if (!TryParseClientAuthMethod(request.ClientAuthMethod, out var clientAuthMethod))
            {
                return Results.BadRequest(new { message = $"Unsupported OIDC client auth method '{request.ClientAuthMethod}'." });
            }

            var connection = await adminService.UpdateOidcConnectionAsync(connectionId, new SqlOSUpdateOidcConnectionRequest(
                request.DisplayName,
                request.ClientId,
                request.ClientSecret,
                request.AllowedCallbackUris,
                request.UseDiscovery,
                request.DiscoveryUrl,
                request.Issuer,
                request.AuthorizationEndpoint,
                request.TokenEndpoint,
                request.UserInfoEndpoint,
                request.JwksUri,
                request.MicrosoftTenant,
                request.Scopes,
                request.ClaimMapping,
                clientAuthMethod,
                request.UseUserInfo,
                request.AppleTeamId,
                request.AppleKeyId,
                request.ApplePrivateKeyPem,
                request.LogoDataUrl), cancellationToken);
            return Results.Ok(ToOidcConnectionResponse(connection));
        });

        api.MapPost("/oidc-connections/{connectionId}/enable", async (HttpContext context, string connectionId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var connection = await adminService.SetOidcConnectionEnabledAsync(connectionId, true, cancellationToken);
            return Results.Ok(new { connection.Id, connection.IsEnabled, connection.UpdatedAt });
        });

        api.MapPost("/oidc-connections/{connectionId}/disable", async (HttpContext context, string connectionId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var connection = await adminService.SetOidcConnectionEnabledAsync(connectionId, false, cancellationToken);
            return Results.Ok(new { connection.Id, connection.IsEnabled, connection.UpdatedAt });
        });

        api.MapGet("/sso-connections", async (HttpContext context, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListSsoConnectionsAsync(cancellationToken));
        });

        api.MapGet("/organizations/{organizationId}/sso-connections", async (HttpContext context, string organizationId, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListOrganizationSsoConnectionsAsync(organizationId, page, pageSize, cancellationToken));
        });

        api.MapPost("/sso-connections/draft", async (HttpContext context, SqlOSCreateSsoConnectionDraftRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var connection = await adminService.CreateSsoConnectionDraftAsync(request, cancellationToken);
            return Results.Ok(new
            {
                connection.Id,
                connection.OrganizationId,
                connection.DisplayName,
                connection.IsEnabled,
                ServiceProviderEntityId = adminService.GetServiceProviderEntityId(),
                AssertionConsumerServiceUrl = adminService.GetAssertionConsumerServiceUrl(connection.Id)
            });
        });

        api.MapPost("/sso-connections/{connectionId}/metadata", async (HttpContext context, string connectionId, SqlOSImportSsoMetadataRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var connection = await adminService.ImportSsoMetadataAsync(connectionId, request, cancellationToken);
            return Results.Ok(new
            {
                connection.Id,
                connection.OrganizationId,
                connection.DisplayName,
                connection.IsEnabled,
                connection.IdentityProviderEntityId,
                connection.SingleSignOnUrl,
                connection.UpdatedAt
            });
        });

        api.MapPost("/sso-connections", async (HttpContext context, SqlOSCreateSsoConnectionRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var connection = await adminService.CreateSsoConnectionAsync(request, cancellationToken);
            return Results.Ok(new
            {
                connection.Id,
                connection.OrganizationId,
                connection.DisplayName,
                connection.IsEnabled,
                connection.IdentityProviderEntityId,
                connection.SingleSignOnUrl,
                connection.AutoProvisionUsers,
                connection.AutoLinkByEmail,
                connection.CreatedAt,
                connection.UpdatedAt
            });
        });

        api.MapGet("/settings/security", async (HttpContext context, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.GetSecuritySettingsAsync(cancellationToken));
        });

        api.MapPut("/settings/security", async (HttpContext context, SqlOSUpdateSecuritySettingsRequest request, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.UpdateSecuritySettingsAsync(request, cancellationToken));
        });

        api.MapGet("/signing-keys", async (HttpContext context, SqlOSCryptoService cryptoService, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var keys = await cryptoService.ListSigningKeysAsync(cancellationToken);
            var rotationSettings = await settingsService.GetKeyRotationSettingsAsync(cancellationToken);
            var activeKey = keys.FirstOrDefault(k => k.IsActive);

            return Results.Ok(new
            {
                keys = keys.Select(k => new
                {
                    k.Id,
                    k.Kid,
                    k.Algorithm,
                    k.IsActive,
                    k.ActivatedAt,
                    k.RetiredAt,
                    ageDays = Math.Round((DateTime.UtcNow - k.ActivatedAt).TotalDays, 1)
                }),
                rotationIntervalDays = rotationSettings.RotationInterval.TotalDays,
                graceWindowDays = rotationSettings.GraceWindow.TotalDays,
                nextRotationDue = activeKey != null
                    ? activeKey.ActivatedAt.Add(rotationSettings.RotationInterval)
                    : (DateTime?)null
            });
        });

        api.MapPost("/signing-keys/rotate", async (HttpContext context, SqlOSCryptoService cryptoService, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var newKey = await cryptoService.RotateSigningKeyAsync(cancellationToken);
            await adminService.RecordAuditAsync(
                "signing_key_rotated_manual",
                "admin",
                "dashboard",
                data: new { newKeyId = newKey.Id, newKid = newKey.Kid },
                cancellationToken: cancellationToken);

            return Results.Ok(new
            {
                newKey.Id,
                newKey.Kid,
                newKey.Algorithm,
                newKey.ActivatedAt
            });
        });

        api.MapGet("/settings/auth-page", async (HttpContext context, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.GetAuthPageSettingsAsync(cancellationToken));
        });

        api.MapPut("/settings/auth-page", async (HttpContext context, SqlOSUpdateAuthPageSettingsRequest request, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.UpdateAuthPageSettingsAsync(request, cancellationToken));
        });

        api.MapGet("/sessions", async (HttpContext context, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListSessionsAsync(page, pageSize, cancellationToken));
        });

        api.MapGet("/audit-events", async (HttpContext context, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListAuditEventsAsync(cancellationToken));
        });
    }

    private static async Task<bool> IsAdminAuthorizedAsync(HttpContext context, SqlOSAuthServerOptions options, IHostEnvironment environment)
    {
        if (options.Dashboard.AuthMode == SqlOSDashboardAuthMode.Password)
        {
            var sessionService = context.RequestServices.GetService<SqlOSDashboardSessionService>();
            if (sessionService == null || !sessionService.HasActiveSession(context))
            {
                return false;
            }

            if (options.Dashboard.AuthorizationCallback != null)
            {
                return await options.Dashboard.AuthorizationCallback(context);
            }

            return true;
        }

        if (options.Dashboard.AuthorizationCallback != null)
        {
            return await options.Dashboard.AuthorizationCallback(context);
        }

        return environment.IsDevelopment();
    }

    private static IResult Html(SqlOSAuthPageViewModel model, int statusCode = StatusCodes.Status200OK)
        => Results.Content(SqlOSAuthPageRenderer.RenderPage(model), contentType: "text/html", statusCode: statusCode);

    private static async Task<SqlOSAuthPageViewModel> BuildAuthPageViewModelAsync(
        string mode,
        string? authorizationRequestId,
        string? email,
        string? error,
        string? displayName,
        string? pendingToken,
        string authPrefix,
        SqlOSAuthorizationServerService authorizationServerService,
        CancellationToken cancellationToken,
        IReadOnlyList<SqlOSOrganizationOption>? organizationSelection = null)
    {
        var settings = await authorizationServerService.GetAuthPageSettingsAsync(cancellationToken);
        var providerBasePath = authorizationRequestId == null
            ? null
            : $"{authPrefix}/login/oidc/{{0}}?request={Uri.EscapeDataString(authorizationRequestId)}&email={Uri.EscapeDataString(email ?? string.Empty)}";
        var providers = providerBasePath == null
            ? Array.Empty<SqlOSAuthPageProviderLink>()
            : (await authorizationServerService.ListEnabledOidcProvidersAsync(cancellationToken))
                .Select(provider => new SqlOSAuthPageProviderLink(
                    provider.ConnectionId,
                    provider.DisplayName,
                    string.Format(providerBasePath, provider.ConnectionId),
                    provider.LogoDataUrl))
                .ToArray();

        return new SqlOSAuthPageViewModel(
            mode,
            settings,
            authPrefix,
            authorizationRequestId,
            email,
            displayName,
            error,
            null,
            pendingToken,
            organizationSelection ?? Array.Empty<SqlOSOrganizationOption>(),
            providers);
    }

    private sealed record LogoutRequest(string? RefreshToken, string? SessionId);
    private sealed record LogoutAllRequest(string UserId);
    private sealed record CreateMembershipRequest(string OrganizationId, string UserId, string Role);
    private static bool TryParseClientAuthMethod(string? value, out SqlOSOidcClientAuthMethod? method)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            method = null;
            return true;
        }

        if (Enum.TryParse<SqlOSOidcClientAuthMethod>(value, ignoreCase: true, out var parsed))
        {
            method = parsed;
            return true;
        }

        method = null;
        return false;
    }

    private static object ToOidcConnectionResponse(SqlOSOidcConnection connection) => new
    {
        connection.Id,
        ProviderType = connection.ProviderType.ToString(),
        connection.DisplayName,
        connection.LogoDataUrl,
        EffectiveLogoDataUrl = SqlOSOidcProviderLogoCatalog.ResolveEffectiveLogoDataUrl(connection.ProviderType, connection.LogoDataUrl),
        connection.ClientId,
        AllowedCallbackUris = connection.AllowedCallbackUrisJson,
        connection.UseDiscovery,
        connection.DiscoveryUrl,
        connection.Issuer,
        connection.AuthorizationEndpoint,
        connection.TokenEndpoint,
        connection.UserInfoEndpoint,
        connection.JwksUri,
        connection.MicrosoftTenant,
        Scopes = connection.ScopesJson,
        ClaimMapping = connection.ClaimMappingJson,
        ClientAuthMethod = connection.ClientAuthMethod.ToString(),
        connection.UseUserInfo,
        connection.AppleTeamId,
        connection.AppleKeyId,
        connection.IsEnabled,
        connection.CreatedAt,
        connection.UpdatedAt
    };

    private sealed record CreateOidcConnectionRequest(
        string ProviderType,
        string DisplayName,
        string ClientId,
        string? ClientSecret,
        List<string> AllowedCallbackUris,
        bool UseDiscovery,
        string? DiscoveryUrl,
        string? Issuer,
        string? AuthorizationEndpoint,
        string? TokenEndpoint,
        string? UserInfoEndpoint,
        string? JwksUri,
        string? MicrosoftTenant,
        List<string>? Scopes,
        SqlOSOidcClaimMapping? ClaimMapping,
        string? ClientAuthMethod,
        bool? UseUserInfo,
        string? AppleTeamId,
        string? AppleKeyId,
        string? ApplePrivateKeyPem,
        string? LogoDataUrl);

    private sealed record UpdateOidcConnectionRequest(
        string DisplayName,
        string ClientId,
        string? ClientSecret,
        List<string> AllowedCallbackUris,
        bool UseDiscovery,
        string? DiscoveryUrl,
        string? Issuer,
        string? AuthorizationEndpoint,
        string? TokenEndpoint,
        string? UserInfoEndpoint,
        string? JwksUri,
        string? MicrosoftTenant,
        List<string>? Scopes,
        SqlOSOidcClaimMapping? ClaimMapping,
        string? ClientAuthMethod,
        bool? UseUserInfo,
        string? AppleTeamId,
        string? AppleKeyId,
        string? ApplePrivateKeyPem,
        string? LogoDataUrl);

    private sealed record ClientLifecycleRequest(string? Reason);
}
