#!/usr/bin/env bash
# Stops Great SSO started by ./up.sh.
#
#   ./down.sh           stop and remove the containers; the database, the broker and the keys stay
#   ./down.sh --purge   also drop the volumes (database, RabbitMQ, signing keys) and the agent's token - the next
#                       ./up.sh seeds from scratch
set -euo pipefail
cd "$(dirname "$0")"

case "${1:-}" in
  "")
    docker compose down --remove-orphans ;;
  --purge)
    docker compose down --volumes --remove-orphans
    rm -f .agent/mcp-token
    echo "Volumes and .agent/mcp-token removed." ;;
  -h|--help)
    sed -n '2,/^set /{/^#/p}' "$0" | sed 's/^# \{0,1\}//' ;;
  *)
    echo "Unknown option: $1 (see --help)" >&2; exit 2 ;;
esac
