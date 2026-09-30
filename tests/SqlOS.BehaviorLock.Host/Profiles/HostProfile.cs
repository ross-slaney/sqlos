using Microsoft.AspNetCore.Builder;
using SqlOS.BehaviorLock.Host.Fakes;
using SqlOS.Configuration;

namespace SqlOS.BehaviorLock.Host.Profiles;

/// <summary>How the behavior-lock harness authenticates as a SqlOS operator in a profile.</summary>
public enum OperatorAccess
{
    /// <summary><c>Dashboard.AuthorizationCallback</c> admits requests that carry the operator header.</summary>
    AuthorizationCallback,

    /// <summary><c>Dashboard.AuthMode = Password</c>: the harness signs in and presents the session cookie.</summary>
    Password,

    /// <summary><c>DevelopmentOnly</c> without a callback in the Development environment: admin APIs are open.</summary>
    DevelopmentOpen,

    /// <summary><c>DevelopmentOnly</c> without a callback outside Development: admin APIs answer 404.</summary>
    None
}

/// <summary>The EF integration style a profile's host uses.</summary>
public enum DbContextStyle
{
    /// <summary>The host context inherits <c>SqlOSDbContext&lt;TContext&gt;</c> (documented default).</summary>
    SqlOSDbContext,

    /// <summary>The host context implements the SqlOS interfaces and calls <c>UseSqlOS</c> itself.</summary>
    ManualInterfaces
}

/// <summary>
/// One supported SqlOS deployment model. Each profile is a complete, documented host
/// configuration; every profile runs on SQL Server and PostgreSQL.
/// </summary>
public sealed class HostProfile
{
    /// <summary>Stable identifier used by scenarios, approvals, and the coverage gate.</summary>
    public required string Name { get; init; }

    /// <summary>The deployment model this profile represents, in one sentence.</summary>
    public required string DeploymentModel { get; init; }

    /// <summary>The documentation that describes this deployment model.</summary>
    public required IReadOnlyList<string> Documentation { get; init; }

    /// <summary>The exact SqlOS options the profile sets, for the README profile table.</summary>
    public required IReadOnlyList<string> OptionsSummary { get; init; }

    public string Environment { get; init; } = "Production";

    public OperatorAccess OperatorAccess { get; init; } = OperatorAccess.AuthorizationCallback;

    public DbContextStyle DbContextStyle { get; init; } = DbContextStyle.SqlOSDbContext;

    /// <summary>
    /// Registers SqlOS with the documented overload that matches the deployment model.
    /// <see langword="true"/> uses <c>builder.AddSqlOS&lt;T&gt;(db =&gt; ..., options =&gt; ...)</c>;
    /// <see langword="false"/> registers the context with <c>AddDbContext</c> and calls <c>builder.AddSqlOS&lt;T&gt;(options =&gt; ...)</c>.
    /// </summary>
    public bool OneCallRegistration { get; init; } = true;

    /// <summary>Configures SqlOS inside the <c>AddSqlOS</c> options callback.</summary>
    public required Action<SqlOSOptions, HostProfileContext> ConfigureSqlOS { get; init; }

    /// <summary>Adds host services (authentication schemes, policies) after <c>AddSqlOS</c>.</summary>
    public Action<WebApplicationBuilder, HostProfileContext>? ConfigureHost { get; init; }

    /// <summary>Maps the host application's own routes.</summary>
    public Action<WebApplication, HostProfileContext>? MapApplication { get; init; }
}

/// <summary>What a profile's configuration callbacks can see.</summary>
public sealed class HostProfileContext
{
    public HostProfileContext(HostProfile profile, BehaviorLockFakes fakes, Func<HttpMessageHandler>? backchannel)
    {
        Profile = profile;
        Fakes = fakes;
        Backchannel = backchannel;
    }

    public HostProfile Profile { get; }

    public BehaviorLockFakes Fakes { get; }

    /// <summary>
    /// Creates a handler that reaches this host in-process (TestServer), for resource-server
    /// middleware such as <c>AddJwtBearer</c> that fetches discovery and JWKS from the issuer.
    /// Null when the host listens on a real socket.
    /// </summary>
    public Func<HttpMessageHandler>? Backchannel { get; }
}
