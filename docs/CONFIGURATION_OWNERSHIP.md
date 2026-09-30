# Configuration ownership and reconciliation

SqlOS configuration can come from code-first startup seeds, the authenticated administration API and dashboard, dynamic protocol registration, internal defaults, or an external authority. Persisted configuration records identify that owner so one control plane cannot silently overwrite another.

## Ownership rules

- `code`: reconciled from a stable startup seed. Dashboard and admin API views are read-only for controlled fields.
- `dashboard`: created and edited through the dashboard or administration API.
- `dynamic`: created by a protocol such as DCR or CIMD. Its protocol lifecycle remains authoritative.
- `system`: an internal default that a first explicit operator edit may claim.
- `external`: reserved for configurations whose source of truth is outside SqlOS.

OAuth clients use their client ID as the seed key. SCIM connections use the required key passed to `SeedScimConnection`. Custom OIDC connections should use `SeedOidcConnection(key, configure)`; provider helpers assign deterministic keys automatically. Global MFA uses `mfa:default`. AuthPage branding uses `auth-page:default`. Email branding uses `auth-email:default`. Those two surfaces share one `SqlOSAuthPageSettings` row but are owned independently.

Reconciliation stores a SHA-256 fingerprint of canonical non-secret configuration and the last successful reconciliation time. Secret values are never part of the fingerprint or diagnostics. Re-running an unchanged seed is idempotent. A seed may update only a record with the same `code` owner and stable source key; a dashboard-owned collision fails startup with a clear conflict instead of adopting or overwriting it.

## Operator controls

Removing a seed marks its record as orphaned but does not delete, disable, or clear it. This avoids turning a deployment mistake into an outage. Operators review the `Seed missing` state and explicitly disable or remove the resource when intended.

Code-owned resources keep a narrow emergency enable/disable control. The dashboard cannot edit their controlled fields, rotate their code-supplied material, or change ownership. Restore normal service by correcting the startup configuration; ownership transfer is intentionally explicit rather than inferred.

Ordinary OAuth client disable/enable stays available for `dashboard` and `dynamic` clients. Code-owned clients reject those routes and use `POST /clients/{id}/emergency-disable` with reason `oauth_client_emergency_disabled`. Emergency enable only clears that incident flag. A seed that created the client as inactive (`IsActive = false` and no `DisabledAt`) cannot be enabled from the dashboard; change the seed.

## Migrated resource families

The shared model is applied to startup-seeded OAuth clients, social/OIDC connections, SCIM directory connections, global MFA settings, hosted AuthPage branding, and auth email branding.

A SCIM connection's grant boundary (`GrantBoundaryResourceId`) follows its owner. For a code-owned connection, the seed is authoritative: setting, changing, or removing the boundary in code revokes, during reconciliation, every managed grant no longer inside it, and the dashboard shows the boundary read-only. For a dashboard-owned connection, the dashboard and admin API can set or move the boundary with the same revocation. Every control plane applies one rule: the boundary resource must exist, and an enabled mapping's fixed resource must be the boundary or a descendant. A seed without a boundary still reconciles so existing deployments keep starting, but its mappings create no grants until code declares one. Operational rows such as sessions, tokens, challenges, and audit events are intentionally outside this model. AuthPage and email branding have no emergency disable control; change a code-owned surface in its seed.
