#!/bin/bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

echo "=== Building @sqlos/headless and installing JS examples ==="

npm ci --prefix packages/headless
npm run build --prefix packages/headless
npm ci --prefix examples/SqlOS.Example.Web
npm ci --prefix examples/SqlOS.Example.AngularWeb

if [[ "${1:-}" == "--expo" ]]; then
  npm ci --prefix examples/SqlOS.Example.ExpoApp
fi

echo "=== JS examples installed from file:../../packages/headless ==="
