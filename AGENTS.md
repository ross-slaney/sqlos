# SqlOS contributor guidance

These instructions apply to the entire repository.

## Product shape

SqlOS is an OpenID Provider and an FGA engine. It is not an API gateway.

- Tokens are JWTs. Same-process hosts authenticate them with the `SqlOS` authentication scheme that `AddSqlOS` registers. Separate resource APIs may use `AddJwtBearer` against JWKS and accept revoke-at-`exp`.
- Apps lock routes with ASP.NET (`AddAuthentication` / `RequireAuthorization` / `[Authorize]`). Hosts that map Microsoft's MCP SDK call `RequireAuthorization("SqlOS.Mcp")` on `MapMcp`. SqlOS must not wrap `/api` or `/mcp`.
- `app.Api` and `app.Mcp` are resource identifiers and host topology (RFC 9728 PRM, token `aud`, CIMD). They do not wrap endpoints, they do not host the MCP SDK, and they do not infer which application routes return 401.
- Do not add `RequireSqlOSAccessToken`, surface `EndpointDataSource` wrappers, path-inferred middleware, or any other hide that decides the application's lock for it.
- Session lookup stays inside `ValidateAccessTokenAsync` (and therefore inside the `SqlOS` handler). That is not a reason to hide `[Authorize]` or to tell hosts they cannot use a JWT bearer scheme.

## Product control-plane parity

Administrative product capabilities should be designed as one domain model exposed through three control planes. Do not build separate policy or validation implementations for code, HTTP APIs, and the dashboard.

### Code-first configuration

- Provide strongly typed options and seeds when configuration should be reproducible in source control.
- Make startup reconciliation deterministic and idempotent.
- Track configuration ownership explicitly. Code-owned records may be reconciled authoritatively, but startup must not overwrite dashboard-owned records or silently change their ownership.
- Keep credentials out of committed configuration. Resolve protected values through the host application's existing configuration or secret mechanism and fail closed when required material is unavailable.

### Programmable administration

- Provide authenticated application services/SDKs and admin APIs for operations developers reasonably need to automate, such as creating connections, rotating credentials, previewing policies, triggering synchronization, and inspecting outcomes.
- Route programmatic operations through the same domain services, normalization, validation, authorization, tenancy, secret handling, and audit behavior used by every other control plane.
- Return stable, machine-readable results and typed failures without exposing stored secrets or internal security material.

### Dashboard workflow

- Treat the embedded dashboard as a first-class operator experience, not a later wrapper around incomplete APIs.
- Support the relevant setup, validation, testing, troubleshooting, rotation, disablement, audit history, ownership/source visibility, and copy-ready integration values for the capability.
- Make code-owned records observable and testable while clearly identifying fields that must be changed in source control.
- Keep secrets write-only or one-time reveal and never render protected credential material after creation.

### Definition of done

- Code-first, API-created, and dashboard-created configuration must produce equivalent runtime behavior when all three control planes apply.
- Add parity tests that exercise realistic paths through each applicable control plane and prove they share behavior rather than merely sharing data shapes.
- Document ownership and reconciliation semantics, especially how code-owned and dashboard-owned records coexist.
- Internal secure defaults do not need artificial configuration or dashboard switches. Apply this standard to product capabilities operators manage, not invisible protocol hardening such as token validation rules.

### Parity test checklist

- Use `ControlPlaneParityHarness` for administrative capabilities supported by code, service/API, and dashboard control planes.
- Arrange the same intent through the production seed, public administration service, and the exact dashboard HTTP route and payload.
- Compare canonical redacted projections; assert ownership differences explicitly and do not snapshot generated IDs, timestamps, tokens, encrypted values, or secret hashes.
- Exercise a real runtime boundary in addition to stored shape, plus invalid input, disable/re-enable, audit, and secret-reveal behavior where relevant.
- Add a dashboard JavaScript contract assertion for new routes or payloads. Use real SQL integration coverage when persistence, reconciliation, locking, or concurrency is material.
- State why a control plane is not applicable instead of inventing an operator switch for an internal security default.
