#!/bin/bash
# Enforces the frozen behavior baseline (tests/SqlOS.BehaviorLock/README.md, "Baseline, current,
# and the ledger"): tests/SqlOS.BehaviorLock/Baseline records what the released SqlOS does, the
# approved files record what this build does, and docs/architecture/8.0-behavior-ledger.md names
# exactly the files where they differ.
#
#   scripts/check-behavior-baseline.sh             # base: origin/$GITHUB_BASE_REF on CI, else origin/main
#   scripts/check-behavior-baseline.sh <base-ref>  # any other base, for example origin/release/8.0
#
# It checks committed content (HEAD) and fails when:
#   1. a baseline file differs from its approved file at the source commit release.txt names, or
#      an approved file of that commit has no baseline file (baselines recorded later, for new
#      coverage, are files the source commit does not have);
#   2. an approved file has no baseline, by its path or by its earlier name in renames.txt;
#   3. an approved file differs from its baseline, or a baselined file is gone, and no ledger
#      entry names it; or an entry names a file that does not differ from its baseline;
#   4. against the base, a baseline file was modified, deleted, or renamed, or a renames.txt line
#      was changed or removed, unless release.txt changed (a re-baseline to a new release).
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"
# shellcheck source=lib/behavior-ledger.sh
. scripts/lib/behavior-ledger.sh

baseline="tests/SqlOS.BehaviorLock/Baseline"
ledger="docs/architecture/8.0-behavior-ledger.md"

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

if ! release_file="$(git show "HEAD:$baseline/release.txt" 2>/dev/null)"; then
    echo "$baseline/release.txt is missing at HEAD." >&2
    exit 1
fi
release="$(awk '$1 == "release" { print $2 }' <<< "$release_file")"
source_commit="$(awk '$1 == "source" { print $2 }' <<< "$release_file")"
if [ -z "$release" ] || [ -z "$source_commit" ] || ! git cat-file -e "$source_commit^{commit}" 2>/dev/null; then
    echo "$baseline/release.txt must name a release and a source commit this clone has (fetch full history)." >&2
    exit 2
fi

echo "=== Behavior baseline check: SqlOS $release baseline ($(git rev-parse --short "$source_commit")) against HEAD ==="

# One tagged record per fact, read by one awk program:
#   current <hash> <approved path>             an approved file at HEAD
#   baseline <hash> <approved path> <file>     a baseline file at HEAD and the approved path it records
#   source <hash> <approved path>              an approved file at the source commit
#   rename <earlier path> <later path>         a renames.txt line
#   named <entry id> <path>                    a path a ledger entry names
# Prints "<hash>\t<path>" for every approved file of a commit: the behavior-lock approvals outside
# the baseline, and the headless snapshot.
approved_files() {
    git ls-tree -r "$1" -- tests/SqlOS.BehaviorLock packages/headless/tests/__snapshots__ | awk -F'\t' -v root="$baseline/" '
        index($2, root) == 1 { next }
        $2 ~ /^tests\/SqlOS\.BehaviorLock\/.*\.verified\.txt$/ || $2 ~ /^packages\/headless\/tests\/__snapshots__\// {
            split($1, f, " ")
            print f[3] "\t" $2
        }'
}

records() {
    approved_files HEAD | awk -F'\t' '{ print "current\t" $1 "\t" $2 }'
    git ls-tree -r HEAD -- "$baseline" | awk -F'\t' -v root="$baseline/" '
        {
            split($1, f, " ")
            path = substr($2, length(root) + 1)
            if (path == "release.txt" || path == "renames.txt") next
            approved = path ~ /^packages\// ? path : "tests/SqlOS.BehaviorLock/" path
            print "baseline\t" f[3] "\t" approved "\t" $2
        }'
    approved_files "$source_commit" | awk -F'\t' '{ print "source\t" $1 "\t" $2 }'
    { git show "HEAD:$baseline/renames.txt" 2>/dev/null || true; } \
        | awk '$1 !~ /^#/ && NF > 0 { if (NF == 3 && $2 == "->") print "rename\t" $1 "\t" $3; else print "malformed\t" $0 }'
    git show "HEAD:$ledger" | ledger_entry_lines | ledger_named_paths | awk -F'\t' '{ print "named\t" $1 "\t" $2 }'
}

report="$(records | awk -F'\t' -v release="$release" '
    function fail(message) { failures[++failure_count] = message }
    function current_of(path,   steps) {
        for (steps = 0; path in later_of; steps++) {
            if (steps > rename_count) return ""
            path = later_of[path]
        }
        return path
    }
    $1 == "current"  { current[$3] = $2; next }
    $1 == "baseline" { recorded[$3] = $2; baseline_file[$3] = $4; baseline_count++; next }
    $1 == "source"   { copied[$3] = $2; next }
    $1 == "malformed" { fail("renames.txt has a malformed line: " $2); next }
    $1 == "rename" {
        rename_count++
        if ($2 in later_of) fail("renames.txt renames " $2 " twice")
        if ($3 in earlier_of) fail("renames.txt renames two files to " $3)
        later_of[$2] = $3
        earlier_of[$3] = $2
        next
    }
    $1 == "named" { named_count++; named_entry[named_count] = $2; named_path[named_count] = $3; next }
    END {
        for (path in copied) {
            if (!(path in recorded)) fail(path " at the source commit has no baseline file")
            else if (copied[path] != recorded[path]) fail(baseline_file[path] " differs from " path " at the source commit")
        }

        for (path in recorded) {
            if (!(path in copied)) added++
            today = current_of(path)
            if (today == "") { fail("renames.txt renames " path " in a cycle"); continue }
            origin[today] = path
            if (today != path) renamed++
            if (!(today in current)) status[today] = "gone"
            else if (current[today] == recorded[path]) status[today] = "same"
            else status[today] = "differs"
        }
        for (path in later_of) {
            if (!(path in recorded) && !(path in earlier_of)) fail("renames.txt renames " path ", which has no baseline")
            if (path in current) fail("renames.txt renames " path ", which still exists")
        }
        for (path in current) {
            total++
            if (!(path in origin)) fail(path " has no baseline: record what SqlOS " release " does with package mode and BEHAVIOR_LOCK_ACCEPT=1")
        }

        for (i = 1; i <= named_count; i++) {
            today = current_of(named_path[i])
            if (!(today in status)) { fail(named_entry[i] " names " named_path[i] ", which is not an approved file with a baseline"); continue }
            if (status[today] == "same") { fail(named_entry[i] " names " named_path[i] ", which matches its baseline"); continue }
            ledgered[today] = 1
            if (!((named_entry[i], today) in counted)) { counted[named_entry[i], today] = 1; per_entry[named_entry[i]]++ }
        }
        for (path in status) {
            if (status[path] == "same") { same++; continue }
            if (status[path] == "gone") gone++; else differs++
            if (!(path in ledgered)) {
                fail(path (status[path] == "gone" ? " is gone (deleted, or a baseline without an approved file)" : " differs from its baseline " baseline_file[origin[path]]) " but no ledger entry names it")
            }
        }

        for (i = 1; i <= failure_count; i++) print "1-fail\t" failures[i]
        printf "2-info\t  Baseline: %d files, %d copied from the source commit byte for byte, %d recorded later for new coverage.\n", baseline_count, baseline_count - added, added
        printf "3-info\t  Approved files: %d, each with a baseline (%d through renames.txt).\n", total, renamed
        printf "4-info\t  Same as the baseline: %d. Different: %d. Gone: %d.\n", same, differs, gone
        for (entry in per_entry) printf "5-info\t  Ledger: %s names %d of them.\n", entry, per_entry[entry]
    }')"

failures=0
while IFS=$'\t' read -r kind message; do
    case "$kind" in
        1-fail) echo "  FAIL: $message"; failures=$((failures + 1)) ;;
        *-info) echo "$message" ;;
    esac
done < <(sort -t$'\t' -k1,1 -k2 <<< "$report")

# Baseline files never change: only a re-baseline to a new release (release.txt changes) may
# modify, delete, or rename them. New coverage adds baseline files, and renames.txt only grows.
if git cat-file -e "$merge_base:$baseline/release.txt" 2>/dev/null; then
    if [ "$(git show "$merge_base:$baseline/release.txt")" != "$release_file" ]; then
        echo "  release.txt changed since $base_ref: this pull request re-baselines, so baseline files may change."
    else
        immutable=0
        while IFS=$'\t' read -r status path renamed_to; do
            case "$status" in
                A) ;;
                M) [ "$path" = "$baseline/renames.txt" ] || { echo "  FAIL: baseline file $path was modified"; immutable=$((immutable + 1)); } ;;
                D) echo "  FAIL: baseline file $path was deleted"; immutable=$((immutable + 1)) ;;
                *) echo "  FAIL: baseline file $path changed ($status${renamed_to:+ to $renamed_to})"; immutable=$((immutable + 1)) ;;
            esac
        done < <(git diff --name-status -M100% "$merge_base" HEAD -- "$baseline")
        if git diff -U0 "$merge_base" HEAD -- "$baseline/renames.txt" | grep -qE '^-[^-]'; then
            echo "  FAIL: renames.txt lost or changed a line; it only grows"
            immutable=$((immutable + 1))
        fi
        failures=$((failures + immutable))
        if [ "$immutable" -eq 0 ]; then
            echo "  Against $base_ref ($(git rev-parse --short "$merge_base")): no baseline file modified, deleted, or renamed; renames.txt only grew."
        fi
    fi
else
    echo "  $base_ref ($(git rev-parse --short "$merge_base")) has no baseline yet: every baseline file is new."
fi

if [ "$failures" -gt 0 ]; then
    echo "Behavior baseline check failed with $failures problem(s). See tests/SqlOS.BehaviorLock/README.md and $ledger." >&2
    exit 1
fi

echo "=== Behavior baseline check passed ==="
