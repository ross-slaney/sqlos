using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Policies;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>What a sign-up did.</summary>
internal abstract record SignUpOutcome
{
    private SignUpOutcome()
    {
    }

    /// <summary>
    /// The account was created and the hub completed its first login. <see cref="Email"/> is the
    /// address the account signed up with, when it has one.
    /// </summary>
    public sealed record SignedUp(LoginEvidence Evidence, LoginCompletion Completion, string? Email = null) : SignUpOutcome;

    /// <summary>Nothing was created.</summary>
    public sealed record Refused(IdentityRefusal Refusal) : SignUpOutcome;
}

/// <summary>When a sign-up records itself: spends its sign-up token and writes its audit row.</summary>
internal enum SignupRecordTiming
{
    /// <summary>Before the hub completes the login (the public API's password, email-code and invitation sign-ups).</summary>
    BeforeCompletion = 1,

    /// <summary>After the hub completed it (every other sign-up).</summary>
    AfterCompletion = 2
}

/// <summary>
/// What each surface does around a sign-up's completion, as 7.2.1 did it. The sign-up processes are
/// one implementation per flow; these are the only places the surfaces still differ, and every
/// difference is visible in this one table.
/// </summary>
/// <param name="Audited">The surface audits the sign-up (<c>user.signup*</c>). #415 audits every surface in layer 5.</param>
/// <param name="RecordTiming">When the sign-up spends its token and writes its audit row.</param>
/// <param name="RunsHostHook">The surface calls the host's sign-up hook (<c>OnHeadlessSignupAsync</c>).</param>
/// <param name="BindsOrganizationToRequest">
/// The hosted AuthPage gives its authorization request the sign-up's organization (the invitation's,
/// or the one the sign-up created) when the request names none.
/// </param>
/// <param name="InvitationSignupNeedsEmailCodes">An invitation sign-up without a password requires email codes to be enabled.</param>
internal sealed record SignupConventions(
    bool Audited,
    SignupRecordTiming RecordTiming,
    bool RunsHostHook,
    bool BindsOrganizationToRequest,
    bool InvitationSignupNeedsEmailCodes)
{
    /// <summary>The conventions of <paramref name="surface"/> for a sign-up with <paramref name="method"/>.</summary>
    public static SignupConventions For(SqlOSRequestSurface surface, string method) => (surface, method) switch
    {
        (SqlOSRequestSurface.Hosted, AuthenticationMethods.PhoneOtp) => new(true, SignupRecordTiming.AfterCompletion, false, true, false),
        (SqlOSRequestSurface.Hosted, _) => new(false, SignupRecordTiming.AfterCompletion, false, true, true),
        (SqlOSRequestSurface.Headless, AuthenticationMethods.Password) => new(false, SignupRecordTiming.AfterCompletion, true, false, true),
        (SqlOSRequestSurface.Headless, _) => new(true, SignupRecordTiming.AfterCompletion, true, false, true),
        (SqlOSRequestSurface.PublicApi, AuthenticationMethods.Password) => new(true, SignupRecordTiming.BeforeCompletion, false, false, false),
        (SqlOSRequestSurface.PublicApi, AuthenticationMethods.PhoneOtp) => new(true, SignupRecordTiming.AfterCompletion, false, false, false),
        (SqlOSRequestSurface.PublicApi, _) => new(true, SignupRecordTiming.BeforeCompletion, true, false, false),
        _ => throw new InvalidOperationException($"A {method} sign-up does not run on the {surface} surface.")
    };
}

/// <summary>
/// The unit of work of a sign-up: the account, the organization it creates, its first login and
/// its record commit together or not at all.
/// </summary>
/// <remarks>
/// <para>
/// A sign-up runs in one database transaction, as in 7.2.1. Inside it the account is saved before
/// the hub completes the login (the 7.2.1 completion reads the account back from the database), and
/// the hub saves its own work; the transaction is what makes the whole sign-up one unit. A refused
/// sign-up, or one that throws, rolls back.
/// </para>
/// <para>
/// The public API's password sign-up runs under the provider's execution strategy, which may retry
/// it (7.2.1); every other sign-up runs once in a plain transaction. A store without transactions
/// (the EF Core in-memory provider unit tests use) cannot roll back, so a failed sign-up deletes the
/// account and the organization it created instead.
/// </para>
/// </remarks>
internal sealed class SignupUnitOfWork(ISqlOSAuthServerDbContext context)
{
    private readonly List<string> _createdOrganizationIds = [];

    /// <summary>The account the sign-up registered, once it has.</summary>
    public SqlOSUser? Account { get; private set; }

    public async Task<SignUpOutcome> RunAsync(Func<Task<SignUpOutcome>> work, bool retriable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!SqlOSSignupOrchestration.SupportsDatabaseTransactions(context))
        {
            return await RunWithoutTransactionAsync(work, cancellationToken);
        }

        if (!retriable || context.Database.CurrentTransaction != null)
        {
            return await RunInTransactionAsync(work, cancellationToken);
        }

        var strategy = context.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0 && context is DbContext retryContext)
            {
                retryContext.ChangeTracker.Clear();
                Account = null;
                _createdOrganizationIds.Clear();
            }

            return await RunInTransactionAsync(work, cancellationToken);
        });
    }

    /// <summary>Records the account the sign-up registered and the organization it created, for a store that cannot roll back.</summary>
    public void Registered(SqlOSUser account, string? createdOrganizationId)
    {
        Account = account;
        if (createdOrganizationId != null)
        {
            _createdOrganizationIds.Add(createdOrganizationId);
        }
    }

    private async Task<SignUpOutcome> RunInTransactionAsync(Func<Task<SignUpOutcome>> work, CancellationToken cancellationToken)
    {
        // A sign-up that runs inside a transaction its caller owns commits or rolls back with it.
        var transaction = context.Database.CurrentTransaction == null
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var outcome = await work();
            if (transaction != null)
            {
                if (outcome is SignUpOutcome.SignedUp)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                else
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
            }

            return outcome;
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<SignUpOutcome> RunWithoutTransactionAsync(Func<Task<SignUpOutcome>> work, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await work();
            if (outcome is not SignUpOutcome.SignedUp)
            {
                await DeleteCreatedAsync(cancellationToken);
            }

            return outcome;
        }
        catch
        {
            await DeleteCreatedAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// What 7.2.1 did on a store without transactions: deletes the account (and its phone numbers)
    /// and the organization the sign-up created.
    /// </summary>
    private async Task DeleteCreatedAsync(CancellationToken cancellationToken)
    {
        if (Account == null)
        {
            return;
        }

        if (_createdOrganizationIds.Count > 0)
        {
            var organizationIds = _createdOrganizationIds.ToArray();
            var organizations = await context.Set<SqlOSOrganization>()
                .Where(x => organizationIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
            context.Set<SqlOSOrganization>().RemoveRange(organizations);
        }

        var accountId = Account.Id;
        var user = await context.Set<SqlOSUser>().FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (user != null)
        {
            var phoneNumbers = await context.Set<SqlOSUserPhoneNumber>()
                .Where(x => x.UserId == user.Id)
                .ToListAsync(cancellationToken);
            context.Set<SqlOSUserPhoneNumber>().RemoveRange(phoneNumbers);
            context.Set<SqlOSUser>().Remove(user);
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Registers an account: the step every sign-up and the operator's user creation share. The
/// <see cref="SqlOSUser"/> aggregate holds the rules (one primary address, verified only with a
/// proof, a password only under the <see cref="PasswordPolicy"/>); this step refuses an address an
/// account already owns and saves, so the account exists for what follows.
/// </summary>
internal static class AccountRegistration
{
    /// <summary>
    /// Registers an account for <paramref name="email"/>, verified when <paramref name="proof"/>
    /// proves it (an email-code or invitation sign-up), with <paramref name="password"/> when one is
    /// given. Refuses an address an account already owns as 7.x did, including the one a concurrent
    /// registration won (the unique index on the canonical address).
    /// </summary>
    public static async Task<SqlOSUser> RegisterAsync(
        ISqlOSAuthServerDbContext context,
        string displayName,
        string email,
        string? password,
        OwnershipProof? proof,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!EmailAddress.TryParse(email, out var address))
        {
            throw new InvalidOperationException(SqlOSEmailAddress.InvalidEmailMessage);
        }

        var existingEmail = await context.Set<SqlOSUserEmail>()
            .FindByEmailAddressAsync(address, email, cancellationToken);
        if (existingEmail != null)
        {
            throw new InvalidOperationException($"Email '{email}' already exists.");
        }

        if (proof is not null && !proof.Address.Equals(address))
        {
            throw SqlOSDomainException.Of(SqlOSDomainError.OwnershipProofMismatch);
        }

        var user = proof is null
            ? SqlOSUser.Register(displayName, address, now)
            : SqlOSUser.Register(displayName, proof, now);
        if (!string.IsNullOrWhiteSpace(password))
        {
            user.SetPassword(password, PasswordPolicy.Default, now);
        }

        context.Set<SqlOSUser>().Add(user);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqlOSSignupOrchestration.IsUniqueConstraintViolation(ex))
        {
            throw new InvalidOperationException($"Email '{email}' already exists.", ex);
        }

        return user;
    }

    /// <summary>
    /// The password sign-up's checks, before anything is written: password sign-up is enabled, the
    /// input is within bounds (trimmed; the password under the <see cref="PasswordPolicy"/>), and
    /// it joins no existing organization without an invitation.
    /// </summary>
    public static async Task<PasswordSignupCheck> CheckPasswordSignupAsync(
        SqlOSSettingsService settings,
        string? displayName,
        string? email,
        string? password,
        string? organizationName,
        string? joinOrganizationId,
        CancellationToken cancellationToken)
    {
        if (!(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).PasswordSignupEnabled)
        {
            return new PasswordSignupCheck.Refused(IdentityRefusals.PasswordSignupDisabled);
        }

        if (Bounded(displayName, "display_name", "Display name is required.", SqlOSSignupOrchestration.DisplayNameMaxLength, SqlOSSignupOrchestration.DisplayNameTooLongMessage) is IdentityRefusal displayNameRefusal)
        {
            return new PasswordSignupCheck.Refused(displayNameRefusal);
        }

        if (Bounded(email, "email", "Email address is required.", SqlOSSignupOrchestration.EmailMaxLength, SqlOSSignupOrchestration.EmailTooLongMessage) is IdentityRefusal emailRefusal)
        {
            return new PasswordSignupCheck.Refused(emailRefusal);
        }

        if (PasswordPolicy.Default.Check(password) is { } passwordRefusal)
        {
            return new PasswordSignupCheck.Refused(new IdentityRefusal($"password_{passwordRefusal.Rule}", passwordRefusal.Message));
        }

        string? trimmedOrganizationName = null;
        if (!string.IsNullOrWhiteSpace(organizationName))
        {
            trimmedOrganizationName = organizationName.Trim();
            if (trimmedOrganizationName.Length > SqlOSSignupOrchestration.OrganizationNameMaxLength)
            {
                return new PasswordSignupCheck.Refused(new IdentityRefusal("organization_name_too_long", SqlOSSignupOrchestration.OrganizationNameTooLongMessage));
            }
        }

        if (SignupRefusals.ForOrganizationJoin(joinOrganizationId) is { } joinRefusal)
        {
            return new PasswordSignupCheck.Refused(joinRefusal);
        }

        return new PasswordSignupCheck.Passed(new PasswordSignupInput(displayName!.Trim(), email!.Trim(), password ?? string.Empty, trimmedOrganizationName));
    }

    /// <summary>Registers the account a password sign-up checked, and the organization it names.</summary>
    public static async Task<SignupRegistration> RegisterWithPasswordAsync(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService admin,
        PasswordSignupInput input,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var user = await RegisterAsync(context, input.DisplayName, input.Email, input.Password, proof: null, now, cancellationToken);
        var (organizations, createdOrganizationId) = await JoinOwnOrganizationAsync(admin, user, input.OrganizationName, cancellationToken);
        return new SignupRegistration(user, organizations, createdOrganizationId);
    }

    /// <summary>
    /// Registers the account an email-code sign-up proved the address of, verified by
    /// <paramref name="proof"/>, and the organization it names. Email codes must be enabled, and
    /// the sign-up joins no existing organization without an invitation.
    /// </summary>
    public static async Task<SignupRegistrationOutcome> RegisterWithEmailOtpAsync(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService admin,
        SqlOSSettingsService settings,
        string displayName,
        string email,
        OwnershipProof? proof,
        string? organizationName,
        string? joinOrganizationId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).EmailOtpEnabled)
        {
            return new SignupRegistrationOutcome.Refused(IdentityRefusals.EmailCodesUnavailable);
        }

        if (SignupRefusals.ForOrganizationJoin(joinOrganizationId) is { } joinRefusal)
        {
            return new SignupRegistrationOutcome.Refused(joinRefusal);
        }

        var user = await RegisterAsync(context, displayName, email, password: null, proof, now, cancellationToken);
        var (organizations, createdOrganizationId) = await JoinOwnOrganizationAsync(admin, user, organizationName, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return new SignupRegistrationOutcome.Registered(new SignupRegistration(user, organizations, createdOrganizationId));
    }

    /// <summary>
    /// Registers the account a phone-code sign-up proved the number of, with that number verified,
    /// and the organization it names. Phone codes must be enabled, the number may belong to no
    /// account, and the sign-up joins no existing organization.
    /// </summary>
    public static async Task<SignupRegistrationOutcome> RegisterWithPhoneOtpAsync(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService admin,
        SqlOSSettingsService settings,
        SqlOSCryptoService crypto,
        string displayName,
        string e164PhoneNumber,
        string? organizationName,
        string? joinOrganizationId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).PhoneOtpEnabled)
        {
            return new SignupRegistrationOutcome.Refused(IdentityRefusals.PhoneCodesUnavailable);
        }

        if (SignupRefusals.ForOrganizationJoin(joinOrganizationId) is { } joinRefusal)
        {
            return new SignupRegistrationOutcome.Refused(joinRefusal);
        }

        var phoneHash = HashedSecret.Sha256(e164PhoneNumber).Hash;
        if (await context.Set<SqlOSUserPhoneNumber>().AsNoTracking().AnyAsync(x => x.PhoneNumberHash == phoneHash && x.RemovedAt == null, cancellationToken))
        {
            return new SignupRegistrationOutcome.Refused(IdentityRefusals.PhoneNumberTaken);
        }

        var user = SqlOSUser.Register(displayName, now);
        user.AddVerifiedPhone(e164PhoneNumber, crypto.ProtectSecret(e164PhoneNumber), now);
        context.Set<SqlOSUser>().Add(user);
        var (organizations, createdOrganizationId) = await JoinOwnOrganizationAsync(admin, user, organizationName, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return new SignupRegistrationOutcome.Registered(new SignupRegistration(user, organizations, createdOrganizationId));
    }

    /// <summary>
    /// Registers the account an invitation sign-up creates, with the invited address verified by
    /// <paramref name="proof"/>. The invitation's membership is accepted by whoever completes the
    /// sign-up (layer 4 owns invitations).
    /// </summary>
    public static async Task<SignupRegistration> RegisterWithInvitationAsync(
        ISqlOSAuthServerDbContext context,
        string displayName,
        string email,
        OwnershipProof? proof,
        DateTime now,
        CancellationToken cancellationToken)
        => new(
            await RegisterAsync(context, displayName, email, password: null, proof, now, cancellationToken),
            Array.Empty<SqlOSOrganizationOption>(),
            CreatedOrganizationId: null);

    private static IdentityRefusal? Bounded(string? value, string field, string requiredMessage, int maxLength, string tooLongMessage)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new IdentityRefusal($"{field}_required", requiredMessage);
        }

        return trimmed.Length > maxLength ? new IdentityRefusal($"{field}_too_long", tooLongMessage) : null;
    }

    /// <summary>
    /// Creates the organization a sign-up names, with the new account as its owner (organizations
    /// are layer 4's; the operator's service still writes them). Returns the account's
    /// organizations and the one created, if any.
    /// </summary>
    public static async Task<(IReadOnlyList<SqlOSOrganizationOption> Organizations, string? CreatedOrganizationId)> JoinOwnOrganizationAsync(
        SqlOSAdminService admin,
        SqlOSUser user,
        string? organizationName,
        CancellationToken cancellationToken)
    {
        string? createdOrganizationId = null;
        if (!string.IsNullOrWhiteSpace(organizationName))
        {
            var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(organizationName, null), cancellationToken);
            await admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(user.Id, "owner"), cancellationToken);
            createdOrganizationId = organization.Id;
        }

        return (await admin.GetUserOrganizationsAsync(user.Id, cancellationToken), createdOrganizationId);
    }
}

/// <summary>A password sign-up's input, trimmed and checked.</summary>
internal sealed record PasswordSignupInput(string DisplayName, string Email, string Password, string? OrganizationName);

/// <summary>What a password sign-up's checks found.</summary>
internal abstract record PasswordSignupCheck
{
    private PasswordSignupCheck()
    {
    }

    public sealed record Passed(PasswordSignupInput Input) : PasswordSignupCheck;

    public sealed record Refused(IdentityRefusal Refusal) : PasswordSignupCheck;
}

/// <summary>A registered account, its organizations, and the organization the sign-up created.</summary>
internal sealed record SignupRegistration(
    SqlOSUser User,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    string? CreatedOrganizationId);

/// <summary>What registering a sign-up's account did.</summary>
internal abstract record SignupRegistrationOutcome
{
    private SignupRegistrationOutcome()
    {
    }

    public sealed record Registered(SignupRegistration Registration) : SignupRegistrationOutcome;

    public sealed record Refused(IdentityRefusal Refusal) : SignupRegistrationOutcome;
}

/// <summary>
/// The parts of a sign-up every flow shares once the account exists: the host's hook, the record
/// (the spent sign-up token and the <c>user.signup*</c> row), and the first login, in the order
/// the surface's <see cref="SignupConventions"/> give them.
/// </summary>
internal sealed class SignupCompletion(
    ISqlOSAuthServerDbContext context,
    IHostSignupHook hook,
    ILoginCompletion completion,
    SignupConventions conventions)
{
    /// <summary>Calls the host's sign-up hook, when the surface calls it and the host configured one.</summary>
    public async Task RunHostHookAsync(
        SqlOSUser user,
        SqlOSAuthorizationRequest? authorizationRequest,
        string? organizationId,
        System.Text.Json.Nodes.JsonObject? customFields,
        CancellationToken cancellationToken)
    {
        if (!conventions.RunsHostHook || !hook.IsConfigured)
        {
            return;
        }

        var organization = string.IsNullOrWhiteSpace(organizationId)
            ? null
            : await context.Set<SqlOSOrganization>().FirstOrDefaultAsync(x => x.Id == organizationId, cancellationToken);
        await hook.RunAsync(user, authorizationRequest, organization, customFields ?? new System.Text.Json.Nodes.JsonObject(), cancellationToken);
    }

    /// <summary>
    /// Records the sign-up and completes its first login at <paramref name="destination"/>, the record
    /// before or after the completion as the surface does it. A sign-up token another request spent
    /// first refuses the sign-up.
    /// </summary>
    public async Task<SignUpOutcome> CompleteAsync(
        LoginEvidence evidence,
        LoginDestination destination,
        SignupRecord record,
        CancellationToken cancellationToken)
    {
        if (conventions.RecordTiming == SignupRecordTiming.BeforeCompletion && !await RecordAsync(evidence.User, record, cancellationToken))
        {
            return new SignUpOutcome.Refused(IdentityRefusals.InvalidCode);
        }

        if (conventions.BindsOrganizationToRequest && destination is LoginDestination.AuthorizationRequest request)
        {
            request.Request.OrganizationId ??= record.OrganizationId;
        }

        var completed = await completion.CompleteAsync(evidence, destination, cancellationToken);
        if (conventions.RecordTiming == SignupRecordTiming.AfterCompletion && !await RecordAsync(evidence.User, record, cancellationToken))
        {
            return new SignUpOutcome.Refused(IdentityRefusals.InvalidCode);
        }

        return new SignUpOutcome.SignedUp(evidence, completed);
    }

    /// <summary>
    /// Spends the sign-up token, then writes the audit row when the surface audits the sign-up: two
    /// saves, so a token another request spent first stops the sign-up before anything is audited.
    /// </summary>
    /// <returns>False when another request spent the sign-up token first.</returns>
    private async Task<bool> RecordAsync(SqlOSUser user, SignupRecord record, CancellationToken cancellationToken)
    {
        if (record.SignupToken is { } signupToken)
        {
            signupToken.Consume(record.SignupTokenKind!, record.Now);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                // Detaching the token discards its change and its events, so a later save of the
                // same unit of work writes neither.
                foreach (var entry in exception.Entries)
                {
                    entry.State = EntityState.Detached;
                }

                return false;
            }
        }

        if (conventions.Audited)
        {
            user.RecordSignUp(record.Method, record.OrganizationId, record.IpAddress);
            await context.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}

/// <summary>
/// How a sign-up records itself: its method and organization for the audit row, the address the
/// request came from, and the sign-up token it spends (none for a password or invitation sign-up).
/// </summary>
internal sealed record SignupRecord(
    string Method,
    string? OrganizationId,
    string? IpAddress,
    DateTime Now,
    SqlOSTemporaryToken? SignupToken = null,
    TemporaryTokenKind? SignupTokenKind = null);
