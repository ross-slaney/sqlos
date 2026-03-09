using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSSsoAuthorizationService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSHomeRealmDiscoveryService _discoveryService;
    private readonly SqlOSSamlService _samlService;
    private readonly SqlOSAuthService _authService;

    public SqlOSSsoAuthorizationService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        SqlOSHomeRealmDiscoveryService discoveryService,
        SqlOSSamlService samlService,
        SqlOSAuthService authService)
    {
        _context = context;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _discoveryService = discoveryService;
        _samlService = samlService;
        _authService = authService;
    }

    public async Task<SqlOSSsoAuthorizationStartResult> StartAuthorizationAsync(SqlOSSsoAuthorizationStartRequest request, CancellationToken cancellationToken = default)
    {
        var discovery = await _discoveryService.DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest(request.Email), cancellationToken);
        if (!string.Equals(discovery.Mode, "sso", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("No SSO organization was found for the supplied email domain.");
        }

        var client = await _adminService.RequireClientAsync(request.ClientId, request.RedirectUri, cancellationToken);
        var authorizationRequest = new SqlOSAuthorizationRequest
        {
            Id = _cryptoService.GenerateId("req"),
            ClientApplicationId = client.Id,
            OrganizationId = discovery.OrganizationId!,
            ConnectionId = discovery.ConnectionId!,
            LoginHintEmail = request.Email,
            RedirectUri = request.RedirectUri,
            State = request.State,
            CodeChallenge = request.CodeChallenge,
            CodeChallengeMethod = request.CodeChallengeMethod,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        _context.Set<SqlOSAuthorizationRequest>().Add(authorizationRequest);
        await _context.SaveChangesAsync(cancellationToken);

        var authorizationUrl = await _samlService.BuildIdentityProviderRedirectForAuthorizationRequestAsync(authorizationRequest.Id, cancellationToken);
        return new SqlOSSsoAuthorizationStartResult(
            authorizationUrl,
            discovery.OrganizationId!,
            discovery.OrganizationName!,
            discovery.PrimaryDomain!);
    }

    public async Task<SqlOSTokenResponse> ExchangeCodeAsync(
        SqlOSPkceExchangeRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var codeHash = _cryptoService.HashToken(request.Code);
        var authorizationCode = await _context.Set<SqlOSAuthorizationCode>()
            .Include(x => x.User)
            .Include(x => x.ClientApplication)
            .FirstOrDefaultAsync(x => x.CodeHash == codeHash, cancellationToken)
            ?? throw new InvalidOperationException("Authorization code is invalid.");

        if (authorizationCode.ConsumedAt != null || authorizationCode.ExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Authorization code is no longer valid.");
        }

        if (!string.Equals(authorizationCode.ClientApplication?.ClientId, request.ClientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Authorization code was not issued for this client.");
        }

        if (!string.Equals(authorizationCode.RedirectUri, request.RedirectUri, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Redirect URI does not match the authorization request.");
        }

        if (!_cryptoService.VerifyPkceCodeVerifier(request.CodeVerifier, authorizationCode.CodeChallenge, authorizationCode.CodeChallengeMethod))
        {
            throw new InvalidOperationException("PKCE verification failed.");
        }

        authorizationCode.ConsumedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return await _authService.CreateSessionTokensForUserAsync(
            authorizationCode.User!,
            authorizationCode.ClientApplication!,
            authorizationCode.OrganizationId,
            authorizationCode.AuthenticationMethod,
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
    }
}
