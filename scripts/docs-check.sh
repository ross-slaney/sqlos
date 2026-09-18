#!/bin/bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

echo "=== Running Docs Checks ==="

node scripts/validate-docs-against-source.mjs
node scripts/validate-docs-layout.mjs
node scripts/validate-doc-images.mjs
node scripts/compile-doc-snippets.mjs
npm ci --prefix web
node --test scripts/web-production-audit.test.mjs
node scripts/web-production-audit.mjs
node scripts/validate-docs-search.mjs
npm run lint --prefix web
npm run build --prefix web
node scripts/validate-docs-production-start.mjs
node scripts/validate-doc-links.mjs

echo "=== Docs Checks Complete ==="
