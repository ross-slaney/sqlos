using SqlOS.Domain;

namespace SqlOS.AuthServer.Policies;

/// <summary>
/// The rules a password must meet, decided in one place and applied at the one choke point every
/// password passes through: <c>SqlOSUser.SetPassword</c>, which password sign-up on every surface,
/// operator-created users and password resets on every surface all call
/// (<c>docs/architecture/domain-model.md</c> §3.6).
/// </summary>
/// <remarks>
/// <para>
/// SqlOS 8.0 keeps the 7.2.1 rule, a password must not be blank, and now applies it on every path:
/// 7.2.1 checked it only at sign-up, so a reset could set an empty password (BL-0006). A new
/// password rule is one more <see cref="IPasswordRule"/> (#417 adds length, blocklist and breach
/// checks), never a branch in a process.
/// </para>
/// <para>
/// A process asks the policy before it does anything it cannot take back (a reset checks the new
/// password before it spends the link) and maps the refusal to its public error; the aggregate
/// enforces it again, so no path can store a password the policy refuses.
/// </para>
/// </remarks>
internal sealed class PasswordPolicy
{
    private readonly IReadOnlyList<IPasswordRule> _rules;

    public PasswordPolicy(IEnumerable<IPasswordRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToArray();
        if (_rules.Any(static rule => rule is null))
        {
            throw new ArgumentException("A password rule cannot be null.", nameof(rules));
        }
    }

    /// <summary>The 8.0 policy: 7.2.1's rule, applied on every path.</summary>
    public static PasswordPolicy Default { get; } = new([new PasswordIsNotBlank()]);

    /// <summary>The first rule <paramref name="password"/> breaks, or <see langword="null"/> when it meets every rule.</summary>
    public PasswordRefusal? Check(string? password)
    {
        foreach (var rule in _rules)
        {
            if (rule.Check(password) is { } refusal)
            {
                return refusal;
            }
        }

        return null;
    }

    /// <summary>Fails with the refusal's message when <paramref name="password"/> breaks a rule.</summary>
    public void Enforce(string? password)
    {
        if (Check(password) is { } refusal)
        {
            throw SqlOSDomainException.Of(SqlOSDomainError.PasswordRejected, refusal.Message);
        }
    }
}

/// <summary>One rule of the <see cref="PasswordPolicy"/>.</summary>
internal interface IPasswordRule
{
    /// <summary>The refusal when <paramref name="password"/> breaks this rule, or <see langword="null"/>.</summary>
    PasswordRefusal? Check(string? password);
}

/// <summary>Why a password was refused: the rule's stable name and the public message SqlOS shows.</summary>
internal sealed record PasswordRefusal(string Rule, string Message);

/// <summary>A password must not be blank: 7.2.1's sign-up rule, with its message.</summary>
internal sealed class PasswordIsNotBlank : IPasswordRule
{
    public const string RuleName = "required";
    public const string Message = "Password is required.";

    private static readonly PasswordRefusal Refusal = new(RuleName, Message);

    public PasswordRefusal? Check(string? password) => string.IsNullOrWhiteSpace(password) ? Refusal : null;
}
