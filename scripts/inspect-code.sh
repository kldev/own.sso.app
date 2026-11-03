#!/usr/bin/env bash
# JetBrains code inspection (the same engine as Rider) over the whole solution. Fails when it reports anything
# at SUGGESTION level or above - fix the code; switch a rule off only in Sso.slnx.DotSettings, with a reason.
#
# Requires the global tool: dotnet tool install -g JetBrains.ReSharper.GlobalTools
#   ./scripts/inspect-code.sh             build + inspect
#   ./scripts/inspect-code.sh --no-build  inspect the last build (hooks run it after their own build)
set -euo pipefail

cd "$(dirname "$0")/.."

if ! command -v jb >/dev/null 2>&1; then
  echo "jb not found - install: dotnet tool install -g JetBrains.ReSharper.GlobalTools" >&2
  echo "(and make sure ~/.dotnet/tools is on PATH)" >&2
  exit 1
fi

if [[ "${1:-}" != "--no-build" ]]; then
  dotnet build Sso.slnx --nologo -v q -m:2
fi

report="$(mktemp)"
trap 'rm -f "$report"' EXIT

# Generated code (Kiota client, Lucide icons, EF migrations) and the shared observability libraries (libs/README.md):
# they would only report the parts Great SSO does not use (yet).
excluded="apps/Sso.ApiClient/Generated/**;apps/Sso.Api/Components/Ui/LucideIcons.g.cs;libs/Sso.Data/Migrations/**"
excluded+=";libs/Sso.Observability/**;libs/Sso.Observability.AspNetCore/**"

jb inspectcode Sso.slnx --no-build --format=Text --severity=SUGGESTION --exclude="$excluded" \
  --output="$report" >/dev/null

issues="$(grep -cE '\.(cs|razor):[0-9]+ ' "$report" || true)"
if (( issues > 0 )); then
  cat "$report"
  echo
  echo "$issues code inspection issue(s) - fix them (Rider: Code | Inspect Code shows the same list)." >&2
  exit 1
fi

echo "Code inspection: no issues."
