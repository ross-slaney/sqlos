using Microsoft.Extensions.Primitives;

namespace SqlOS.Hosting;

/// <summary>
/// Records when application code mapped the auth server, so the startup filter can withdraw only
/// that copy and a manual <c>MapAuthServer()</c> call never registers those routes twice.
/// </summary>
internal sealed class SqlOSEndpointMappingState
{
    private readonly object _gate = new();
    private CancellationTokenSource _changed = new();
    private bool _mappedByApplication;

    /// <summary>
    /// <see langword="true"/> once application code called <c>MapAuthServer()</c>.
    /// The SqlOS-owned <see cref="SqlOSEndpointDataSource"/> then withdraws its auth-server routes
    /// and keeps the audit, email, and calendar routes. It decides lazily, so the application call
    /// may happen before or after the startup filter.
    /// </summary>
    public bool MappedByApplication
    {
        get
        {
            lock (_gate)
            {
                return _mappedByApplication;
            }
        }
    }

    /// <summary>
    /// <see langword="true"/> while the SqlOS startup filter is mapping its own endpoints, so the
    /// shared mapping helpers do not mistake that pass for an application call.
    /// </summary>
    public bool OwnedMappingInProgress { get; set; }

    /// <summary>Records that application code mapped the auth server. Returns whether this call changed the state.</summary>
    public bool MarkMappedByApplication()
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (_mappedByApplication)
            {
                return false;
            }

            _mappedByApplication = true;
            previous = _changed;
            _changed = new CancellationTokenSource();
        }

        previous.Cancel();
        previous.Dispose();
        return true;
    }

    /// <summary>Gets a change token that fires when <see cref="MappedByApplication"/> changes.</summary>
    public IChangeToken GetChangeToken()
    {
        lock (_gate)
        {
            return new CancellationChangeToken(_changed.Token);
        }
    }
}
