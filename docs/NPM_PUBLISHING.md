# Publishing `@sqlos/headless`

SqlOS publishes **two** artifacts from the same GitHub release tag:

| Artifact | Registry | Version |
| --- | --- | --- |
| `SqlOS` | NuGet | `src/SqlOS/SqlOS.csproj` `<Version>` |
| `@sqlos/headless` | npm | `packages/headless/package.json` `version` |

Those versions must match. `scripts/validate-docs-against-source.mjs` fails a PR if they drift.

This package is the headless AuthPage state machine. It is not a general OAuth/OIDC client.

## Trusted publishing (latest)

Release publishes use GitHub Actions OIDC. There is no long-lived `NPM_TOKEN` on the `latest` path.

1. Create the npm organization `sqlos` (or transfer `@sqlos/headless` into it).
2. On npmjs.com, open the package (or org) **Trusted Publisher** settings.
3. Bind GitHub:
   - Repository: `ross-slaney/sqlos`
   - Workflow: `publish.yml` (job `publish-npm`)
   - Environment: none (the job does not use a GitHub environment)
4. A GitHub release `vX.Y.Z` runs `.github/workflows/publish.yml`. After the NuGet job's usual gates, `publish-npm` runs `npm ci`, `npm test`, `npm run build` in `packages/headless`, then:

   ```bash
   npm publish --provenance --access public --tag latest
   ```

5. Use **npm 11+** in that job so the OIDC exchange happens inside `npm publish`. Do not set `NODE_AUTH_TOKEN` or `NPM_TOKEN` on the latest publish step.

To rotate the binding: remove the trusted publisher on npmjs.com, add a new one pointing at the same repo/workflow, and confirm the next `v*` release publishes. Do not add a classic npm automation token to restore `latest`.

## PR preview dist-tags

`.github/workflows/publish-npm.yml` publishes a unique version on each PR:

`@sqlos/headless@<base>-pr.<n>.<sha>.<run>.<attempt>` with dist-tag `pr-<n>`.

The workflow comments the install ref on the PR. In-repo examples **must not** use that tag; they keep `"@sqlos/headless": "file:../../packages/headless"`.

Bind a second trusted publisher for workflow `publish-npm.yml` (PR preview job) on the same package. Preview publish also uses OIDC, not `NPM_TOKEN`.

## Preview-tag cleanup

Trusted publishing cannot run `npm dist-tag rm`. On PR close, `publish-npm.yml` job `remove-preview-tag` uses repository secret `NPM_TOKEN` only to:

```bash
npm dist-tag rm @sqlos/headless pr-<n>
```

Granular token permissions: `dist-tag` on `@sqlos/headless`. Rotate it if it leaks. This is the only remaining token on the npm path.

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

When cutting a SqlOS version, bump `packages/headless/package.json` in the same version PR as `SqlOS.csproj`. See `.agents/skills/release-sqlos/SKILL.md`.
