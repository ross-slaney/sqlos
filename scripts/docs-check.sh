#!/bin/bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

echo "=== Running Docs Checks ==="

node scripts/validate-docs-against-source.mjs
node scripts/compile-doc-snippets.mjs
npm ci --prefix web
npm run lint --prefix web
npm run build --prefix web
node scripts/validate-doc-links.mjs

echo "=== Docs Checks Complete ==="
