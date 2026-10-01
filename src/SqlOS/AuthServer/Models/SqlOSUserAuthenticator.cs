using SqlOS.AuthServer.Contracts;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Models;

/// <summary>
/// An authenticator app (TOTP) of a <see cref="SqlOSUser"/>: its secret, protected with the host's
/// data protection, the code parameters, and the last time step a code was accepted for.
/// </summary>
/// <remarks>
/// Part of the <see cref="SqlOSUser"/> aggregate. An authenticator is enrolled unconfirmed,
/// confirmed by its first code, then accepts each time step at most once (a replayed code is
/// refused); <see cref="LastAcceptedTimeStep"/> is a concurrency token, so two requests cannot both
/// accept codes for the account at once. It is revoked by its owner, by a newer enrollment while
/// still unconfirmed, or by a claim of the account's address (<see cref="Domain.Revocation"/>).
/// </remarks>
public sealed class SqlOSUserAuthenticator
{
    internal const string DefaultDisplayName = "Authenticator app";

    private SqlOSUserAuthenticator()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string UserId { get; private set; } = string.Empty;
    public string Type { get; private set; } = SqlOSMfaFactorTypes.Totp;
    public string DisplayName { get; private set; } = DefaultDisplayName;
    public string SecretProtected { get; private set; } = string.Empty;
    public int SecretVersion { get; private set; } = 1;
    public string Algorithm { get; private set; } = "SHA1";
    public int Digits { get; private set; } = 6;
    public int PeriodSeconds { get; private set; } = 30;
    public bool IsConfirmed { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public DateTime? LastUsedAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public string? RevocationReason { get; private set; }
    public long? LastAcceptedTimeStep { get; private set; }

    public SqlOSUser? User { get; private set; }

    internal Revocation Revocation => new(RevokedAt, RevocationReason);

    /// <summary>True until the authenticator is revoked.</summary>
    internal bool IsActive => !Revocation.IsRevoked;

    internal bool IsTotp => string.Equals(Type, SqlOSMfaFactorTypes.Totp, StringComparison.Ordinal);

    /// <summary>A new, unconfirmed authenticator app of <paramref name="userId"/>.</summary>
    internal static SqlOSUserAuthenticator EnrollTotp(
        string userId,
        string protectedSecret,
        string? displayName,
        TotpParameters parameters,
        DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedSecret);
        return new SqlOSUserAuthenticator
        {
            Id = SqlOSIds.New("mfa"),
            UserId = userId,
            Type = SqlOSMfaFactorTypes.Totp,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? DefaultDisplayName : displayName.Trim(),
            SecretProtected = protectedSecret,
            SecretVersion = 1,
            Algorithm = parameters.Algorithm,
            Digits = parameters.Digits,
            PeriodSeconds = parameters.PeriodSeconds,
            IsConfirmed = false,
            CreatedAt = now
        };
    }

    /// <summary>The first code, for <paramref name="acceptedTimeStep"/>, confirmed the authenticator.</summary>
    internal void Confirm(long acceptedTimeStep, DateTime now)
    {
        Revocation.EnsureNotRevoked();
        if (IsConfirmed)
        {
            throw SqlOSDomainException.Of(SqlOSDomainError.InvalidMemberState, "The authenticator is already confirmed.");
        }

        IsConfirmed = true;
        ConfirmedAt = now;
        LastUsedAt = now;
        LastAcceptedTimeStep = acceptedTimeStep;
    }

    /// <summary>
    /// Accepts a code for <paramref name="timeStep"/>, unless a code for that step or a later one was
    /// already accepted: a TOTP code is good once.
    /// </summary>
    internal bool AcceptCode(long timeStep, DateTime now)
    {
        Revocation.EnsureNotRevoked();
        if (!IsConfirmed)
        {
            throw SqlOSDomainException.Of(SqlOSDomainError.InvalidMemberState, "An unconfirmed authenticator accepts no codes.");
        }

        if (LastAcceptedTimeStep is { } lastAccepted && timeStep <= lastAccepted)
        {
            return false;
        }

        LastAcceptedTimeStep = timeStep;
        LastUsedAt = now;
        return true;
    }

    internal void Revoke(string reason, DateTime now) => (RevokedAt, RevocationReason) = Revocation.Revoke(reason, now);
}

/// <summary>The code parameters of an authenticator app: HMAC algorithm, digits and period.</summary>
internal readonly record struct TotpParameters(string Algorithm, int Digits, int PeriodSeconds);
