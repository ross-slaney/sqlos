# SqlOS behavior lock

The behavior lock records what SqlOS 7.2.1 does, as seen from outside, so the 8.0.0 refactor ([#435](https://github.com/ross-slaney/sqlos/issues/435)) can rebuild the internals and prove nothing observable changed. Every scenario drives a real host over HTTP and approves a scrubbed, deterministic transcript. A pure refactor never changes an approved file. When one changes, the change is either a regression to fix or an intended change that needs an entry in the [behavior ledger](../../docs/architecture/8.0-behavior-ledger.md).

## What it locks

| Observation | Where it shows up |
| --- | --- |
| HTTP exchanges: method, path and query, request headers, cookies and body, status, response headers, `Set-Cookie`, body | every `## n. actor: caption` section of a transcript |
| Decoded JWTs: header, claims, lifetimes | `jwt {access-token#1}` blocks after the exchange that returned them |
| Outbound effects: email, SMS, HTTP to upstreams, DNS lookups | `~ email`, `~ sms`, `~ http`, `~ dns` lines under the exchange that caused them |
| Audit events, read back through the admin audit API | `## audit` sections |
| End state, read through dashboard and admin APIs | exchanges made with `t.ObserveStateAsync(...)` |
| Library APIs hosts call directly (FGA checks and filters, token validation, services) | exchanges with the `/__probe` routes of the host |
| Documents (SAML metadata, generated files) | `## document` sections |
| The public .NET API of `SqlOS.dll` | `PublicApi/SqlOS.verified.txt` |
| The bootstrapped schema and the EF Core mapping, per provider | `Schema/*.verified.txt` |
| The `@sqlos/headless` exported surface | `packages/headless/tests/__snapshots__/public-surface.snap.txt` |
| Data written by 7.2.1 keeps working after an upgrade | `Scenarios/Upgrade` (the upgrade gate) |

## Layout

| Path | What it is |
| --- | --- |
| `tests/SqlOS.BehaviorLock.Host` | The host under test: a minimal ASP.NET app with one profile per deployment model, fakes for every outbound seam, and the `/__probe` library surface. Builds against SqlOS as source (default) or as the released package. |
| `tests/SqlOS.BehaviorLock` | This suite (MSTest). Scenarios talk HTTP to the host in-process (TestServer); only probes reach library APIs. |
| `tests/SqlOS.BehaviorLock.UpgradeSeed` | A console app pinned to the released package that seeds the upgrade gate's database. |
| `Scenarios/<Area>/<Area>Scenarios.cs` | Scenarios, one class per area. |
| `Scenarios/<Area>/Approved/<Class>.<Method>.verified.txt` | Approved transcripts. A mismatch writes `.received.txt` beside them. |
| `Gates/` | Whole-surface gates: public API, schema, route coverage, dashboard script scan, ledger parsing, scrubber unit tests. |
| `Coverage/dashboard-routes.manifest` | Dashboard API routes that are string-routed by middleware, so endpoint enumeration cannot see them. |
| `Infrastructure/` | The harness: transcripts, scrubbing, approvals, databases, fakes, coverage. |

## Running

The suite needs Docker: `tests/SqlOS.IntegrationTests.AppHost` starts SQL Server or PostgreSQL through Aspire, and every scenario gets a fresh database.

```bash
scripts/behavior-lock.sh                                   # source build, SQL Server
SQLOS_TEST_PROVIDER=postgresql scripts/behavior-lock.sh    # source build, PostgreSQL
scripts/behavior-lock.sh --mode package                    # the released 7.2.1 package
scripts/behavior-lock.sh --filter "FullyQualifiedName~Scenarios.Saml"
scripts/behavior-lock.sh --no-build                        # reuse ./scripts/build.sh output (source mode only)
```

Or directly: `dotnet test tests/SqlOS.BehaviorLock -p:SqlOSUnderTest=package`. `-p:SqlOSBaselineVersion=7.2.1` picks the package version, and the upgrade seed takes `-p:SqlOSUpgradeFromVersion`.

| Variable | Effect |
| --- | --- |
| `SQLOS_TEST_PROVIDER` | `sqlserver` (default) or `postgresql`. |
| `BEHAVIOR_LOCK_ACCEPT=1` | Accepts every mismatch by writing the `.verified.txt` file. Ignored on build servers. |
| `BEHAVIOR_LOCK_REPEAT=N` | Runs each scenario N times on fresh hosts and fails unless every run renders the identical transcript. Use 2 before you approve a new scenario. |
| `BEHAVIOR_LOCK_DIFF=1` | Opens your configured diff tool on a mismatch. |
| `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true` | Required on CI runners, as for the integration tests. |

Package mode guards itself: it fails unless the loaded `SqlOS.dll` is byte for byte the NuGet package's, and source mode fails if it loaded the package. Switch modes with a build, not with `--no-build`.

## Writing a scenario

```csharp
[TestClass]
public sealed class HostedPasswordScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Password_sign_in_redeems_the_code_for_tokens()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        var request = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "password" });
        var page = t.Observe(await t.GetAsync(request.Url), "open the hosted password sign-in page");
        var login = t.Observe(
            await t.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "submit email and password");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(login.NextUrlParameter("code"))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("sign-in events");
        await t.ApproveAsync();
    }
}
```

1. **Pick the profile** that matches the deployment model you are locking (see [Profiles](#profiles)). Prefer an existing profile. `Transcript.StartAsync(profile, options => ...)` accepts scenario-specific `ConfigureSqlOS` and `ConfigureServices`; use them rarely, and only for the one knob the scenario is about.
2. **Name the scenario as a behavior**, in a sentence: `A_directory_provisions_a_user_at_a_verified_domain`. Put it in `Scenarios/<Area>/<Area>Scenarios.cs`; one class may hold many scenarios. Use `[Scenario]` instead of `[TestMethod]`.
3. **Arrange with `t.Setup`**, which is never recorded: `CreateUserAsync`, `CreateOrganizationAsync`, `AddMembershipAsync`, `VerifyDomainAsync`, `CreateSamlConnectionAsync`, `CreateScimConnectionAsync`, `SignInWithPasswordAsync`, `PublishDnsTxt`, `PublishClientMetadata`, `OperatorPostAsync`. Setup goes through the admin API (or probes when the profile has no operator access) and skips the audit events it causes. Never write to tables directly: setup must behave the same against the package and the source.
4. **Act with actors.** `t.Browser` (the default browser; `t.GetAsync`, `t.SubmitAsync`, and `t.PostFormAsync` use it), `t.Api` (an API client with no cookies), `t.Operator` (carries the profile's dashboard credential), and more with `t.NewBrowser("name")` and `t.NewClient("name")`. Browsers keep cookies and send `Origin` on unsafe methods. Per request, `options => options.Bearer(token)`, `.Header(...)`, `.WithoutCredentials()`, `.WithoutOrigin()`, `.WithOrigin(...)`, `.WithoutCookies()`, `.Cookie("name=value")`, `.FromAddress("198.51.100.7")`. `browser.SetCookie(name, value)` seeds a cookie from an earlier visit.
5. **Record every exchange.** Each request returns an `HttpExchange`; pass it to `t.Observe(exchange, "caption")` to lock it or `t.Discard(exchange)` when it is only a precondition. `ApproveAsync` fails if one is neither. Read responses with `JsonString("data.0.id")`, `Location`, `NextUrl` and `NextUrlParameter("code")` (they follow the meta-refresh interstitial too), `Form("/action")` with `.With(...)` and `.Without(...)`, `SetCookieValue(name)`, and `Header(name)`.
6. **Observe side effects and state.** `t.ObserveAuditAsync("caption")` records the audit events written since the last audit observation. `t.ObserveStateAsync(route, "caption")` records a dashboard or admin read. `t.ObserveDocument(caption, text)` records a document; `t.Note(text)` adds a line for the reader. Effects (email, SMS, HTTP, DNS) attach to the exchange that caused them automatically; `t.ObserveUnattributedEffects(caption)` records ones no exchange caused. `t.LatestEmailTo(email)` and `t.LatestSmsCodeTo(phone)` read what the fakes captured.
7. **Call library APIs through probes.** Scenarios only talk HTTP. For an API hosts call in-process, use or add a route in `tests/SqlOS.BehaviorLock.Host/Probes/ProbeEndpoints.cs` that calls one documented member and returns its result as JSON. Probes compile against both the package and the source, which makes them, with `SourceCompatibilityCanary.cs`, the source-compatibility canary.
8. **Approve.** End with `await t.ApproveAsync()`. Run the scenario with `BEHAVIOR_LOCK_REPEAT=2 BEHAVIOR_LOCK_ACCEPT=1`, then read the whole `.verified.txt`: it is the behavior you are locking, so check it says what the product should do, and say so in the caption when it does not (see known defects below).

### Fakes

Nothing leaves the process. The host replaces every outbound seam (`BehaviorLockFakes`): email senders, the SMS channel, the DNS TXT verifier, and `IHttpClientFactory`, which answers social and custom OIDC upstreams (`FakeOidcUpstream`: authorization codes such as `success:{email}`), calendar providers (`FakeCalendarUpstream`), and client ID metadata documents (`t.Setup.PublishClientMetadata`). The suite's SAML identity provider (`TestSamlIdentityProvider`) signs valid and adversarial responses. Per-request effects are attributed to the exchange by the `X-BehaviorLock-Exchange` header the actors send.

### Coverage tags

`[Covers("METHOD /route/template")]` names a route the scenario exercises, exactly as the endpoint's route pattern (`[Covers("GET /sqlos/admin/auth/api/users/{userId}")]`) or as a line of `Coverage/dashboard-routes.manifest` for string-routed dashboard APIs. A transcript fails if a declared route was not hit by an observed exchange, and `Every_covers_declaration_names_a_known_route` fails on a tag that names no route.

`Route_coverage_report` inventories every route of every profile (endpoint data sources plus the dashboard manifest) and writes `TestResults/BehaviorLock/route-coverage/route-coverage.md` and `uncovered-routes.txt`, in a stable order, as the worklist for catalog authors. `Every_route_in_every_profile_has_a_scenario` turns that list into a failing gate; it is ignored until the scenario catalog is complete. `Dashboard_scripts_only_call_known_routes` scans the dashboard JavaScript for API calls and fails on a path that no route or manifest line answers, so a new string-routed dashboard API cannot go unnoticed.

## Reading a transcript

```
## 2. browser: submit email and password
> POST /sqlos/auth/login/password
> Content-Type: application/x-www-form-urlencoded
> Cookie: sqlos_auth_page_csrf_{hex#1}={cookie#1}
> Origin: https://sqlos.example.test
>   __RequestVerificationToken={csrf#1}
>   requestId={req#1}
>   email={email:alice}
>   password={password:alice}
< 200 OK
< Content-Type: text/html; charset=utf-8
< Set-Cookie: sqlos_auth_page={cookie#2}; expires=+7d; path=/; secure; samesite=lax; httponly
    <!DOCTYPE html>
    ...
```

An effect sits under the exchange that caused it:

```
  ~ email (transactional) to {email:alice}
      subject: Your Behavior Lock sign-in code
      text:
        Your Behavior Lock sign-in code is {otp#1}. It expires in 10 minute(s).
```

`>` lines are the request, `<` lines the response status and headers (sorted; `Date`, `Server`, `Content-Length`, `Transfer-Encoding`, `Connection`, `Keep-Alive`, `ETag`, `Last-Modified`, and tracing headers are dropped). Bodies are indented: JSON is canonical (members sorted, arrays in order), forms one field per line, HTML normalized with AngleSharp (inline `<style>` and `<script>` become content digests such as `{script-sha256:…}`, data URIs become `{data-uri:type}`).

## Scrubbing

Transcripts must be byte-identical across runs, machines, providers, and the package and source builds. The scrubber replaces every per-run value with a placeholder, numbered in order of first appearance, and keeps everything else, so a changed format or a new field still shows up.

- **Named values** read as `{kind:name}`: `t.Unique.Email("alice")` is `{email:alice}` (its upper-case normalized form `{email:ALICE}`), `t.Unique.Password`, `Domain`, and `Slug` likewise. Register your own with `t.Scrub(value, kind, name)`.
- **Role values** read as `{kind#n}`: a JSON property, form field, query parameter, hidden input, or JWT claim whose name is in `ValueRoles` (`code`, `state`, `refresh_token`, `mfaToken`, `tokenPrefix`, ...) registers its value, in URL, form, and HTML encodings too. Add a role there when a new secret or handle appears. `t.Scrub(value, kind)` registers one by hand, for example a code shown only as page text.
- **Detected patterns**: JWTs (`{access-token#1}`, keyed by identity so tokens minted in different seconds for the same session and claims share a placeholder), SqlOS IDs by prefix (`usr_…` is `{usr#1}`), GUIDs, timestamps by format class (`{datetime:utc-z}`, `{datetime:unspecified}`, `{datetime:offset}`, so a change from `Z` to no suffix is visible), RFC 1123 dates, hex digests, and high-entropy tokens.
- **Clock values**: epoch seconds in JSON are `{epoch}`; JWT lifetimes are exact (`exp-iat=10m`); remaining-seconds fields such as `expires_in` are rounded (`~600`); cookie expiry is relative (`expires=+7d`, `max-age=900`, `{unix-epoch}` for deletion).
- **Unordered data**: a JSON array SqlOS builds without an order (listed in `CanonicalJson.UnorderedArrayFields`, with the reason) is sorted before rendering.

Never scrub behavior away. Before adding a rule, find where the nondeterminism comes from; if SqlOS itself is nondeterministic (an unordered query, a clock read twice), lock it with the narrowest rule and a comment naming the source.

## Approvals and the ledger

1. A mismatch fails the scenario with a compact diff and writes `<name>.received.txt` beside `<name>.verified.txt`.
2. Read the diff. If the change is a regression, fix the code, not the approval.
3. If the change is intended, accept it locally with `BEHAVIOR_LOCK_ACCEPT=1` (CI never accepts) and commit the updated `.verified.txt`.
4. **Modifying or deleting an approved file needs a ledger entry in the same pull request** (`docs/architecture/8.0-behavior-ledger.md`): its ID, the approved files it justifies, the category (defect fixed, DX improvement, or maintainability improvement), the justification, and the issue. Adding a new approved file (new coverage) needs no entry, and neither does renaming one without changing its content. `scripts/check-behavior-ledger.sh` enforces this on every pull request; run it locally against `origin/main` before you push.
5. The baseline job runs the suite against the released package. It skips approved files the ledger lists, because they describe intended 8.0.0 behavior, and checks everything else.

The same rules cover `PublicApi/SqlOS.verified.txt` (regenerate with `BEHAVIOR_LOCK_ACCEPT=1` and a filter on `PublicApiGateTests`), the schema approvals (`SchemaGateTests`, one pair per provider), and the headless snapshot (`npx vitest run -u` in `packages/headless`).

## The upgrade gate

`Scenarios/Upgrade` proves that data written by the released package keeps working. `SeededDatabase.CreateAsync()` creates a database and runs `tests/SqlOS.BehaviorLock.UpgradeSeed` (built against SqlOS 7.2.1, in its own process) on it. The seed hosts the `upgrade` profile and builds the dataset through the public surface: users with passwords and TOTP, an organization with memberships and a verified domain, SAML and SCIM connections (grant boundary, group mapping, provisioned group), first-party, third-party, DCR, and CIMD clients with consent, sessions and refresh tokens, a pending device authorization, an FGA tree with grants, a calendar connection, and the audit history those write. It persists the data-protection keys and writes an `UpgradeManifest` of per-run values. The scenario then starts the build under test on that database, names the manifest values in the transcript (`{usr:alice}`, `{refresh-token:portal}`), and locks what existing data does after the upgrade.

To extend it, add the data to `UpgradeSeeder` (through HTTP or probes, never SQL), carry what the gate needs in `UpgradeManifest`, and use it in the scenario. The seed compiles the host's sources against the package, so host code it uses must compile against 7.2.1 (`#if SQLOS_UNDER_TEST_PACKAGE` when an API differs).

## Profiles

Each profile is one documented deployment model, defined in `tests/SqlOS.BehaviorLock.Host/Profiles/HostProfiles.cs` with its exact options (`OptionsSummary`) and the docs it follows. All use the host's `behaviorlock` schema for application tables, the workspace authorization model, and the fakes. Unless noted, the dashboard is authorized by `Dashboard.AuthorizationCallback` checking the `X-BehaviorLock-Operator` header, which `t.Operator` sends.

| Profile | Deployment model |
| --- | --- |
| `hosted` | Single application, hosted AuthPage, every first factor (password, email OTP, magic link, SMS OTP), Google, Microsoft, GitHub, and a custom OIDC connection. |
| `headless` | Single application whose own UI drives sign-in through the headless API, native headless clients allowed. |
| `multi-app` | Standalone identity server (`ConfigureApplication`) with first-party, access-restricted, partner, confidential, native, CLI, and machine clients, and resource APIs validated with `AddJwtBearer` and `AddSqlOSJwt`. |
| `mcp` | Single application with a same-host MCP resource (`app.Mcp`) and the `SqlOS.Mcp` policy on the host's MCP route. |
| `dcr` | Dedicated authorization server with dynamic client registration (`EnableChatGptCompatibility`) and client ID metadata documents. |
| `dashboard-password` | Dashboard protected by `AuthMode = Password`; `t.Operator` signs in at start. |
| `dashboard-callback` | Dashboard authorized by `AuthorizationCallback` in Production. |
| `dashboard-dev` | Development environment, no callback: the dashboard is open. |
| `dashboard-off` | Production, no callback: the dashboard and admin APIs answer 404. Setup uses probes. |
| `enterprise` | SAML, SCIM at the default base path (`/sqlos/scim/v2`), and the SSO setup portal. |
| `enterprise-scim-path` | The same with `ScimBasePath = /scim/v2`, outside the dashboard prefix. |
| `modules` | Calendar, transactional email, and audit admin modules. |
| `oauth-only` | OAuth 2.0 authorization server with the OpenID Provider role disabled. |
| `upgrade` | Standalone identity server with every persisted feature in use and TOTP required; used by the upgrade gate. |
| `legacy-host` | Explicit wiring: `AddDbContext` plus `AddSqlOS<T>(options)`, a context implementing the SqlOS interfaces, a manual `MapAuthServer()`. |

`tests/SqlOS.BehaviorLock.Host` also runs as a Kestrel app for exploring a profile by hand: `dotnet run --project tests/SqlOS.BehaviorLock.Host -- --BehaviorLock:Profile=hosted --BehaviorLock:Provider=SqlServer --ConnectionStrings:BehaviorLock="..."`.

## Known defects and limits

Scenarios lock what 7.2.1 does, including defects; a caption says so when a transcript records one, and the fix arrives with a ledger entry. Recorded so far:

- With the default `ScimBasePath`, the dashboard middleware answers every unlisted path under `/sqlos`, so a directory client never reaches SCIM (`ScimUserScenarios.A_directory_client_cannot_reach_scim_under_the_dashboard_prefix`).
- SCIM creates FGA subjects with IDs of their own (`subj_…`, with `ExternalRef` set to the user ID), so an FGA check by user ID, as the FGA guides write it, does not see grants from SCIM group mappings unless the host provisioned that user's subject first with the same `ExternalRef` and organization (the upgrade gate records this).

Limits of the harness:

- The host runs on TestServer, so there is no TLS, HTTP/2, or Kestrel limit behavior; the client address is `203.0.113.10` unless a request sets `FromAddress`.
- `IHttpClientFactory` is replaced wholesale, so SqlOS's own outbound handlers (for example the CIMD fetch's address filtering) are not exercised; their policy checks before the request still are.
- The hourly signing-key rotation service and the calendar sync scheduler are off; scenarios trigger rotation and sync explicitly.
- Data protection is ephemeral per host except in the upgrade gate, which persists the key ring across the seed and the host.
