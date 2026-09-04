#!/bin/bash
# Browser end-to-end tests for the headless example UIs (Next.js + Angular
# driving @sqlos/headless against the real Example app host). Single entry
# point for the "Headless Examples E2E" CI job and for local runs.
#
# Needs Docker (SQL Server container), Node, and the .NET SDK. Boots on
# alternate ports (5162/3110/4300/1439) so a running demo is not disturbed.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

project="examples/SqlOS.Example.E2eTests"

./scripts/setup-js-examples.sh

docker pull mcr.microsoft.com/mssql/server:2022-latest || true

dotnet build "$project" --configuration Release

# CI runners have pwsh; the tests also self-install Chromium on first launch.
if command -v pwsh >/dev/null 2>&1; then
  pwsh "$project/bin/Release/net9.0/playwright.ps1" install --with-deps chromium
fi

ASPIRE_ALLOW_UNSECURED_TRANSPORT=true \
  dotnet test "$project" --configuration Release --no-build --logger "console;verbosity=normal"
