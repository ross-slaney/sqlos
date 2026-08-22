#!/usr/bin/env bash
# Run the OpenID Foundation conformance suite's OIDCC certification plans
# against the SqlOS example OP (examples/SqlOS.SignInWithX.AppX).
#
#   ./run-conformance.sh [--keep]
#
# --keep leaves the SQL container, App X, and the suite containers running
# after the run (useful for inspecting results in the suite UI at
# https://localhost.emobix.co.uk:8443).
#
# Environment overrides:
#   SUITE_DIR    conformance-suite clone location (default /private/tmp/oidf-conformance-suite)
#   SUITE_TAG    conformance-suite git tag        (default release-v5.2.3)
#   EXPORT_DIR   where per-test result exports go (default <here>/export)
#   SQL_PORT     host port for the SQL container  (default 1437)
#   APPX_PORT    host port for App X              (default 5102)
#
# See README.md in this directory for how the networking fits together.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"

SUITE_DIR="${SUITE_DIR:-/private/tmp/oidf-conformance-suite}"
SUITE_TAG="${SUITE_TAG:-release-v5.2.3}"
SUITE_REPO="${SUITE_REPO:-https://gitlab.com/openid/conformance-suite.git}"
EXPORT_DIR="${EXPORT_DIR:-$HERE/export}"
LOG_DIR="$HERE/logs"
SQL_PORT="${SQL_PORT:-1437}"
APPX_PORT="${APPX_PORT:-5102}"
SQL_CONTAINER="${SQL_CONTAINER:-sqlos-conformance-sql}"
SQL_PASSWORD="${SQL_PASSWORD:-LocalDevPassword123!}"
COMPOSE_PROJECT="sqlos-conformance"
KEEP=0
[ "${1:-}" = "--keep" ] && KEEP=1

mkdir -p "$EXPORT_DIR" "$LOG_DIR"

APPX_PID=""
cleanup() {
    local code=$?
    if [ "$KEEP" = "1" ]; then
        echo "--keep: leaving App X (pid ${APPX_PID:-n/a}), $SQL_CONTAINER and the suite containers running."
        echo "Suite UI: https://localhost.emobix.co.uk:8443"
        return 0
    fi
    echo "Tearing down (exit code $code)..."
    [ -n "$APPX_PID" ] && kill "$APPX_PID" 2>/dev/null || true
    SUITE_DIR="$SUITE_DIR" APPX_PORT="$APPX_PORT" docker compose -p "$COMPOSE_PROJECT" -f "$HERE/docker-compose.conformance.yml" down -v 2>/dev/null || true
    docker rm -f "$SQL_CONTAINER" >/dev/null 2>&1 || true
}
trap cleanup EXIT

# ---------------------------------------------------------------- 1. SQL
if ! docker ps --format '{{.Names}}' | grep -qx "$SQL_CONTAINER"; then
    docker rm -f "$SQL_CONTAINER" >/dev/null 2>&1 || true
    echo "Starting SQL Server container $SQL_CONTAINER on port $SQL_PORT..."
    docker run -d --name "$SQL_CONTAINER" \
        -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$SQL_PASSWORD" \
        -p "$SQL_PORT:1433" --platform linux/amd64 \
        mcr.microsoft.com/mssql/server:2022-latest >/dev/null
fi
echo "Waiting for SQL Server..."
for i in $(seq 1 60); do
    if docker exec "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa \
        -P "$SQL_PASSWORD" -C -Q "SELECT 1" >/dev/null 2>&1; then
        break
    fi
    [ "$i" = "60" ] && { echo "SQL Server did not become ready" >&2; exit 1; }
    sleep 2
done

# ---------------------------------------------------------------- 2. App X
echo "Building App X..."
dotnet build "$REPO_ROOT/examples/SqlOS.SignInWithX.AppX" -c Release -v q --nologo

echo "Starting App X on port $APPX_PORT (log: $LOG_DIR/appx.log)..."
ASPNETCORE_URLS="http://0.0.0.0:$APPX_PORT" \
AppX__PublicOrigin="https://sqlos-op" \
AppX__Conformance__Enabled=true \
ConnectionStrings__DefaultConnection="Server=localhost,$SQL_PORT;Database=sqlos-appx-conformance;User Id=sa;Password=$SQL_PASSWORD;TrustServerCertificate=True" \
dotnet run --no-launch-profile --no-build -c Release \
    --project "$REPO_ROOT/examples/SqlOS.SignInWithX.AppX" \
    > "$LOG_DIR/appx.log" 2>&1 &
APPX_PID=$!

DISCOVERY="http://localhost:$APPX_PORT/sqlos/auth/.well-known/openid-configuration"
echo "Waiting for App X discovery document..."
for i in $(seq 1 60); do
    curl -sf "$DISCOVERY" >/dev/null && break
    kill -0 "$APPX_PID" 2>/dev/null || { echo "App X exited; see $LOG_DIR/appx.log" >&2; exit 1; }
    [ "$i" = "60" ] && { echo "App X did not become ready; see $LOG_DIR/appx.log" >&2; exit 1; }
    sleep 2
done

# ---------------------------------------------------------------- 3. Suite clone + build
if [ ! -d "$SUITE_DIR/.git" ]; then
    echo "Cloning conformance suite into $SUITE_DIR..."
    git clone "$SUITE_REPO" "$SUITE_DIR"
fi
git -C "$SUITE_DIR" fetch --tags --quiet 2>/dev/null || true
git -C "$SUITE_DIR" checkout --quiet "$SUITE_TAG"

if [ ! -f "$SUITE_DIR/target/fapi-test-suite.jar" ]; then
    echo "Building conformance suite (dockerized maven, Java 21)..."
    docker run --rm -v "$SUITE_DIR":/src -v "$HOME/.m2":/root/.m2 -w /src \
        maven:3-eclipse-temurin-21 mvn -B package -DskipTests
fi

if [ ! -x "$SUITE_DIR/.venv/bin/python" ]; then
    echo "Creating python venv for the suite's CI driver..."
    python3 -m venv "$SUITE_DIR/.venv"
    "$SUITE_DIR/.venv/bin/pip" -q install -r "$SUITE_DIR/scripts/requirements.txt"
fi

# ---------------------------------------------------------------- 4. Suite up
echo "Starting conformance suite containers..."
SUITE_DIR="$SUITE_DIR" APPX_PORT="$APPX_PORT" docker compose -p "$COMPOSE_PROJECT" \
    -f "$HERE/docker-compose.conformance.yml" up -d --build

echo "Waiting for the conformance suite API..."
for i in $(seq 1 90); do
    if curl -skf https://localhost.emobix.co.uk:8443/api/runner/available >/dev/null 2>&1 \
       || curl -sk -o /dev/null -w '%{http_code}' https://localhost.emobix.co.uk:8443/api/currentuser 2>/dev/null | grep -qE '^(200|401)$'; then
        break
    fi
    [ "$i" = "90" ] && { echo "Conformance suite did not become ready" >&2; exit 1; }
    sleep 2
done

# ---------------------------------------------------------------- 5. Run plans
export CONFORMANCE_SERVER="https://localhost.emobix.co.uk:8443"
# devmode: no CONFORMANCE_TOKEN needed and the self-signed 8443 cert is accepted
export CONFORMANCE_DEV_MODE=1
# Run every module even when several in a row fail, so a run against an OP
# with a known protocol gap still produces a complete per-test report instead
# of aborting on the driver's 3-consecutive-failures circuit breaker.
export CONFORMANCE_MAX_CONSECUTIVE_FAILURES="${CONFORMANCE_MAX_CONSECUTIVE_FAILURES:-1000}"

EXPECTED_ARGS=()
[ -s "$HERE/expected-failures.json" ] && EXPECTED_ARGS+=(--expected-failures-file "$HERE/expected-failures.json")
[ -s "$HERE/expected-skips.json" ] && EXPECTED_ARGS+=(--expected-skips-file "$HERE/expected-skips.json")

set +e
(cd "$SUITE_DIR" && exec "$SUITE_DIR/.venv/bin/python" scripts/run-test-plan.py \
    --export-dir "$EXPORT_DIR" \
    ${EXPECTED_ARGS[@]+"${EXPECTED_ARGS[@]}"} \
    'oidcc-config-certification-test-plan' "$HERE/config/sqlos-config.json" \
    'oidcc-basic-certification-test-plan[server_metadata=discovery][client_registration=static_client]' "$HERE/config/sqlos-basic.json") \
    2>&1 | tee "$LOG_DIR/run-test-plan.log"
RESULT=${PIPESTATUS[0]}
set -e

echo "run-test-plan.py exited with $RESULT. Exports in $EXPORT_DIR, log in $LOG_DIR/run-test-plan.log"
exit "$RESULT"
