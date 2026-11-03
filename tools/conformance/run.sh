#!/usr/bin/env bash
# Starts (or with "down" removes) the OpenID Foundation conformance suite locally. The suite is cloned once outside
# the repository; its own docker compose builds and runs it.
set -euo pipefail
dir="${CONFORMANCE_DIR:-$HOME/.cache/great-sso/conformance-suite}"
if [[ "${1:-}" == "down" ]]; then
    (cd "$dir" && docker compose down -v)
    exit 0
fi
[[ -d "$dir" ]] || git clone --depth 1 https://gitlab.com/openid/conformance-suite.git "$dir"
cd "$dir"
# The suite's README: build the server image with Maven in Docker, then compose up.
[[ -f target/fapi-test-suite.jar ]] || docker run --rm -v "$PWD":/usr/src/mymaven -v "$HOME/.m2":/root/.m2 \
    -w /usr/src/mymaven maven:3-eclipse-temurin-17 mvn -q -B clean package -DskipTests=true
docker compose up -d
echo "Conformance suite: https://localhost.emobix.co.uk:8443 (stop: $0 down)"
