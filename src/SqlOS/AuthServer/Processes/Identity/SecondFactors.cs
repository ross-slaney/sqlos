using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Checks a second factor against an account: a code of one of its confirmed authenticator apps,
/// or one of its unused recovery codes. The step the MFA challenge and the host's own factor check
/// (<see cref="VerifySecondFactor"/>) share.
/// </summary>
/// <remarks>
/// An authenticator code is accepted once per time step: a step at or before the last accepted one
/// is a replay. The accepted step, or the spent recovery code, is saved before the check returns,
/// so a factor can never be used twice, even by requests sent together (the authenticator's
/// accepted step and the recovery code's consumption are concurrency tokens). Checking needs the
/// authenticator channel (<see cref="SqlOSTotpMfaService"/>); a facade built without it fails the
/// check as 7.x did.
/// </remarks>
internal sealed class SecondFactors(ISqlOSAuthServerDbContext context, SqlOSTotpMfaService? authenticators)
{
    public static readonly IdentityRefusal CodeRequired = new("mfa_code_required", "MFA code is required.");
    public static readonly IdentityRefusal CodeReplayed = new("mfa_code_replayed", "MFA code has already been used.");
    public static readonly IdentityRefusal RecoveryCodeReplayed = new("recovery_code_replayed", "Recovery code has already been used.");

    /// <summary>Checks <paramref name="code"/> as a second factor of account <paramref name="userId"/> at <paramref name="now"/>.</summary>
    public async Task<SecondFactorCheck> VerifyAsync(string userId, string? code, DateTime now, CancellationToken cancellationToken)
    {
        var channel = authenticators ?? throw new InvalidOperationException("TOTP MFA service is not registered.");
        if (string.IsNullOrWhiteSpace(code))
        {
            return new SecondFactorCheck.Refused(CodeRequired);
        }

        var totp = await TryAuthenticatorCodeAsync(channel, userId, code, now, cancellationToken);
        if (totp != null)
        {
            return totp;
        }

        var recovery = await TryRecoveryCodeAsync(userId, code, now, cancellationToken);
        return recovery ?? new SecondFactorCheck.Refused(MfaChallenges.CodeInvalid);
    }

    private async Task<SecondFactorCheck?> TryAuthenticatorCodeAsync(
        SqlOSTotpMfaService channel,
        string userId,
        string code,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var user = await context.FindUserAsync(userId, SqlOSUserParts.Authenticators, cancellationToken);
        if (user == null)
        {
            return null;
        }

        foreach (var authenticator in user.ConfirmedTotpAuthenticators)
        {
            var secret = channel.Unprotect(authenticator.SecretProtected);
            if (!channel.TryMatchCode(secret, code, authenticator.PeriodSeconds, authenticator.Digits, now, out var matchedStep))
            {
                continue;
            }

            // A code for a time step already accepted is a replay.
            if (!user.AcceptTotpCode(authenticator.Id, matchedStep, now))
            {
                continue;
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return new SecondFactorCheck.Verified(user, SqlOSMfaFactorTypes.Totp);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new SecondFactorCheck.Refused(CodeReplayed);
            }
        }

        return null;
    }

    private async Task<SecondFactorCheck?> TryRecoveryCodeAsync(string userId, string code, DateTime now, CancellationToken cancellationToken)
    {
        var normalized = SqlOSTotpMfaService.NormalizeRecoveryCode(code);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var user = await context.FindUserAsync(userId, SqlOSUserParts.RecoveryCodes, cancellationToken);
        if (user == null || !user.UseRecoveryCode(normalized, now))
        {
            return null;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return new SecondFactorCheck.Verified(user, SqlOSMfaFactorTypes.RecoveryCode);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new SecondFactorCheck.Refused(RecoveryCodeReplayed);
        }
    }
}

/// <summary>What a second-factor check found.</summary>
internal abstract record SecondFactorCheck
{
    private SecondFactorCheck()
    {
    }

    /// <summary>The code proved <see cref="Factor"/> (<c>totp</c> or <c>recovery_code</c>) of the tracked <see cref="User"/>.</summary>
    public sealed record Verified(SqlOSUser User, string Factor) : SecondFactorCheck;

    public sealed record Refused(IdentityRefusal Refusal) : SecondFactorCheck;
}

/// <summary>
/// Checks a second factor of an account for trusted host code
/// (<see cref="SqlOSTotpMfaService.VerifySecondFactorCodeAsync"/>): a step-up the host runs itself,
/// outside any MFA challenge. The factor is spent as an MFA challenge spends it.
/// </summary>
internal sealed class VerifySecondFactor(SecondFactors secondFactors, TimeProvider clock)
{
    public Task<SecondFactorCheck> ExecuteAsync(VerifySecondFactorCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return secondFactors.VerifyAsync(command.UserId, command.Code, clock.GetUtcNow().UtcDateTime, cancellationToken);
    }
}

/// <summary>A second factor host code presents for an account.</summary>
internal sealed record VerifySecondFactorCommand(string UserId, string? Code);
