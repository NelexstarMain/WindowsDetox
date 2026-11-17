#!/usr/bin/env bash
# Appends every symbol reported by RS0016 (not part of declared public API) to PublicAPI.Unshipped.txt.
# Usage: eng/update-public-api.sh src/Blocker.SDK
# Review the resulting diff: every new line is a public API commitment.
set -euo pipefail
proj_dir="${1:?project directory required}"
log="$(mktemp)"
trap 'rm -f "$log"' EXIT
dotnet build "$proj_dir" --no-incremental -p:TreatWarningsAsErrors=false >"$log" 2>&1 || true
unshipped="$proj_dir/PublicAPI.Unshipped.txt"
grep -o "RS0016: Symbol '.*' is not part of the declared public API" "$log" \
  | sed -E "s/^RS0016: Symbol '(.*)' is not part of the declared public API$/\1/" \
  | sort -u >>"$unshipped.new" || true
{ head -n1 "$unshipped"; { tail -n +2 "$unshipped"; cat "$unshipped.new"; } | sort -u; } >"$unshipped.tmp"
mv "$unshipped.tmp" "$unshipped"
rm -f "$unshipped.new"
echo "$(($(wc -l <"$unshipped") - 1)) entries in $unshipped"
