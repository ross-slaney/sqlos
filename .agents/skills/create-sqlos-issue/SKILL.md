---
name: create-sqlos-issue
description: Create high-quality GitHub issues for ross-slaney/sqlos from desired-state prompts. Use when the user asks to turn a rough product/security/docs idea such as "I want magic link sign in" into a repo-grounded SqlOS issue, draft, or filed GitHub issue with current implementation context, relevant files, labels, milestone/priority where defensible, acceptance criteria, docs/example/test scope, and implementation guidance for another coding agent.
---

# Create SqlOS Issue

Use this skill to convert a rough desired state into a SqlOS issue that is ready for an implementation agent. The value is current repo research and sharp scope, not a generic issue template.

## Non-Negotiables

- Inspect the current repo and recent issues before drafting.
- Include current-state evidence with concrete file paths, symbols, routes, docs, tests, or missing surfaces.
- Check for duplicates or adjacent issues and link them.
- Separate what already exists from what is missing.
- Assign all defensible issue metadata: priority in title/body, labels, milestone when clearly mapped, assignee only when the repo convention or user explicitly indicates one.
- When filing (not drafting), always add the issue to the **sqlos Roadmap** project and assign **Business Value** and **Job Size**. Do not assign **Release** unless the caller explicitly named a current board release. Never default Release to **No Release**.
- Do not invent implementation facts. If evidence is absent, say "I did not find..." and list the search terms or areas checked.
- If the user asked to file the issue, create it with `gh issue create` after the research, then score it on the roadmap. If they asked for a draft, do not file it and do not add a project item.

## Fast Context Pass

From the SqlOS repo root, inspect current state, then open the files that matter:

```bash
git status --short --branch
gh issue list --repo ross-slaney/sqlos --state all --limit 35 --json number,title,state,labels,body,url
gh label list --repo ross-slaney/sqlos --limit 100 --json name,description
gh api repos/ross-slaney/sqlos/milestones
gh project field-list 1 --owner ross-slaney
rg -n "<important terms>" src tests docs web/content examples README.md
```

If `gh issue view --comments` fails because of deprecated Projects Classic fields, use `gh issue view --json ...` or `gh api repos/ross-slaney/sqlos/issues/<number>`.

## Research Checklist

Build a short evidence map before writing the issue.

- **Runtime:** `src/SqlOS/AuthServer`, `src/SqlOS/Fga`, `src/SqlOS/Email`, schema SQL, extension registration, route mapping.
- **Admin/dashboard:** `src/SqlOS/Dashboard/wwwroot/app.js`, admin API contracts/routes/services.
- **Hosted/headless surfaces:** AuthPage renderer, endpoint routes, headless contracts/services, example auth UI.
- **Examples:** `examples/SqlOS.Example.Api`, `examples/SqlOS.Example.Web`, `examples/SqlOS.Todo.*`.
- **Tests:** `tests/SqlOS.Tests`, `tests/SqlOS.IntegrationTests`, `examples/*IntegrationTests`.
- **Docs:** `docs/*`, `web/content/docs/authserver/*`, `web/content/docs/fga/*`, `README.md`.
- **Existing issues:** last 30-40 issues plus targeted searches for the same nouns.

Keep notes in this shape:

```markdown
Current evidence:
- `path/file.cs`: relevant symbol or missing behavior.
- `path/file.mdx`: docs mention X but not Y.
- `tests/...`: existing coverage proves A; no coverage found for B.
Adjacent issues:
- #NN: related because...
```

## Choose the Issue Shape

Use the closest existing SqlOS issue pattern:

- **Feature/security auth issues**: `Priority`, `Threat / product scenario`, `Standards context` when relevant, `Current SqlOS context`, `Recommended product model`, `Required implementation shape`, `Measurable acceptance criteria`, `Tests required`, `Documentation required`.
- **Focused hardening/bug issues**: `Problem`, `Required outcome`, `Acceptance criteria`, with current evidence at the top.
- **Provider-mode/platform gaps**: `Goal`, `Current repo context`, `Required behavior`, protocol-specific sections, `Admin/dashboard/docs`, `Example app showcase`, `Tests`.
- **Docs/content issues**: `Reader`, `Use case`, `What to cover`, `What to avoid`, `Deliverable`, and docs validation gates.
- **Dashboard/example parity issues**: `Goal`, `Current repo context`, `Required behavior`, `UX expectations`, `Example app showcase`, `Tests`.

Do not force every issue into every section. Preserve the issue's natural shape while keeping repo evidence and validation expectations.

## Metadata Rules

Use current labels, not guessed labels:

- `bug` for verified broken behavior or security flaw.
- `enhancement` for feature/product capability.
- `documentation` for docs/blog/content only.
- `critical-path` only for P0/P1 or roadmap-blocking security/foundation issues.
- `track:activation` for credential, onboarding, login, signup, and user activation flows.
- `track:foundation` for auth core, token, cryptography, schema, DCR/CIMD, concurrency, and platform invariants.
- `track:governance-tooling` for dashboard/admin/audit/ops tooling.
- `track:growth` for public docs, examples, blog, adoption, and positioning.
- `track:workspace` for workspace/FGA user experience.
- `IdP` for downstream identity-provider mode, OIDC Provider, SAML IdP, outbound SCIM, claim release, and provider-mode setup.

Milestones currently used by this repo are Phase 1 Foundation, Phase 2 Credential Expansion, and Phase 3 Enterprise Onboarding. Set one only when the issue clearly maps to the phase. Otherwise omit it and mention the uncertainty in the handoff.

Priority convention:

- Put `P1`, `P2`, `P3`, or combined `P1/P2` in the title when the recent issue family uses it.
- Explain the priority in the body. Tie it to security risk, roadmap dependency, or product polish.

Roadmap project fields (required when filing):

- Always add the new issue to [sqlos Roadmap](https://github.com/users/ross-slaney/projects/1) (`gh project` number `1`, owner `ross-slaney`).
- Always assign **Business Value** (`BV 1`–`BV 4`) and **Job Size** (`Size 1`–`Size 4`). Caller overrides win. Otherwise score them and explain the score in the handoff.
- Leave **Release** unset unless the skill invocation explicitly provided a current board option, for example `release: 4.1.0 Security hardening`. Do not assign **No Release**.
- Do not invent Release names, BV values, or Size values. Read current options from `gh project field-list 1 --owner ross-slaney`.
- Status and Track are optional. Set Status to `Backlog` when filing unless the caller asked for another current status. Set Track only when the issue clearly maps to one current Track option.

Scoring guidance:

- **BV 4**: live security hole, blocking product capability, or core activation/auth path.
- **BV 3**: important enterprise-readiness or platform capability, not a live incident.
- **BV 2**: contributor tooling, polish, or incremental developer experience.
- **BV 1**: nice-to-have, cosmetic, or very low leverage.
- **Size 1**: docs, config, or skill-only work on one surface.
- **Size 2**: focused change across one or two surfaces.
- **Size 3**: multiple surfaces or moderate product work.
- **Size 4**: cross-cutting work across control planes or a large feature.

Caller overrides look like `bv: 3`, `size: 2`, or `release: 4.1.0 Security hardening` in the skill invocation. If those are absent, score BV/Size yourself and leave Release unset.

## Drafting Rules

Write for another coding agent that will implement from the issue alone.

- Start with the desired outcome in plain language.
- Include "Current SqlOS context" or equivalent with concrete paths and what each path currently does.
- List likely implementation files, but phrase as "Likely" unless confirmed.
- Include hosted, headless, SDK/API, dashboard, docs, examples, and tests when the feature crosses those surfaces.
- Include negative/adversarial tests for auth, token, tenant, or security changes.
- Include validation gates from the relevant repo surfaces. Typical gates:
  - `dotnet test tests/SqlOS.Tests/SqlOS.Tests.csproj`
  - `dotnet test tests/SqlOS.IntegrationTests/SqlOS.IntegrationTests.csproj`
  - `dotnet test SqlOS.sln`
  - `npm run build --prefix web`
  - `scripts/docs-check.sh`
  - `node --check src/SqlOS/Dashboard/wwwroot/app.js` when dashboard JS changes.
- For public docs or example app work, explicitly require docs/example parity rather than runtime-only delivery.
- For security issues, include concrete attack shapes and what tests must prove.

## Filing Workflow

1. Draft the title, labels, milestone, body, BV, and Size. Leave Release unset unless the caller named a current board release.
2. Re-check duplicates with targeted `gh issue list --search`.
3. If the user asked for a draft only, stop here. Do not run `gh issue create` and do not add a project item.
4. If filing, run `gh issue create --repo ross-slaney/sqlos --title ... --body-file ... --label ...` and add `--milestone` only when certain.
5. Immediately add the new issue to the roadmap and set required fields by name. Do not hard-code GraphQL option IDs.

```bash
gh project item-add 1 --owner ross-slaney --url <issue-url>
gh project item-edit 1 --owner ross-slaney --url <issue-url> \
  --field "Business Value" --value "BV <1-4>"
gh project item-edit 1 --owner ross-slaney --url <issue-url> \
  --field "Job Size" --value "Size <1-4>"
```

Set `--field Release` only when the caller provided a named board release. Do not assign **No Release**. Set Status to `Backlog` and Track when those are defensible.
6. If a needed label does not exist, create a narrow label only when the user requested that taxonomy or the issue family already has a clear label pattern.
7. Return the issue URL plus labels, BV, and Size. List Release, Status, and Track only when set. Mention any other field intentionally left unset. If project add or field assignment fails after `gh issue create`, report the issue URL and the exact error. Do not pretend the issue is scored.

## Quality Bar

A good SqlOS issue should let a future agent begin by reading the listed files, understand what exists today, avoid duplicate work, know which surfaces must stay in parity, and know which tests prove completion. If the draft could apply to any auth library, it is not specific enough.
