# SqlOS Marketing Site

This folder contains the public docs and marketing site for `SqlOS`.

It is a small Next.js app that covers:
- the merged `AuthServer` and `Fga` modules
- the shared example stack
- integration and testing docs
- background blog posts

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
```

## Deployment

The production deployment targets Azure Container Apps and is documented in [DEPLOYMENT.md](./DEPLOYMENT.md), including the exact GitHub Actions variables, secret names, and Azure service principal setup steps.
