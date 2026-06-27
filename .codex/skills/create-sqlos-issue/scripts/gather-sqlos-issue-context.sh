#!/usr/bin/env bash
set -uo pipefail

repo="${GH_REPO:-ross-slaney/sqlos}"
terms=("$@")

echo "== Repository =="
git rev-parse --show-toplevel 2>/dev/null || true
git status --short --branch 2>/dev/null || true
git log --oneline -1 2>/dev/null || true
echo

echo "== Recent issues =="
gh issue list --repo "$repo" --state all --limit 35 \
  --json number,title,state,labels \
  --jq '.[] | "#\(.number) [\(.state)] \(.title) :: \([.labels[].name] | join(", "))"' 2>/dev/null || true
echo

echo "== Labels =="
gh label list --repo "$repo" --limit 100 --json name,description \
  --jq '.[] | "\(.name)\t\(.description // "")"' 2>/dev/null || true
echo

echo "== Milestones =="
gh api "repos/$repo/milestones" --jq '.[] | "#\(.number) \(.title) [\(.state)] due=\(.due_on // "none")"' 2>/dev/null || true
echo

if [ "${#terms[@]}" -eq 0 ]; then
  echo "== Search =="
  echo "No search terms supplied."
  exit 0
fi

echo "== Targeted issue search =="
for term in "${terms[@]}"; do
  echo "-- $term"
  gh issue list --repo "$repo" --state all --limit 20 --search "$term in:title,body" \
    --json number,title,state,labels \
    --jq '.[] | "#\(.number) [\(.state)] \(.title) :: \([.labels[].name] | join(", "))"' 2>/dev/null || true
done
echo

echo "== Repo text search =="
for term in "${terms[@]}"; do
  echo "-- $term"
  rg -n --glob '!bin/**' --glob '!obj/**' --glob '!node_modules/**' --glob '!.git/**' \
    "$term" src tests docs web/content examples README.md 2>/dev/null | sed -n '1,120p' || true
done
