using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Dashboard;

/// <summary>
/// Unrecorded FGA preconditions for the dashboard and probe scenarios. Every call goes through the
/// library probes (<c>/__probe/fga/*</c>), the documented APIs a host uses, never through SQL, so
/// the same arrangement works against the released package and the source. Calls must succeed.
/// Resource and non-user subject IDs are fixed strings, so the grant IDs SqlOS derives from them
/// are the same in every run.
/// </summary>
internal sealed class FgaFixture
{
    private readonly Transcript _transcript;
    private readonly HttpActor _probe;

    public FgaFixture(Transcript transcript)
    {
        _transcript = transcript;
        _probe = transcript.NewClient("fga-setup");
    }

    /// <summary>Provisions an FGA user subject whose ID is the SqlOS user ID, as the FGA guides do.</summary>
    public Task UserSubjectAsync(ScenarioUser user)
        => SendAsync("/__probe/fga/subjects", new { type = "user", subjectId = user.Id, displayName = user.DisplayName, email = user.Email });

    public Task AgentSubjectAsync(string subjectId, string displayName)
        => SendAsync("/__probe/fga/subjects", new { type = "agent", subjectId, displayName });

    public Task ServiceAccountSubjectAsync(string subjectId, string displayName, string clientId)
        => SendAsync("/__probe/fga/subjects", new { type = "service_account", subjectId, displayName, clientId, clientSecretHash = "fixture-secret-hash" });

    /// <summary>Saves a workspace entity (an ISqlOSResourceEntity) and returns its resource ID.</summary>
    public async Task<string> WorkspaceAsync(string id, string name, string? parentResourceId = null)
    {
        var saved = await SendAsync("/__probe/fga/workspaces", new { id, name, parentResourceId });
        return saved.JsonString("workspace.resourceId");
    }

    /// <summary>Creates a manual resource under the root resource with <c>CreateResource</c>.</summary>
    public async Task<string> RootChildAsync(string id, string name)
    {
        var created = await SendAsync("/__probe/fga/resources", new { mode = "create", resourceTypeId = BehaviorLockAuthorization.WorkspaceType, name, resourceId = id });
        return created.JsonString("id");
    }

    public async Task<string> GrantAsync(string subjectId, string resourceId, string role)
    {
        var grant = await SendAsync("/__probe/fga/grants", new { subjectId, resourceId, role });
        return grant.JsonString("id");
    }

    private async Task<HttpExchange> SendAsync(string target, object body)
    {
        var exchange = _transcript.Discard(await _probe.PostJsonAsync(target, body));
        if (exchange.StatusCode != 200)
        {
            throw new InvalidOperationException($"FGA setup failed: {exchange.Describe()} {exchange.ResponseBody}");
        }

        return exchange;
    }
}
