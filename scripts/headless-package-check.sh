#!/bin/bash
# Single entry point for the "Headless JS Package" CI job (pull-request.yml and
# main.yml both call this), so the package tests and example builds cannot
# drift between the two workflows.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

./scripts/setup-js-examples.sh --expo

echo "=== @sqlos/headless: typecheck, unit tests, contract drift check ==="
npm test --prefix packages/headless

echo "=== Example builds against file:../../packages/headless ==="
npm run build --prefix examples/SqlOS.Example.Web
npm run build --prefix examples/SqlOS.Example.AngularWeb
npm exec --prefix examples/SqlOS.Example.ExpoApp -- tsc --noEmit -p examples/SqlOS.Example.ExpoApp/tsconfig.json

echo "=== Headless package check complete ==="
