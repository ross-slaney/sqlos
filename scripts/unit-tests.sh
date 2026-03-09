#!/bin/bash
set -e

echo "=== Running Unit Tests ==="

mkdir -p TestResults/Unit

dotnet test tests/SqlOS.Tests/SqlOS.Tests.csproj \
    --configuration Release \
    --no-build \
    --collect:"XPlat Code Coverage" \
    --settings tests/coverlet.runsettings \
    --results-directory TestResults/Unit \
    --logger "console;verbosity=normal" \
    --logger "trx;LogFileName=UnitTests.trx"

dotnet test examples/SqlOS.Example.Tests/SqlOS.Example.Tests.csproj \
    --configuration Release \
    --no-build \
    --collect:"XPlat Code Coverage" \
    --settings tests/coverlet.runsettings \
    --results-directory TestResults/Unit \
    --logger "console;verbosity=normal" \
    --logger "trx;LogFileName=ExampleUnitTests.trx"

echo "=== Unit Tests Complete ==="
