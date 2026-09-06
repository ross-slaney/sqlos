#!/bin/bash
set -e

echo "=== Running Integration Tests ==="

mkdir -p TestResults/Integration

dotnet test tests/SqlOS.IntegrationTests/SqlOS.IntegrationTests.csproj \
    --configuration Release \
    --no-build \
    --collect:"XPlat Code Coverage" \
    --settings tests/coverlet.runsettings \
    --results-directory TestResults/Integration \
    --logger "console;verbosity=normal" \
    --logger "trx;LogFileName=IntegrationTests.trx"

provider="$(printf '%s' "${SQLOS_TEST_PROVIDER:-}" | tr '[:upper:]' '[:lower:]')"
if [ "$provider" = "postgresql" ] || [ "$provider" = "postgres" ] || [ "$provider" = "npgsql" ]; then
    echo "Skipping Example/Todo integration tests on PostgreSQL."
    echo "Those fixtures host SQL Server and are covered by the default integration-tests job."
    echo "=== Integration Tests Complete ==="
    exit 0
fi

dotnet test examples/SqlOS.Example.IntegrationTests/SqlOS.Example.IntegrationTests.csproj \
    --configuration Release \
    --no-build \
    --collect:"XPlat Code Coverage" \
    --settings tests/coverlet.runsettings \
    --results-directory TestResults/Integration \
    --logger "console;verbosity=normal" \
    --logger "trx;LogFileName=ExampleIntegrationTests.trx"

dotnet test examples/SqlOS.Todo.IntegrationTests/SqlOS.Todo.IntegrationTests.csproj \
    --configuration Release \
    --no-build \
    --collect:"XPlat Code Coverage" \
    --settings tests/coverlet.runsettings \
    --results-directory TestResults/Integration \
    --logger "console;verbosity=normal" \
    --logger "trx;LogFileName=TodoIntegrationTests.trx"

echo "=== Integration Tests Complete ==="
