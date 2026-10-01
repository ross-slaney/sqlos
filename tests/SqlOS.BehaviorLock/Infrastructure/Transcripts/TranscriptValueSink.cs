using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace SqlOS.BehaviorLock.Infrastructure.Transcripts;

/// <summary>
/// What renderers report values to while they walk a response: role-bearing values are
/// registered with the <see cref="Scrubber"/>, JWTs are collected so the transcript can show
/// their decoded header and claims, and the lifetimes of claims a host echoes are collected so
/// the transcript can show them next to the body.
/// </summary>
public sealed partial class TranscriptValueSink
{
    private readonly Scrubber _scrubber;
    private readonly bool _recording;
    private readonly List<string> _jwts = [];
    private readonly HashSet<string> _seenJwts = new(StringComparer.Ordinal);
    private readonly List<string> _claimLifetimes = [];

    public TranscriptValueSink(Scrubber scrubber)
        : this(scrubber, recording: true)
    {
    }

    private TranscriptValueSink(Scrubber scrubber, bool recording)
    {
        _scrubber = scrubber;
        _recording = recording;
    }

    public Scrubber Scrubber => _scrubber;

    /// <summary>
    /// A sink that registers and collects nothing, for rendering content only to compare it
    /// (see <see cref="Scrubber.Mask"/>) without changing what the transcript registers.
    /// </summary>
    public static TranscriptValueSink Detached(Scrubber scrubber) => new(scrubber, recording: false);

    /// <summary>Registers <paramref name="value"/> when it looks like an opaque secret rather than a word or code name.</summary>
    public void Register(string? value, string kind)
    {
        if (_recording && value != null && LooksOpaque(value, kind))
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

    /// <summary>Queues a JWT for decoding once per identity (see <see cref="JwtRendering.Identity"/>).</summary>
    public void AddJwt(string jwt)
    {
        if (_recording && _seenJwts.Add(JwtRendering.Identity(jwt)))
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
    /// Records the relative lifetimes (<c>exp-iat=10m</c>) of a claims list a host echoed, whose
    /// epoch values render as <c>{epoch}</c> (see <see cref="CanonicalJson"/>).
    /// </summary>
    public void AddClaimLifetimes(string lifetimes)
    {
        if (_recording && lifetimes.Length > 0)
        {
            _claimLifetimes.Add(lifetimes);
        }
    }

    /// <summary>Returns the echoed-claim lifetimes collected since the last call, in body order.</summary>
    public IReadOnlyList<string> DrainClaimLifetimes()
    {
        var drained = _claimLifetimes.ToList();
        _claimLifetimes.Clear();
        return drained;
    }

    /// <summary>
    /// Opaque values carry a digit or mixed case and no whitespace. That keeps error codes such as
    /// <c>invalid_grant</c> readable even when they sit under a role-bearing field like <c>code</c>.
    /// A redaction marker such as SqlOS's <c>[redacted]</c> is never a secret, even under a field
    /// named <c>password</c>: registering it would scrub every redaction in the transcript into a
    /// password placeholder.
    /// </summary>
    internal static bool LooksOpaque(string value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || RedactionMarker().IsMatch(value))
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

    /// <summary>A bracketed word that stands in for a removed value: <c>[redacted]</c>, <c>[hidden]</c>.</summary>
    [GeneratedRegex(@"^\[[A-Za-z][A-Za-z _\-]*\]$", RegexOptions.CultureInvariant)]
    private static partial Regex RedactionMarker();
}
