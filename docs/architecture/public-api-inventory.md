# SqlOS public API inventory

This page classifies every public type of the `SqlOS` assembly as Host API, Extension point or Incidental, with the evidence for each. Tasks 2 to 5 of the 8.0.0 refactor (#436 to #439) use it to justify any public API change. A change to a Host API type needs a behavior-ledger entry in `docs/architecture/8.0-behavior-ledger.md`, and the approval [`tests/SqlOS.BehaviorLock/PublicApi/SqlOS.verified.txt`](../../tests/SqlOS.BehaviorLock/PublicApi/SqlOS.verified.txt) changes with it.

[`scripts/public-api-inventory.py`](../../scripts/public-api-inventory.py) generates this page. Do not edit it by hand.

## Regenerate

```bash
python3 scripts/public-api-inventory.py              # rewrite this page
python3 scripts/public-api-inventory.py --check      # exit 1 when this page is stale
python3 scripts/public-api-inventory.py --root <dir> # inventory another checkout
```

The script needs only python3, reads the approval, the documentation and the examples without building anything, and writes the same bytes for the same files. `--check` exits 1 when the page is stale, and the script exits 2 when it cannot read the approval, for example after a PublicApiGenerator format change. Regenerate the page in the change that edits the approval, a documentation page or an example.

## Rules

**Types.** Each type declaration in the approval is one row: classes, records, structs, interfaces, enums and delegates, including static classes and nested types, which are named `Outer.Inner`. A generic type shows its type parameters, as in `SqlOSCursorPage<T>`, and matches by its name without them. Kind repeats the declaration's modifiers and keyword. PublicApiGenerator renders a record as a class or struct that implements `System.IEquatable<T>` of itself, so such a type with no `Equals` member in the approval is shown as a record.

**Evidence files.**

- Documentation: `web/content/docs/**/*.mdx`, `docs/**/*.md` except `docs/architecture/**`, and the root `README.md`.
- Examples: `examples/**/*.cs`, `*.cshtml` and `*.razor`, except the EF Core files generated in `Migrations/` (`*.Designer.cs` and `*ModelSnapshot.cs`).
- The script lists files with `git ls-files --cached --others --exclude-standard`, or walks the directories outside git. A path under `bin/`, `obj/`, `node_modules/` or a directory whose name starts with `.` never counts.

**Matching.**

- A reference is a whole, case-sensitive identifier, where identifier characters are ASCII letters, digits and `_`: `SqlOSUser` matches neither `SqlOSUserEmail` nor `ISqlOSUser`.
- Every occurrence counts: prose, code, comments, link targets and file names such as `SqlOSCryptoService.cs`.
- A type matches by its simple name, without namespace or type parameters. Types that share a simple name share its references, and the evidence says so.
- A nested type matches as `Outer.Inner`, or by its simple name alone when that name contains `SqlOS` and no other public type uses it.
- Hosts call extension methods without naming their static class, so a static class also matches its extension method names: a name with two or more capitals, such as `GrantRoleAsync`, as a whole identifier, and a one-word name, such as `Allows`, only as a call (`.Allows(` or `.Allows<`). Classes that declare the same method name share its references, as `ServiceCollectionExtensions` and `WebApplicationBuilderExtensions` do for `AddSqlOS`.

**Classification.** Each type gets the first class that applies.

1. **Host API**: at least one reference in the evidence files. The evidence lists the referencing files in path order with the number of references in each, the first 5 and then the count of the rest, and names the extension methods that matched.
2. **Extension point**: no reference, but hosts can implement, inherit or configure the type. The evidence lists every reason that applies:
   - `interface` or `delegate`.
   - `abstract class`, or `unsealed class`: a class that is neither `sealed` nor `static` and has a public or protected constructor in the approval. Records read `abstract record` and `unsealed record`.
   - `options type` or `builder type`: the simple name ends in `Options` or `Builder`.
   - `exposed by Owner.Member`: the type appears in the signature of a public member of an options or builder type, as a property or field type, a parameter or return type, or a generic argument of one, such as the `T` of an `Action<T>` configure callback or the arguments of a `Func<...>` hook. Only direct signatures count, not the members of the exposed type. The first 3 members are listed.
3. **Incidental**: neither. The evidence is `none`.

The classification records use and shape, not intent. A Host API type may be an implementation detail that a page names, and an Incidental type may still appear in the signature of a Host API member.

## Summary

The evidence files are 126 documentation pages and 72 example source files.

| Classification | Types |
|---|---|
| Host API | 208 |
| Extension point | 61 |
| Incidental | 245 |
| Total public types | 514 |

### Issue #435 metrics

Issue #435 measured the public surface on SqlOS 7.2.0 (`d608257`). The recomputed column applies this page's rules to the approval.

| Metric | Issue #435 | Recomputed | Why the issue value differs |
|---|---|---|---|
| Public types | 539 | 514 | The issue counted `public` type declarations in `src/SqlOS` on 7.2.0, including 25 extra `partial` declarations (`EndpointRouteBuilderExtensions` 21, `SqlOSAdminService` 4) and 2 records nested in internal classes: 512 visible types. 7.2.1 added `SqlOSScimGrantBoundaryErrors` and `SqlOSScimGrantBoundaryException`. |
| Public `*Service` classes | 47 | 47 | None. |
| `*Service` classes in no doc and no example | 29 | 29 | None. |

A `*Service` class is a class, not an interface or record, whose name ends in `Service`. The 29 with no reference are `SqlOSAuditLogService`, `SqlOSAuthorizationServerService`, `SqlOSCalendarSyncHostedService`, `SqlOSCimdClientService`, `SqlOSClientAuthenticationService`, `SqlOSClientCredentialsService`, `SqlOSConsentService`, `SqlOSDashboardLoginThrottlingService`, `SqlOSDashboardSessionService`, `SqlOSDeliveryAdmissionService`, `SqlOSDeviceAuthorizationService`, `SqlOSDynamicClientRegistrationService`, `SqlOSEmailOtpService`, `SqlOSFgaAuthService`, `SqlOSFgaSubjectService`, `SqlOSHeadlessAuthService`, `SqlOSIssuerSessionService`, `SqlOSMachineClientAdminService`, `SqlOSMagicLinkService`, `SqlOSMfaAttemptAdmissionService`, `SqlOSMfaPolicyService`, `SqlOSOtpAdminService`, `SqlOSPasswordLoginAbuseService`, `SqlOSPhoneOtpService`, `SqlOSSamlService`, `SqlOSSigningKeyRotationService`, `SqlOSTotpMfaService`, `SqlOSTransactionalEmailService`, `SqlOSUserInfoService`.

## SqlOS

1 type: 1 Host API, 0 Extension point, 0 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSDbContext<TContext>` | abstract class | Host API | `docs/QUICK_REFERENCE.md` (2), `examples/SqlOS.Example.Api/Data/ExampleAppDbContext.cs` (1), `examples/SqlOS.Example.Api/FgaRetail/Data/RetailDbContext.cs` (1), `examples/SqlOS.Example.Api/FgaRetail/Endpoints/ChainEndpoints.cs` (1), `examples/SqlOS.Example.Api/FgaRetail/Endpoints/InventoryEndpoints.cs` (1), +20 more |

## SqlOS.AuditLogs

13 types: 11 Host API, 0 Extension point, 2 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `ISqlOSAuditLogService` | interface | Host API | `docs/AUDIT_LOGS.md` (1), `examples/SqlOS.Example.Api/FgaRetail/Services/RetailAuditService.cs` (2), `examples/SqlOS.OneCall.Api/NotesMcpToolCallAudit.cs` (1), `web/content/docs/guides/audit-logs.mdx` (2), `web/content/docs/guides/service-account-jobs.mdx` (2), +4 more |
| `SqlOSAuditActor` | sealed record | Host API | `docs/AUDIT_LOGS.md` (1), `examples/SqlOS.Example.Api/FgaRetail/Services/RetailAuditService.cs` (1), `examples/SqlOS.OneCall.Api/NotesMcpToolCallAudit.cs` (3), `web/content/docs/guides/audit-logs.mdx` (1), `web/content/docs/guides/service-account-jobs.mdx` (1), +2 more |
| `SqlOSAuditContext` | sealed record | Host API | `docs/AUDIT_LOGS.md` (1), `examples/SqlOS.Example.Api/FgaRetail/Services/RetailAuditService.cs` (1), `examples/SqlOS.OneCall.Api/NotesMcpToolCallAudit.cs` (1), `web/content/docs/guides/audit-logs.mdx` (1), `web/content/docs/guides/service-account-jobs.mdx` (1), +2 more |
| `SqlOSAuditLogCsvExportResult` | sealed record | Host API | `web/content/docs/reference/audit-logs.mdx` (1) |
| `SqlOSAuditLogEndpointRouteBuilderExtensions` | static class | Incidental | none |
| `SqlOSAuditLogEvent` | sealed record | Host API | `web/content/docs/reference/audit-logs.mdx` (1) |
| `SqlOSAuditLogIdempotencyConflictException` | sealed class | Host API | `docs/AUDIT_LOGS.md` (1), `web/content/docs/reference/audit-logs.mdx` (1) |
| `SqlOSAuditLogListRequest` | sealed record | Host API | `web/content/docs/reference/audit-logs.mdx` (3) |
| `SqlOSAuditLogListResult` | sealed record | Host API | `web/content/docs/reference/audit-logs.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSAuditLogRecordRequest` | sealed record | Host API | `docs/AUDIT_LOGS.md` (1), `examples/SqlOS.Example.Api/FgaRetail/Services/RetailAuditService.cs` (1), `examples/SqlOS.OneCall.Api/NotesMcpToolCallAudit.cs` (1), `web/content/docs/guides/audit-logs.mdx` (1), `web/content/docs/guides/service-account-jobs.mdx` (1), +2 more |
| `SqlOSAuditLogRecordResult` | sealed record | Host API | `web/content/docs/reference/audit-logs.mdx` (1) |
| `SqlOSAuditLogService` | sealed class | Incidental | none |
| `SqlOSAuditTarget` | sealed record | Host API | `docs/AUDIT_LOGS.md` (1), `examples/SqlOS.Example.Api/FgaRetail/Endpoints/ChainEndpoints.cs` (3), `examples/SqlOS.Example.Api/FgaRetail/Endpoints/InventoryEndpoints.cs` (6), `examples/SqlOS.Example.Api/FgaRetail/Endpoints/LocationEndpoints.cs` (6), `examples/SqlOS.Example.Api/FgaRetail/Services/RetailAuditService.cs` (1), +5 more |

## SqlOS.AuthServer.Authentication

3 types: 2 Host API, 1 Extension point, 0 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSJwtAuthenticationExtensions` | static class | Host API | `web/content/docs/reference/hosting-api.mdx` (2); via extension method `AddSqlOSJwt` |
| `SqlOSJwtDefaults` | static class | Host API | `docs/QUICK_REFERENCE.md` (1), `examples/SqlOS.OneCall.Api/NotesApplication.cs` (1), `web/content/docs/authserver/mcp-server.mdx` (1), `web/content/docs/reference/hosting-api.mdx` (1) |
| `SqlOSJwtOptions` | sealed class | Extension point | options type |

## SqlOS.AuthServer.Configuration

46 types: 8 Host API, 36 Extension point, 2 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSApplicationAssignmentSeedOptions` | sealed class | Extension point | options type; exposed by `SqlOSClientSeedOptions.Assignments` |
| `SqlOSApplicationOptions` | class | Host API | `web/content/docs/reference/hosting-api.mdx` (2) |
| `SqlOSAuthEmailBranding` | sealed record | Incidental | none |
| `SqlOSAuthEmailSeedOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.AuthEmailSeed`, `SqlOSAuthServerOptions.SeedAuthEmails` |
| `SqlOSAuthPageSeedOptions` | sealed class | Host API | `web/content/docs/reference/hosting-api.mdx` (1) |
| `SqlOSAuthServerModelConfiguration` | static class | Incidental | none |
| `SqlOSAuthServerOptions` | class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (8), `examples/SqlOS.Example.Api/Endpoints/ExampleDemoEndpoints.cs` (1), `examples/SqlOS.Example.Api/Middleware/ExampleBearerTokenMiddleware.cs` (1), `examples/SqlOS.Example.Api/Program.cs` (1), `examples/SqlOS.Example.IntegrationTests/SqlOSExampleApiIntegrationTests.cs` (2), +2 more |
| `SqlOSCimdOptions` | sealed class | Extension point | options type; exposed by `SqlOSClientRegistrationOptions.Cimd` |
| `SqlOSCimdTrustContext` | sealed class | Extension point | exposed by `SqlOSCimdOptions.TrustPolicy` |
| `SqlOSClientRegistrationOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.ClientRegistration`, `SqlOSAuthServerOptions.ConfigureClientRegistration`, `SqlOSAuthServerOptions.EnablePortableMcpClients` |
| `SqlOSClientRegistrationPolicyDecision` | sealed record | Host API | `web/content/docs/authserver/client-id-metadata-documents.mdx` (2), `web/content/docs/authserver/dynamic-client-registration.mdx` (2) |
| `SqlOSClientSeedOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.ClientSeeds`, `SqlOSAuthServerOptions.SeedClient`, `SqlOSAuthServerOptions.SeedMachineClient` |
| `SqlOSDeviceAuthorizationOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.ConfigureDeviceAuthorization`, `SqlOSAuthServerOptions.DeviceAuthorization` |
| `SqlOSDynamicClientRegistrationOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.EnableChatGptCompatibility`, `SqlOSAuthServerOptions.EnableVsCodeCompatibility`, `SqlOSClientRegistrationOptions.Dcr` |
| `SqlOSDynamicClientRegistrationPolicyContext` | sealed class | Extension point | exposed by `SqlOSDynamicClientRegistrationOptions.Policy` |
| `SqlOSEmailOtpMessageContext` | sealed record | Extension point | exposed by `SqlOSEmailOtpOptions.BuildMessage` |
| `SqlOSEmailOtpOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.ConfigureEmailOtp`, `SqlOSAuthServerOptions.EmailOtp` |
| `SqlOSHeadlessAuthOptions` | sealed class | Host API | `web/content/docs/reference/hosting-api.mdx` (2) |
| `SqlOSHeadlessSignupHookContext` | sealed record | Extension point | exposed by `SqlOSHeadlessAuthOptions.OnHeadlessSignupAsync` |
| `SqlOSHeadlessUiRouteContext` | sealed record | Extension point | exposed by `SqlOSHeadlessAuthOptions.BuildUiUrl` |
| `SqlOSInvitationMessageContext` | sealed record | Extension point | exposed by `SqlOSInvitationOptions.BuildMessage` |
| `SqlOSInvitationOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.ConfigureInvitations`, `SqlOSAuthServerOptions.Invitations` |
| `SqlOSMachineClientGrantSeedOptions` | sealed record | Extension point | options type; exposed by `SqlOSMachineClientSeedOptions.Grants` |
| `SqlOSMachineClientSeedOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.SeedMachineClient`, `SqlOSClientSeedOptions.MachineClient` |
| `SqlOSMagicLinkMessageContext` | sealed record | Extension point | exposed by `SqlOSMagicLinkOptions.BuildMessage` |
| `SqlOSMagicLinkOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.ConfigureMagicLink`, `SqlOSAuthServerOptions.MagicLink` |
| `SqlOSMagicLinkUrlContext` | sealed record | Extension point | exposed by `SqlOSMagicLinkOptions.BuildLoginUrl` |
| `SqlOSMfaOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.ConfigureMfa`, `SqlOSAuthServerOptions.Mfa` |
| `SqlOSMfaSeedOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.MfaSeed`, `SqlOSAuthServerOptions.SeedMfaPolicy` |
| `SqlOSOidcConnectionSeedOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.OidcConnectionSeeds`, `SqlOSAuthServerOptions.SeedOidcConnection` |
| `SqlOSOpenIdProviderOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.ConfigureOpenIdProvider`, `SqlOSAuthServerOptions.OpenIdProvider` |
| `SqlOSOrganizationMfaPolicySeedOptions` | sealed class | Extension point | options type; exposed by `SqlOSMfaSeedOptions.Organizations` |
| `SqlOSPasswordLoginAbuseOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.ConfigurePasswordLoginAbuse`, `SqlOSAuthServerOptions.PasswordLogin` |
| `SqlOSPasswordResetMessageContext` | sealed record | Extension point | exposed by `SqlOSPasswordResetOptions.BuildMessage` |
| `SqlOSPasswordResetOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.ConfigurePasswordReset`, `SqlOSAuthServerOptions.PasswordReset` |
| `SqlOSPasswordResetUrlContext` | sealed record | Extension point | exposed by `SqlOSPasswordResetOptions.BuildResetUrl` |
| `SqlOSPhoneOtpOptions` | sealed class | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSResourceIndicatorOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.ConfigureResourceIndicators`, `SqlOSAuthServerOptions.ResourceIndicators` |
| `SqlOSSamlConnectionSeedOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.SamlConnectionSeeds`, `SqlOSAuthServerOptions.SeedSamlConnection` |
| `SqlOSScimConnectionSeedOptions` | sealed class | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSScimGroupMappingSeedOptions` | sealed class | Extension point | options type; exposed by `SqlOSScimConnectionSeedOptions.GroupMappings`, `SqlOSScimConnectionSeedOptions.MapGroup`, `SqlOSScimConnectionSeedOptions.MapGroupExternalId`, +1 more |
| `SqlOSScopeDisplaySeedOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.ScopeDisplaySeeds` |
| `SqlOSSingleApplicationOptions` | sealed class | Host API | `web/content/docs/reference/hosting-api.mdx` (4) |
| `SqlOSSsoPortalOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.ConfigureSsoPortal`, `SqlOSAuthServerOptions.SsoPortal` |
| `SqlOSSsoSetupUiRouteContext` | sealed record | Extension point | exposed by `SqlOSSsoPortalOptions.BuildUiUrl` |
| `SqlOSTotpMfaOptions` | sealed class | Extension point | options type; exposed by `SqlOSMfaOptions.Totp` |

## SqlOS.AuthServer.Contracts

180 types: 88 Host API, 0 Extension point, 92 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSAcceptEmailInvitationRequest` | sealed record | Host API | `docs/INVITATIONS.md` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSAcceptEmailInvitationSignupRequest` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSAdminSessionRevocationRequest` | sealed record | Host API | `web/content/docs/authserver/sessions-and-tokens.mdx` (2), `web/content/docs/reference/api-reference.mdx` (1) |
| `SqlOSAdminSessionRevocationResult` | sealed record | Incidental | none |
| `SqlOSApplicationAccessCheckResult` | sealed record | Incidental | none |
| `SqlOSApplicationAccessModes` | static class | Host API | `web/content/docs/guides/configuration.mdx` (1), `web/content/docs/guides/multi-app-access.mdx` (4), `web/content/docs/guides/standalone-identity-server.mdx` (1) |
| `SqlOSApplicationAssignmentAccess` | static class | Host API | `web/content/docs/guides/multi-app-access.mdx` (3) |
| `SqlOSApplicationAssignmentPrincipalTypes` | static class | Host API | `web/content/docs/guides/multi-app-access.mdx` (3) |
| `SqlOSAuthEmailBrandingSettingsDto` | sealed record | Incidental | none |
| `SqlOSAuthPageSettingsDto` | sealed record | Incidental | none |
| `SqlOSAuthorizationServerMetadataDto` | sealed record | Incidental | none |
| `SqlOSAuthorizationUrlRequest` | sealed record | Incidental | none |
| `SqlOSCompleteOidcAuthorizationRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSCompleteOidcAuthorizationResult` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSConfigurationOwners` | static class | Incidental | none |
| `SqlOSConfigurationOwnershipDto` | sealed record | Incidental | none |
| `SqlOSConsentGrantSummary` | sealed record | Incidental | none |
| `SqlOSConsentScopeDisplay` | sealed record | Incidental | none |
| `SqlOSCreateApplicationAssignmentRequest` | sealed record | Host API | `web/content/docs/guides/multi-app-access.mdx` (3) |
| `SqlOSCreateClientRequest` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSCreateEmailInvitationRequest` | sealed record | Host API | `docs/INVITATIONS.md` (1), `web/content/docs/authserver/memberships.mdx` (1), `web/content/docs/guides/invite-only-b2b-onboarding.mdx` (1), `web/content/docs/guides/standalone-identity-server.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1), +1 more |
| `SqlOSCreateMembershipRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (1), `web/content/docs/authserver/memberships.mdx` (1), `web/content/docs/authserver/organizations.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSCreateOidcConnectionRequest` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSCreateOrganizationRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (1), `web/content/docs/authserver/organizations.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSCreateScimConnectionRequest` | sealed record | Host API | `docs/QUICK_REFERENCE.md` (1) |
| `SqlOSCreateScimConnectionResult` | sealed record | Incidental | none |
| `SqlOSCreateScimGroupMappingRequest` | sealed record | Incidental | none |
| `SqlOSCreateScopeDisplayNameRequest` | sealed record | Incidental | none |
| `SqlOSCreateSsoConnectionDraftRequest` | sealed record | Host API | `web/content/docs/authserver/saml-sso.mdx` (1), `web/content/docs/guides/invite-only-b2b-onboarding.mdx` (1), `web/content/docs/guides/scim-directory-sync.mdx` (1), `web/content/docs/guides/scim-saml-access-lifecycle.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1), +1 more |
| `SqlOSCreateSsoConnectionRequest` | sealed record | Incidental | none |
| `SqlOSCreateSsoPortalSessionRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleEndpoints.cs` (1), `web/content/docs/guides/customer-managed-sso.mdx` (1) |
| `SqlOSCreateUserRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (1), `examples/SqlOS.SignInWithX.AppX/Program.cs` (1), `web/content/docs/authserver/users-and-credentials.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSCreateVerificationTokenRequest` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (2), `web/content/docs/reference/authserver-api.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSCreateWorkspaceRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleEndpoints.cs` (1) |
| `SqlOSDeviceAuthorizationApprovalRequest` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSDeviceAuthorizationException` | sealed class | Incidental | none |
| `SqlOSDeviceAuthorizationResolveResult` | sealed record | Incidental | none |
| `SqlOSDeviceAuthorizationStartRequest` | sealed record | Host API | `web/content/docs/guides/standalone-identity-server.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSDeviceAuthorizationStartResult` | sealed record | Incidental | none |
| `SqlOSDeviceTokenPollRequest` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSDeviceTokenPollResult` | sealed record | Incidental | none |
| `SqlOSDomainOwnershipRecord` | sealed record | Incidental | none |
| `SqlOSDynamicClientRegistrationRequest` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (1) |
| `SqlOSDynamicClientRegistrationResponse` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (1) |
| `SqlOSEmailInvitationResult` | sealed record | Host API | `web/content/docs/guides/invite-only-b2b-onboarding.mdx` (1) |
| `SqlOSEmailOtpSignupStartRequest` | sealed record | Host API | `docs/EMAIL_OTP.md` (1), `web/content/docs/authserver/email-otp.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSEmailOtpSignupStartResult` | sealed record | Incidental | none |
| `SqlOSEmailOtpSignupVerifyRequest` | sealed record | Host API | `docs/EMAIL_OTP.md` (1), `web/content/docs/authserver/email-otp.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSEmailOtpStartRequest` | sealed record | Host API | `docs/EMAIL_OTP.md` (1), `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `web/content/docs/guides/standalone-identity-server.mdx` (1), `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1), +1 more |
| `SqlOSEmailOtpStartResult` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (1) |
| `SqlOSEmailOtpVerifyRequest` | sealed record | Host API | `docs/EMAIL_OTP.md` (1), `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSEmailVerificationRequestResult` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (1) |
| `SqlOSForgotPasswordRequest` | sealed record | Host API | `web/content/docs/authserver/password-login.mdx` (1), `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (2), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSHeadlessActionResult` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (1) |
| `SqlOSHeadlessConsentRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessDeviceAuthorizationApproveRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessDeviceAuthorizationDto` | sealed record | Incidental | none |
| `SqlOSHeadlessDeviceAuthorizationResolveRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessEmailOtpSignupStartRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessEmailOtpSignupVerifyRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessEmailOtpStartRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessEmailOtpVerifyRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessIdentifyRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessInvitationResolveRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessInvitationSignupRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessMagicLinkCompleteRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessMagicLinkStartRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessMfaTotpEnrollmentStartRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessMfaTotpEnrollmentVerifyRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessMfaVerifyRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessOrganizationSelectionRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessPasswordLoginRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessPasswordResetEmailRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessPhoneOtpSignupStartRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessPhoneOtpSignupVerifyRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessPhoneOtpStartRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessPhoneOtpVerifyRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessProviderDto` | sealed record | Incidental | none |
| `SqlOSHeadlessProviderStartRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessSignupRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessStartRequest` | sealed record | Incidental | none |
| `SqlOSHeadlessValidationException` | sealed class | Host API | `examples/SqlOS.Example.Api/Program.cs` (1), `web/content/docs/guides/custom-login-ui.mdx` (1), `web/content/docs/guides/email-otp-onboarding.mdx` (2) |
| `SqlOSHeadlessViewModel` | sealed record | Incidental | none |
| `SqlOSHomeRealmDiscoveryRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (2), `web/content/docs/authserver/home-realm-discovery.mdx` (1), `web/content/docs/authserver/password-login.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSHomeRealmDiscoveryResult` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSImportSsoMetadataRequest` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (2), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSInvitationAcceptanceResult` | sealed record | Incidental | none |
| `SqlOSIssuanceAssurance` | sealed class | Incidental | none |
| `SqlOSLoginResult` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `web/content/docs/authserver/phone-otp.mdx` (2), `web/content/docs/reference/api-reference.mdx` (5), `web/content/docs/reference/authserver-api.mdx` (14), `web/content/docs/reference/sdk-reference.mdx` (4) |
| `SqlOSMagicLinkCompleteRequest` | sealed record | Host API | `docs/MAGIC_LINK.md` (1), `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `web/content/docs/authserver/magic-link.mdx` (1), `web/content/docs/reference/api-reference.mdx` (1) |
| `SqlOSMagicLinkCompleteResult` | sealed record | Incidental | none |
| `SqlOSMagicLinkStartRequest` | sealed record | Host API | `docs/MAGIC_LINK.md` (1), `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `web/content/docs/authserver/magic-link.mdx` (1), `web/content/docs/reference/api-reference.mdx` (1) |
| `SqlOSMagicLinkStartResult` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (1) |
| `SqlOSMfaAuthenticatorDto` | sealed record | Incidental | none |
| `SqlOSMfaChallengeVerifyRequest` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (2) |
| `SqlOSMfaChallengeVerifyResult` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (1) |
| `SqlOSMfaFactorTypes` | static class | Host API | `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (2) |
| `SqlOSMfaSettingsDto` | sealed record | Incidental | none |
| `SqlOSMfaStatusResult` | sealed record | Incidental | none |
| `SqlOSOAuthGrantTypes` | static class | Incidental | none |
| `SqlOSOidcAuthorizationUrlRequest` | sealed record | Host API | `web/content/docs/authserver/oidc-auth.mdx` (1) |
| `SqlOSOidcAuthorizationUrlResult` | sealed record | Incidental | none |
| `SqlOSOidcClaimMapping` | sealed class | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSOidcClientAuthMethod` | enum | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSOidcProviderSummary` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSOidcProviderType` | enum | Host API | `web/content/docs/authserver/oidc-auth.mdx` (1), `web/content/docs/guides/mobile-social-sign-in.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (3) |
| `SqlOSOrganizationDomainResult` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSOrganizationMfaPolicyDto` | sealed record | Incidental | none |
| `SqlOSOrganizationOption` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (5) |
| `SqlOSPasswordLoginRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `web/content/docs/authserver/organizations.mdx` (1), `web/content/docs/authserver/password-login.mdx` (1), `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (3), +1 more |
| `SqlOSPasswordResetEmailResult` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSPasswordResetRequestResult` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (2) |
| `SqlOSPhoneOtpEnrollmentStartRequest` | sealed record | Incidental | none |
| `SqlOSPhoneOtpEnrollmentVerifyRequest` | sealed record | Incidental | none |
| `SqlOSPhoneOtpSignupStartRequest` | sealed record | Host API | `docs/SMS_OTP.md` (1), `web/content/docs/authserver/phone-otp.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (3), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSPhoneOtpSignupStartResult` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSPhoneOtpSignupVerifyRequest` | sealed record | Host API | `docs/SMS_OTP.md` (1), `web/content/docs/authserver/phone-otp.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (3), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSPhoneOtpStartRequest` | sealed record | Host API | `docs/SMS_OTP.md` (1), `web/content/docs/authserver/phone-otp.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (3), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSPhoneOtpStartResult` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (2) |
| `SqlOSPhoneOtpVerifyRequest` | sealed record | Host API | `docs/SMS_OTP.md` (1), `web/content/docs/authserver/phone-otp.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (3), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSPkceExchangeRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `web/content/docs/authserver/oidc-auth.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSRefreshRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `web/content/docs/authserver/refresh-and-logout.mdx` (3), `web/content/docs/authserver/sessions-and-tokens.mdx` (1), `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSResendEmailInvitationRequest` | sealed record | Host API | `docs/INVITATIONS.md` (1), `web/content/docs/guides/invite-only-b2b-onboarding.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSResetPasswordRequest` | sealed record | Host API | `web/content/docs/guides/account-recovery-sessions.mdx` (1), `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSResolvedCredentialSettings` | sealed record | Incidental | none |
| `SqlOSRevokeApplicationAssignmentRequest` | sealed record | Incidental | none |
| `SqlOSRevokeEmailInvitationRequest` | sealed record | Host API | `docs/INVITATIONS.md` (1), `web/content/docs/guides/invite-only-b2b-onboarding.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSRevokeSsoPortalSessionRequest` | sealed record | Incidental | none |
| `SqlOSRotateScimTokenResult` | sealed record | Incidental | none |
| `SqlOSScimGrantBoundaryErrors` | static class | Incidental | none |
| `SqlOSScimGroupMappingMatchTypes` | static class | Incidental | none |
| `SqlOSScimSources` | static class | Incidental | none |
| `SqlOSSecuritySettingsDto` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSSelectOrganizationRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `web/content/docs/authserver/organizations.mdx` (1), `web/content/docs/authserver/password-login.mdx` (1), `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (2), +1 more |
| `SqlOSSendPasswordResetEmailRequest` | sealed record | Host API | `web/content/docs/guides/account-recovery-sessions.mdx` (1), `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSSendUserPasswordResetEmailRequest` | sealed record | Incidental | none |
| `SqlOSSetApplicationAccessModeRequest` | sealed record | Host API | `web/content/docs/guides/multi-app-access.mdx` (1) |
| `SqlOSSignupRequest` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (3), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSSocialProviderProtocol` | enum | Incidental | none |
| `SqlOSSsoAuthorizationStartRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSSsoAuthorizationStartResult` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSSsoMetadataValidationResult` | sealed record | Incidental | none |
| `SqlOSSsoPortalConnectionResult` | sealed record | Incidental | none |
| `SqlOSSsoPortalDomainRequest` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSSsoPortalEnrollmentPolicyRequest` | sealed record | Incidental | none |
| `SqlOSSsoPortalEnrollmentPolicyResult` | sealed record | Incidental | none |
| `SqlOSSsoPortalMetadataRequest` | sealed record | Incidental | none |
| `SqlOSSsoPortalOrganizationResult` | sealed record | Incidental | none |
| `SqlOSSsoPortalRevokeOrganizationSessionsRequest` | sealed record | Incidental | none |
| `SqlOSSsoPortalRevokeOrganizationSessionsResult` | sealed record | Incidental | none |
| `SqlOSSsoPortalSessionResult` | sealed record | Incidental | none |
| `SqlOSSsoPortalStateResult` | sealed record | Incidental | none |
| `SqlOSSsoPortalTestRequest` | sealed record | Incidental | none |
| `SqlOSSsoPortalTestResult` | sealed record | Incidental | none |
| `SqlOSSsoProviderGuide` | sealed record | Incidental | none |
| `SqlOSSsoSetupActionResult` | sealed record | Host API | `web/content/docs/authserver/saml-sso.mdx` (1) |
| `SqlOSSsoSetupAllowedActions` | sealed record | Incidental | none |
| `SqlOSSsoSetupServiceProvider` | sealed record | Incidental | none |
| `SqlOSSsoSetupViewModel` | sealed record | Incidental | none |
| `SqlOSStartOidcAuthorizationRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSStartOidcAuthorizationResult` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSTokenResponse` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (2), `web/content/docs/reference/api-reference.mdx` (2), `web/content/docs/reference/authserver-api.mdx` (7), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSTotpChallengeEnrollmentStartRequest` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (1) |
| `SqlOSTotpEnrollmentStartRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleEndpoints.cs` (1), `web/content/docs/authserver/mfa-totp.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (2) |
| `SqlOSTotpEnrollmentStartResult` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSTotpEnrollmentVerifyRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleEndpoints.cs` (1), `web/content/docs/authserver/mfa-totp.mdx` (1), `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (2) |
| `SqlOSTotpEnrollmentVerifyResult` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSUpdateAuthEmailBrandingSettingsRequest` | sealed record | Incidental | none |
| `SqlOSUpdateAuthPageSettingsRequest` | sealed record | Incidental | none |
| `SqlOSUpdateMfaSettingsRequest` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSUpdateOidcConnectionRequest` | sealed record | Incidental | none |
| `SqlOSUpdateOrganizationMfaPolicyRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSUpdateOrganizationRequest` | sealed record | Incidental | none |
| `SqlOSUpdateScimConnectionRequest` | sealed record | Incidental | none |
| `SqlOSUpdateScimGroupMappingRequest` | sealed record | Incidental | none |
| `SqlOSUpdateScopeDisplayNameRequest` | sealed record | Incidental | none |
| `SqlOSUpdateSecuritySettingsRequest` | sealed record | Host API | `web/content/docs/authserver/security-settings.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSUpdateSsoPortalProviderRequest` | sealed record | Incidental | none |
| `SqlOSValidatedToken` | sealed record | Host API | `examples/SqlOS.Todo.Api/Program.cs` (2), `web/content/docs/authserver/token-validation.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (3), `web/content/docs/reference/hosting-api.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSVerifyEmailRequest` | sealed record | Host API | `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |

## SqlOS.AuthServer.Errors

4 types: 3 Host API, 0 Extension point, 1 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSPublicAuthError` | sealed record | Incidental | none |
| `SqlOSPublicAuthErrorMapper` | static class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (4), `examples/SqlOS.Example.IntegrationTests/SqlOSExampleOidcAuthIntegrationTests.cs` (1), `examples/SqlOS.Example.IntegrationTests/SqlOSExamplePublicAuthErrorsIntegrationTests.cs` (5), `examples/SqlOS.Todo.IntegrationTests/TodoSampleIntegrationTests.cs` (1) |
| `SqlOSPublicAuthErrorSurface` | enum | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (3) |
| `SqlOSPublicAuthException` | sealed class | Host API | `web/content/docs/authserver/clients.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |

## SqlOS.AuthServer.Extensions

2 types: 2 Host API, 0 Extension point, 0 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `EndpointRouteBuilderExtensions` | static class | Host API | `web/content/docs/reference/hosting-api.mdx` (1); via extension method `MapAuthServer` |
| `SqlOSAccessTokenValidationExtensions` | static class | Host API | `docs/QUICK_REFERENCE.md` (5), `examples/SqlOS.OneCall.Api/Notes.cs` (2), `examples/SqlOS.OneCall.Api/NotesApplication.cs` (2), `examples/SqlOS.OneCall.Api/NotesMcpToolCallAudit.cs` (1), `web/content/docs/authserver/mcp-server.mdx` (3), +21 more; via extension method `GetSqlOSValidatedToken` |

## SqlOS.AuthServer.Interfaces

8 types: 8 Host API, 0 Extension point, 0 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `ISqlOSAuthEmailSender` | interface | Host API | `docs/INVITATIONS.md` (1), `docs/TRANSACTIONAL_EMAIL.md` (2), `examples/SqlOS.Example.IntegrationTests/Infrastructure/TestAuthEmailSender.cs` (1), `examples/SqlOS.Example.IntegrationTests/SqlOSExampleEmailOtpIntegrationTests.cs` (2), `examples/SqlOS.Example.IntegrationTests/SqlOSExampleMagicLinkIntegrationTests.cs` (2), +4 more |
| `ISqlOSAuthServerDbContext` | interface | Host API | `web/content/docs/reference/hosting-api.mdx` (3) |
| `ISqlOSDomainDnsVerifier` | interface | Host API | `docs/EXAMPLE_APP.md` (1), `web/content/docs/authserver/saml-sso.mdx` (1), `web/content/docs/guides/customer-managed-sso.mdx` (3), `web/content/docs/reference/authserver-api.mdx` (1) |
| `ISqlOSOtpDeliveryChannel` | interface | Host API | `examples/SqlOS.Todo.IntegrationTests/TodoSampleIntegrationTests.cs` (3), `web/content/docs/authserver/phone-otp.mdx` (5), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSAuthEmailMessage` | sealed record | Host API | `docs/INVITATIONS.md` (1), `docs/MAGIC_LINK.md` (1), `examples/SqlOS.Example.IntegrationTests/Infrastructure/TestAuthEmailSender.cs` (4), `examples/SqlOS.Todo.IntegrationTests/Infrastructure/TestAuthEmailSender.cs` (3), `web/content/docs/authserver/email-otp.mdx` (1), +1 more |
| `SqlOSOtpDeliveryCheckResult` | sealed record | Host API | `examples/SqlOS.Todo.IntegrationTests/TodoSampleIntegrationTests.cs` (2), `web/content/docs/authserver/phone-otp.mdx` (2) |
| `SqlOSOtpDeliveryContext` | sealed record | Host API | `examples/SqlOS.Todo.IntegrationTests/TodoSampleIntegrationTests.cs` (4), `web/content/docs/authserver/phone-otp.mdx` (2) |
| `SqlOSOtpDeliveryStartResult` | sealed record | Host API | `examples/SqlOS.Todo.IntegrationTests/TodoSampleIntegrationTests.cs` (2), `web/content/docs/authserver/phone-otp.mdx` (2) |

## SqlOS.AuthServer.Models

49 types: 14 Host API, 0 Extension point, 35 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSApplicationAssignment` | sealed class | Incidental | none |
| `SqlOSAuditEvent` | sealed class | Host API | `docs/AUDIT_LOGS.md` (1), `examples/SqlOS.Example.IntegrationTests/SqlOSExampleApiIntegrationTests.cs` (4), `examples/SqlOS.Example.IntegrationTests/SqlOSExamplePublicAuthErrorsIntegrationTests.cs` (1) |
| `SqlOSAuthPageSettings` | sealed class | Host API | `docs/CONFIGURATION_OWNERSHIP.md` (1) |
| `SqlOSAuthorizationCode` | sealed class | Incidental | none |
| `SqlOSAuthorizationRequest` | sealed class | Incidental | none |
| `SqlOSClientApplication` | sealed class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleDemoEndpoints.cs` (1), `web/content/docs/reference/authserver-api.mdx` (2) |
| `SqlOSClientCredential` | sealed class | Incidental | none |
| `SqlOSConsentGrant` | sealed class | Incidental | none |
| `SqlOSCredential` | sealed class | Incidental | none |
| `SqlOSDeviceAuthorization` | sealed class | Incidental | none |
| `SqlOSEmailOtpChallenge` | sealed class | Incidental | none |
| `SqlOSExternalIdentity` | sealed class | Incidental | none |
| `SqlOSInvitation` | sealed class | Incidental | none |
| `SqlOSIssuerSessionFamily` | sealed class | Incidental | none |
| `SqlOSMembership` | sealed class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleDemoEndpoints.cs` (2), `examples/SqlOS.Example.Api/Services/ExampleFgaService.cs` (1), `web/content/docs/guides/authserver-to-fga.mdx` (1), `web/content/docs/guides/multi-app-access.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSMfaAttemptBucket` | sealed class | Incidental | none |
| `SqlOSMfaAttemptReservation` | sealed class | Incidental | none |
| `SqlOSMfaAttemptReservationBucket` | sealed class | Incidental | none |
| `SqlOSMfaSettings` | sealed class | Incidental | none |
| `SqlOSOidcConnection` | sealed class | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSOrganization` | sealed class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleDemoEndpoints.cs` (2), `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (1), `examples/SqlOS.Example.Api/Services/ExampleFgaService.cs` (2), `web/content/docs/guides/authserver-to-fga.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSOrganizationDomain` | sealed class | Host API | `examples/SqlOS.Example.IntegrationTests/SqlOSExampleApiIntegrationTests.cs` (2), `web/content/docs/authserver/saml-sso.mdx` (1) |
| `SqlOSOrganizationDomainStatuses` | static class | Host API | `examples/SqlOS.Example.IntegrationTests/SqlOSExampleApiIntegrationTests.cs` (1) |
| `SqlOSOrganizationMfaPolicy` | sealed class | Incidental | none |
| `SqlOSPasswordLoginBucket` | sealed class | Incidental | none |
| `SqlOSPasswordLoginReservation` | sealed class | Incidental | none |
| `SqlOSPasswordLoginReservationBucket` | sealed class | Incidental | none |
| `SqlOSPhoneOtpChallenge` | sealed class | Incidental | none |
| `SqlOSRecoveryCode` | sealed class | Incidental | none |
| `SqlOSRefreshToken` | sealed class | Host API | `examples/SqlOS.Example.IntegrationTests/SqlOSExampleApiIntegrationTests.cs` (1) |
| `SqlOSSamlReplay` | sealed class | Incidental | none |
| `SqlOSScimConnection` | sealed class | Incidental | none |
| `SqlOSScimExternalId` | sealed class | Incidental | none |
| `SqlOSScimGroupMapping` | sealed class | Incidental | none |
| `SqlOSScimManagedGrant` | sealed class | Incidental | none |
| `SqlOSScimOperationCommit` | sealed class | Incidental | none |
| `SqlOSScimSyncEvent` | sealed class | Incidental | none |
| `SqlOSScopeDisplayName` | sealed class | Incidental | none |
| `SqlOSSession` | sealed class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `examples/SqlOS.Example.IntegrationTests/SqlOSExampleApiIntegrationTests.cs` (1), `web/content/docs/guides/account-recovery-sessions.mdx` (3) |
| `SqlOSSettings` | sealed class | Incidental | none |
| `SqlOSSigningKey` | sealed class | Incidental | none |
| `SqlOSSsoConnection` | sealed class | Host API | `web/content/docs/reference/authserver-api.mdx` (2) |
| `SqlOSSsoPortalSession` | sealed class | Incidental | none |
| `SqlOSTemporaryToken` | sealed class | Host API | `examples/SqlOS.Example.IntegrationTests/SqlOSExampleApiIntegrationTests.cs` (3) |
| `SqlOSUser` | sealed class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (3), `examples/SqlOS.Example.Api/Endpoints/ExampleDemoEndpoints.cs` (3), `examples/SqlOS.Example.Api/Services/ExampleFgaService.cs` (2), `web/content/docs/guides/authserver-to-fga.mdx` (1), `web/content/docs/guides/scim-directory-sync.mdx` (1), +1 more |
| `SqlOSUserAuthenticator` | sealed class | Incidental | none |
| `SqlOSUserEmail` | sealed class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleDemoEndpoints.cs` (2), `examples/SqlOS.Example.IntegrationTests/SqlOSExampleApiIntegrationTests.cs` (4), `examples/SqlOS.SignInWithX.AppX/Program.cs` (1), `web/content/docs/guides/scim-directory-sync.mdx` (1) |
| `SqlOSUserMfaPolicyOverride` | sealed class | Incidental | none |
| `SqlOSUserPhoneNumber` | sealed class | Incidental | none |

## SqlOS.AuthServer.Services

85 types: 20 Host API, 0 Extension point, 65 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSAcsAuthEmailSender` | sealed class | Incidental | none |
| `SqlOSAdminService` | sealed class | Host API | `docs/AUDIT_LOGS.md` (1), `docs/QUICK_REFERENCE.md` (2), `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1), `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (2), `examples/SqlOS.SignInWithX.AppX/Program.cs` (1), +7 more |
| `SqlOSAuthPageProviderLink` | sealed record | Incidental | none |
| `SqlOSAuthPageRenderer` | static class | Incidental | none |
| `SqlOSAuthPageViewModel` | sealed record | Incidental | none |
| `SqlOSAuthService` | sealed class | Host API | `docs/MAGIC_LINK.md` (1), `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (14), `examples/SqlOS.Example.Api/Endpoints/ExampleDemoEndpoints.cs` (1), `examples/SqlOS.Example.Api/Endpoints/ExampleEndpoints.cs` (3), `examples/SqlOS.Example.Api/Middleware/ExampleBearerTokenMiddleware.cs` (1), +14 more |
| `SqlOSAuthorizationRequestLoginResult` | sealed record | Incidental | none |
| `SqlOSAuthorizationServerService` | sealed class | Incidental | none |
| `SqlOSAuthorizeRequestInput` | sealed record | Incidental | none |
| `SqlOSCimdClientService` | sealed class | Incidental | none |
| `SqlOSClientAllowlistWarning` | sealed record | Incidental | none |
| `SqlOSClientAllowlistWarnings` | static class | Incidental | none |
| `SqlOSClientAuthenticationException` | sealed class | Host API | `web/content/docs/authserver/refresh-and-logout.mdx` (1), `web/content/docs/reference/api-reference.mdx` (1) |
| `SqlOSClientAuthenticationService` | sealed class | Incidental | none |
| `SqlOSClientCredentialCreated` | sealed record | Incidental | none |
| `SqlOSClientCredentialDto` | sealed record | Incidental | none |
| `SqlOSClientCredentialsException` | sealed class | Incidental | none |
| `SqlOSClientCredentialsService` | sealed class | Incidental | none |
| `SqlOSClientCredentialsTokenResult` | sealed record | Incidental | none |
| `SqlOSClientRegistrationException` | sealed class | Host API | `web/content/docs/reference/api-reference.mdx` (1) |
| `SqlOSClientResolutionService` | sealed class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (1) |
| `SqlOSConsentService` | sealed class | Incidental | none |
| `SqlOSCreateClientCredentialRequest` | sealed record | Incidental | none |
| `SqlOSCreateMachineClientRequest` | sealed record | Incidental | none |
| `SqlOSCryptoService` | sealed class | Host API | `docs/MFA_TOTP.md` (1), `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (5), `web/content/docs/authserver/mfa-totp.mdx` (1), `web/content/docs/authserver/overview.mdx` (1), `web/content/docs/guides/production-readiness.mdx` (2), +1 more |
| `SqlOSCssColor` | static class | Incidental | none |
| `SqlOSDeliveryAdmissionDecision` | sealed record | Incidental | none |
| `SqlOSDeliveryAdmissionService` | sealed class | Incidental | none |
| `SqlOSDeviceAuthorizationService` | sealed class | Incidental | none |
| `SqlOSDnsOverHttpsDomainVerifier` | sealed class | Incidental | none |
| `SqlOSDomainOwnershipVerification` | static class | Incidental | none |
| `SqlOSDynamicClientRegistrationRateLimiter` | sealed class | Incidental | none |
| `SqlOSDynamicClientRegistrationService` | sealed class | Incidental | none |
| `SqlOSEmailOtpService` | sealed class | Incidental | none |
| `SqlOSEmailOtpSignupVerificationResult` | sealed record | Incidental | none |
| `SqlOSEmailOtpVerificationResult` | sealed record | Incidental | none |
| `SqlOSHeadlessAuthService` | sealed class | Incidental | none |
| `SqlOSHomeRealmDiscoveryService` | sealed class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (2), `web/content/docs/authserver/overview.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSInvitationService` | sealed class | Host API | `web/content/docs/guides/invite-only-b2b-onboarding.mdx` (1) |
| `SqlOSIssuanceAssuranceDecision` | sealed record | Incidental | none |
| `SqlOSIssuerSession` | sealed record | Incidental | none |
| `SqlOSIssuerSessionService` | sealed class | Incidental | none |
| `SqlOSKeyRotationSettings` | sealed record | Incidental | none |
| `SqlOSMachineClientAdminService` | sealed class | Incidental | none |
| `SqlOSMachineClientCreated` | sealed record | Incidental | none |
| `SqlOSMachineClientDto` | sealed record | Incidental | none |
| `SqlOSMachineClientGrantRequest` | sealed record | Incidental | none |
| `SqlOSMachineClientValidation` | sealed record | Incidental | none |
| `SqlOSMagicLinkService` | sealed class | Incidental | none |
| `SqlOSMfaAttemptAdmissionService` | sealed class | Incidental | none |
| `SqlOSMfaPolicyEvaluation` | sealed record | Incidental | none |
| `SqlOSMfaPolicyService` | sealed class | Incidental | none |
| `SqlOSOidcAuthService` | sealed class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (3), `examples/SqlOS.Example.Api/Endpoints/ExampleCalendarEndpoints.cs` (1), `web/content/docs/authserver/oidc-auth.mdx` (2), `web/content/docs/authserver/overview.mdx` (1), `web/content/docs/guides/calendar-integration.mdx` (1), +2 more |
| `SqlOSOidcBrowserAuthService` | sealed class | Host API | `web/content/docs/authserver/clients.mdx` (1), `web/content/docs/authserver/oidc-auth.mdx` (3), `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSOpenIdScopeWarning` | sealed record | Incidental | none |
| `SqlOSOpenIdScopeWarnings` | static class | Incidental | none |
| `SqlOSOrganizationDomainService` | sealed class | Host API | `web/content/docs/reference/authserver-api.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSOtpAdminRateLimiter` | sealed class | Incidental | none |
| `SqlOSOtpAdminService` | sealed class | Incidental | none |
| `SqlOSOtpDiagnosticResponse` | sealed record | Incidental | none |
| `SqlOSOtpMethodReadiness` | sealed record | Incidental | none |
| `SqlOSOtpReadinessResponse` | sealed record | Incidental | none |
| `SqlOSOtpTestDeliveryResult` | sealed record | Incidental | none |
| `SqlOSPasswordAuthenticationResult` | sealed record | Incidental | none |
| `SqlOSPasswordLoginAbuseService` | sealed class | Incidental | none |
| `SqlOSPasswordLoginAttempt` | sealed record | Incidental | none |
| `SqlOSPhoneOtpService` | sealed class | Incidental | none |
| `SqlOSPhoneOtpSignupVerificationResult` | sealed record | Incidental | none |
| `SqlOSPhoneOtpVerificationResult` | sealed record | Incidental | none |
| `SqlOSResolvedClient` | sealed record | Incidental | none |
| `SqlOSResolvedSecuritySettings` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSSamlService` | sealed class | Incidental | none |
| `SqlOSSchemaInitializer` | sealed class | Host API | `web/content/docs/guides/production-readiness.mdx` (2) |
| `SqlOSScimGrantBoundaryException` | sealed class | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSSessionRevocationService` | sealed class | Host API | `web/content/docs/guides/account-recovery-sessions.mdx` (1) |
| `SqlOSSettingsService` | sealed class | Host API | `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (2), `examples/SqlOS.Todo.Api/Program.cs` (1), `web/content/docs/authserver/overview.mdx` (1), `web/content/docs/authserver/refresh-and-logout.mdx` (1), `web/content/docs/authserver/security-settings.mdx` (2), +2 more |
| `SqlOSSsoAuthorizationService` | sealed class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleAuthEndpoints.cs` (4), `web/content/docs/authserver/home-realm-discovery.mdx` (1), `web/content/docs/authserver/overview.mdx` (1), `web/content/docs/reference/authserver-api.mdx` (2), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSSsoPortalPageRenderer` | static class | Incidental | none |
| `SqlOSSsoPortalService` | sealed class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleEndpoints.cs` (1), `web/content/docs/authserver/saml-sso.mdx` (1), `web/content/docs/guides/customer-managed-sso.mdx` (3), `web/content/docs/reference/api-reference.mdx` (2), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSTokenEndpointResult` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1) |
| `SqlOSTokenRequest` | sealed record | Host API | `web/content/docs/reference/authserver-api.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSTotpMfaService` | sealed class | Incidental | none |
| `SqlOSTwilioVerifyOtpChannel` | sealed class | Incidental | none |
| `SqlOSUserInfoResult` | sealed record | Incidental | none |
| `SqlOSUserInfoService` | sealed class | Incidental | none |

## SqlOS.Calendar.Configuration

3 types: 0 Host API, 2 Extension point, 1 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSCalendarModelConfiguration` | static class | Incidental | none |
| `SqlOSCalendarOptions` | sealed class | Extension point | options type; exposed by `SqlOSOptions.Calendar`, `SqlOSOptions.ConfigureCalendar` |
| `SqlOSCalendarSyncSchedulerOptions` | sealed class | Extension point | options type; exposed by `SqlOSCalendarOptions.ConfigureSyncScheduler`, `SqlOSCalendarOptions.SyncScheduler` |

## SqlOS.Calendar.Contracts

13 types: 4 Host API, 1 Extension point, 8 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSCalendarAccessTokenResult` | sealed record | Incidental | none |
| `SqlOSCalendarConflictContext` | sealed record | Extension point | exposed by `SqlOSCalendarOptions.OnTwoWayConflictAsync` |
| `SqlOSCalendarConflictDecision` | enum | Host API | `web/content/docs/guides/calendar-integration.mdx` (2) |
| `SqlOSCalendarConnectionSummary` | sealed record | Host API | `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSCalendarEventDraft` | sealed record | Host API | `web/content/docs/guides/calendar-integration.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSCalendarEventPage` | sealed record | Incidental | none |
| `SqlOSCalendarEventSnapshot` | sealed record | Incidental | none |
| `SqlOSCalendarSummary` | sealed record | Incidental | none |
| `SqlOSCalendarSyncResult` | sealed record | Incidental | none |
| `SqlOSCalendarTokenResult` | sealed record | Incidental | none |
| `SqlOSCompleteCalendarConnectResult` | sealed record | Incidental | none |
| `SqlOSStartCalendarConnectRequest` | sealed record | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleCalendarEndpoints.cs` (1), `web/content/docs/guides/calendar-integration.mdx` (1), `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (2) |
| `SqlOSStartCalendarConnectResult` | sealed record | Incidental | none |

## SqlOS.Calendar.Extensions

1 type: 0 Host API, 0 Extension point, 1 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `EndpointRouteBuilderExtensions` | static class | Incidental | none |

## SqlOS.Calendar.Interfaces

2 types: 0 Host API, 1 Extension point, 1 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `ISqlOSCalendarProviderAdapter` | interface | Extension point | interface |
| `SqlOSCalendarProviderContext` | sealed record | Incidental | none |

## SqlOS.Calendar.Models

6 types: 1 Host API, 0 Extension point, 5 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSCalendarConnection` | sealed class | Incidental | none |
| `SqlOSCalendarConnectionStatus` | enum | Incidental | none |
| `SqlOSCalendarEvent` | sealed class | Incidental | none |
| `SqlOSCalendarIntegrationMode` | enum | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleCalendarEndpoints.cs` (2), `web/content/docs/guides/calendar-integration.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSCalendarProviderType` | enum | Incidental | none |
| `SqlOSCalendarSyncState` | sealed class | Incidental | none |

## SqlOS.Calendar.Services

6 types: 2 Host API, 0 Extension point, 4 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `CalendarConnectRequestPayload` | sealed record | Incidental | none |
| `SqlOSCalendarService` | sealed class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleCalendarEndpoints.cs` (5), `web/content/docs/guides/calendar-integration.mdx` (1), `web/content/docs/reference/api-reference.mdx` (2), `web/content/docs/reference/index.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (2) |
| `SqlOSCalendarSyncHostedService` | sealed class | Incidental | none |
| `SqlOSCalendarSyncService` | sealed class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleCalendarEndpoints.cs` (1), `web/content/docs/guides/calendar-integration.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (3) |
| `SqlOSGoogleCalendarAdapter` | sealed class | Incidental | none |
| `SqlOSMicrosoftGraphCalendarAdapter` | sealed class | Incidental | none |

## SqlOS.Configuration

5 types: 2 Host API, 3 Extension point, 0 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSBrowserSecurityOptions` | sealed class | Extension point | options type; exposed by `SqlOSOptions.BrowserSecurity` |
| `SqlOSDashboardAuthMode` | enum | Host API | `docs/QUICK_REFERENCE.md` (2), `examples/SqlOS.Example.Api/Program.cs` (1), `web/content/docs/authserver/dashboard-overview.mdx` (1), `web/content/docs/getting-started.mdx` (1), `web/content/docs/guides/configuration.mdx` (1), +5 more |
| `SqlOSDashboardLoginThrottlingOptions` | sealed class | Extension point | options type; exposed by `SqlOSDashboardOptions.LoginThrottling` |
| `SqlOSDashboardOptions` | sealed class | Extension point | options type; exposed by `SqlOSAuthServerOptions.Dashboard`, `SqlOSFgaOptions.Dashboard`, `SqlOSOptions.Dashboard` |
| `SqlOSOptions` | sealed class | Host API | `web/content/docs/reference/api-reference.mdx` (5), `web/content/docs/reference/hosting-api.mdx` (9) |

## SqlOS.Dashboard

7 types: 0 Host API, 0 Extension point, 7 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSDashboardLoginLockoutResult` | sealed record | Incidental | none |
| `SqlOSDashboardLoginReservation` | sealed record | Incidental | none |
| `SqlOSDashboardLoginReservationResult` | sealed record | Incidental | none |
| `SqlOSDashboardLoginThrottleRejection` | sealed record | Incidental | none |
| `SqlOSDashboardLoginThrottlingService` | sealed class | Incidental | none |
| `SqlOSDashboardMiddleware` | sealed class | Incidental | none |
| `SqlOSDashboardSessionService` | sealed class | Incidental | none |

## SqlOS.Email.Configuration

2 types: 0 Host API, 1 Extension point, 1 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSEmailModelConfiguration` | static class | Incidental | none |
| `SqlOSEmailOptions` | sealed class | Extension point | options type; exposed by `SqlOSOptions.ConfigureEmail`, `SqlOSOptions.Email` |

## SqlOS.Email.Contracts

8 types: 6 Host API, 0 Extension point, 2 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSCreateEmailTemplateRequest` | sealed record | Host API | `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSEmailMessage` | sealed record | Host API | `examples/SqlOS.Example.IntegrationTests/Infrastructure/TestAuthEmailSender.cs` (1), `examples/SqlOS.Example.IntegrationTests/SqlOSExampleApiIntegrationTests.cs` (2), `examples/SqlOS.Todo.IntegrationTests/Infrastructure/TestAuthEmailSender.cs` (1) |
| `SqlOSEmailProviderResult` | sealed record | Host API | `examples/SqlOS.Example.IntegrationTests/Infrastructure/TestAuthEmailSender.cs` (2), `examples/SqlOS.Example.IntegrationTests/SqlOSExampleApiIntegrationTests.cs` (2), `examples/SqlOS.Todo.IntegrationTests/Infrastructure/TestAuthEmailSender.cs` (2) |
| `SqlOSPreviewEmailTemplateRequest` | sealed record | Incidental | none |
| `SqlOSRenderedEmailPreview` | sealed record | Host API | `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSSendEmailRequest` | sealed record | Host API | `docs/TRANSACTIONAL_EMAIL.md` (1), `web/content/docs/guides/transactional-email.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (2) |
| `SqlOSSendEmailResult` | sealed record | Host API | `web/content/docs/guides/transactional-email.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (2) |
| `SqlOSUpdateEmailTemplateRequest` | sealed record | Incidental | none |

## SqlOS.Email.Extensions

1 type: 0 Host API, 0 Extension point, 1 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `EndpointRouteBuilderExtensions` | static class | Incidental | none |

## SqlOS.Email.Interfaces

2 types: 2 Host API, 0 Extension point, 0 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `ISqlOSEmailSender` | interface | Host API | `docs/TRANSACTIONAL_EMAIL.md` (1), `examples/SqlOS.Example.IntegrationTests/Infrastructure/TestAuthEmailSender.cs` (1), `examples/SqlOS.Example.IntegrationTests/SqlOSExampleApiIntegrationTests.cs` (15), `examples/SqlOS.Example.IntegrationTests/SqlOSExampleEmailOtpIntegrationTests.cs` (2), `examples/SqlOS.Example.IntegrationTests/SqlOSExampleMagicLinkIntegrationTests.cs` (2), +6 more |
| `ISqlOSTransactionalEmailService` | interface | Host API | `web/content/docs/guides/transactional-email.mdx` (4), `web/content/docs/reference/index.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (2) |

## SqlOS.Email.Models

3 types: 0 Host API, 0 Extension point, 3 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSEmailDelivery` | sealed class | Incidental | none |
| `SqlOSEmailDeliveryStatuses` | static class | Incidental | none |
| `SqlOSEmailTemplate` | sealed class | Incidental | none |

## SqlOS.Email.Services

8 types: 2 Host API, 0 Extension point, 6 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSAcsEmailSender` | sealed class | Incidental | none |
| `SqlOSBuiltInEmailTemplateDefinition` | sealed record | Incidental | none |
| `SqlOSBuiltInEmailTemplates` | static class | Incidental | none |
| `SqlOSDefaultEmailSender` | sealed class | Incidental | none |
| `SqlOSEmailAdminService` | sealed class | Host API | `web/content/docs/reference/api-reference.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSEmailTemplateRenderer` | sealed class | Incidental | none |
| `SqlOSEmailTemplateValidationException` | sealed class | Host API | `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSTransactionalEmailService` | sealed class | Incidental | none |

## SqlOS.Extensions

5 types: 4 Host API, 0 Extension point, 1 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `ModelBuilderExtensions` | static class | Host API | `web/content/docs/guides/choosing-a-provider.mdx` (2); via extension method `UseSqlOS` |
| `ServiceCollectionExtensions` | static class | Host API | `docs/AUDIT_LOGS.md` (1), `docs/AUTH_PAGE.md` (1), `docs/CLIENT_REGISTRATION_DEVEX_2026.md` (5), `docs/EMAIL_OTP.md` (1), `docs/INVITATIONS.md` (1), +67 more; via extension method `AddSqlOS` |
| `SqlOSErgonomicsExtensions` | static class | Host API | `docs/QUICK_REFERENCE.md` (3), `examples/SqlOS.Example.Api/Services/ExampleFgaService.cs` (4), `examples/SqlOS.OneCall.Api/Notes.cs` (3), `examples/SqlOS.Todo.Api/Program.cs` (3), `examples/SqlOS.Todo.Api/Services/TodoFgaService.cs` (3), +12 more; via extension methods `Allows`, `CreateResourceAsync`, `CreateResourceWithIdAsync`, `DeleteResourceAsync`, `GrantRoleAsync`, `ProvisionAgentSubjectAsync`, `ProvisionResourceWithIdAsync`, `ProvisionServiceAccountSubjectAsync`, `ProvisionUserSubjectAsync`, `RevokeRoleAsync` |
| `WebApplicationBuilderExtensions` | static class | Host API | `docs/AUDIT_LOGS.md` (1), `docs/AUTH_PAGE.md` (1), `docs/CLIENT_REGISTRATION_DEVEX_2026.md` (5), `docs/EMAIL_OTP.md` (1), `docs/INVITATIONS.md` (1), +67 more; via extension method `AddSqlOS` |
| `WebApplicationExtensions` | static class | Incidental | none |

## SqlOS.Fga.Configuration

5 types: 1 Host API, 3 Extension point, 1 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSFgaModelConfiguration` | static class | Incidental | none |
| `SqlOSFgaOptions` | class | Extension point | unsealed class; options type; exposed by `SqlOSOptions.Fga` |
| `SqlOSFgaRoleSeedBuilder` | sealed class | Extension point | builder type; exposed by `SqlOSFgaSeedBuilder.Role` |
| `SqlOSFgaSeedBuilder` | sealed class | Host API | `web/content/docs/reference/hosting-api.mdx` (1) |
| `SqlOSFgaTableNames` | class | Extension point | unsealed class; exposed by `SqlOSFgaOptions.TableNames` |

## SqlOS.Fga.Dashboard

1 type: 0 Host API, 1 Extension point, 0 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSFgaDashboardMiddleware` | class | Extension point | unsealed class |

## SqlOS.Fga.Extensions

2 types: 2 Host API, 0 Extension point, 0 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `ModelBuilderExtensions` | static class | Host API | `examples/SqlOS.Example.Api/FgaRetail/Data/RetailDbContext.cs` (1); via extension method `ApplySqlOSFgaModel` |
| `SqlOSFgaConvenienceExtensions` | static class | Host API | `examples/SqlOS.Example.Api/FgaRetail/Endpoints/ChainEndpoints.cs` (2), `examples/SqlOS.Example.Api/FgaRetail/Endpoints/InventoryEndpoints.cs` (2), `examples/SqlOS.Example.Api/FgaRetail/Endpoints/LocationEndpoints.cs` (2), `web/content/docs/fga/detail-endpoint.mdx` (5), `web/content/docs/fga/list-filter.mdx` (1), +1 more; via extension methods `AuthorizedDetailAsync`, `CreateResource` |

## SqlOS.Fga.Interfaces

5 types: 5 Host API, 0 Extension point, 0 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `IHasResourceId` | interface | Host API | `docs/QUICK_REFERENCE.md` (1), `examples/SqlOS.Example.Api/FgaRetail/Models/Chain.cs` (1), `examples/SqlOS.Example.Api/FgaRetail/Models/InventoryItem.cs` (1), `examples/SqlOS.Example.Api/FgaRetail/Models/Location.cs` (1), `web/content/docs/fga/detail-endpoint.mdx` (2), +2 more |
| `ISqlOSFgaAuthService` | interface | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleEndpoints.cs` (2), `examples/SqlOS.Example.Api/FgaRetail/Endpoints/ChainEndpoints.cs` (5), `examples/SqlOS.Example.Api/FgaRetail/Endpoints/InventoryEndpoints.cs` (5), `examples/SqlOS.Example.Api/FgaRetail/Endpoints/LocationEndpoints.cs` (7), `examples/SqlOS.OneCall.Api/Notes.cs` (1), +15 more |
| `ISqlOSFgaDbContext` | interface | Host API | `examples/SqlOS.Example.Api/FgaRetail/Data/RetailDbContext.cs` (1), `web/content/docs/reference/hosting-api.mdx` (3), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `ISqlOSFgaSubjectService` | interface | Host API | `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (2), `web/content/docs/fga/subject-types.mdx` (1), `web/content/docs/guides/fga-groups.mdx` (1), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `ISqlOSResourceEntity` | interface | Host API | `examples/SqlOS.Example.Api/FgaRetail/Data/RetailDbContext.cs` (1), `examples/SqlOS.Example.Api/FgaRetail/Endpoints/ChainEndpoints.cs` (1), `examples/SqlOS.Example.Api/FgaRetail/Endpoints/InventoryEndpoints.cs` (1), `examples/SqlOS.Example.Api/FgaRetail/Endpoints/LocationEndpoints.cs` (1), `examples/SqlOS.Example.Api/Models/Workspace.cs` (1), +14 more |

## SqlOS.Fga.Models

22 types: 14 Host API, 8 Extension point, 0 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSFgaAccessCheckResult` | class | Host API | `web/content/docs/reference/fga-api.mdx` (3), `web/content/docs/reference/sdk-reference.mdx` (1) |
| `SqlOSFgaAccessTrace` | class | Host API | `web/content/docs/reference/fga-api.mdx` (4) |
| `SqlOSFgaAccessibleResource` | class | Host API | `examples/SqlOS.Example.Api/FgaRetail/Data/RetailDbContext.cs` (1) |
| `SqlOSFgaAgent` | class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleDemoEndpoints.cs` (1), `examples/SqlOS.Example.Api/Middleware/ExampleBearerTokenMiddleware.cs` (1), `web/content/docs/fga/subject-types.mdx` (1) |
| `SqlOSFgaGrant` | class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleDemoEndpoints.cs` (1), `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (10), `web/content/docs/fga/grants.mdx` (1), `web/content/docs/guides/scim-directory-sync.mdx` (1), `web/content/docs/reference/fga-api.mdx` (2) |
| `SqlOSFgaGrantTrace` | class | Extension point | unsealed class |
| `SqlOSFgaPermission` | class | Host API | `web/content/docs/fga/permissions.mdx` (1) |
| `SqlOSFgaPermissionAssignmentTrace` | class | Extension point | unsealed class |
| `SqlOSFgaResource` | class | Host API | `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (17), `web/content/docs/fga/core-concepts.mdx` (1), `web/content/docs/fga/creating-resources.mdx` (2), `web/content/docs/fga/mutation-create-resource.mdx` (1), `web/content/docs/guides/authserver-to-fga.mdx` (1), +2 more |
| `SqlOSFgaResourceAccessTrace` | class | Host API | `web/content/docs/reference/fga-api.mdx` (1) |
| `SqlOSFgaResourcePathNodeTrace` | class | Extension point | unsealed class |
| `SqlOSFgaResourceType` | class | Extension point | unsealed class |
| `SqlOSFgaRole` | class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleDemoEndpoints.cs` (1), `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (4), `web/content/docs/fga/roles.mdx` (1) |
| `SqlOSFgaRolePermission` | class | Extension point | unsealed class |
| `SqlOSFgaRoleTrace` | class | Extension point | unsealed class |
| `SqlOSFgaServiceAccount` | class | Host API | `examples/SqlOS.Example.Api/Endpoints/ExampleDemoEndpoints.cs` (1), `examples/SqlOS.Example.Api/Middleware/ExampleBearerTokenMiddleware.cs` (1), `web/content/docs/fga/subject-types.mdx` (1), `web/content/docs/guides/service-account-jobs.mdx` (3), `web/content/docs/reference/glossary.mdx` (1) |
| `SqlOSFgaSubject` | class | Host API | `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (2), `examples/SqlOS.Example.Api/FgaRetail/Services/RetailAuditService.cs` (1), `web/content/docs/guides/authserver-to-fga.mdx` (2), `web/content/docs/guides/fga-groups.mdx` (2), `web/content/docs/reference/fga-api.mdx` (3) |
| `SqlOSFgaSubjectInfo` | class | Extension point | unsealed class |
| `SqlOSFgaSubjectType` | class | Extension point | unsealed class |
| `SqlOSFgaUser` | class | Host API | `web/content/docs/fga/subject-types.mdx` (1) |
| `SqlOSFgaUserGroup` | class | Host API | `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (2), `web/content/docs/fga/subject-types.mdx` (2), `web/content/docs/guides/fga-groups.mdx` (1), `web/content/docs/guides/scim-directory-sync.mdx` (1) |
| `SqlOSFgaUserGroupMembership` | class | Host API | `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (3), `web/content/docs/guides/scim-directory-sync.mdx` (1) |

## SqlOS.Fga.Services

7 types: 3 Host API, 3 Extension point, 1 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSFgaAuthService` | class | Extension point | unsealed class |
| `SqlOSFgaFunctionInitializer` | class | Extension point | unsealed class |
| `SqlOSFgaHierarchyValidator` | sealed class | Incidental | none |
| `SqlOSFgaSchemaInitializer` | class | Host API | `web/content/docs/guides/production-readiness.mdx` (2) |
| `SqlOSFgaSeedData` | class | Host API | `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (1) |
| `SqlOSFgaSeedService` | class | Host API | `examples/SqlOS.Example.Api/FgaRetail/Seeding/RetailSeedService.cs` (2) |
| `SqlOSFgaSubjectService` | class | Extension point | unsealed class |

## SqlOS.Hosting

2 types: 1 Host API, 1 Extension point, 0 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `ISqlOSHostExtension` | interface | Extension point | interface; exposed by `SqlOSApplicationOptions.HostExtensions` |
| `SqlOSBootstrapHostedService` | sealed class | Host API | `examples/SqlOS.SignInWithX.AppX/Program.cs` (1), `web/content/docs/guides/production-readiness.mdx` (1) |

## SqlOS.Pagination

5 types: 1 Host API, 0 Extension point, 4 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSCursorCodec` | static class | Incidental | none |
| `SqlOSCursorException` | sealed class | Incidental | none |
| `SqlOSCursorPage<T>` | sealed class | Incidental | none |
| `SqlOSCursorPagination` | static class | Host API | `web/content/docs/guides/production-readiness.mdx` (2) |
| `SqlOSKeyset<T>` | sealed class | Incidental | none |

## SqlOS.Services

2 types: 1 Host API, 0 Extension point, 1 Incidental.

| Type | Kind | Classification | Evidence |
|---|---|---|---|
| `SqlOSBootstrapper` | sealed class | Host API | `web/content/docs/guides/configuration.mdx` (1), `web/content/docs/guides/production-readiness.mdx` (4), `web/content/docs/guides/troubleshooting.mdx` (1) |
| `SqlOSSigningKeyRotationService` | sealed class | Incidental | none |
