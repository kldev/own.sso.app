#!/usr/bin/env bash
# Registers the conformance suite's two clients in the local Great SSO database (deploy/infrastructure.yml) with new secrets, PKCE off for
# them only (ADR 0012). Prints the secrets once; the database keeps base64url(SHA-256).
set -euo pipefail
pg=$(docker ps --format '{{.Names}}' | grep "${SSO_STACK:-sso-infra}_postgres" | head -1)
su=$(docker exec "$pg" printenv POSTGRES_USER)
callback="https://localhost.emobix.co.uk:8443/test/a/great-sso/callback"
for id in conformance-1 conformance-2; do
    secret=$(python3 -c 'import secrets,base64;print(base64.urlsafe_b64encode(secrets.token_bytes(32)).rstrip(b"=").decode())')
    hash=$(printf '%s' "$secret" | openssl dgst -sha256 -binary | base64 | tr '+/' '-_' | tr -d '=')
    docker exec "$pg" psql -q -U "$su" -d sso -v ON_ERROR_STOP=1 -c "
        INSERT INTO oidc.clients (id, client_id, name, secret_hash, redirect_uris, providers, require_pkce)
        VALUES (uuidv7(), '$id', 'OpenID conformance suite', '$hash', ARRAY['$callback'], ARRAY['github','google'], false)
        ON CONFLICT (client_id) DO UPDATE SET secret_hash = EXCLUDED.secret_hash, secret_set_at = now();" >/dev/null
    echo "$id  $secret"
done
