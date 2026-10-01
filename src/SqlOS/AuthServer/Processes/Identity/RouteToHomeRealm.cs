using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Home-realm discovery for an authorization request: the address a person typed decides whether
/// they sign in here or at their organization's SAML identity provider. The hosted and headless
/// surfaces run it before a credential process (identify, password, email code, sign-in link and
/// the sign-ups that name an address), exactly where 7.2.1 did.
/// </summary>
/// <remarks>
/// The request remembers the address as its login hint and the discovered connection (or forgets a
/// connection an earlier discovery bound); an organization that requires SSO for the address answers
/// with the redirect to its identity provider. It is its own process, not a step of each credential
/// process, because the headless surface answers its failures differently from the credential's
/// (a JSON error rather than the view the credential's refusal renders). Layer 3 folds it into the
/// authorization request's state machine.
/// </remarks>
internal sealed class RouteToHomeRealm(
    ISqlOSAuthServerDbContext context,
    SqlOSHomeRealmDiscoveryService discovery,
    SqlOSSamlService saml)
{
    public async Task<HomeRealmRoute> ExecuteAsync(RouteToHomeRealmCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = await discovery.DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest(command.Email), cancellationToken);
        command.Request.LoginHintEmail = command.Email;
        SqlOSHomeRealmDiscoveryService.BindToAuthorizationRequest(command.Request, result);
        await context.SaveChangesAsync(cancellationToken);

        return string.Equals(result.Mode, "sso", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(result.ConnectionId)
            ? new HomeRealmRoute.IdentityProvider(
                await saml.BuildIdentityProviderRedirectForAuthorizationRequestAsync(command.Request.Id, cancellationToken))
            : HomeRealmRoute.SignInHere.Instance;
    }
}

/// <summary>Home-realm discovery for <paramref name="Email"/> on <paramref name="Request"/>.</summary>
internal sealed record RouteToHomeRealmCommand(SqlOSAuthorizationRequest Request, string Email);

/// <summary>Where the person signs in.</summary>
internal abstract record HomeRealmRoute
{
    private HomeRealmRoute()
    {
    }

    /// <summary>Here, with a SqlOS credential.</summary>
    public sealed record SignInHere : HomeRealmRoute
    {
        public static SignInHere Instance { get; } = new();
    }

    /// <summary>At the organization's SAML identity provider, through <see cref="RedirectUrl"/>.</summary>
    public sealed record IdentityProvider(string RedirectUrl) : HomeRealmRoute;
}
