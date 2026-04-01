#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"

npm pkg set "dependencies.@emcy/docs=file:../../emcydocs" --prefix "$repo_root/web"
npm install --prefix "$repo_root/web"
