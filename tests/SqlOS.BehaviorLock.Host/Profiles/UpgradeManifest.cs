namespace SqlOS.BehaviorLock.Host.Profiles;

/// <summary>
/// What the upgrade seed wrote with the released package, handed to the upgrade gate scenario:
/// the per-run values (IDs, secrets, tokens) it needs to use the existing data. Names, emails,
/// and the domain are fixed (<see cref="UpgradeData"/>), because every run seeds a fresh database.
/// </summary>
/// <param name="SeededWith">The informational version of the SqlOS assembly that wrote the data.</param>
/// <param name="Alice">Password and TOTP; organization admin; <c>workspace_admin</c> on the root workspace.</param>
/// <param name="Bob">Provisioned by SCIM into the mapped group; no password.</param>
/// <param name="Carol">Password only (never signed in); organization member; direct reader grant.</param>
/// <param name="SessionCookie">Alice's hosted sign-in session (<c>sqlos_auth_page</c>).</param>
/// <param name="RefreshTokens">One live refresh token per client kind, in seeding order.</param>
public sealed record UpgradeManifest(
    string SeededWith,
    UpgradeUser Alice,
    UpgradeUser Bob,
    UpgradeUser Carol,
    string OrganizationId,
    string SamlConnectionId,
    string ScimConnectionId,
    string ScimToken,
    string ScimGroupId,
    string ScimMappingId,
    string SessionCookie,
    IReadOnlyList<UpgradeRefreshToken> RefreshTokens,
    string DynamicClientId,
    UpgradeDeviceAuthorization PendingDevice,
    string CalendarConnectionId);

/// <param name="FgaSubjectId">
/// The user's FGA subject. The seed provisions Alice's and Carol's with their user IDs, as the FGA
/// docs do; SCIM creates Bob's with an ID of its own.
/// </param>
/// <param name="TotpSecret">Base32 authenticator secret, when the user enrolled TOTP.</param>
/// <param name="LastTotpStep">The last TOTP step SqlOS accepted from this user, so the gate never replays a code.</param>
public sealed record UpgradeUser(string Id, string Email, string FgaSubjectId, string? Password, string? TotpSecret, long LastTotpStep);

/// <param name="Label">Names the client kind in transcripts: portal, partner, dynamic, or cimd.</param>
public sealed record UpgradeRefreshToken(string Label, string ClientId, string RefreshToken);

public sealed record UpgradeDeviceAuthorization(string ClientId, string DeviceCode, string UserCode);

/// <summary>
/// What the <see cref="UpgradeData.DirectorySubjectsDataset"/> seed wrote: an organization whose
/// SCIM directory provisioned Bob and Ann into its mapped Engineering group. The released package
/// gave each a subject of SCIM's own; the host then provisioned Ann's subject by her user ID and
/// granted it <c>workspace_admin</c> on the child workspace, and an operator granted her SCIM
/// subject the same role there and <c>workspace_reader</c> on the root.
/// </summary>
public sealed record DirectorySubjectsManifest(
    string SeededWith,
    string OrganizationId,
    string ScimConnectionId,
    string ScimToken,
    string ScimGroupId,
    string ScimMappingId,
    DirectoryUser Bob,
    DirectoryUser Ann);

/// <param name="ScimSubjectId">The subject SCIM created for the user, with an ID of its own.</param>
public sealed record DirectoryUser(string Id, string ScimSubjectId);
