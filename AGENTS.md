# SqlOS contributor guidance

These instructions apply to the entire repository.

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
