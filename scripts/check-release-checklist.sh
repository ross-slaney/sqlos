#!/bin/bash
set -euo pipefail

release_body="${RELEASE_BODY:-}"

if [[ -z "$release_body" ]]; then
  echo "RELEASE_BODY is required."
  exit 1
fi

required_items=(
  "Hosted owned-app flow validated"
  "Headless owned-app flow validated"
  "Portable CIMD flow validated"
  "Compatibility DCR flow validated"
  "Protected-resource metadata and audience validation validated"
)

missing_items=()

for item in "${required_items[@]}"; do
  if [[ "$release_body" != *"- [x] $item"* && "$release_body" != *"- [X] $item"* ]]; then
    missing_items+=("$item")
  fi
done

if (( ${#missing_items[@]} > 0 )); then
  echo "Release compatibility checklist is incomplete."
  echo "Add these checked items to the GitHub release body before publishing:"
  for item in "${missing_items[@]}"; do
    echo "- [x] $item"
  done
  exit 1
fi

echo "Release compatibility checklist satisfied."
