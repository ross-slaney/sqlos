using System.Text.Json;

namespace SqlOS.Domain;

/// <summary>
/// A purpose a temporary token serves, and the rules every token of that purpose follows: the
/// purpose string stored in <c>SqlOSTemporaryToken.Purpose</c>, how long a token lives, which
/// binding columns the purpose uses, and whether using a token spends it
/// (<c>docs/architecture/domain-model.md</c> §15, Amendment 1).
/// </summary>
/// <remarks>
/// A token is issued, read and consumed only through its kind, so a token minted for one purpose
/// can never be presented for another, and a purpose's lifetime and bindings are decided in one
/// place (<c>SqlOSTemporaryTokenKinds</c>). Kinds are composition over the one
/// <c>SqlOSTemporaryTokens</c> table: a row of a purpose SqlOS does not know (a host's own
/// purpose) still loads.
/// </remarks>
internal abstract class TemporaryTokenKind
{
    private protected TemporaryTokenKind(
        string purpose,
        TemporaryTokenLifetime lifetime,
        TemporaryTokenBindings bindings,
        bool isSingleUse,
        Func<string, TemporaryTokenBinding, ISqlOSDomainEvent>? issued)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (purpose.Length > MaxPurposeLength)
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, $"A temporary token purpose has at most {MaxPurposeLength} characters.");
        }

        Purpose = purpose;
        Lifetime = lifetime;
        Bindings = bindings;
        IsSingleUse = isSingleUse;
        _issued = issued;
    }

    private readonly Func<string, TemporaryTokenBinding, ISqlOSDomainEvent>? _issued;

    /// <summary>The length of the <c>Purpose</c> column.</summary>
    public const int MaxPurposeLength = 80;

    /// <summary>The purpose stored with every token of this kind.</summary>
    public string Purpose { get; }

    public TemporaryTokenLifetime Lifetime { get; }

    /// <summary>The binding columns a token of this kind may carry.</summary>
    public TemporaryTokenBindings Bindings { get; }

    /// <summary>
    /// True when using a token for its purpose spends it (a link, a code, a pending step). False
    /// for tokens presented again and again until they are retired or expire (an issuer session
    /// cookie) and for records that are never presented.
    /// </summary>
    public bool IsSingleUse { get; }

    /// <summary>The CLR type of the payload stored as JSON.</summary>
    public abstract Type PayloadType { get; }

    /// <summary>
    /// The domain event issuing a token of this kind raises besides the generic issue event, for a
    /// purpose whose issuing is audited, or null.
    /// </summary>
    public ISqlOSDomainEvent? IssuedEvent(string tokenId, TemporaryTokenBinding binding)
        => _issued?.Invoke(tokenId, binding);

    /// <summary>
    /// Fails unless <paramref name="binding"/> only uses columns this kind binds: a token can never
    /// carry a binding its purpose does not define.
    /// </summary>
    public void EnsureAllows(TemporaryTokenBinding binding)
    {
        Require(binding.UserId, TemporaryTokenBindings.User);
        Require(binding.ClientApplicationId, TemporaryTokenBindings.ClientApplication);
        Require(binding.OrganizationId, TemporaryTokenBindings.Organization);
        Require(binding.IssuerSessionFamilyId, TemporaryTokenBindings.IssuerSession);

        void Require(string? value, TemporaryTokenBindings column)
        {
            if (value is not null && !Bindings.HasFlag(column))
            {
                throw new ArgumentException($"Temporary tokens of purpose '{Purpose}' are not bound to a {column} value.", nameof(binding));
            }
        }
    }

    public override string ToString() => Purpose;
}

/// <summary>A <see cref="TemporaryTokenKind"/> whose tokens carry a <typeparamref name="TPayload"/>.</summary>
/// <remarks>
/// The payload is stored exactly as SqlOS 7.x stored it: <see cref="JsonSerializer"/> with its
/// default options, so member names are the declared names in declaration order. Changing a
/// payload type changes stored tokens; it needs the same care as a column change, and a member
/// added later needs a default so tokens minted before it still read.
/// </remarks>
internal sealed class TemporaryTokenKind<TPayload> : TemporaryTokenKind
    where TPayload : class
{
    public TemporaryTokenKind(
        string purpose,
        TemporaryTokenLifetime lifetime,
        TemporaryTokenBindings bindings,
        bool isSingleUse = true,
        Func<string, TemporaryTokenBinding, ISqlOSDomainEvent>? issued = null)
        : base(purpose, lifetime, bindings, isSingleUse, issued)
    {
    }

    public override Type PayloadType => typeof(TPayload);

    /// <summary>The payload JSON stored for <paramref name="payload"/>, or null for no payload.</summary>
    public string? Serialize(TPayload? payload)
        => payload is null ? null : JsonSerializer.Serialize(payload, payload.GetType());

    /// <summary>The payload stored as <paramref name="payloadJson"/>, or null when there is none.</summary>
    public TPayload? Deserialize(string? payloadJson)
        => string.IsNullOrWhiteSpace(payloadJson) ? null : JsonSerializer.Deserialize<TPayload>(payloadJson);
}

/// <summary>How long a token of a kind lives.</summary>
/// <remarks>
/// A fixed lifetime is a SqlOS constant and belongs to the kind; a configured lifetime is a host
/// setting (an option or a dashboard-owned security setting) that the issuing code reads and
/// passes when it issues the token. A kind accepts exactly one of the two, so a constant cannot
/// be overridden at one call site and a setting cannot be forgotten at another. The default value
/// is configured by an unnamed setting.
/// </remarks>
internal readonly record struct TemporaryTokenLifetime
{
    private TemporaryTokenLifetime(TimeSpan? fixedLifetime, string? setting)
    {
        FixedLifetime = fixedLifetime;
        Setting = setting;
    }

    /// <summary>The lifetime of every token, or null when it is configured.</summary>
    public TimeSpan? FixedLifetime { get; }

    /// <summary>The setting the issuing code reads the lifetime from, or null when it is fixed.</summary>
    public string? Setting { get; }

    public bool IsConfigured => FixedLifetime is null;

    public static TemporaryTokenLifetime Fixed(TimeSpan lifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        return new TemporaryTokenLifetime(lifetime, null);
    }

    /// <summary>A lifetime the issuing code passes from <paramref name="setting"/>.</summary>
    public static TemporaryTokenLifetime Configured(string setting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        return new TemporaryTokenLifetime(null, setting);
    }

    /// <summary>
    /// The lifetime of a token issued with <paramref name="configured"/>: the fixed lifetime, which
    /// takes no value, or the configured value, which is required. A configured value is used as
    /// the host set it, as in 7.x.
    /// </summary>
    public TimeSpan Resolve(TimeSpan? configured)
    {
        if (FixedLifetime is { } fixedLifetime)
        {
            return configured is null
                ? fixedLifetime
                : throw new ArgumentException("This kind's lifetime is fixed; do not pass one.", nameof(configured));
        }

        return configured
            ?? throw new ArgumentException($"This kind's lifetime is configured by {Setting}; pass its value.", nameof(configured));
    }

    public override string ToString()
        => FixedLifetime?.ToString("c", System.Globalization.CultureInfo.InvariantCulture) ?? Setting ?? "configured";
}

/// <summary>The binding columns of a temporary token.</summary>
[Flags]
internal enum TemporaryTokenBindings
{
    None = 0,

    /// <summary><c>UserId</c>: the account the token acts for.</summary>
    User = 1,

    /// <summary><c>ClientApplicationId</c>: the client the flow started from.</summary>
    ClientApplication = 2,

    /// <summary><c>OrganizationId</c>: the organization the flow is scoped to.</summary>
    Organization = 4,

    /// <summary><c>IssuerSessionFamilyId</c>: the issuer-session family a session cookie belongs to.</summary>
    IssuerSession = 8,

    All = User | ClientApplication | Organization | IssuerSession
}

/// <summary>The binding values of one token. A null value leaves the column empty.</summary>
internal readonly record struct TemporaryTokenBinding(
    string? UserId = null,
    string? ClientApplicationId = null,
    string? OrganizationId = null,
    string? IssuerSessionFamilyId = null)
{
    public static TemporaryTokenBinding None => default;
}
