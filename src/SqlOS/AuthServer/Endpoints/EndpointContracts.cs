using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.AuthServer.Security;
using SqlOS.Configuration;
using SqlOS.Dashboard;

namespace SqlOS.AuthServer.Extensions;

public static partial class EndpointRouteBuilderExtensions
{
    private sealed record LogoutRequest(string? RefreshToken);
    private sealed record CreateOrganizationInvitationRequest(
        string Email,
        string Role,
        string? ClientId,
        string? RedirectUri,
        string? Scope,
        string? Resource,
        DateTime? ExpiresAt,
        JsonObject? CustomFields,
        string? InvitedByUserId,
        bool? SendEmail);
    private sealed record RevokeInvitationRequest(string? Reason);
    private sealed record LogoutAllRequest(string? RefreshToken);
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
        Protocol = connection.Protocol.ToString(),
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
    private sealed record CreateScimConnectionDashboardRequest(string DisplayName, bool Enabled = true);
    private sealed record UpdateScimConnectionDashboardRequest(string DisplayName, bool Enabled = true);
}
