#!/usr/bin/env bash
# Follows the logs of Great SSO's containers.
#
#   ./logs.sh                  every service, the last 100 lines each, then follow
#   ./logs.sh api runner       only these (api, runner, postgres, rabbitmq, mailpit, db-prepare, liquibase, db-finish)
#   TAIL=500 ./logs.sh api     more history
set -euo pipefail
cd "$(dirname "$0")"

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  sed -n '2,/^set /{/^#/p}' "$0" | sed 's/^# \{0,1\}//'
  exit 0
fi
exec docker compose logs --follow --tail="${TAIL:-100}" "$@"
