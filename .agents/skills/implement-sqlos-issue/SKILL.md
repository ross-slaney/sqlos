---
name: implement-sqlos-issue
description: Implement an existing ross-slaney/sqlos GitHub issue from repository research through a focused code change, tests, documentation, a clean pull request, and CI follow-through. Use when the user asks to work on, implement, fix, resolve, or make a PR for a SqlOS issue, especially when they provide an issue number or URL and expect a validated PR rather than analysis alone.
---

# Implement a SqlOS Issue

Take ownership of the issue through a validated pull request. Treat the issue as the desired outcome, verify its claims against the current repository, and keep the implementation inside its defensible scope.

## Non-negotiables

- Read the root `AGENTS.md` and obey any narrower instructions before editing.
- Start implementation from current `origin/main` in an isolated worktree. Preserve the user's active checkout and unrelated changes.
- Read the complete issue, comments, linked work, and current implementation before deciding the change.
- Implement the smallest coherent solution that satisfies the issue. Do not bundle opportunistic cleanup.
- Reuse production domain services and validation. Do not create parallel code, API, and dashboard policy implementations.
- Test behavior, not only data shapes. Include negative and tenant-isolation cases for authorization, identity, secret, token, or administrative changes.
- Do not weaken assertions, remove coverage, or change CI merely to make a failure disappear.
- Do not merge, release, close issues manually, or delete the worktree unless the user asks.
- Never claim completion without listing the checks actually run and their outcomes.

## 1. Establish the contract

Resolve the repository root and inspect local state without modifying it:

```bash
git status --short --branch
git remote -v
git worktree list --porcelain
```

Read the issue and its discussion. Prefer structured output so metadata is not lost:

```bash
gh issue view <number> --repo ross-slaney/sqlos \
  --json number,title,body,state,labels,milestone,assignees,comments,url
gh pr list --repo ross-slaney/sqlos --state all \
  --search "<issue number or distinctive terms>" \
  --json number,title,state,url,headRefName,baseRefName
```

If `gh issue view` fails on legacy project metadata, use `gh api repos/ross-slaney/sqlos/issues/<number>` and fetch comments separately.

Write a compact internal contract before editing:

- User-visible or operator-visible outcome.
- Current behavior and evidence.
- Acceptance criteria from the issue.
- Relevant runtime, service/API, dashboard, persistence, SDK, docs, example, and test surfaces.
- Explicit non-goals, dependencies, and ambiguities.

Resolve ambiguity from code, tests, related issues, and established patterns when safe. Ask the user only when materially different product outcomes remain possible.

## 2. Route specialized work

Use the narrowest applicable installed skill in addition to this workflow:

- Use `model-shrbac-authorization` before writing code that changes SHRBAC, FGA, roles, permissions, grants, resource trees, or tenant access.
- Use the appropriate Codex Security skill when the issue is a security finding or requires security-specific validation.
- Use `create-sqlos-issue` only when the task is to draft or file a new issue; do not use it merely because implementation references an existing issue.
- Use `release-sqlos` only when the user explicitly requests merge, versioning, publishing, or consumer rollout. A normal issue PR stops before those actions.

## 3. Create the isolated worktree

Fetch current remote state, then create a new branch directly from `origin/main`:

```bash
git fetch --prune origin
git worktree add -b codex/issue-<number>-<slug> <safe-sibling-path> origin/main
```

Before creating it, verify the branch and path do not already exist. If stale worktree metadata blocks setup, inspect it and use `git worktree prune --verbose` only for entries whose directories no longer exist.

Run all subsequent edits, builds, tests, commits, and PR commands from the new worktree. Re-run `git status --short --branch` there and confirm the base commit belongs to current `origin/main`.

## 4. Map the implementation

Search by domain terms, routes, symbols, DTO fields, test names, and documentation headings. Follow calls to the shared service or runtime boundary rather than stopping at the first controller or UI component.

For an operator-managed capability, apply the repository's control-plane parity standard:

- Model one domain behavior shared by strongly typed code/seeds, administration service or API, and dashboard.
- Preserve deterministic, idempotent reconciliation and explicit ownership.
- Route each control plane through the same normalization, validation, authorization, tenancy, secret handling, and audit behavior.
- Keep protected values write-only or one-time reveal.
- Use `ControlPlaneParityHarness` and the exact production seed, public service, and dashboard HTTP route when all three planes apply.
- Compare canonical redacted projections, assert expected ownership differences, and exercise a real runtime boundary.
- State why a control plane is inapplicable instead of inventing an operator switch for an internal secure default.

Identify the tests that should fail before the fix or prove the missing behavior. Prefer extending existing fixtures and conventions over adding a parallel harness.

## 5. Implement the issue

Make a minimal, production-shaped change:

- Keep contracts strongly typed and errors stable and machine-readable.
- Enforce authorization and tenancy on the server; UI state alone is never sufficient.
- Keep credentials and security material out of committed configuration, logs, responses, snapshots, and dashboard rendering.
- Update all affected DTO fixtures, persistence mappings, dashboard JavaScript contracts, examples, and public docs when the contract changes.
- Add real SQL integration coverage when persistence, migrations, reconciliation, locking, or concurrency is material.
- Preserve compatibility unless the issue explicitly authorizes a breaking change.

Run focused tests while iterating. Diagnose failures against the current base and environment before changing product code.

## 6. Validate proportionally

Run targeted tests first, then the repository gates relevant to the finished diff. For a normal product-code PR, the default complete local gate is:

```bash
./scripts/build.sh
./scripts/unit-tests.sh
./scripts/integration-tests.sh
./scripts/docs-check.sh
git diff --check
```

The build must precede the test scripts because they run with `--no-build`. If the issue touches only a narrow surface, focused checks help iteration but do not silently substitute for required repository gates. If a gate is genuinely inapplicable or environment-blocked, explain why and preserve the exact error.

Also perform direct runtime or operator-path proof when the change cannot be established adequately by static and automated tests—for example, a dashboard workflow, hosted auth flow, migration, or example application. Record what was exercised and the observed outcome without exposing secrets.

Before committing, review:

```bash
git status --short
git diff --stat origin/main...HEAD
git diff origin/main...HEAD
```

Confirm every changed file belongs to the issue and every acceptance criterion has evidence.

## 7. Publish the pull request

Commit only intended files with an issue-focused message, push the `codex/` branch, and open a PR to `main`. Include:

- The problem and resulting behavior.
- The implementation shape and control-plane implications.
- Tests and runtime evidence with exact commands.
- Documentation or examples changed.
- `Closes #<number>` when the PR fully resolves the issue; otherwise use a non-closing reference and explain the remaining scope.

Do not describe a PR as ready while known required checks are failing. Watch the PR checks, inspect failures, and fix failures caused by the change. Distinguish product failures from infrastructure or permission failures using concrete evidence.

## Definition of done

The task is complete only when:

- The issue's acceptance criteria are satisfied by the current branch.
- The diff is focused and based on current `origin/main`.
- Applicable code, API/SDK, dashboard, runtime, docs, examples, and parity surfaces agree.
- Required local validation passes, or any external blocker is reported precisely.
- The branch is pushed and the PR exists when the user requested implementation or a PR.
- Required CI is green before calling the PR ready, unless the user explicitly accepts a documented external blocker.
- The handoff includes the PR URL, concise change summary, exact validation results, and any remaining risk.
