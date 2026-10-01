using System.Collections;
using System.Collections.Immutable;

namespace SqlOS.Domain;

/// <summary>
/// An OAuth scope set (RFC 6749 §3.3), with the ordering and normalization SqlOS 7.x applies.
/// </summary>
/// <remarks>
/// <para>
/// The wire form is space-delimited. Parsing splits on spaces, trims each entry, drops empty
/// entries and keeps the first occurrence of each scope, compared ordinally; the set keeps that
/// order, and <see cref="ToString"/> joins it with single spaces. Parsing never rejects a scope:
/// unknown or malformed scopes are dropped later by the grant (requested ∩ allowed), as in 7.x.
/// <see cref="IsValidToken"/> is the RFC 6749 <c>scope-token</c> syntax that dynamic client
/// registration enforces.
/// </para>
/// <para>
/// Equality is set equality: two sets with the same scopes in a different order are equal, as a
/// metadata refresh that only reorders scopes is the same grant. The default value is the empty
/// set. <c>SqlOSScopePolicy</c> delegates to this type.
/// </para>
/// </remarks>
internal readonly struct ScopeSet : IEquatable<ScopeSet>, IReadOnlyCollection<string>
{
    private readonly ImmutableArray<string> _scopes;

    private ScopeSet(ImmutableArray<string> scopes) => _scopes = scopes;

    public static ScopeSet Empty => default;

    public int Count => Scopes.Length;

    private ImmutableArray<string> Scopes => _scopes.IsDefault ? [] : _scopes;

    /// <summary>Parses a space-delimited scope string.</summary>
    public static ScopeSet Parse(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return Empty;
        }

        return new ScopeSet(scope
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray());
    }

    /// <summary>The set of <paramref name="scopes"/>, normalized as if they were one space-delimited string.</summary>
    public static ScopeSet Of(IEnumerable<string?> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        return Parse(string.Join(' ', scopes));
    }

    /// <summary>True when <paramref name="token"/> is an RFC 6749 <c>scope-token</c>: printable ASCII without space, <c>"</c> or <c>\</c>.</summary>
    public static bool IsValidToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        foreach (var ch in token)
        {
            if (ch is < (char)0x21 or > (char)0x7E or '"' or '\\')
            {
                return false;
            }
        }

        return true;
    }

    public bool Contains(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);

    /// <summary>True when every scope of this set is in <paramref name="other"/>.</summary>
    public bool IsSubsetOf(ScopeSet other)
    {
        foreach (var scope in Scopes)
        {
            if (!other.Contains(scope))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The scopes of this set that <paramref name="allowed"/> contains, in this set's order (the grant rule).</summary>
    public ScopeSet IntersectWith(IEnumerable<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        return new ScopeSet(Scopes.Where(allowedSet.Contains).ToImmutableArray());
    }

    /// <summary>This set followed by the scopes of <paramref name="other"/> it does not contain yet (the consent union rule).</summary>
    public ScopeSet UnionWith(ScopeSet other)
    {
        var union = Scopes.ToBuilder();
        foreach (var scope in other.Scopes)
        {
            if (!union.Contains(scope, StringComparer.Ordinal))
            {
                union.Add(scope);
            }
        }

        return new ScopeSet(union.ToImmutable());
    }

    public bool Equals(ScopeSet other) => Count == other.Count && IsSubsetOf(other);

    public override bool Equals(object? obj) => obj is ScopeSet other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var scope in Scopes)
        {
            hash ^= StringComparer.Ordinal.GetHashCode(scope);
        }

        return hash;
    }

    /// <summary>The space-delimited wire form, in set order.</summary>
    public override string ToString() => string.Join(' ', Scopes);

    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)Scopes).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static bool operator ==(ScopeSet left, ScopeSet right) => left.Equals(right);

    public static bool operator !=(ScopeSet left, ScopeSet right) => !left.Equals(right);
}
