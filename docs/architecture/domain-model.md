# SqlOS domain model

Status: accepted for SqlOS 8.0.0 (#436). Applies to all code under `src/SqlOS`.
Audience: contributors and coding agents changing SqlOS.
Companion documents: `docs/architecture/8.0-behavior-ledger.md` (every intended external change), `docs/architecture/public-api-inventory.md` (who uses which public type), `AGENTS.md` ("Domain model rules").

## 1. Why this exists

SqlOS 7.x is written as *transaction scripts*: a service method loads rows, decides, sets fields and saves. The entities are data holders with public setters. The scripts are organized by **the surface a request arrives on**, not by **the concept being changed**:

- `SqlOSAuthService` is the public API.
- `SqlOSHeadlessAuthService` serves the headless UI.
- `HostedPrimaryEndpoints` and the other hosted endpoints serve the hosted AuthPage.
- `SqlOSAdminService` serves admin.

So the rules for one concept are re-implemented once per surface. Measured on the 7.2.1 baseline:

| Measure | 7.2.1 |
|---|---|
| Public settable properties on entities | 734, across 70 entity classes, with about 24 methods |
| Places that write `RevokedAt` | 43, in 17 files |
| Places that write `IsActive` | 36, in 10 files |
| Places that write `ConsumedAt` | 25, in 12 files |
| Places that set `IsVerified` | 13, in 8 files |
| Assignments to `AuthorizationRequest` fields | 43, in 9 files (two of them HTTP endpoint files) |
| `SaveChangesAsync` call sites | 258, in 48 files |
| Hand-written audit writes | 148, in 38 files |
| `DateTime.UtcNow` uses | 389 |
| Duplicated flows | sign-up and MFA verification ×3 services; email-OTP verification, magic-link completion and organization selection ×2 |

Every Critical and High finding of the September 2026 audit was a script that skipped a rule another script applied (#418 to #423). The fixes in 7.2.1 patched each path. This design makes those classes of bug **impossible to write** and moves the knowledge a developer needs from "every path that touches X" to "the class X".

**Goals, in order:**

1. Eliminate classes of bugs by construction.
2. Reduce cognitive complexity.
3. Preserve every external behavior except the ones recorded in the ledger.

**Unchanged by design:**

- The FGA read path: `fn_IsResourceAccessible`, `CheckAccessAsync`, `BuildFilterAsync`, `TraceResourceAccessAsync` and their SQL, which implement the published algorithm.
- The protocol codecs: JWT, SAML XML parsing and signature validation, CIMD fetching and SSRF defenses.
- The hosted HTML renderers, the headless view models, and the SQL schema scripts. The only schema changes are the additions the ledger lists.

## 2. The shape of the code

```
Adapters      endpoints · hosted renderers · headless view models · dashboard middleware · hosted services
              parse transport → call ONE process → map its outcome to HTML / JSON / redirect / status
                 │
Facades       public services hosts already call (SqlOSAuthService, SqlOSAdminService, SqlOSFgaAuthService, …)
              7.x signatures unless the ledger says otherwise; they only delegate
                 │
Processes     internal, one class per use case: load aggregates → call methods → ask policies → save once
                 │
Domain        aggregates · lifecycle parts · value objects · proofs · policies · domain events
              no HttpContext, no clock, no DbContext, no I/O
                 │
Infrastructure EF mapping and SQL scripts · admission stores · key custody · JWT / SAML / CIMD codecs · email / SMS
```

**Namespaces**

- **Entities keep their names and namespaces** (`SqlOS.AuthServer.Models`, `SqlOS.Fga.Models`, `SqlOS.Calendar.Models`, `SqlOS.Email.Models`). Hosts query them in their own `DbContext`, and moving them would break every host for no benefit.
- **New code:**
  - `SqlOS.Domain` for building blocks: events, lifecycle parts, value objects, proofs and errors
  - `SqlOS.<Module>.Policies`
  - `SqlOS.<Module>.Processes.<Area>` (for example `SqlOS.AuthServer.Processes.Identity`)
- New types are `internal` unless a public entity getter or a public facade has to expose them.

## 3. Building blocks

### 3.1 Aggregates and the domain-event buffer

An **aggregate** is a cluster of entities that must be consistent after every save, with one **root** that outside code talks to. Children are reached and changed only through the root. Aggregates reference other aggregates **by ID**. Existing navigation properties stay readable for host LINQ, but domain code never mutates another aggregate through a navigation.

Roots **compose** an event buffer rather than inheriting from a public base class. A public base type would add public API, and a buffer is a thing an aggregate *has*:

```csharp
// SqlOS.Domain (internal)
internal interface ISqlOSAggregate
{
    DomainEventBuffer Events { get; }
}

internal sealed class DomainEventBuffer
{
    private readonly List<ISqlOSDomainEvent> _pending = [];
    public void Raise(ISqlOSDomainEvent domainEvent) => _pending.Add(domainEvent);
    public IReadOnlyList<ISqlOSDomainEvent> Drain() { var copy = _pending.ToArray(); _pending.Clear(); return copy; }
}

// A public entity implements the internal interface explicitly, so hosts never see it.
public sealed class SqlOSUser : ISqlOSAggregate
{
    private readonly DomainEventBuffer _events = new();
    DomainEventBuffer ISqlOSAggregate.Events => _events;
    // …
}
```

### 3.2 Entities

- **State.** All persisted state has a **private setter**. The public getter stays, with the same name and type as 7.x.
- **Construction.** Entities are built by a constructor or `static Create…` factory that enforces creation invariants, plus a private parameterless constructor for EF.
- **Change.** Every state change is an **intention-revealing method** that validates, mutates and then raises a domain event. There is no generic `Update(dto)`.
- **Time.** Methods take `DateTime now` and never read a clock.
- **Collections.** The aggregate mutates them through a private backing list and exposes them as `IReadOnlyCollection<T>`.
- **IDs.** They come from `SqlOSIds.New("usr")`, which produces exactly the 7.x format: prefix, underscore, then the truncated GUID from `SqlOSCryptoService.GenerateId`.

### 3.3 Value objects

A value object is immutable, validated at construction and compared by value. If one exists, it is valid.

| Value object | Rules it owns |
|---|---|
| `EmailAddress` | display form plus **one canonical form** (trim, IDNA/ASCII domain, invariant case, reject control characters and markup); the only equality used for lookups is ordinal on the canonical form (#422) |
| `DomainName` | normalized DNS name; rejects wildcards, IP literals and reserved roots, as today |
| `PhoneNumber` | E.164 through libphonenumber, as today |
| `HashedSecret` | stores the hash only; `Matches(raw)` compares in constant time; never exposes the raw value |
| `RedirectUri` | the exact-match and loopback rules of `SqlOSRedirectUriPolicy` |
| `ScopeSet` | space-delimited set with today's ordering and normalization |
| `ResourceIndicator` | absolute URI rules of RFC 8707, as today |
| `TimeWindow` | effective-from and effective-to for FGA grants and service accounts |

They live in the existing string columns. Entities keep public `string` getters and add an `internal` value-object accessor, for example `internal EmailAddress Address => EmailAddress.FromStored(Email, NormalizedEmail);`.

### 3.4 Lifecycle parts (composition over inheritance)

The same lifecycle concepts repeat across entities:

| Field | Entities that have it |
|---|---|
| `ExpiresAt` | 13 |
| `RevokedAt` | 13 |
| `ConsumedAt` | 8 |
| `AttemptCount` | 3 |
| `VerifiedAt` | 3 |

Each concept is one **part**: an immutable `readonly record struct` holding the rule, with pure transitions that return the next state or throw a domain error:

```csharp
internal readonly record struct Consumption(DateTime? ConsumedAt)
{
    public bool IsConsumed => ConsumedAt is not null;
    public Consumption Consume(DateTime now) =>
        IsConsumed ? throw SqlOSDomainException.Of(SqlOSDomainError.AlreadyConsumed) : new(now);
}

internal readonly record struct Revocation(DateTime? RevokedAt, string? Reason)
{
    public bool IsRevoked => RevokedAt is not null;
    public Revocation Revoke(string reason, DateTime now) => IsRevoked ? this : new(now, reason); // idempotent
}

internal readonly record struct Expiry(DateTime ExpiresAt)
{
    public bool IsExpired(DateTime now) => now >= ExpiresAt;
    public void EnsureActive(DateTime now) { if (IsExpired(now)) throw SqlOSDomainException.Of(SqlOSDomainError.Expired); }
}
```

The catalog: `Expiry`, `Consumption`, `Revocation`, `AttemptBudget`, `Verification`, `Enablement` (active, disabled-at and reason) and `HashedSecret`.

**The state stays in the entity's existing flat columns.** The entity builds the part from its fields, calls the transition and applies the result:

```csharp
public sealed class SqlOSRefreshToken : ISqlOSAggregate
{
    public DateTime ExpiresAt { get; private set; }
    public DateTime? ConsumedAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public string? RevocationReason { get; private set; }

    internal Expiry Expiry => new(ExpiresAt);
    internal Revocation Revocation => new(RevokedAt, RevocationReason);

    internal void Consume(DateTime now)
    {
        Expiry.EnsureActive(now);
        ConsumedAt = new Consumption(ConsumedAt).Consume(now).ConsumedAt;
    }

    internal void Revoke(string reason, DateTime now)
        => (RevokedAt, RevocationReason) = Revocation.Revoke(reason, now);
}
```

**Why flat columns and not EF Core complex types** (decided):

- Hosts read and filter on these properties today (`user.IsActive`, `session.RevokedAt`), and complex types would change every one of those host-visible shapes.
- EF Core 9 complex types must be non-null.
- They can't take part in keys or indexes, and the existing filtered indexes use these columns.
- They complicate concurrency tokens and set-based updates, which `ConsumedAt` and `AttemptCount` rely on.

With flat columns, the rule has one home (the part) and the entities compose it, and the public shape, LINQ and schema stay unchanged.

**Why not inheritance:** the entities need different *mixes* of parts. A base-class hierarchy (`ExpiringEntity`, `ExpiringConsumableEntity`, `ExpiringRevocableEntity`…) explodes combinatorially. Composition lets each entity have exactly the parts it needs.

### 3.5 Proofs (capability types)

Some decisions must be made by exactly one piece of code, and every consumer must be unable to proceed without that decision. The decision is a **type**:

- `internal sealed`
- an `internal` constructor
- constructed only by its listed producers, enforced by an IL-scanning architecture test (§11)

Consumers take the proof as a parameter, never a boolean or a string.

| Proof | Means | Producers | Consumers |
|---|---|---|---|
| `LoginEvidence` | this user authenticated, by these methods, at this time | credential processes (password, OTP, magic link, TOTP, recovery), `SsoConnection.Accept`, `OidcConnection.Accept` | the authorization hub (`AuthorizationRequest.Authenticate`, direct login) |
| `OwnershipProof` | this person controls this mailbox or phone | completed challenges (email OTP, magic link, email verification, reset link, invitation), `OidcConnection.Accept` when the provider verified the email, `SsoConnection.Accept` **only inside the organization's verified domains** | `User.VerifyEmail`, `User.ClaimWithProof`, `User.LinkExternalIdentity` |
| `LoginDecision` | the hub allowed issuance for this request or client | `LoginPolicy` only | `AuthorizationRequest.IssueCode`, direct-login session creation, the token issuer |
| `GrantAuthority` | this caller may write this FGA grant | the operator (admin API or dashboard), host code (the documented FGA write API), a SCIM mapping **only within the connection's boundary** | `SqlOSFgaGrant.Create` |
| `DnsProof` | the DNS TXT record for this claim was observed | the DNS verifier adapter | `Organization.ConfirmDomain` |

### 3.6 Policies

A policy is a pure decision object, composed of small rules, with no I/O. Processes load whatever the policy needs and pass it in.

- **`LoginPolicy`** is an ordered list of `ILoginRule`s (`RuleResult Check(LoginContext)`). It returns a `LoginDecision` or a typed refusal. Its 8.0 rules reproduce 7.2.1 behavior:
  - first-party client for direct login
  - consent granted or first-party
  - resource allowed for the client (#429)
  - user active
  - organization and membership active
  - MFA satisfied

  **A future security feature is one new rule.** #409 SSO enforcement and #416 IP allowlists will each be one rule.
- **`MfaRequirementPolicy`**: from settings, the organization policy and the user override.
- **`PasswordPolicy`**: 8.0 keeps today's rule (not blank) and applies it on *every* path through `User.SetPassword`. #417 adds rules in 8.1.
- **`EmailOwnershipPolicy`**: which sources may issue an `OwnershipProof` for which addresses.
- **`GrantBoundaryPolicy`**: SCIM mapping boundaries, from #421.

### 3.7 Processes

- **One internal class per use case**, named as a verb phrase: `SignInWithPassword`, `VerifyEmailOtp`, `CompleteMagicLink`, `ExchangeAuthorizationCode`, `RotateRefreshToken`, `AcceptSamlAssertion`, `ProvisionScimUser`, `RevokeSessions`. There's no MediatR and no generic handler interface.
- **Shape:** `Task<TOutcome> ExecuteAsync(TCommand command, CancellationToken ct)`.
  - Commands are records.
  - Outcomes are **closed hierarchies**, an abstract record with sealed cases (for example `Succeeded`, `MfaRequired`, `OrganizationSelectionRequired`, `Refused(code)`), which adapters handle with an exhaustive `switch`.
- **Unit of work:** exactly one `SaveChangesAsync` per execution. A process that must persist a failure (a spent attempt or a failure audit) saves, then returns the failure outcome. A process never calls another process's save. It can call another process's *domain* logic, or share a small internal helper.
- **Surface-agnostic:** there's no `HttpContext`. Adapters pass a `SqlOSRequestContext` record (IP, user agent, request and correlation IDs, surface) used for audit and admission.
- **Time is read once per execution** from `TimeProvider`.
- **Where one behavior is shared, it lives in one process.** The hosted, headless and public-API surfaces for the same flow call the same process.

### 3.8 Adapters and facades

- **Endpoints** parse and validate transport, call one process, and map the outcome. They contain no business rules and **never touch the DbContext**.
- **Renderers and view models are unchanged,** and the behavior lock proves it.
- **Public facades keep their 7.x signatures and delegate.** Public services the inventory classifies as *Incidental* may become `internal` in layer 5, with a ledger entry and a migration note.

## 4. Aggregate map

"Infrastructure" rows are persistence the domain uses through an adapter, not aggregates. "Log" rows are append-only.

### Identity

| Root | Owns | Key invariants |
|---|---|---|
| `SqlOSUser` | `SqlOSUserEmail`, `SqlOSUserPhoneNumber`, `SqlOSCredential`, `SqlOSUserAuthenticator`, `SqlOSRecoveryCode`, `SqlOSExternalIdentity`, `SqlOSUserMfaPolicyOverride` | See the rules below. |
| `SqlOSEmailOtpChallenge`, `SqlOSPhoneOtpChallenge` | — | Built from `Expiry`, `Consumption`, `AttemptBudget` and `HashedSecret`, plus the **stored** recipient. Codes go only to the stored recipient. Attempts can't be lost under concurrency: an email-code attempt is spent with one conditional update before the code is compared (#424, §15 Amendment 2). |
| `SqlOSTemporaryToken` | — | Issued and consumed only through **typed token kinds** (`TemporaryTokenKind<TPayload>`), one per purpose, carrying the payload type, lifetime, single use and bindings (§15 Amendment 1). Built from `Expiry`, `Consumption` and `HashedSecret`. Payload JSON is unchanged. |
| Infrastructure | password-login and MFA-attempt buckets and reservations | Behind one internal admission gate. The algorithms are unchanged. |

**`SqlOSUser` rules:**

- There is one primary email.
- An email becomes verified **only** with an `OwnershipProof`.
- The first proof on an unverified address **claims** it. A claim revokes every credential, authenticator, recovery code, phone and external identity attached before it, except the presented credential. The claim process also revokes consent grants and calendar connections, which are other aggregates.
- A password is set only through `SetPassword(secret, PasswordPolicy)`.
- The user can be deactivated and reactivated.

### Organizations

| Root | Owns | Key invariants |
|---|---|---|
| `SqlOSOrganization` | `SqlOSOrganizationDomain` (a domain claim), `SqlOSOrganizationMfaPolicy` | Domain claims move pending → verified (only with a `DnsProof`) → revoked. The organization has `Enablement`. |
| `SqlOSMembership` | — | Its own aggregate, so a user in many organizations doesn't lock them together. Has a role and `Enablement`. |
| `SqlOSInvitation` | — | `HashedSecret`, `Expiry` and `Revocation`. Acceptance is bound to the invited email. Accepting it creates a `Membership`. |
| `SqlOSApplicationAssignment` | — | Access of a principal or organization to a client application, with `Revocation`. |

### Federation

| Root | Owns | Key invariants |
|---|---|---|
| `SqlOSSsoConnection` (SAML) | — | `Accept(validatedAssertion, request)`: the connection's organization is the only organization (#418). Proof only inside verified domains (#420). JIT per settings. Produces `LoginEvidence`. |
| `SqlOSOidcConnection` | — | Normalized configuration with write-only secrets. `Accept(validatedIdentity)` produces `LoginEvidence`, and an `OwnershipProof` only when the provider verified the email (#423). |
| `SqlOSScimConnection` | `SqlOSScimGroupMapping`, with the connection's grant boundary | Token as a `HashedSecret` with rotation and `Enablement`. Mappings grant only through `GrantAuthority`, inside the boundary (#421). |
| `SqlOSScimExternalId` | — | The link from a SCIM external ID to a user or group, with ownership rules (#420). |
| `SqlOSSsoPortalSession` | — | Single-use link token, idle and absolute `Expiry`, `Revocation`. |
| Infrastructure | `SqlOSSamlReplay`, `SqlOSScimOperationCommit` | Replay cache and idempotency. |
| Log | `SqlOSScimManagedGrant`, `SqlOSScimSyncEvent` | Managed grants are the SCIM mapping's record of grants it wrote. |

### OAuth / OpenID Connect

| Root | Owns | Key invariants |
|---|---|---|
| `SqlOSClientApplication` | `SqlOSClientCredential` | An explicit trust kind (first-party, operator-registered, DCR, CIMD), redirect URIs, audiences, scopes and grant types. DCR and CIMD are factories sharing one validation path. `Enablement`, plus emergency disable. |
| `SqlOSAuthorizationRequest` | — | A **state machine**: pending → authenticated → organization selected → MFA satisfied → consented → code issued, plus denied and expired. The binding (client, redirect, resource, PKCE, nonce, state, prompt, the connection and its organization) is fixed at creation, and there are no setters. Has a concurrency token. |
| `SqlOSAuthorizationCode` | — | `HashedSecret`, `Expiry`, `Consumption` and the binding. `Redeem(client, redirect, verifier, now)`. |
| `SqlOSConsentGrant` | — | User × client × scopes × resource, plus the metadata fingerprint. `Revocation`. `Covers(request)`. |
| `SqlOSDeviceAuthorization` | — | A state machine: pending → approved, denied or expired → redeemed. Bound to its client. |
| `SqlOSSession` | `SqlOSRefreshToken` family | `Rotate` with the grace window and reuse detection, which revokes the family. **Organization lineage** (#427). `Revocation`. |
| `SqlOSIssuerSessionFamily` | — | `IsUsable(now)`. **Bound to one user**, so a different user's credential sign-in starts a new family (#437). `Revocation`. |
| `SqlOSSigningKey` | — | Active → retiring → retired. Custody goes through an adapter. |
| Configuration | `SqlOSScopeDisplayName` | Code-owned or dashboard-owned. |

### Configuration, audit, calendar and email

| Root | Owns | Notes |
|---|---|---|
| `SqlOSSettings`, `SqlOSMfaSettings`, `SqlOSAuthPageSettings` | — | Configuration aggregates with code-owned versus dashboard-owned semantics (`docs/CONFIGURATION_OWNERSHIP.md`). |
| `SqlOSAuditEvent` | — | **Append-only projection** of domain events and failure records. Never mutated. |
| `SqlOSCalendarConnection` | `SqlOSCalendarSyncState` | Provider tokens protected as today; `Revocation`. Events (`SqlOSCalendarEvent`) are their own rows keyed by connection. |
| `SqlOSEmailTemplate` | — | Configuration; `Enablement`. |
| `SqlOSEmailDelivery` | — | Log with status transitions. |

### FGA write model

The read path is unchanged.

| Root | Owns | Key invariants |
|---|---|---|
| `SqlOSFgaResourceType` | `SqlOSFgaPermission` | Definitions. |
| `SqlOSFgaRole` | `SqlOSFgaRolePermission` | A permission's resource type is compatible with the role's use. |
| `SqlOSFgaResource` | — (a tree node) | `CreateChild` respects the depth limit. `MoveTo` does a bounded ancestor walk and forbids cycles. `Deactivate` keeps today's effect. |
| `SqlOSFgaGrant` | — | Created **only** through `SqlOSFgaGrant.Create(subject, role, resource, window, GrantAuthority)`. |
| `SqlOSFgaSubject` | `SqlOSFgaUser`, `SqlOSFgaAgent`, `SqlOSFgaServiceAccount` (expiry), `SqlOSFgaUserGroup` + memberships | The kind is fixed at creation. Groups can't gain cross-tenant members through tenant-controlled authorities. |

## 5. Transactions and concurrency

- One save per process execution (§3.7).
- **Contested state uses concurrency tokens on existing columns where they already capture the contention:**
  - `ConsumedAt` on codes, tokens and challenges
  - refresh-token rotation state

  Email-code attempts are contested too, but a token would only detect a lost update after the code was compared. An attempt is spent with one conditional update (`AttemptCount < MaxAttempts`) before the comparison instead, outside any transaction the caller could roll back (#424, §15 Amendment 2).

  Add a row version (SQL Server `rowversion`, PostgreSQL `xmin`) only where no existing column works, for example the `AuthorizationRequest` state. Each addition is a ledgered schema change covered by the upgrade gate.
- **Cross-aggregate uniqueness stays a database constraint**: the canonical email, active domain claims and the SAML entity ID. Processes translate violations into the 7.x errors.
- **Bulk changes** (for example revoking every session of an organization) may use a set-based update when a named domain service owns it and raises the same events as the one-by-one path, so the audit output is identical.

## 6. Domain events and audit

- Events are past-tense records in `SqlOS.Domain.Events` (`EmailClaimed`, `SessionRevoked`, `ScimGrantRejected`, …).
- **One projection maps events to `SqlOSAuditEvent` rows**, reproducing the 7.2.1 row for each action exactly: event type string, source, actor, targets and metadata shape. The behavior lock enforces this.
  - Layers 2 to 4 project **only actions audited in 7.2.1**.
  - Layer 5 adds the missing actions from #415, each ledgered.
- **Failure records** (no state change, for example a failed password) go through an `IAuditRecorder` in the same unit of work and the same projection.
- **Pipeline:**
  - A `SaveChangesInterceptor` collects events from tracked `ISqlOSAggregate` entries during `SavingChanges`, repeating until no new events appear, and adds the audit rows **in the same transaction** as the change.
  - It dispatches internal post-commit handlers in `SavedChanges`. Public handlers come later with #286.
  - `AddSqlOS<TContext>` registers the interceptor with EF Core 9's `services.ConfigureDbContext<TContext>((sp, options) => …)`, and `SqlOSDbContext` adds it too. Registration is idempotent. Hosts change nothing.
- **Request context** (IP, user agent, request and correlation IDs) comes from a scoped `SqlOSRequestContext`, set by the adapter.

## 7. Time

- `TimeProvider` is registered with `TryAddSingleton(TimeProvider.System)`, so hosts may replace it (an additive public change, ledgered).
- Processes read the time once per execution, and domain code takes `now`.
- **UTC convention:** every `DateTime` read from the database has `Kind = Utc` (a model-wide value converter), which fixes #325, ledgered.

## 8. Errors

- **Invariant violations** throw `SqlOSDomainException` carrying a stable `SqlOSDomainError` code. It's internal.
- **Expected results** such as a wrong code or a refusal are outcome cases, not exceptions.
- **Adapters map both to the exact 7.x public messages and status codes** through `SqlOSPublicAuthErrorMapper` and the existing error types. No new public error string appears without a ledger entry.

## 9. EF mapping conventions

- **Setters and construction.** Setters are private, EF uses them, and a private parameterless constructor exists for EF.
- **Collections.** Aggregate collections use backing fields: `builder.Navigation(x => x.Emails).UsePropertyAccessMode(PropertyAccessMode.Field)`.
- **Temporary tokens.** `SqlOSTemporaryToken` keeps one mapping. Purposes are typed token kinds, not table-per-hierarchy subtypes (§15).
- **Schema.** No change except the ledgered additions: both providers, the upgrade gate, and the schema approval updated in the same PR.
- **UTC.** A model-wide UTC converter for `DateTime` and `DateTime?`.

## 10. Public API policy

`docs/architecture/public-api-inventory.md` classifies every public type:

- **Host API:** documented or used by examples. These keep source compatibility for reads. Writes move to methods and facades, **with the ledger explaining each change and the upgrade guide showing before and after code.**
- **Extension point:** interfaces, base types and options hosts implement or configure. Unchanged unless a ledger entry justifies the change.
- **Incidental:** public, but neither documented nor used by examples. These become `internal` in layer 5 when that improves maintainability. Each gets a ledger entry.

**Rules for any change:**

- A public API change must meaningfully improve developer experience or maintainability. The maintainer's rule is "unchanged unless justified".
- It is recorded in the ledger, and it's visible in the public API approval.
- The test host's probes (the behavior lock) are the source-compatibility canary. A probe that stops compiling is a public API change.

## 11. Architecture tests

These live in `tests/SqlOS.Tests/Architecture`. Tests that start from an allowlist may only shrink it. By the end of layer 5, every allowlist is empty.

1. **No public setters on entities.** Uses reflection on the entity namespaces.
2. **Only the owning entity writes lifecycle columns.** Mostly implied by private setters, and asserted by an IL scan of setter call sites.
3. **Endpoints, renderers and middleware never touch the database.** An IL scan shows that types in `*.Endpoints`, `*.Dashboard` (middleware) and the renderers don't reference `DbContext`, `ISqlOSAuthServerDbContext` or `ISqlOSFgaDbContext`.
4. **Only processes save.** An IL scan of `SaveChangesAsync` call sites, allowlisted during migration.
5. **Proofs are constructed only by their producers.** An IL scan of `newobj` for each proof type (§3.5).
6. **No clock reads in domain or process code.** No `DateTime.UtcNow`, `DateTime.Now`, `DateTimeOffset.UtcNow` or `DateTimeOffset.Now`.
7. **Domain and process code has no HTTP dependency.** No `HttpContext` or `Microsoft.AspNetCore.Http` in those namespaces.

Use `System.Reflection.Metadata` or `Mono.Cecil` (a test-only dependency) for the IL scans.

## 12. Testing strategy

- **Domain unit tests**, with no database, for every part, value object, policy, proof rule and aggregate transition. They're fast and exhaustive.
- **Process tests** against real SQL, for flows where persistence, concurrency or transactions matter. They include concurrency tests for contested state.
- **The behavior lock** (`tests/SqlOS.BehaviorLock`) is the oracle for external behavior, run against the 7.2.1 package and against source.
- **Existing tests keep their assertions.** Service-level unit tests move to domain and process tests without losing an assertion. Integration, example, conformance and e2e tests stay green.

## 13. Migration plan (the 8.0.0 stack)

Each layer keeps every gate green, and every external difference is ledgered.

| Layer | Issue | Moves into the model |
|---|---|---|
| 1 | #435 | Nothing. It builds the behavior lock, the public API, schema and upgrade gates, the ledger and the metrics. |
| 2 | #436 | This record and the `AGENTS.md` rules; the building blocks; the event and audit pipeline; `TimeProvider`; the UTC convention (#325); the architecture tests. Identity: `User`, challenges, temporary tokens, admission (#424), `OwnershipProof`, and one process per credential flow, used by the hosted, headless and public surfaces. They produce `LoginEvidence` for a temporary hub adapter. |
| 3 | #437 | The hub: `Client` (with DCR and CIMD factories), `AuthorizationRequest`, `LoginPolicy` → `LoginDecision`, codes, consent (#429), `DeviceAuthorization`, `Session` and its refresh families (#427), `IssuerSession` (one user per family), `SigningKey`, the token issuer, and revocation. The layer 2 adapter is removed. |
| 4 | #438 | `Organization`, domain claims, `Membership`, `Invitation`, `SsoConnection`, `OidcConnection`, `ScimConnection` and its links, portal sessions, and the FGA write model with `GrantAuthority`. |
| 5 | #439 | Configuration, email, calendar, audit logs and dashboard sessions; audit completeness (#415); the public API finalization; allowlists to zero; removing legacy code; the security re-audit; docs, the upgrade guide, the architecture guide and the release article. |

## 14. Decisions and rejected alternatives

| Decision | Rejected alternative | Why |
|---|---|---|
| Lifecycle parts are value objects over flat columns | EF Core complex types | §3.4: they'd change host-visible shapes, can't be null, can't be used in indexes or keys, and complicate concurrency tokens |
| Composed event buffer plus an internal interface | A public `AggregateRoot` base class | No new public API. An aggregate *has* a buffer; it isn't a buffer. |
| `DbContext` plus small query helpers | Repository and unit-of-work wrappers | EF's `DbContext` already is the unit of work. Wrappers would add indirection without adding safety. |
| Plain process classes | MediatR or CQRS | Indirection and ceremony without a benefit at this size. Processes are ordinary classes injected where they're used. |
| Outcome hierarchies plus domain exceptions | Result types everywhere, or exceptions everywhere | Expected branches are data the adapter must handle exhaustively. Broken invariants are exceptional. |
| Proof types checked by an architecture test | Booleans and conventions | A security decision you can't forge by construction. |
| Entities keep their names and namespaces | Moving them to a new `Domain` namespace | Host source compatibility. |
| FGA read path untouched | Modeling reads as objects | Performance and the published algorithm. The write side gets the model. |
| Public domain events deferred to #286 (9.1.0) | Public events in 8.0 | Keep 8.0 focused. The internal pipeline makes #286 small. |

## 15. Amendments

### Amendment 1: after the foundation slice (layer 2, T2-A)

| Topic | Amendment | Why |
|---|---|---|
| Temporary tokens (§4, §9) | `SqlOSTemporaryToken` uses **typed token kinds** instead of table-per-hierarchy subtypes. A `TemporaryTokenKind<TPayload>` value carries each purpose's rules: the purpose string, the payload type, the lifetime, single use, and bindings (client, request, organization, issuer session). The entity issues and consumes only through a kind, and payload JSON stays byte-identical. | About 15 purposes are created in 8 services, including layer 4's OIDC browser state. EF table-per-hierarchy needs one discriminator value per type, and a row with an unmapped purpose fails to load. Kinds are composition, change no public shape, and have no discriminator hazard. |
| `RedirectUri` (§3.3) | The value object **owns** the redirect rules, and `SqlOSRedirectUriPolicy` delegates to it. | The domain must not depend on AuthServer services. |
| Email canonical form (§3.3) | `EmailAddress` rejects invisible control and format characters, but still accepts the visible ASCII that 7.2.1 accepted. Rejecting markup characters would be a ledgered behavior change (#425 tracks it). | Moving the rules must not change them. |
| Errors (§8) | `SqlOSDomainException` derives from `InvalidOperationException`, so existing catch blocks keep working. Adapters must map it explicitly, or its type name would leak into the public error event. | Compatibility while layers migrate. |
| Domain events (§6) | **Every event type must have a registered projection**, or the save fails before anything is written. Events carry a process-wide sequence number, so one save orders events across aggregates. **Post-commit handlers run only after the real commit**: the save's own transaction, an ambient EF transaction, or a `TransactionScope`. Pending work is keyed by EF's transaction object, and a rolled-back or failed commit runs nothing. Handlers get a fresh DI scope carrying the saving scope's request context. A handler that throws is logged and the rest still run, because the change is already committed. | Correctness under pooled connections (Npgsql reuses transaction objects), `TransactionScope` and retried commits. Each bug was proven by a failing test before the fix. |
| Time (§7) | `TimeProvider.System` is registered, but ASP.NET Core 9's `AddAuthentication`, which `AddSqlOS` calls, already registers it. That's no external change, so there's no ledger entry. | Accuracy. |
| Architecture tests (§11) | Mono.Cecil (test-only) scans the compiled IL. Allowlists are sorted, checked-in files. Growth is capped by a committed `high-water-marks.txt`. A stale entry fails. Self-tests prove that each rule fires. | Rules hold in CI without a deep clone of main. |
| Behavior lock (§12) | Layer 1's approvals are **frozen as the 7.2.1 baseline set**. Package runs compare every scenario against the baseline, and source runs compare against the current approvals. A gate requires the difference between the baseline and current sets to be exactly the files the ledger names. | The before/after proof keeps full strength as intended changes accumulate. |

### Amendment 2: after the challenges slice (layer 2, T2-B)

| Topic | Amendment | Why |
|---|---|---|
| OTP attempts (§4, §5) | `AttemptCount` is **not** a concurrency token. The attempt ledger spends an email-code attempt with one conditional update (`AttemptCount < MaxAttempts`, open and unexpired) **before** the code is compared, and only that update produces the `EmailOtpAttemptReservation` proof the challenge's `RegisterAttempt` requires. The wrong code that spent the last attempt invalidates the challenge with a second conditional update, exactly once. Both expressions are the challenge's own (`AtomicAttempts`), and neither goes through the change tracker. Sign-in verification refuses to run inside a database or ambient transaction, whose rollback would erase the attempt. Sign-up verification still runs inside the sign-up transaction until the 7.2.2 change that moves it out (#449, PR #460) is carried forward. | A token detects a lost update only at save time, after every concurrent guess has compared its code, so parallel guesses still get more comparisons than the budget. Reserving first bounds comparisons by the budget, and it is the 7.2.2 fix's semantics, so carrying PR #460 forward reconciles trivially. |
| Failure records | A process that must persist a failure record before it throws saves inside an existing save site; a refusal that needs a database read is returned by a query (for example the sign-up refusal for an address an account already owns) and recorded by the caller. | The save-call-site allowlist only shrinks until processes move into `*.Processes` namespaces (T2-D). |
| Test contexts | The hand-built test contexts (`TestSqlOSInMemoryDbContext`, `TestSqlOSDbContext`) attach the domain-events interceptor in their constructors, as `SqlOSDbContext` does. | Services that tests build by hand must write the audit rows of their events, as every host context does through `AddSqlOS` or `SqlOSDbContext`. |
