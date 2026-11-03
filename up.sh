#!/usr/bin/env bash
# Starts Great SSO on docker compose (docker-compose.yml), waits for the host and prints its address and how a person
# and an agent sign in. The agent's MCP token is made once and kept in .agent/mcp-token (git-ignored).
#
#   ./up.sh              build the images and start everything (only what changed is recreated)
#   ./up.sh --no-build   start with the images already built
set -euo pipefail
cd "$(dirname "$0")"

BUILD=(--build)
for arg in "$@"; do
  case "$arg" in
    --no-build) BUILD=() ;;
    -h|--help) sed -n '2,/^set /{/^#/p}' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $arg (see --help)" >&2; exit 2 ;;
  esac
done

# .env is optional; read it here too, for what this script prints and decides.
if [[ -f .env ]]; then
  set -a; . ./.env; set +a
fi
PORT="${SSO_PORT:-6100}"
URL="http://localhost:${PORT}"
ADMIN_USERNAME="${BootstrapAdmin__Username:-admin}"
ADMIN_EMAIL="${BootstrapAdmin__Email:-admin@example.com}"

# A real provider in .env -> no development stub (ADR 0018: the stub replaces every provider).
if [[ -n "${Providers__GitHub__ClientId:-}${Providers__Google__ClientId:-}${Providers__Microsoft__ClientId:-}" ]]; then
  export DevSignIn__Enabled=false
fi
STUB="${DevSignIn__Enabled:-true}"

docker compose up -d "${BUILD[@]}" --remove-orphans

# One-shot steps first: a failed one never lets the host start, so say which and show its log.
echo "Waiting for the host (${URL})..."
deadline=$((SECONDS + 300))
while :; do
  for step in db-prepare liquibase db-finish; do
    id=$(docker compose ps -aq "$step" 2>/dev/null || true)
    [[ -z "$id" ]] && continue
    state=$(docker inspect --format '{{.State.Status}} {{.State.ExitCode}}' "$id")
    if [[ "$state" == exited* && "$state" != "exited 0" ]]; then
      echo "Step '$step' failed:" >&2
      docker compose logs --no-log-prefix --tail=40 "$step" >&2
      exit 1
    fi
  done
  api=$(docker compose ps -aq api 2>/dev/null || true)
  if [[ -n "$api" ]]; then
    health=$(docker inspect --format '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}}' "$api")
    case "$health" in
      "running healthy") break ;;
      exited*|dead*)
        echo "The host stopped:" >&2
        docker compose logs --no-log-prefix --tail=60 api >&2
        exit 1 ;;
    esac
  fi
  if (( SECONDS > deadline )); then
    echo "The host is not healthy after 5 minutes. ./logs.sh api" >&2
    exit 1
  fi
  sleep 2
done

# The agent's MCP token: reuse the saved one while it is valid for an active admin, otherwise make a new one. Only its
# SHA-256 goes to identity.mcp_tokens - the same row the panel's Claude Code page writes (ADR 0013).
psql_sso() {
  docker compose exec -T postgres psql -U postgres -d sso -v ON_ERROR_STOP=1 -qAt "$@"
}
TOKEN_FILE=.agent/mcp-token
token=""
if [[ -s "$TOKEN_FILE" ]]; then
  token=$(<"$TOKEN_FILE")
  valid=$(psql_sso -v token="$token" <<'SQL'
SELECT count(*) FROM identity.mcp_tokens t JOIN identity.users u ON u.id = t.user_id
WHERE t.token_hash = sha256(convert_to(:'token', 'UTF8'))
  AND t.revoked_at IS NULL AND (t.expires_at IS NULL OR t.expires_at > now())
  AND u.role = 'admin' AND u.status = 'active';
SQL
)
  [[ "$valid" == "1" ]] || token=""
fi
if [[ -z "$token" ]]; then
  # mcp_ + 256 random bits in base64url, as the panel makes it (Secrets.NewToken).
  token="mcp_$(head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n')"
  owner=$(psql_sso -v token="$token" -v admin="$ADMIN_USERNAME" <<'SQL'
WITH admin AS (
  SELECT id, username FROM identity.users
  WHERE role = 'admin' AND status = 'active'
  ORDER BY (username = :'admin') DESC, created_at
  LIMIT 1
), issued AS (
  INSERT INTO identity.mcp_tokens (user_id, name, token_hash, expires_at)
  SELECT id, 'Local agent (up.sh)', sha256(convert_to(:'token', 'UTF8')), now() + interval '365 days' FROM admin
  RETURNING user_id
)
SELECT a.username FROM issued JOIN admin a ON a.id = issued.user_id;
SQL
)
  if [[ -z "$owner" ]]; then
    echo "No active admin in the database - no MCP token for the agent." >&2
    token=""
  else
    mkdir -p .agent
    (umask 077; printf '%s' "$token" > "$TOKEN_FILE")
  fi
fi

echo
docker compose ps --format 'table {{.Service}}\t{{.Status}}'
cat <<EOF

Great SSO is running
  Panel          ${URL}/admin
  API reference  ${URL}/scalar
  OIDC           ${URL}/.well-known/openid-configuration
  MCP            ${URL}/mcp
  MailPit        http://localhost:${SSO_MAILPIT_PORT:-6125}
  RabbitMQ       http://localhost:${SSO_RABBITMQ_UI_PORT:-6172}   (sso / ${SSO_RABBITMQ_PASSWORD:-sso})
  PostgreSQL     localhost:${SSO_PG_PORT:-6132}   (postgres / ${SSO_PG_ADMIN_PASSWORD:-postgres}, database sso)

Sign in (person or browser agent)
EOF
if [[ "$STUB" == "true" ]]; then
  cat <<EOF
  ${URL}/admin -> any provider button -> the development stub asks for an e-mail
  E-mail         ${ADMIN_EMAIL}   (admin; no password - nobody has one in Great SSO)
EOF
else
  echo "  ${URL}/admin through a provider from .env, with the admin's provider e-mail (${ADMIN_EMAIL})"
fi
if [[ -n "$token" ]]; then
  cat <<EOF

Agent (Claude Code over MCP, as ${ADMIN_USERNAME}; token in ${TOKEN_FILE}, valid 365 days)
  Token          ${token}
  claude mcp add --transport http great-sso ${URL}/mcp --header "Authorization: Bearer ${token}"
EOF
fi
