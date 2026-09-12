#!/usr/bin/env bash
# Add a filed ross-slaney/sqlos issue to the sqlos Roadmap project and
# assign Business Value and Job Size by field name. Release is optional.
set -euo pipefail

owner="${SQLOS_ROADMAP_OWNER:-ross-slaney}"
project_number="${SQLOS_ROADMAP_NUMBER:-1}"
repo="${GH_REPO:-ross-slaney/sqlos}"

issue=""
issue_url=""
bv=""
size=""
release=""
status=""
track=""
dry_run=0
fields_json=""
check_fields=0

usage() {
  cat <<'EOF'
Add a filed SqlOS issue to the sqlos Roadmap and set required intake fields.

Usage:
  add-sqlos-issue-to-roadmap.sh --issue <number|url> --bv <1-4|BV N> --size <1-4|Size N> [options]

Required:
  --issue, --issue-url   GitHub issue number or URL
  --bv                   Business Value: 1-4 or "BV N"
  --size                 Job Size: 1-4 or "Size N"

Optional:
  --release              Release option name. Omit to leave Release unset.
  --status               Status option name (for example Backlog)
  --track                Track option name
  --dry-run              Print planned gh commands without changing the board
  --fields-json FILE     Validate options against this field-list JSON
  --check-fields         Fetch or read field options and exit
  -h, --help             Show this help

Environment:
  GH_REPO                  default ross-slaney/sqlos
  SQLOS_ROADMAP_OWNER      default ross-slaney
  SQLOS_ROADMAP_NUMBER     default 1
EOF
}

fail() {
  echo "error: $*" >&2
  exit 2
}

normalize_bv() {
  local raw="${1-}"
  raw="${raw#"${raw%%[![:space:]]*}"}"
  raw="${raw%"${raw##*[![:space:]]}"}"
  local upper
  upper="$(printf '%s' "$raw" | tr '[:lower:]' '[:upper:]')"
  if [[ "$upper" =~ ^BV[[:space:]]*([1-4])$ ]]; then
    printf 'BV %s' "${BASH_REMATCH[1]}"
    return 0
  fi
  if [[ "$upper" =~ ^([1-4])$ ]]; then
    printf 'BV %s' "${BASH_REMATCH[1]}"
    return 0
  fi
  return 1
}

normalize_size() {
  local raw="${1-}"
  raw="${raw#"${raw%%[![:space:]]*}"}"
  raw="${raw%"${raw##*[![:space:]]}"}"
  local upper
  upper="$(printf '%s' "$raw" | tr '[:lower:]' '[:upper:]')"
  if [[ "$upper" =~ ^SIZE[[:space:]]*([1-4])$ ]]; then
    printf 'Size %s' "${BASH_REMATCH[1]}"
    return 0
  fi
  if [[ "$upper" =~ ^([1-4])$ ]]; then
    printf 'Size %s' "${BASH_REMATCH[1]}"
    return 0
  fi
  return 1
}

resolve_issue_url() {
  local raw="$1"
  if [[ "$raw" =~ ^https://github.com/[^/]+/[^/]+/issues/[0-9]+$ ]]; then
    printf '%s' "$raw"
    return 0
  fi
  if [[ "$raw" =~ ^[0-9]+$ ]]; then
    printf 'https://github.com/%s/issues/%s' "$repo" "$raw"
    return 0
  fi
  return 1
}

load_fields_json() {
  if [ -n "$fields_json" ]; then
    cat "$fields_json"
    return 0
  fi
  gh project field-list "$project_number" --owner "$owner" --format json
}

field_options() {
  local payload="$1"
  local field_name="$2"
  python3 -c '
import json, sys
data = json.loads(sys.argv[1])
name = sys.argv[2]
fields = data.get("fields") or data
if isinstance(fields, dict):
    fields = fields.get("fields") or []
for field in fields:
    if field.get("name") == name:
        for option in field.get("options") or []:
            print(option.get("name", ""))
        raise SystemExit(0)
raise SystemExit(1)
' "$payload" "$field_name"
}

require_option() {
  local field_name="$1"
  local value="$2"
  local payload="$3"
  local options
  if ! options="$(field_options "$payload" "$field_name")"; then
    fail "roadmap field '$field_name' was not found"
  fi
  while IFS= read -r option; do
    [ "$option" = "$value" ] && return 0
  done <<<"$options"
  fail "invalid $field_name value '$value'. Allowed: $(printf '%s' "$options" | paste -sd ', ' -)"
}

print_field_summary() {
  local payload="$1"
  python3 -c '
import json, sys
data = json.loads(sys.argv[1])
fields = data.get("fields") or data
if isinstance(fields, dict):
    fields = fields.get("fields") or []
wanted = ("Status", "Track", "Business Value", "Job Size", "Release")
for field in fields:
    name = field.get("name")
    if name not in wanted:
        continue
    options = [option.get("name", "") for option in (field.get("options") or [])]
    print(f"{name}: {", ".join(options)}")
' "$payload"
}

run_or_echo() {
  if [ "$dry_run" -eq 1 ]; then
    printf '+'
    printf ' %q' "$@"
    printf '\n'
    return 0
  fi
  "$@"
}

while [ $# -gt 0 ]; do
  case "$1" in
    --issue|--issue-url)
      [ $# -ge 2 ] || fail "$1 requires a value"
      issue="$2"
      shift 2
      ;;
    --bv)
      [ $# -ge 2 ] || fail "$1 requires a value"
      bv="$2"
      shift 2
      ;;
    --size)
      [ $# -ge 2 ] || fail "$1 requires a value"
      size="$2"
      shift 2
      ;;
    --release)
      [ $# -ge 2 ] || fail "$1 requires a value"
      release="$2"
      shift 2
      ;;
    --status)
      [ $# -ge 2 ] || fail "$1 requires a value"
      status="$2"
      shift 2
      ;;
    --track)
      [ $# -ge 2 ] || fail "$1 requires a value"
      track="$2"
      shift 2
      ;;
    --fields-json)
      [ $# -ge 2 ] || fail "$1 requires a value"
      fields_json="$2"
      shift 2
      ;;
    --dry-run)
      dry_run=1
      shift
      ;;
    --check-fields)
      check_fields=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      fail "unknown argument: $1"
      ;;
  esac
done

if [ "$check_fields" -eq 1 ]; then
  payload="$(load_fields_json)"
  print_field_summary "$payload"
  exit 0
fi

[ -n "$issue" ] || fail "--issue is required"
[ -n "$bv" ] || fail "--bv is required"
[ -n "$size" ] || fail "--size is required"

if ! issue_url="$(resolve_issue_url "$issue")"; then
  fail "could not resolve issue URL from '$issue'"
fi

if ! bv="$(normalize_bv "$bv")"; then
  fail "invalid --bv '$bv'. Use 1-4 or 'BV N'"
fi
if ! size="$(normalize_size "$size")"; then
  fail "invalid --size '$size'. Use 1-4 or 'Size N'"
fi

payload="$(load_fields_json)"
require_option "Business Value" "$bv" "$payload"
require_option "Job Size" "$size" "$payload"
if [ -n "$release" ]; then
  require_option "Release" "$release" "$payload"
fi
if [ -n "$status" ]; then
  require_option "Status" "$status" "$payload"
fi
if [ -n "$track" ]; then
  require_option "Track" "$track" "$payload"
fi

echo "Roadmap: https://github.com/users/${owner}/projects/${project_number}"
echo "Issue: $issue_url"
echo "Business Value: $bv"
echo "Job Size: $size"
if [ -n "$release" ]; then
  echo "Release: $release"
fi
if [ -n "$status" ]; then
  echo "Status: $status"
fi
if [ -n "$track" ]; then
  echo "Track: $track"
fi

if [ "$dry_run" -eq 1 ]; then
  run_or_echo gh project item-add "$project_number" --owner "$owner" --url "$issue_url" --format json
else
  if ! gh project item-add "$project_number" --owner "$owner" --url "$issue_url" --format json >/dev/null; then
    echo "warning: item-add failed; trying field edits in case the issue is already on the board" >&2
  fi
fi

run_or_echo gh project item-edit "$project_number" --owner "$owner" --url "$issue_url" \
  --field "Business Value" --value "$bv"
run_or_echo gh project item-edit "$project_number" --owner "$owner" --url "$issue_url" \
  --field "Job Size" --value "$size"
if [ -n "$release" ]; then
  run_or_echo gh project item-edit "$project_number" --owner "$owner" --url "$issue_url" \
    --field "Release" --value "$release"
fi

if [ -n "$status" ]; then
  run_or_echo gh project item-edit "$project_number" --owner "$owner" --url "$issue_url" \
    --field "Status" --value "$status"
fi
if [ -n "$track" ]; then
  run_or_echo gh project item-edit "$project_number" --owner "$owner" --url "$issue_url" \
    --field "Track" --value "$track"
fi
