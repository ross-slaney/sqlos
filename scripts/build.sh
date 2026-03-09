#!/bin/bash
set -e

echo "=== Building SqlOS ==="

dotnet restore SqlOS.sln
dotnet build SqlOS.sln --configuration Release --no-restore

echo "=== Build Complete ==="
