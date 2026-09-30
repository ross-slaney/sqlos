#!/bin/bash
# Runs the 8.0 behavior lock (tests/SqlOS.BehaviorLock). See tests/SqlOS.BehaviorLock/README.md.
#
#   scripts/behavior-lock.sh                         # source build, SQL Server
#   SQLOS_TEST_PROVIDER=postgresql scripts/behavior-lock.sh
#   scripts/behavior-lock.sh --mode package          # the released baseline package
#   scripts/behavior-lock.sh --filter "FullyQualifiedName~Scenarios.Saml"
#   scripts/behavior-lock.sh --no-build              # reuse ./scripts/build.sh output (source mode)
#
# Environment: BEHAVIOR_LOCK_ACCEPT=1 accepts changed approvals (never on CI),
# BEHAVIOR_LOCK_REPEAT=N runs every scenario N times and fails unless the transcripts match.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

mode="source"
filter=""
build=1
workers=""
while [ $# -gt 0 ]; do
    case "$1" in
        --mode) mode="$2"; shift 2 ;;
        --filter) filter="$2"; shift 2 ;;
        --no-build) build=0; shift ;;
        --workers) workers="$2"; shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

if [ "$mode" != "source" ] && [ "$mode" != "package" ]; then
    echo "--mode must be source or package" >&2
    exit 2
fi

provider="$(printf '%s' "${SQLOS_TEST_PROVIDER:-sqlserver}" | tr '[:upper:]' '[:lower:]')"
results="TestResults/BehaviorLock/${mode}-${provider}"
mkdir -p "$results"

echo "=== Behavior lock: SqlOS ${mode}, provider ${provider} ==="
if [ "$build" -eq 1 ]; then
    dotnet build tests/SqlOS.BehaviorLock/SqlOS.BehaviorLock.csproj --configuration Release -p:SqlOSUnderTest="$mode"
elif [ "$mode" = "package" ]; then
    echo "--no-build reuses the solution build, which is source mode; build package mode explicitly." >&2
    exit 2
fi

test_args=(
    tests/SqlOS.BehaviorLock/SqlOS.BehaviorLock.csproj
    --configuration Release
    --no-build
    --results-directory "$results"
    --logger "console;verbosity=normal"
    --logger "trx;LogFileName=BehaviorLock.trx"
)
if [ -n "$filter" ]; then
    test_args+=(--filter "$filter")
fi
if [ -n "$workers" ]; then
    test_args+=(-- "MSTest.Parallelize.Workers=$workers")
fi

dotnet test "${test_args[@]}"
echo "=== Behavior lock complete ==="
