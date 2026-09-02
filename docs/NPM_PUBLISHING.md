# Publishing `@sqlos/headless`

SqlOS publishes **two** artifacts from the same GitHub release tag:

| Artifact | Registry | Version |
| --- | --- | --- |
| `SqlOS` | NuGet | `src/SqlOS/SqlOS.csproj` `<Version>` |
| `@sqlos/headless` | npm | `packages/headless/package.json` `version` |

Those versions must match. `scripts/validate-docs-against-source.mjs` fails a PR if they drift.

This package is the headless AuthPage state machine. It is not a general OAuth/OIDC client.

## One trusted-publisher workflow

npm Trusted Publishing allows **only one** trusted publisher per package, and it matches the **caller workflow filename**. PR preview and release `latest` therefore share a single file:

`.github/workflows/publish-npm.yml`

Do not split npm publish across `publish.yml` and another workflow. NuGet stays in `publish.yml`; npm never publishes from that file.

## Trusted publishing setup

Release and preview publishes use GitHub Actions OIDC. There is no long-lived `NPM_TOKEN` on the publish path.

1. Create the npm organization `sqlos` (or transfer `@sqlos/headless` into it) and publish the package once if it does not exist yet (manual `npm publish` is fine for first create).
2. On npmjs.com open [package access settings](https://www.npmjs.com/package/@sqlos/headless/access) → **Trusted Publisher**.
3. Bind GitHub Actions:
   - Organization or user: `ross-slaney`
   - Repository: `sqlos`
   - Workflow filename: **`publish-npm.yml`** (filename only — not `publish.yml`)
   - Environment: none
   - Allowed actions: **`npm publish`** (required for publishers created after 20 May 2026)
4. Use **Node 24** (npm requires Node ≥ 22.14.0) and **npm ≥ 11.5.1** so the OIDC exchange happens inside `npm publish`. Do not set `registry-url` on `actions/setup-node` for that job, and do not set `NODE_AUTH_TOKEN` or `NPM_TOKEN` on the publish step — either one makes npm skip OIDC and fail with `ENEEDAUTH`.

If a previous attempt bound `publish.yml`, delete that publisher and add `publish-npm.yml`. npm does not validate the binding when you save it.

### What each trigger does

| Trigger | Dist-tag | Version |
| --- | --- | --- |
| Pull request against `main` | `pr-<n>` | `<base>-pr.<n>.<sha>.<run>.<attempt>` |
| GitHub release `vX.Y.Z` (`release: published`) | `latest` | exact `packages/headless/package.json` version (must match NuGet) |

In-repo examples **must not** use the preview tag; they keep `"@sqlos/headless": "file:../../packages/headless"`.

To rotate the binding: remove the trusted publisher on npmjs.com, add a new one pointing at `publish-npm.yml`, and confirm the next PR preview or `v*` release publishes. Do not add a classic npm automation token to restore `latest`.

Preview dist-tags (`pr-<n>`) are left in place when a PR closes. Trusted publishing covers `npm publish` only, and SqlOS does not keep a long-lived npm token for `dist-tag rm`. In-repo examples never install from those tags.

## Local and CI installs never use the registry

From the repository root:

```bash
./scripts/setup-js-examples.sh --expo
```

or:

```bash
npm ci --prefix packages/headless
npm run build --prefix packages/headless
npm ci --prefix examples/SqlOS.Example.Web
npm ci --prefix examples/SqlOS.Example.AngularWeb
```

`file:` copies the built `dist`. Build `@sqlos/headless` before example `npm ci`.

## Release checklist addition

When cutting a SqlOS version, bump `packages/headless/package.json` in the same version PR as `SqlOS.csproj`. The GitHub release tag that publishes NuGet also runs `publish-npm.yml` for `@sqlos/headless@latest`. See `.agents/skills/release-sqlos/SKILL.md`.
