#!/bin/bash
# Enforces docs/architecture/8.0-behavior-ledger.md on a branch: every approved behavior-lock
# file the branch modifies or deletes must be named by a ledger entry the branch adds, entries
# the base already has must not change, and every entry must be well formed.
#
#   scripts/check-behavior-ledger.sh             # base: origin/$GITHUB_BASE_REF on CI, else origin/main
#   scripts/check-behavior-ledger.sh <base-ref>  # any other base, for example origin/release/8.0
#
# Only committed changes are checked: the ledger at HEAD against merge-base..HEAD. Approved files are the behavior-lock
# approvals (tests/SqlOS.BehaviorLock/**/*.verified.txt, outside the frozen Baseline/) and the
# @sqlos/headless public surface snapshot (packages/headless/tests/__snapshots__). Adding one needs
# no entry; renaming one without changing its content needs no entry. A rename with changes that
# the branch appends to Baseline/renames.txt is a modification of the renamed file: an entry names
# its new path. scripts/check-behavior-baseline.sh checks the whole ledger against the baseline.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"
# shellcheck source=lib/behavior-ledger.sh
. scripts/lib/behavior-ledger.sh

ledger="docs/architecture/8.0-behavior-ledger.md"
renames="tests/SqlOS.BehaviorLock/Baseline/renames.txt"
approved=(
    ':(glob)tests/SqlOS.BehaviorLock/**/*.verified.txt'
    ':(glob)packages/headless/tests/__snapshots__/**'
    ':(exclude)tests/SqlOS.BehaviorLock/Baseline/**'
)
heading='^### BL-[0-9]{4}: [^ ]'

if [ $# -gt 1 ]; then
    echo "Usage: $0 [base-ref]" >&2
    exit 2
fi

if [ $# -eq 1 ]; then
    base_ref="$1"
elif [ -n "${GITHUB_BASE_REF:-}" ]; then
    base_ref="origin/$GITHUB_BASE_REF"
    if ! git rev-parse --verify --quiet "$base_ref" >/dev/null; then
        git fetch --no-tags --quiet origin "$GITHUB_BASE_REF:refs/remotes/origin/$GITHUB_BASE_REF"
    fi
else
    base_ref="origin/main"
fi

if ! merge_base="$(git merge-base "$base_ref" HEAD 2>/dev/null)"; then
    echo "No merge base between $base_ref and HEAD. Fetch the base with full history (actions/checkout fetch-depth: 0)." >&2
    exit 2
fi

if ! head_ledger="$(git show "HEAD:$ledger" 2>/dev/null)"; then
    echo "$ledger is missing at HEAD." >&2
    exit 1
fi

# Prints the backticked approved-file paths in "<id>\t<line>" records read from stdin.
entry_paths() {
    ledger_named_paths | cut -f2
}

# The records of one entry.
entry() {
    awk -F'\t' -v id="$2" '$1 == id' <<< "$1"
}

failures=0
fail() {
    echo "  FAIL: $1"
    failures=$((failures + 1))
}

echo "=== Behavior ledger check: $base_ref ($(git rev-parse --short "$merge_base")..HEAD) ==="

base_ledger="$(git show "$merge_base:$ledger" 2>/dev/null || true)"
head_entries="$(ledger_entry_lines <<< "$head_ledger")"
base_entries="$(ledger_entry_lines <<< "$base_ledger")"
head_ids="$( (grep -E "$heading" <<< "$head_ledger" || true) | cut -c5-11)"
base_ids="$( (grep -E "$heading" <<< "$base_ledger" || true) | cut -c5-11)"

# Headings that look like entries but do not follow the format are almost always typos.
while IFS= read -r line; do
    fail "malformed entry heading '$line' (expected '### BL-NNNN: summary')"
done < <( (grep -E '^###[[:space:]]*BL' <<< "$head_ledger" || true) | (grep -vE "$heading" || true))

# IDs are unique and ascending; every entry has its four fields.
previous=""
for id in $head_ids; do
    if [ -n "$previous" ] && [[ ! "$id" > "$previous" ]]; then
        fail "$id is duplicated or out of order (it follows $previous)"
    fi
    previous="$id"

    records="$(entry "$head_entries" "$id")"
    body="$(cut -f2- <<< "$records")"
    if ! grep -qE '^- \*\*Approved files:\*\*' <<< "$body"; then
        fail "$id has no '- **Approved files:**' field"
    elif [ -z "$(entry_paths <<< "$records")" ]; then
        fail "$id names no approved file in backticks"
    fi
    if ! grep -qE '^- \*\*Category:\*\* (defect fixed|DX improvement|maintainability improvement)[[:space:]]*$' <<< "$body"; then
        fail "$id needs '- **Category:**' with one of: defect fixed, DX improvement, maintainability improvement"
    fi
    if ! grep -qE '^- \*\*Justification:\*\* [^[:space:]]' <<< "$body"; then
        fail "$id has no '- **Justification:**'"
    fi
    if ! grep -qE '^- \*\*Issue:\*\* .*(#[0-9]+|/issues/[0-9]+)' <<< "$body"; then
        fail "$id needs '- **Issue:**' with an issue reference (#123 or an issue URL)"
    fi
done

# Entries the base already has are frozen: revise a decision with a new entry.
for id in $base_ids; do
    if [ "$(entry "$base_entries" "$id")" != "$(entry "$head_entries" "$id")" ]; then
        fail "$id exists on $base_ref and must not change; add a new entry instead"
    fi
done

# Approved-file paths named by the entries this branch adds.
new_ids=()
new_paths=""
for id in $head_ids; do
    if ! grep -qxF "$id" <<< "$base_ids"; then
        new_ids+=("$id")
        new_paths+="$(entry_paths <<< "$(entry "$head_entries" "$id")")"$'\n'
    fi
done

# Renames this branch appends to the rename log, as "<earlier path> -> <later path>" lines.
rename_lines() {
    { git show "$1:$renames" 2>/dev/null || true; } | awk 'NF == 3 && $2 == "->" && $1 !~ /^#/' | sort
}
added_renames="$(comm -13 <(rename_lines "$merge_base") <(rename_lines HEAD))"
later_name_of() {
    awk -v earlier="$1" '$1 == earlier { print $3 }' <<< "$added_renames"
}
is_rename_target() {
    awk -v later="$1" '$3 == later { found = 1 } END { exit !found }' <<< "$added_renames"
}

changed=0
while IFS=$'\t' read -r status path renamed_to; do
    [ -z "$status" ] && continue
    later="$(later_name_of "$path")"
    case "$status" in
        A)
            if ! is_rename_target "$path"; then
                echo "  new:      $path"
            fi
            ;;
        R100)
            echo "  renamed:  $path -> $renamed_to"
            ;;
        D)
            changed=$((changed + 1))
            if [ -n "$later" ]; then
                if grep -qxF "$later" <<< "$new_paths"; then
                    echo "  ledgered: $path -> $later (renamed in $renames)"
                else
                    fail "$path was renamed to $later with changes, but no ledger entry added on this branch names $later"
                fi
            elif grep -qxF "$path" <<< "$new_paths"; then
                echo "  ledgered: $path (deleted)"
            else
                fail "$path changed ($status) but no ledger entry added on this branch names it"
            fi
            ;;
        *)
            changed=$((changed + 1))
            if grep -qxF "$path" <<< "$new_paths"; then
                echo "  ledgered: $path"
            else
                fail "$path changed ($status) but no ledger entry added on this branch names it"
            fi
            ;;
    esac
done < <(git diff --name-status -M100% "$merge_base" HEAD -- "${approved[@]}")

echo "Approved files modified or deleted: $changed. Ledger entries added: ${#new_ids[@]}."
if [ "$failures" -gt 0 ]; then
    echo "Behavior ledger check failed with $failures problem(s). See $ledger and tests/SqlOS.BehaviorLock/README.md." >&2
    exit 1
fi

echo "=== Behavior ledger check passed ==="
