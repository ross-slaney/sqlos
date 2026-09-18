# SqlOS Marketing Site

Public docs and marketing for SqlOS. Next.js app.

It includes:
- AuthServer and FGA guides
- Example stack notes
- Integration and testing docs
- Blog posts

## Local Development

```bash
cd web
npm install
npm run dev
```

## Production Build

```bash
cd web
npm run build
npm run start
```

`web/Dockerfile` uses the same `npm run build` / `npm run start` path. `scripts/docs-check.sh` audits the production `web` dependency graph (`npm audit --omit=dev`) and fails on high or critical findings. Reviewable exceptions, if one is genuinely needed, live in `npm-audit-exceptions.json`.

## Deployment

Production uses Azure Container Apps. Steps, GitHub vars, and service principal setup: [DEPLOYMENT.md](./DEPLOYMENT.md).
