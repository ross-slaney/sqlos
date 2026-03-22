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
