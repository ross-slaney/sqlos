---
name: release-sqlos
description: Cut a SqlOS NuGet release by bumping the package version, updating public version-contract docs, adding a release article when needed, opening a version PR, enabling auto-merge after CI, and publishing the GitHub release tag that triggers NuGet. Use when the user asks to release, ship, cut a version, bump the package, publish to NuGet, or create a GitHub release for SqlOS.
---

# Release SqlOS

Take a completed `main` through a version PR and a published GitHub release. The published tag is what publishes the NuGet package. Do not treat an issue PR as a release.

Asking to release, ship, cut a version, or publish to NuGet authorizes commit, push, PR, auto-merge, and `gh release create` for this workflow only.

## Non-negotiables

- Work on a branch in the main checkout. Do not create a worktree.
- Release only what is already on `origin/main` plus the version/docs/blog bump. Do not bundle leftover feature work.
- Do not create the GitHub release, tag, or NuGet push until the version PR is merged to `main`.
- Do not `dotnet nuget push` locally. `.github/workflows/publish.yml` publishes on `release: published`.
- Use tag `vX.Y.Z` and release title `SqlOS X.Y.Z`. The last releases are `v3.24.1`, `v3.24.0`, `v3.23.0`.
- Squash-merge. Recent release commits on `main` look like `Release SqlOS 3.24.1 (#257)`.
- The GitHub release body must include the five checked compatibility lines exactly, or publish CI fails in `scripts/check-release-checklist.sh`.
- Do not invent validation. If a required check failed, fix it or stop.
- If the working tree has unrelated changes, stop and ask.

## 1. Establish the release

```bash
git status --short --branch
git fetch --prune origin
git log --oneline origin/main -15
gh release list --repo ross-slaney/sqlos --limit 5
```

Read `src/SqlOS/SqlOS.csproj` `<Version>` and the latest tag. They should already match on `main`.

Collect unreleased work:

```bash
git log --oneline "$(gh release view --repo ross-slaney/sqlos --json tagName --jq .tagName)..origin/main"
gh pr list --repo ross-slaney/sqlos --state merged --limit 30 \
  --json number,title,mergedAt,url
```

Choose the next version:

- **Patch** `X.Y.Z+1` — fixes, hardening, no new public capability.
- **Minor** `X.Y+1.0` — new user-visible capability or docs surface.
- **Major** `X+1.0.0` — breaking public contract.

Use the version the user named. If they did not, propose one from the unreleased commits and continue when it is obvious (patch vs minor). Ask only when major/breaking is in play or the commit set is mixed.

Write a one-screen contract before editing: current version, next version, merged PRs/issues in the release, blog yes/no, and any upgrade note.

## 2. Branch from current main

```bash
git checkout main
git pull --ff-only origin main
git checkout -b release-<version>
```

Reuse `release-<version>` if it already exists and is this release. Do not reset a branch that is not yours.

## 3. Bump the package and version contract

Canonical version: `src/SqlOS/SqlOS.csproj` `<Version>`.

`scripts/validate-docs-against-source.mjs` fails the PR unless these also contain the new version:

- `README.md` — `dotnet add package SqlOS --version <version>`
- `web/content/docs/quickstarts/add-to-app.mdx` — same install command
- `web/content/docs/reference/index.mdx` — `SqlOS <version>` in the banner/title

Also update every **current** public version-contract string. Search the old version and replace current-contract mentions:

```bash
rg -n --glob '!**/bin/**' --glob '!**/obj/**' --glob '!**/package-lock.json' \
  --glob '!**/node_modules/**' '<old-version>'
```

Typical current-contract files from recent releases:

- `README.md` (install command and the “If NuGet doesn't list …” note)
- `web/content/docs/docs-index.mdx`
- `web/content/docs/getting-started.mdx`
- `web/content/docs/quickstarts/add-to-app.mdx`
- `web/content/docs/quickstarts/ef-authorization.mdx`
- `web/content/docs/quickstarts/protect-api.mdx`
- `web/content/docs/quickstarts/run-example-stack.mdx`
- `web/content/docs/quickstarts/run-todo.mdx`
- `web/content/docs/reference/index.mdx`
- evergreen install commands such as `web/content/blog/hierarchical-authorization-native-ef-core.mdx`

Do **not** rewrite historical release articles (`web/content/blog/sqlos-3-24-…` stays about 3.24). Examples project-reference `src/SqlOS`; do not add a PackageReference version there.

Update feature docs only when this release changes their behavior.

## 4. Write the release article when the release needs one

Write `web/content/blog/sqlos-<x-y-z>-<slug>.mdx` for minor/major releases, and for patches with a user-visible or security story. Skip only when the user says so, or the bump is a mechanical republish with no new story.

Match the existing release-article shape (`sqlos-3-24-1-fail-closed-at-the-boundaries.mdx`, `sqlos-3-24-three-control-planes-one-auth-system.mdx`):

```md
---
title: "SqlOS <version>: <short outcome>"
description: "<one sentence operators/developers can use>"
date: "<YYYY-MM-DD>"
author: "Ross Slaney"
tags: ["AuthServer", "SqlOS"]
---
```

Add accurate tags from the change (OAuth, FGA, SAML, Security, Dashboard, and so on).

Body:

- What shipped and why it matters, in product language.
- Upgrade notes with `dotnet add package SqlOS --version <version>`.
- Links to the docs that changed.
- No secrets, no internal file tours, no “we also refactored.”

## 5. Validate the bump

From the repo root:

```bash
git diff --check
./scripts/docs-check.sh
dotnet pack src/SqlOS/SqlOS.csproj -c Release -o /tmp/sqlos-nupkg
```

Confirm the nupkg name and metadata are `SqlOS.<version>`. Delete the temp pack afterward.

CI will run build, unit, integration, and coverage. Run `./scripts/build.sh`, `./scripts/unit-tests.sh`, and `./scripts/integration-tests.sh` locally when the unreleased product change is large or the last CI on `main` is stale. Do not skip `docs-check.sh`.

## 6. Open the version PR and enable auto-merge

Commit only the release files:

```text
Release SqlOS <version>
```

Push and open the PR against `main`:

- Title: `Release SqlOS <version>`
- Body: `## Release` (what this version publishes, with issue/PR links), `## Changes` (csproj, docs contract, blog), `## Validation` (exact commands and outcomes)

```bash
gh pr merge --repo ross-slaney/sqlos --auto --squash <number>
```

Watch required checks. If they fail because of this bump, fix and push. If auto-merge is denied, report the permission error and keep watching; do not merge with admin override.

Wait until the PR is actually merged and `origin/main` contains `<Version><version></Version>`:

```bash
gh pr view <number> --repo ross-slaney/sqlos --json state,mergedAt,mergeCommit,url
git fetch origin main
git show origin/main:src/SqlOS/SqlOS.csproj | rg '<Version>'
```

Do not create the release on a still-open PR, a draft, or a local-only commit.

## 7. Publish the GitHub release

Create a **published** release (not draft, not prerelease) targeted at `main`. `gh release create` creates the `vX.Y.Z` tag.

```bash
gh release create "v<version>" \
  --repo ross-slaney/sqlos \
  --title "SqlOS <version>" \
  --target main \
  --notes-file /tmp/sqlos-release-notes.md
```

Release notes must follow this shape (see `v3.24.1`):

```md
<one-paragraph summary>

## Highlights

- <user-visible change>
- <user-visible change>

## Validation

The release passed the complete GitHub matrix: build, website/docs, unit tests, core SQL integration tests, example-app integration tests, Todo integration tests, merged coverage, and coverage threshold.

- [x] Hosted owned-app flow validated
- [x] Headless owned-app flow validated
- [x] Portable CIMD flow validated
- [x] Compatibility DCR flow validated
- [x] Protected-resource metadata and audience validation validated

[Read the release article](https://sqlos.dev/blog/<slug>)

**Full changelog:** https://github.com/ross-slaney/sqlos/compare/v<previous>...v<version>
```

The five checklist lines must match `scripts/check-release-checklist.sh` exactly, including `- [x] `. Treat the merged PR's green GitHub matrix as the evidence for those items. Do not publish with unchecked boxes. Omit the article link only when there is no article.

## 8. Watch NuGet publish

```bash
gh run list --repo ross-slaney/sqlos --workflow "Publish to NuGet" --limit 3
```

Watch the run created by this release. If `check-release-checklist.sh` fails, the body is wrong — edit the release notes; do not push a second tag. If pack/push fails, stop and report the job log. Do not delete or retag over an existing `vX.Y.Z`.

NuGet listing can lag. The package URL is `https://www.nuget.org/packages/SqlOS/<version>`.

## Definition of done

- `origin/main` has the new `<Version>` and matching current-contract docs.
- The version PR is merged (squash).
- GitHub release `vX.Y.Z` is published with the required checklist.
- Publish-to-NuGet either succeeded or the failure is reported with the run URL.
- Handoff includes the PR URL, release URL, publish run URL, package URL, version, and any remaining risk (NuGet delay, skipped blog, checks not run locally).
