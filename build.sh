#!/usr/bin/env bash
set -euo pipefail
PROJECT_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$PROJECT_ROOT"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_HOME="$PROJECT_ROOT/tools/cli-home"
if [ -x "$PROJECT_ROOT/tools/dotnet/dotnet" ]; then
  TASK_DOTNET="$PROJECT_ROOT/tools/dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1; then
  TASK_DOTNET="$(command -v dotnet)"
else
  mkdir -p tools
  curl --fail --location --silent --show-error https://dot.net/v1/dotnet-install.sh -o tools/dotnet-install.sh
  bash tools/dotnet-install.sh --version 10.0.100 --install-dir "$PROJECT_ROOT/tools/dotnet" --no-path --azure-feed https://builds.dotnet.microsoft.com/dotnet
  TASK_DOTNET="$PROJECT_ROOT/tools/dotnet/dotnet"
fi
"$TASK_DOTNET" run --project tests/ServerWorkbench.Tests -c Release -- "$@"
"$TASK_DOTNET" build src/ServerWorkbench.Desktop/ServerWorkbench.Desktop.csproj -c Release --nologo
python3 tools/package.py
