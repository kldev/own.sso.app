#!/usr/bin/env bash
# Turns an EF Core migration (libs/Sso.Data/Migrations) into a Liquibase changeset (ADR 0003): the model is the
# source of the schema, Liquibase is the only thing that applies it.
#
#   dotnet ef migrations add <Name> --project libs/Sso.Data --startup-project libs/Sso.Data --output-dir Migrations
#   db/scripts/export-ef-migration.sh <Name> <slug>     e.g. AddPhone people-add-phone
#
# Writes db/changelog/<yyyy>/1-ddl/<yyyymmdd>-<nn>-ef-<slug>.sql: the up script of `dotnet ef migrations script
# <previous> <Name> --no-transactions` (Liquibase runs the changeset in a transaction), and as --rollback the script
# back to the previous migration. EF's history table never reaches the database - its statements are dropped here;
# the history is Liquibase's databasechangelog. The changeset carries "--comment: ef-migration <id>", which the
# test SchemaTests.Every_migration_has_its_exported_changeset compares with the model. Adds the file to
# db/CHANGELOG.md. Run it once per migration; a changeset applied anywhere is never edited (a new migration fixes it).
set -euo pipefail
cd "$(dirname "$0")/../.."

name="${1:?usage: export-ef-migration.sh <MigrationName> <slug>}"
slug="${2:?usage: export-ef-migration.sh <MigrationName> <slug>}"
[[ "$slug" =~ ^[a-z][a-z0-9-]*$ ]] || { echo "slug: lower case letters, digits and dashes" >&2; exit 2; }

ef() { dotnet tool run dotnet-ef "$@" --project libs/Sso.Data --startup-project libs/Sso.Data; }

dotnet build libs/Sso.Data -m:2 --nologo -v q >/dev/null
ids=$(ef migrations list --no-connect --no-build --json 2>/dev/null \
    | python3 -c 'import json,sys; print("\n".join(m["id"] for m in json.load(sys.stdin)))')
id=$(grep -E "_${name}\$" <<<"$ids" || true)
[[ -n "$id" ]] || { echo "No migration named $name in libs/Sso.Data/Migrations." >&2; exit 1; }
previous=$(grep -B1 -xF "$id" <<<"$ids" | grep -vxF "$id" || true)
from="${previous:-0}"

if grep -rqF -- "--comment: ef-migration $id" db/changelog; then
    echo "$id is exported already: $(grep -rlF -- "--comment: ef-migration $id" db/changelog)" >&2
    exit 1
fi

# Drops every statement that touches EF's history table; statements are separated by blank lines in EF's scripts.
strip_history() {
    python3 -c '
import re, sys
text = sys.stdin.read()
blocks = re.split(r"\n\s*\n", text)
kept = [b.strip("\n") for b in blocks if b.strip() and "__EFMigrationsHistory" not in b]
sys.stdout.write("\n\n".join(kept) + "\n")
'
}

up=$(ef migrations script "$from" "$id" --no-transactions --no-build 2>/dev/null | strip_history)
down=$(ef migrations script "$id" "$from" --no-transactions --no-build 2>/dev/null | strip_history)
if grep -q "__EFMigrationsHistory" <<<"$up$down"; then
    echo "EF's history table is still in the script - export stopped." >&2
    exit 1
fi

day=$(date +%Y%m%d)
year=${day:0:4}
dir="db/changelog/$year/1-ddl"
mkdir -p "$dir"
last=$(find "$dir" -maxdepth 1 -name "$day-*.sql" -printf '%f\n' | sed -E 's/^[0-9]{8}-([0-9]{2})-.*/\1/' | sort -n | tail -1)
nn=$(printf '%02d' $((10#${last:-0} + 1)))
file="$dir/$day-$nn-ef-$slug.sql"

{
    echo "--liquibase formatted sql"
    echo
    echo "--changeset sso:$day-$nn-ef-$slug splitStatements:false"
    echo "--comment: ef-migration $id"
    echo "--comment: Exported by db/scripts/export-ef-migration.sh from libs/Sso.Data/Migrations - do not edit by hand."
    echo "$up"
    grep -v '^[[:space:]]*$' <<<"$down" | sed 's/^/--rollback /'
} >"$file"

entry="- \`${file#db/changelog/}\` - EF migration $id"
heading="## $(date +%Y-%m-%d)"
if ! grep -qxF "$heading" db/CHANGELOG.md; then
    printf '\n%s\n\n' "$heading" >>db/CHANGELOG.md
fi
echo "$entry" >>db/CHANGELOG.md
echo "Wrote $file and its entry in db/CHANGELOG.md."
