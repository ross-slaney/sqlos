#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
script="$script_dir/add-sqlos-issue-to-roadmap.sh"
fields="$script_dir/fixtures/roadmap-fields.json"
failed=0

assert_ok() {
  local name="$1"
  shift
  if "$@"; then
    echo "PASS $name"
  else
    echo "FAIL $name"
    failed=1
  fi
}

assert_fails() {
  local name="$1"
  shift
  if "$@" >/tmp/sqlos-roadmap-test-stdout.txt 2>/tmp/sqlos-roadmap-test-stderr.txt; then
    echo "FAIL $name (expected non-zero)"
    failed=1
  else
    echo "PASS $name"
  fi
}

contains() {
  local needle="$1"
  local file="$2"
  grep -F -- "$needle" "$file" >/dev/null
}

does_not_contain() {
  local needle="$1"
  local file="$2"
  ! grep -F -- "$needle" "$file" >/dev/null
}

assert_ok "help" "$script" --help

assert_fails "missing bv and size" "$script" --dry-run --fields-json "$fields" --issue 357
assert_fails "missing size" "$script" --dry-run --fields-json "$fields" --issue 357 --bv 2
assert_fails "missing bv" "$script" --dry-run --fields-json "$fields" --issue 357 --size 2
assert_fails "invalid bv" "$script" --dry-run --fields-json "$fields" --issue 357 --bv 5 --size 2
assert_fails "invalid size" "$script" --dry-run --fields-json "$fields" --issue 357 --bv 2 --size 9
assert_fails "invalid release" "$script" --dry-run --fields-json "$fields" --issue 357 --bv 2 --size 2 --release "Not A Release"

"$script" --dry-run --fields-json "$fields" --issue 357 --bv 2 --size 2 \
  >/tmp/sqlos-roadmap-default.txt
assert_ok "default omits release summary" does_not_contain 'Release:' /tmp/sqlos-roadmap-default.txt
assert_ok "default omits release field" does_not_contain '--field Release' /tmp/sqlos-roadmap-default.txt
assert_ok "default omits no-release value" does_not_contain 'No Release' /tmp/sqlos-roadmap-default.txt
assert_ok "default bv summary" contains 'Business Value: BV 2' /tmp/sqlos-roadmap-default.txt
assert_ok "default size summary" contains 'Job Size: Size 2' /tmp/sqlos-roadmap-default.txt
assert_ok "default issue url" contains 'https://github.com/ross-slaney/sqlos/issues/357' /tmp/sqlos-roadmap-default.txt

"$script" --dry-run --fields-json "$fields" \
  --issue-url https://github.com/ross-slaney/sqlos/issues/350 \
  --bv "BV 3" --size "Size 4" --release "4.1.0 Security hardening" \
  --status Backlog --track "Authentication & Accounts" \
  >/tmp/sqlos-roadmap-explicit.txt
assert_ok "explicit release command" contains '--value 4.1.0\ Security\ hardening' /tmp/sqlos-roadmap-explicit.txt
assert_ok "explicit release summary" contains 'Release: 4.1.0 Security hardening' /tmp/sqlos-roadmap-explicit.txt
assert_ok "explicit status" contains 'Status: Backlog' /tmp/sqlos-roadmap-explicit.txt
assert_ok "explicit track" contains 'Track: Authentication & Accounts' /tmp/sqlos-roadmap-explicit.txt

"$script" --check-fields --fields-json "$fields" >/tmp/sqlos-roadmap-fields.txt
assert_ok "check-fields lists no release" contains 'No Release' /tmp/sqlos-roadmap-fields.txt

if [ "$failed" -ne 0 ]; then
  echo "add-sqlos-issue-to-roadmap tests failed"
  exit 1
fi

echo "add-sqlos-issue-to-roadmap tests passed"
