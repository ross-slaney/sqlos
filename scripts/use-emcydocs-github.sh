#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
ref="${1:-a4a2ab3}"

npm pkg set "dependencies.@emcy/docs=github:ross-slaney/emcydocs#${ref}" --prefix "$repo_root/web"
npm install --prefix "$repo_root/web"
