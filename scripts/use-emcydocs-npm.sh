#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <version>"
  exit 1
fi

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
version="$1"

npm pkg set "dependencies.@emcy/docs=${version}" --prefix "$repo_root/web"
npm install --prefix "$repo_root/web"
