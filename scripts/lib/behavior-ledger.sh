# How docs/architecture/8.0-behavior-ledger.md names approved files, shared by
# scripts/check-behavior-ledger.sh (each pull request ledgers its own changes) and
# scripts/check-behavior-baseline.sh (the ledger names exactly the difference from the baseline).
# Source this file; it defines functions only.

# Prints "<id>\t<line>" for every non-blank line of every entry (a "### BL-NNNN: summary"
# section), the heading included. Blank lines are skipped so appending an entry does not alter
# the previous one.
ledger_entry_lines() {
    awk '
        /^## / { id = ""; next }
        /^### / {
            id = ""
            if ($0 ~ /^### BL-[0-9][0-9][0-9][0-9]: [^ ]/) {
                id = substr($0, 5, 7)
            }
        }
        id != "" && $0 !~ /^[ \t]*$/ { print id "\t" $0 }
    '
}

# Reads "<id>\t<line>" records and prints "<id>\t<path>" for every backticked approved-file path.
ledger_named_paths() {
    awk -F'\t' '{
        id = $1
        line = substr($0, length(id) + 2)
        while (match(line, /`[^` \t]+\.(txt|md|snap|json)`/)) {
            print id "\t" substr(line, RSTART + 1, RLENGTH - 2)
            line = substr(line, RSTART + RLENGTH)
        }
    }'
}
