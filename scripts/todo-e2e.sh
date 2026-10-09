#!/bin/bash
# Browser end-to-end tests for the Todo sample on PostgreSQL: hosted signup
# into the Razor client, and the real Todo CLI binary through the
# device-approve journey. Single entry point for the "Todo Web + CLI E2E (PostgreSQL)"
# CI job and for local runs.
#
# Needs Docker (Postgres container) and the .NET SDK. Boots on alternate
# ports (5180/5190) so a running demo on 5080/5090 is not disturbed.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

project="examples/SqlOS.Todo.E2eTests"

docker pull postgres:16 || true

dotnet build "$project" --configuration Release

# CI runners have pwsh; the tests also self-install Chromium on first launch.
if command -v pwsh >/dev/null 2>&1; then
  pwsh "$project/bin/Release/net10.0/playwright.ps1" install --with-deps chromium
fi

ASPIRE_ALLOW_UNSECURED_TRANSPORT=true \
  dotnet test "$project" --configuration Release --no-build --logger "console;verbosity=normal"
