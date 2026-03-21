using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSHeadlessAuthService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSAuthorizationServerService _authorizationServerService;
    private readonly SqlOSHomeRealmDiscoveryService _discoveryService;
    private readonly SqlOSOidcBrowserAuthService _oidcBrowserAuthService;
    private readonly SqlOSSamlService _samlService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSHeadlessAuthService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSAuthorizationServerService authorizationServerService,
        SqlOSHomeRealmDiscoveryService discoveryService,
        SqlOSOidcBrowserAuthService oidcBrowserAuthService,
        SqlOSSamlService samlService,
        SqlOSSettingsService settingsService,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _adminService = adminService;
        _authorizationServerService = authorizationServerService;
        _discoveryService = discoveryService;
        _oidcBrowserAuthService = oidcBrowserAuthService;
        _samlService = samlService;
        _settingsService = settingsService;
        _options = options.Value;
    }

    public bool IsEnabled => string.Equals(_settingsService.CurrentPresentationMode, "headless", StringComparison.OrdinalIgnoreCase)
        && _options.Headless.BuildUiUrl != null;

    public string GetHeadlessApiBasePath() => _options.Headless.ResolveApiBasePath(_options.BasePath);

    public string BuildStandaloneUiUrl(
        HttpContext httpContext,
        string view,
        string? requestId = null,
        string? email = null,
        JsonObject? uiContext = null)
        => BuildUiUrl(
            httpContext,
            requestId,
            view,
            error: null,
            pendingToken: null,
            email: email,
            displayName: null,
            uiContext: uiContext);

    public string BuildUiUrl(
        HttpContext httpContext,
        string? requestId,
        string view,
        string? error,
        string? pendingToken,
        string? email,
        string? displayName,
        JsonObject? uiContext)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("Headless auth mode is not enabled.");
        }

        if (_options.Headless.BuildUiUrl == null)
        {
            throw new InvalidOperationException("Headless auth mode requires BuildUiUrl to be configured.");
        }

        return _options.Headless.BuildUiUrl(
            new SqlOSHeadlessUiRouteContext(
                httpContext,
                requestId,
                NormalizeView(view),
                error,
                pendingToken,
                email,
                displayName,
                uiContext));
    }

    public async Task<string?> TryBuildUiUrlForAuthorizationRequestAsync(
        HttpContext httpContext,
        string authorizationRequestId,
        string view,
        string? error,
        string? pendingToken,
        string? email,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.TryGetActiveAuthorizationRequestAsync(authorizationRequestId, cancellationToken);
        if (authorizationRequest == null || !IsHeadlessRequest(authorizationRequest))
        {
            return null;
        }

        return BuildUiUrl(
            httpContext,
            authorizationRequest.Id,
            view,
            error,
            pendingToken,
            email ?? authorizationRequest.LoginHintEmail,
            displayName,
            ParseUiContext(authorizationRequest.UiContextJson));
    }

    public async Task<SqlOSHeadlessViewModel> GetRequestAsync(
        string requestId,
        string? requestedView,
        string? error,
        string? pendingToken,
        string? email,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(requestId, cancellationToken);
        return await BuildViewModelAsync(
            authorizationRequest,
            requestedView,
            error,
            pendingToken,
            email,
            displayName,
            fieldErrors: null,
            organizationSelection: null,
            cancellationToken);
    }

    public async Task<SqlOSHeadlessActionResult> IdentifyAsync(
        SqlOSHeadlessIdentifyRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        var discovery = await _discoveryService.DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest(request.Email), cancellationToken);

        authorizationRequest.LoginHintEmail = request.Email;
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

        await _context.SaveChangesAsync(cancellationToken);

        if (string.Equals(discovery.Mode, "sso", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(discovery.ConnectionId))
        {
            return Redirect(await _samlService.BuildIdentityProviderRedirectForAuthorizationRequestAsync(authorizationRequest.Id, cancellationToken));
        }

        return View(await BuildViewModelAsync(
            authorizationRequest,
            "password",
            error: null,
            pendingToken: null,
            email: request.Email,
            displayName: null,
            fieldErrors: null,
            organizationSelection: null,
            cancellationToken));
    }

    public async Task<SqlOSHeadlessActionResult> PasswordLoginAsync(
        HttpContext httpContext,
        SqlOSHeadlessPasswordLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);

        try
        {
            var authentication = await _authorizationServerService.AuthenticatePasswordAsync(request.Email, request.Password, cancellationToken);

            if (!string.IsNullOrWhiteSpace(authorizationRequest.OrganizationId))
            {
                if (authentication.Organizations.All(x => x.Id != authorizationRequest.OrganizationId))
                {
                    throw new InvalidOperationException("The selected organization is not available to this user.");
                }

                return Redirect(await _authorizationServerService.IssueAuthorizationRedirectAsync(
                    authorizationRequest,
                    authentication.User,
                    authorizationRequest.OrganizationId,
                    authentication.AuthenticationMethod,
                    httpContext,
                    cancellationToken));
            }

            if (authentication.Organizations.Count > 1)
            {
                var pendingToken = await _authorizationServerService.CreatePendingOrganizationSelectionAsync(
                    authentication.User,
                    authorizationRequest,
                    authentication.AuthenticationMethod,
                    cancellationToken);

                return View(await BuildViewModelAsync(
                    authorizationRequest,
                    "organization",
                    error: null,
                    pendingToken,
                    email: request.Email,
                    displayName: null,
                    fieldErrors: null,
                    organizationSelection: authentication.Organizations,
                    cancellationToken));
            }

            return Redirect(await _authorizationServerService.IssueAuthorizationRedirectAsync(
                authorizationRequest,
                authentication.User,
                authentication.Organizations.FirstOrDefault()?.Id,
                authentication.AuthenticationMethod,
                httpContext,
                cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "password",
                ex.Message,
                pendingToken: null,
                email: request.Email,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> SignUpAsync(
        HttpContext httpContext,
        SqlOSHeadlessSignupRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        IDbContextTransaction? transaction = null;
        SqlOSPasswordAuthenticationResult? signup = null;

        try
        {
            if (SupportsDatabaseTransactions())
            {
                transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            signup = await _authorizationServerService.SignUpAsync(
                request.DisplayName,
                request.Email,
                request.Password,
                request.OrganizationName,
                authorizationRequest.OrganizationId,
                cancellationToken);

            var selectedOrganizationId = authorizationRequest.OrganizationId ?? signup.Organizations.FirstOrDefault()?.Id;
            SqlOSOrganization? organization = null;
            if (!string.IsNullOrWhiteSpace(selectedOrganizationId))
            {
                organization = await _context.Set<SqlOSOrganization>()
                    .FirstOrDefaultAsync(x => x.Id == selectedOrganizationId, cancellationToken);
            }

            if (_options.Headless.OnHeadlessSignupAsync != null)
            {
                await _options.Headless.OnHeadlessSignupAsync(
                    new SqlOSHeadlessSignupHookContext(
                        httpContext,
                        authorizationRequest,
                        signup.User,
                        organization,
                        request.CustomFields ?? new JsonObject()),
                    cancellationToken);
            }

            var redirectUrl = await _authorizationServerService.IssueAuthorizationRedirectAsync(
                authorizationRequest,
                signup.User,
                selectedOrganizationId,
                signup.AuthenticationMethod,
                httpContext,
                cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return Redirect(redirectUrl);
        }
        catch (SqlOSHeadlessValidationException ex)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                await CleanupNonTransactionalSignupArtifactsAsync(signup, authorizationRequest.OrganizationId, request.OrganizationName, cancellationToken);
            }
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "signup",
                ex.GlobalErrors.FirstOrDefault() ?? ex.Message,
                pendingToken: null,
                email: request.Email,
                displayName: request.DisplayName,
                fieldErrors: ex.FieldErrors,
                organizationSelection: null,
                cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                await CleanupNonTransactionalSignupArtifactsAsync(signup, authorizationRequest.OrganizationId, request.OrganizationName, cancellationToken);
            }
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "signup",
                ex.Message,
                pendingToken: null,
                email: request.Email,
                displayName: request.DisplayName,
                fieldErrors: null,
                organizationSelection: null,
                cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> SelectOrganizationAsync(
        HttpContext httpContext,
        SqlOSHeadlessOrganizationSelectionRequest request,
        CancellationToken cancellationToken = default)
        => Redirect(await _authorizationServerService.CompletePendingOrganizationSelectionAsync(
            request.PendingToken,
            request.OrganizationId,
            httpContext,
            cancellationToken));

    public async Task<SqlOSHeadlessActionResult> StartProviderAsync(
        HttpContext httpContext,
        SqlOSHeadlessProviderStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _oidcBrowserAuthService.CreateAuthorizationUrlForAuthRequestAsync(
            request.RequestId,
            request.ConnectionId,
            request.Email,
            httpContext,
            cancellationToken);

        return Redirect(result.AuthorizationUrl);
    }

    public async Task<SqlOSHeadlessViewModel> BuildViewModelAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        string? requestedView,
        string? error,
        string? pendingToken,
        string? email,
        string? displayName,
        IReadOnlyDictionary<string, string>? fieldErrors,
        IReadOnlyList<SqlOSOrganizationOption>? organizationSelection,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetAuthPageSettingsAsync(cancellationToken);
        var providers = (await _authorizationServerService.ListEnabledOidcProvidersAsync(cancellationToken))
            .Select(provider => new SqlOSHeadlessProviderDto(
                provider.ConnectionId,
                provider.ProviderType,
                provider.DisplayName,
                provider.LogoDataUrl))
            .ToArray();

        return new SqlOSHeadlessViewModel(
            NormalizeView(requestedView),
            _options.BasePath.TrimEnd('/'),
            GetHeadlessApiBasePath(),
            settings,
            authorizationRequest.Id,
            authorizationRequest.ClientApplication?.ClientId,
            authorizationRequest.ClientApplication?.Name,
            email ?? authorizationRequest.LoginHintEmail,
            displayName,
            error,
            Info: null,
            fieldErrors ?? new Dictionary<string, string>(StringComparer.Ordinal),
            pendingToken,
            organizationSelection ?? Array.Empty<SqlOSOrganizationOption>(),
            providers,
            ParseUiContext(authorizationRequest.UiContextJson));
    }

    public static bool IsHeadlessRequest(SqlOSAuthorizationRequest authorizationRequest)
        => string.Equals(authorizationRequest.PresentationMode, "headless", StringComparison.OrdinalIgnoreCase);

    public static JsonObject? ParseUiContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    public static string? NormalizeUiContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return ParseUiContext(json)?.ToJsonString();
    }

    public static string NormalizeView(string? requestedView)
        => string.Equals(requestedView, "signup", StringComparison.OrdinalIgnoreCase)
            ? "signup"
            : string.Equals(requestedView, "password", StringComparison.OrdinalIgnoreCase)
                ? "password"
                : string.Equals(requestedView, "organization", StringComparison.OrdinalIgnoreCase)
                    ? "organization"
                    : string.Equals(requestedView, "logged-out", StringComparison.OrdinalIgnoreCase)
                        ? "logged-out"
                        : "login";

    private static SqlOSHeadlessActionResult Redirect(string url)
        => new("redirect", url, null);

    private static SqlOSHeadlessActionResult View(SqlOSHeadlessViewModel viewModel)
        => new("view", null, viewModel);

    private bool SupportsDatabaseTransactions()
        => !string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    private async Task CleanupNonTransactionalSignupArtifactsAsync(
        SqlOSPasswordAuthenticationResult? signup,
        string? existingOrganizationId,
        string? organizationName,
        CancellationToken cancellationToken)
    {
        if (signup == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(organizationName) && string.IsNullOrWhiteSpace(existingOrganizationId))
        {
            var organizationIds = signup.Organizations
                .Select(static x => x.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (organizationIds.Length > 0)
            {
                var organizations = await _context.Set<SqlOSOrganization>()
                    .Where(x => organizationIds.Contains(x.Id))
                    .ToListAsync(cancellationToken);

                if (organizations.Count > 0)
                {
                    _context.Set<SqlOSOrganization>().RemoveRange(organizations);
                }
            }
        }

        var user = await _context.Set<SqlOSUser>()
            .FirstOrDefaultAsync(x => x.Id == signup.User.Id, cancellationToken);
        if (user != null)
        {
            _context.Set<SqlOSUser>().Remove(user);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }
}
