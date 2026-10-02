#!/bin/bash
# Sign-in latency benchmark (tests/SqlOS.Benchmarks.SignIn): a first-party client's direct password
# sign-in, an email-code sign-in, and the hosted authorize, password and token round trip, over
# HTTP in-process against the behavior-lock host on a fresh database. The method and the recorded
# numbers: docs/architecture/8.0-baseline-metrics.md#sign-in-latency.
#
#   scripts/sign-in-benchmark.sh --mode source                        # this checkout, SQL Server
#   scripts/sign-in-benchmark.sh --mode package --provider postgresql # the released baseline package
#   scripts/sign-in-benchmark.sh --mode package --build-only           # build now, measure later
#   scripts/sign-in-benchmark.sh --mode source --no-build --iterations 400 --warmup 100
#   scripts/sign-in-benchmark.sh --compare TestResults/SignInBenchmark/*.json
#
# Benchmark options: --provider sqlserver|postgresql (default SQLOS_TEST_PROVIDER, else
# sqlserver), --iterations N (400), --warmup N (100), --scenarios password,email-code,hosted,
# --accounts N (accounts the database holds before the run, default 0), --server "<connection
# string>" (instead of the Aspire container), --output <file>, --label <text> (how results name
# the build). --compare prints result files side by side as Markdown (--steps adds each request,
# --sql the statements whose time per sign-in changed most), with whichever build exists.
#
# Each mode builds into artifacts/sign-in-benchmark/<mode>, so the package and source builds
# coexist, runs can alternate between them without rebuilding (--no-build), and the solution's own
# bin/ output, which the behavior lock's --no-build runs use, is untouched. Results are written to
# TestResults/SignInBenchmark. The database server comes from tests/SqlOS.IntegrationTests.AppHost,
# so Docker must be running unless --server names one.
#
# Timings are only as quiet as the machine: run it alone, and never in pull-request CI.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

mode=""
build=1
run=1
compare=0
benchmark_args=()
while [ $# -gt 0 ]; do
    case "$1" in
        --mode) mode="$2"; shift 2 ;;
        --no-build) build=0; shift ;;
        --build-only) run=0; shift ;;
        --compare) compare=1; shift ;;
        --provider|--iterations|--warmup|--scenarios|--accounts|--server|--output|--label)
            benchmark_args+=("$1" "$2"); shift 2 ;;
        --steps|--sql) benchmark_args+=("$1"); shift ;;
        -h|--help) awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"; exit 0 ;;
        *)
            if [ "$compare" -eq 1 ]; then
                benchmark_args+=("$1"); shift
            else
                echo "Unknown argument: $1" >&2; exit 2
            fi ;;
    esac
done

benchmark_dll() {
    printf '%s' "artifacts/sign-in-benchmark/$1/bin/SqlOS.Benchmarks.SignIn/release/SqlOS.Benchmarks.SignIn.dll"
}

build_mode() {
    dotnet build tests/SqlOS.Benchmarks.SignIn/SqlOS.Benchmarks.SignIn.csproj \
        --configuration Release \
        -p:SqlOSUnderTest="$1" \
        --artifacts-path "artifacts/sign-in-benchmark/$1"
}

if [ "$compare" -eq 1 ]; then
    if [ -z "$mode" ]; then
        for candidate in source package; do
            if [ -f "$(benchmark_dll "$candidate")" ]; then mode="$candidate"; break; fi
        done
    fi
    mode="${mode:-source}"
    if [ ! -f "$(benchmark_dll "$mode")" ]; then
        build_mode "$mode"
    fi
    dotnet "$(benchmark_dll "$mode")" compare ${benchmark_args[@]+"${benchmark_args[@]}"}
    exit 0
fi

if [ "$mode" != "source" ] && [ "$mode" != "package" ]; then
    echo "--mode must be source or package" >&2
    exit 2
fi

if [ "$build" -eq 1 ]; then
    echo "=== Building the sign-in benchmark against SqlOS ${mode} ==="
    build_mode "$mode"
elif [ ! -f "$(benchmark_dll "$mode")" ]; then
    echo "No ${mode} build in artifacts/sign-in-benchmark/${mode}; run once without --no-build." >&2
    exit 2
fi

if [ "$run" -eq 0 ]; then
    exit 0
fi

# ASP.NET Core hosts run the server garbage collector; a console app would default to the
# workstation one, so the benchmark's in-process host runs with the server GC, as production does.
export DOTNET_gcServer=1
echo "=== Sign-in benchmark: SqlOS ${mode} ==="
dotnet "$(benchmark_dll "$mode")" ${benchmark_args[@]+"${benchmark_args[@]}"}
