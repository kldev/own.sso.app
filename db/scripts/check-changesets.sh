#!/usr/bin/env bash
# Rules of db/changelog (ADR 0003), run by the pre-commit hook and ./verify schema:
#   - every changeset is a dated file changelog/<yyyy>/{1-ddl,2-data}/<yyyymmdd>-<nn>-<slug>.sql with one changeset
#     whose id is the file's name, a --comment, a --rollback, and an entry in CHANGELOG.md;
#   - no logic in the database: no function, procedure, view, trigger, policy or rule, no SECURITY DEFINER, no
#     runOnChange - rules live in C# (libs/Sso.Data, apps/Sso.Api);
#   - EF's history table never reaches the database (the history is Liquibase's databasechangelog);
#   - an *-ef-* changeset names the EF migration it was exported from, and that migration exists.
#   ./db/scripts/check-changesets.sh               the rules above
#   ./db/scripts/check-changesets.sh --base main   also: no changeset of <base> modified, renamed or deleted
set -euo pipefail

cd "$(dirname "$0")/.."

base=""
if [[ "${1:-}" == "--base" ]]; then
  base="${2:?--base needs a git ref}"
fi

errors=0
fail() { echo "ERROR: $*" >&2; errors=$((errors + 1)); }

# SQL without comment lines - rules must not fire on text in --comment: or -- notes.
code() { grep -v '^[[:space:]]*--' "$1" || true; }

while IFS= read -r file; do
  rel="${file#changelog/}"
  name="$(basename "$file" .sql)"
  [[ "$rel" =~ ^[0-9]{4}/(1-ddl|2-data)/[0-9]{8}-[0-9]{2}-[a-z][a-z0-9-]*\.sql$ ]] \
    || fail "$file: changesets are changelog/<yyyy>/{1-ddl,2-data}/<yyyymmdd>-<nn>-<slug>.sql"
  [[ "$(head -n 1 "$file")" == "--liquibase formatted sql" ]] || fail "$file: first line must be '--liquibase formatted sql'"
  [[ "$(grep -c '^--changeset ' "$file" || true)" == "1" ]] || fail "$file: exactly one changeset per file"
  grep -q "^--changeset sso:${name}\b" "$file" || fail "$file: the changeset id must be sso:$name"
  grep -q '^--comment: ' "$file" || fail "$file: no '--comment:' - say why the change exists"
  grep -q '^--rollback ' "$file" || fail "$file: no '--rollback'"
  grep -qF "$rel" CHANGELOG.md || fail "$file: not in CHANGELOG.md (add an entry with '$rel')"
  grep -qi 'runOnChange' "$file" && fail "$file: runOnChange - changesets are dated and never edited once applied"
  code "$file" | grep -qiP '\bCREATE\s+(OR\s+REPLACE\s+)?(FUNCTION|PROCEDURE|VIEW|MATERIALIZED\s+VIEW|TRIGGER|POLICY|RULE)\b' \
    && fail "$file: no functions, procedures, views, triggers, policies or rules - the logic is in C# (ADR 0003)"
  code "$file" | grep -qiP '\bSECURITY\s+DEFINER\b|\bSET\s+(LOCAL\s+)?ROLE\b|\bROW\s+LEVEL\s+SECURITY\b' \
    && fail "$file: SECURITY DEFINER, SET ROLE and row level security have no place here"
  grep -q '__EFMigrationsHistory' "$file" && fail "$file: EF's history table - export with db/scripts/export-ef-migration.sh"
  if [[ "$name" == *-ef-* ]]; then
    id="$(sed -n 's/^--comment: ef-migration \([0-9]\{14\}_[A-Za-z0-9_]*\)$/\1/p' "$file")"
    if [[ -z "$id" ]]; then
      fail "$file: an EF changeset names its migration (--comment: ef-migration <id>)"
    elif [[ ! -f "../libs/Sso.Data/Migrations/$id.cs" ]]; then
      fail "$file: migration $id is not in libs/Sso.Data/Migrations"
    fi
  fi
done < <(find changelog -name '*.sql' | sort)

# Every migration of the model is exported.
for migration in ../libs/Sso.Data/Migrations/[0-9]*_*.cs; do
  [[ "$migration" == *.Designer.cs ]] && continue
  id="$(basename "$migration" .cs)"
  grep -rqF -- "--comment: ef-migration $id" changelog \
    || fail "migration $id has no changeset - run db/scripts/export-ef-migration.sh ${id#*_} <slug>"
done

if [[ -n "$base" ]]; then
  while IFS=$'\t' read -r status path _; do
    [[ "$path" == db/changelog/*.sql ]] || continue
    [[ "$status" == A ]] && continue
    fail "$path: a changeset of $base was changed ($status) - add a new one instead"
  done < <(git diff --name-status "$base" -- changelog)
fi

if ((errors > 0)); then
  echo "$errors problem(s) in db/changelog." >&2
  exit 1
fi
echo "All changesets follow the rules."
