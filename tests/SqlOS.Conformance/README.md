# SqlOS OIDC conformance harness

Runs the [OpenID Foundation conformance suite](https://gitlab.com/openid/conformance-suite)
OIDCC certification plans against the SqlOS example OP
(`examples/SqlOS.SignInWithX.AppX` in conformance mode):

1. `oidcc-config-certification-test-plan`
2. `oidcc-basic-certification-test-plan[server_metadata=discovery][client_registration=static_client]`

Getting certified means these two plans pass; see the memory note
"Certified OP = conformance suite in CI" for the definition of done.

## Pinned suite version

`release-v5.2.3` (set via `SUITE_TAG`). The suite is cloned to
`/private/tmp/oidf-conformance-suite` by default (`SUITE_DIR` overrides) and is
never modified — all local adjustments live in
`docker-compose.conformance.yml` in this directory.

## Prerequisites

- Docker Desktop (macOS) or Docker Engine with the compose plugin
- .NET 9 SDK
- python3 (a venv with the suite's `scripts/requirements.txt` is created
  automatically inside the suite clone)
- Internet access on first run (clone + maven dependencies + docker images)

Java/maven are NOT required on the host: the suite jar is built with a
dockerized `maven:3-eclipse-temurin-21` (the suite requires Java 21;
`$HOME/.m2` is reused as cache).

## How to run

```sh
tests/SqlOS.Conformance/run-conformance.sh            # full run, tears down
tests/SqlOS.Conformance/run-conformance.sh --keep     # leave everything up
```

Exit code is `run-test-plan.py`'s: 0 only if every test passes or matches
`expected-failures.json` / `expected-skips.json` exactly (unused entries fail
too). Per-test exports (HTML logs) land in `export/`, the driver log in
`logs/run-test-plan.log`, the OP log in `logs/appx.log`. With `--keep` the
suite UI is at https://localhost.emobix.co.uk:8443 (devmode, no login).

What the script does:

1. Starts SQL Server 2022 in a container (`sqlos-conformance-sql`, host port
   1437) and waits for it.
2. Builds and starts App X on `0.0.0.0:5102` with
   `AppX__Conformance__Enabled=true` (seeds `conformance-client-one/-two`,
   the `conformance-user@x.test` user) and
   `AppX__PublicOrigin=https://sqlos-op` — the https front described below.
3. Clones/builds the conformance suite at the pinned tag if missing.
4. `docker compose -f docker-compose.conformance.yml up` (mongo + suite
   server + nginx ingress publishing 8443 + the `sqlos-op` TLS front for the
   OP).
5. Runs both plans through the suite's own `scripts/run-test-plan.py` with
   `--export-dir`, then tears everything down (unless `--keep`).

## Networking

Three parties must see each other:

- **Host -> suite API**: `run-test-plan.py` runs on the host and talks to
  `https://localhost.emobix.co.uk:8443`. That hostname publicly resolves to
  127.0.0.1, and nginx publishes 8443 -> works out of the box.
- **Suite -> OP**: the OIDCC *config* certification plan requires every
  advertised endpoint to be https (`CheckDiscEndpointAllEndpointsAreHttps`
  and friends fail on a plain-http issuer, even in devmode — verified
  empirically), so App X is fronted by the `sqlos-op` compose service: nginx
  with a self-signed cert for the in-network hostname `sqlos-op`, proxying to
  `host.docker.internal:5102` (`op-proxy/`). The OP's public origin, issuer
  and `discoveryUrl` are all `https://sqlos-op/...`, resolvable by the suite
  server and its in-JVM automation browser (HtmlUnit) via compose DNS. This
  mirrors the suite's own `docker-compose-localtest.yml`, whose sample OP is
  an https compose service with a self-signed cert; the suite's HTTP clients
  trust-all in devmode and HtmlUnit tolerates it. Docker Desktop resolves
  `host.docker.internal` natively; `extra_hosts: host-gateway` makes the same
  file work on Linux CI. For host-side debugging the proxy is also published
  as `https://localhost:5443` (`curl -k --connect-to sqlos-op:443:127.0.0.1:5443 https://sqlos-op/...`).
- **Suite -> its own callback**: after login the OP redirects the automation
  browser to `https://localhost.emobix.co.uk:8443/test/a/sqlos/callback`.
  Inside the `server` container 127.0.0.1 is NOT nginx, so the suite's
  `--fintechlabs.startredir=true` starts their `redir` helper, which forwards
  container-local `:8443` to the `nginx` service (the compose service MUST be
  named `nginx` — the target is hardcoded in the suite's `Application.java`).
  `extra_hosts` maps `localhost.emobix.co.uk` to 127.0.0.1 so the resolution
  does not depend on public DNS.

## Browser automation

App X's hosted login is a two-step flow, scripted in
`config/sqlos-basic.json`:

1. `/sqlos/auth/authorize?...` renders an email ("identify") form posting to
   `/sqlos/auth/login/identify` — fill `name=email`, click the submit button.
2. The identify response renders the password form (posting to
   `/sqlos/auth/login/password`) — fill `name=password`, click submit. Hidden
   `requestId`/`__RequestVerificationToken` fields ride along.
3. The OP redirects to the suite callback; the automation waits for
   `id=submission_complete`.

Hard-won details (each was an actual failure mode):

- **requestId-guarded selectors**: SqlOS renders its normal login form even
  on authorize *error* pages (e.g. unregistered redirect_uri, missing
  response_type — HTTP 400 with a `.callout.error` message but the form is
  still there). Without a guard the automation logs in through the error
  page and dead-ends at `/sqlos/auth/login?status=signed-in`. The login
  commands therefore target
  `//form[.//input[@name='requestId']]//...` — the hidden `requestId` input
  only exists when a real authorization request is being resumed.
- **Error page screenshot task**: error tests expect either an error
  redirect to the callback or an error page (whose screenshot placeholder
  must be satisfied). The third task waits for the `.callout.error` element
  and satisfies the placeholder via `update-image-placeholder-optional`,
  letting `oidcc-response-type-missing`, `oidcc-ensure-registered-redirect-uri`
  and `oidcc-ensure-request-object-with-redirect-uri` finish (result REVIEW —
  certification submission requires eyeballing those screenshots).
- **Anchored callback match**: the Verify Complete task must match
  `https://localhost.emobix.co.uk:8443/test/a/sqlos/callback*` — a leading
  `*/test/a/sqlos/callback*` glob also matches the *authorize* URL, because
  the redirect_uri appears in its query string.
- All tasks are task-level `"optional": true` and login commands are
  element-`"optional"`: silent-SSO re-runs jump straight to the callback,
  and error tests never leave the authorize page. The conformance clients
  are seeded first-party, so no consent page appears.

## Expected failures / skips

`expected-failures.json` / `expected-skips.json` are passed to
`run-test-plan.py`, which fails the run if an entry goes unused — every entry
below is therefore continuously re-verified. Justification, line by line:

**Expected warnings** (all SHOULD-level; none blocks certification):

- `oidcc-discovery-endpoint-verification` / `CheckForUnexpectedParametersInServerMetadata`:
  SqlOS advertises `resource_parameter_supported`, which is not a registered
  OAuth AS metadata name. Intentional SqlOS extension for resource
  indicators; revisit if OIDF registers an equivalent field.
- `oidcc-ensure-request-with-acr-values-succeeds` / `ValidateIdTokenACRClaimAgainstAcrValuesRequest`:
  SqlOS has no ACR concept, so requested `acr_values` yield no `acr` claim.
  Candidate follow-up alongside per-application claim policies (issue #121).
- `oidcc-codereuse-30seconds` / `EnsureHttpStatusCodeIs4xx`: tokens issued
  from a code replayed after 30 seconds are not proactively revoked
  (RFC 6749 §4.1.2 SHOULD). Authlete's certified deployment carries the same
  expected warning. Roadmap: revoke the session minted from a replayed code.
- `oidcc-claims-essential` / `EnsureUserInfoContainsName`: the OIDC `claims`
  request parameter is not supported (issue #121), so an essential `name`
  requested without the `profile` scope is not released.
- `oidcc-scope-profile` / `VerifyScopesReturnedInUserInfoClaims`: the user
  model stores `name`/`preferred_username`/`updated_at` but not the remaining
  optional profile claims (`given_name`, `family_name`, `picture`, …);
  issue #121 adds richer claim storage and release.

**Expected skips**:

- `oidcc-scope-address`, `oidcc-scope-phone`, `oidcc-scope-all`: the
  `address`/`phone` scopes are not advertised in `scopes_supported`, so the
  suite skips these modules by design.
- `oidcc-unsigned-request-object-supported-correctly-or-rejected-as-unsupported`:
  SqlOS advertises `request_parameter_supported: false` /
  `request_uri_parameter_supported: false` and answers `request`-bearing
  authorizations with `request_not_supported`; the module verifies the
  advertisement and skips, the spec-correct outcome for an OP without JAR.

## Findings and current state (suite release-v5.2.3)

The first baseline run (2026-08-21) found **four real protocol bugs** that
SqlOS's own 1,000+ unit/integration tests and the Auth.js federation example
had all missed — every internal test happened to use PKCE, `client_secret_basic`
or `none`, and GET:

1. **Token endpoint required PKCE for every code** — non-PKCE confidential
   exchanges always returned 400 `invalid_grant`. Fixed: PKCE is verified
   exactly when the code was issued with a challenge; presenting a verifier
   for a non-PKCE code fails closed (RFC 7636 §4.4.1).
2. **`/authorize` was GET-only** (OIDC Core 3.1.2.1 requires POST). Fixed:
   POST with form parameters shares the GET handler.
3. **`request`/`request_uri` were silently ignored** (OIDC Core 6.1). Fixed:
   explicit `request_not_supported`/`request_uri_not_supported` error
   redirects with state echo, and discovery advertises both parameters as
   unsupported.
4. **No `client_secret_post`** token-endpoint authentication. Fixed:
   registration-driven — clients registered with `client_secret_post`
   authenticate with body credentials; `client_secret_basic` clients still
   reject them; discovery advertises the method when such a client exists.

The suite also drove two smaller conformance fixes: `Cache-Control:
no-store`/`Pragma: no-cache` on every token response (RFC 6749 §5.1), and
scope-gated profile/email claims moved out of the ID token into UserInfo
(OIDC Core §5.4 — the suite warns on unrequested claims in the ID token).

**Current state — both plans green:**

| Plan | Modules | Outcome |
|------|---------|---------|
| `oidcc-config-certification-test-plan` | 2 | 0 failures; 1 expected warning (`resource_parameter_supported`) |
| `oidcc-basic-certification-test-plan[server_metadata=discovery][client_registration=static_client]` | 36 | 1,741 condition successes, 0 failures; 4 expected warnings; 4 expected skips; 3 REVIEW (error-page screenshots, reviewed at certification submission) |

`run-test-plan.py` exits 0; the expected files are exact (unused entries fail
the run), so any regression or new suite finding fails CI.

## CI

The `conformance` job in `.github/workflows/pull-request.yml` and `main.yml`
runs this harness on every pull request and push to main:

- The suite clone (with its maven `target/`) and `~/.m2` are cached, keyed by
  the hash of `run-conformance.sh` — bumping `SUITE_TAG` in the script
  invalidates the cache and rebuilds at the new tag.
- `SUITE_DIR` points into the runner temp directory; everything else is the
  same script a developer runs locally.
- Per-test exports and logs upload as the `conformance-results` artifact on
  every run, pass or fail.
