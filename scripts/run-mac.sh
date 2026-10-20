#!/bin/bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
exec dotnet run --project "$repo_dir/src/CellPort.App/CellPort.App.csproj" -- "$@"
