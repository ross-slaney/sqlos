using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace SqlOS.BehaviorLock.Infrastructure.Transcripts;

/// <summary>
/// What renderers report values to while they walk a response: role-bearing values are
/// registered with the <see cref="Scrubber"/>, and JWTs are collected so the transcript can show
/// their decoded header and claims.
/// </summary>
public sealed partial class TranscriptValueSink
{
    private readonly Scrubber _scrubber;
    private readonly List<string> _jwts = [];
    private readonly HashSet<string> _seenJwts = new(StringComparer.Ordinal);

    public TranscriptValueSink(Scrubber scrubber)
    {
        _scrubber = scrubber;
    }

    public Scrubber Scrubber => _scrubber;

    /// <summary>Registers <paramref name="value"/> when it looks like an opaque secret rather than a word or code name.</summary>
    public void Register(string? value, string kind)
    {
        if (value != null && LooksOpaque(value, kind))
        {
            _scrubber.Register(value, kind);
        }
    }

    /// <summary>Registers the value of a named field when <see cref="ValueRoles"/> gives the field a role.</summary>
    public void RegisterRole(string fieldName, string? value)
    {
        if (ValueRoles.KindFor(fieldName) is { } kind)
        {
            Register(value, kind);
        }
    }

    /// <summary>Looks inside a string for JWTs and for URLs whose query parameters have roles.</summary>
    public void Inspect(string value)
    {
        if (JwtRendering.LooksLikeJwt(value))
        {
            AddJwt(value);
            return;
        }

        foreach (Match match in EmbeddedJwt().Matches(value))
        {
            AddJwt(match.Value);
        }

        var queryStart = value.IndexOf('?', StringComparison.Ordinal);
        if (queryStart >= 0 && !value.Contains(' ', StringComparison.Ordinal))
        {
            RegisterQuery(value[queryStart..]);
        }
    }

    public void RegisterQuery(string query)
    {
        var fragment = query.IndexOf('#', StringComparison.Ordinal);
        var queryPart = fragment >= 0 ? query[..fragment] : query;
        foreach (var (key, values) in QueryHelpers.ParseQuery(queryPart))
        {
            foreach (var value in values)
            {
                RegisterRole(key, value);
                if (value != null)
                {
                    Inspect(value);
                }
            }
        }

        if (fragment >= 0)
        {
            RegisterQuery("?" + query[(fragment + 1)..]);
        }
    }

    public void AddJwt(string jwt)
    {
        if (_seenJwts.Add(jwt))
        {
            _jwts.Add(jwt);
        }
    }

    /// <summary>Returns the JWTs collected since the last call, in the order first seen.</summary>
    public IReadOnlyList<string> DrainJwts()
    {
        var drained = _jwts.ToList();
        _jwts.Clear();
        return drained;
    }

    /// <summary>
    /// Opaque values carry a digit or mixed case and no whitespace. That keeps error codes such as
    /// <c>invalid_grant</c> readable even when they sit under a role-bearing field like <c>code</c>.
    /// </summary>
    internal static bool LooksOpaque(string value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (kind is "otp" or "user-code" or "recovery-code" or "password" or "totp-secret")
        {
            return true;
        }

        return value.Any(char.IsDigit) || (value.Any(char.IsUpper) && value.Any(char.IsLower));
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{4,}\.eyJ[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedJwt();
}
