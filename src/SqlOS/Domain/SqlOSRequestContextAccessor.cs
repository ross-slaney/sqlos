namespace SqlOS.Domain;

/// <summary>
/// Holds the <see cref="SqlOSRequestContext"/> of the current dependency-injection scope.
/// Registered as scoped: the adapter that serves a request sets it, and processes, the audit
/// recorder and the domain-event interceptor of the same scope read it.
/// </summary>
internal sealed class SqlOSRequestContextAccessor
{
    private SqlOSRequestContext _current = SqlOSRequestContext.System;

    public SqlOSRequestContext Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }
}
