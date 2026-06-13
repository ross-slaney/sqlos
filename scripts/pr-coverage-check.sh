#!/bin/bash
set -e

echo "=== PR Coverage Check ==="

# Determine the base branch
if [ -n "$GITHUB_BASE_REF" ]; then
    BASE_BRANCH="origin/$GITHUB_BASE_REF"
    echo "GitHub Actions detected. Base branch: $BASE_BRANCH"
    git fetch origin "$GITHUB_BASE_REF" 2>/dev/null || true
else
    BASE_BRANCH="origin/main"
    echo "Local run. Using base branch: $BASE_BRANCH"
fi

COVERAGE_FILE="TestResults/Coverage/coverage.merged.cobertura.xml"

if [ ! -f "$COVERAGE_FILE" ]; then
    echo "Coverage file not found at $COVERAGE_FILE"
    echo "Skipping coverage check."
    exit 0
fi

# Check if targeting main (enforce coverage)
IS_MAIN_PR=false
if [ "$GITHUB_BASE_REF" = "main" ] || [ "$GITHUB_BASE_REF" = "master" ]; then
    IS_MAIN_PR=true
fi

echo "Coverage file: $COVERAGE_FILE"
echo "Is main PR: $IS_MAIN_PR"

if ! git merge-base "$BASE_BRANCH" HEAD >/dev/null 2>&1; then
    echo "No merge base found for $BASE_BRANCH...HEAD. Skipping diff coverage check."
    exit 0
fi

# Run diff-cover
mkdir -p TestResults/Coverage

if [ "$IS_MAIN_PR" = true ]; then
    echo "Enforcing 70% coverage on new code for PR to main..."
    diff-cover "$COVERAGE_FILE" \
        --compare-branch="$BASE_BRANCH" \
        --fail-under=70 \
        --html-report TestResults/Coverage/diff-coverage.html \
        --exclude "*/Migrations/*"
else
    echo "Running coverage report (no enforcement for non-main PRs)..."
    diff-cover "$COVERAGE_FILE" \
        --compare-branch="$BASE_BRANCH" \
        --html-report TestResults/Coverage/diff-coverage.html \
        --exclude "*/Migrations/*" || true
fi

echo "=== PR Coverage Check Complete ==="
