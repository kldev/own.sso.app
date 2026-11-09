#!/usr/bin/env bash
# Regenerates the C# client of the REST API (Kiota) from openapi.json - the API's OpenAPI document (/openapi/v1.json),
# written at build time by Microsoft.Extensions.ApiDescription.Server and kept in git, so a change of the API shows up
# in review next to the code that made it. The Blazor panel calls /api only through this client (ADR 0002).
#
#   ./generate.sh            write openapi.json from the code, then regenerate Generated/
#   ./generate.sh --no-doc   regenerate Generated/ from the committed openapi.json only
#
# After a change of an endpoint, a request or a row type in apps/Sso.Api/Features: run it and commit both.
set -euo pipefail

cd "$(dirname "$0")"

if [[ "${1:-}" != "--no-doc" ]]; then
  # The document is made by starting the host's configuration without serving: Development settings, no seed.
  ASPNETCORE_ENVIRONMENT=Development Seed__Enabled=false \
    dotnet build ../Sso.Api --nologo -v q -p:GenerateOpenApi=true >/dev/null
  python3 -m json.tool openapi.json > openapi.json.tmp && mv openapi.json.tmp openapi.json
  echo "openapi.json written from apps/Sso.Api"
fi

dotnet tool restore >/dev/null
dotnet kiota generate \
  --language csharp \
  --openapi openapi.json \
  --class-name SsoApi \
  --namespace-name Sso.ApiClient.Generated \
  --output Generated \
  --exclude-backward-compatible \
  --clean-output
