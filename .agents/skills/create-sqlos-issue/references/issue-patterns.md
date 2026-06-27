# SqlOS Issue Patterns

Use this reference when a draft needs closer alignment with recent SqlOS issues.

## Recent Patterns

Feature/security auth issues, such as magic links and passkeys, include:

- Priority
- Threat / product scenario
- Standards or guidance context when relevant
- Current SqlOS context
- Recommended product model
- Required implementation shape
- Measurable acceptance criteria
- Tests required to prove the issue is fixed
- Documentation required

Provider-mode issues include:

- Goal
- Current repo context
- Required behavior
- Protocol-specific requirement sections
- Admin/dashboard/docs
- Example app showcase
- Tests

Focused hardening issues include:

- Problem
- Required outcome
- Acceptance criteria

Docs/blog issues include:

- Reader
- Use case
- What to cover
- What to avoid
- Deliverable

## Context Depth Expected

Recent high-quality issues cite current code and product reality, for example:

- Existing endpoints or missing endpoints.
- Relevant service, contract, schema, dashboard, doc, example, and test files.
- Current behavior to preserve.
- Related issues and dependencies.
- Security threats or user workflows.
- Concrete test names or invariants.

The issue should not say only "add feature X." It should explain where feature X fits into SqlOS today and how another agent proves it is complete.

## Common Validation Gates

Use only the gates relevant to the surfaces changed:

- `dotnet test tests/SqlOS.Tests/SqlOS.Tests.csproj`
- `dotnet test tests/SqlOS.IntegrationTests/SqlOS.IntegrationTests.csproj`
- `dotnet test SqlOS.sln`
- `npm run build --prefix web`
- `scripts/docs-check.sh`
- `node --check src/SqlOS/Dashboard/wwwroot/app.js`

## Duplicate Checks

Search both words and concepts. For "magic link", also search:

- passwordless
- email link
- email OTP
- temporary token
- bearer token

For "sign in with SqlOS", also search:

- IdP
- OIDC Provider
- downstream relying party
- SAML IdP
- claim release

For "SSO setup", also search:

- delegated portal
- SAML
- OIDC
- home realm discovery
- provider setup
