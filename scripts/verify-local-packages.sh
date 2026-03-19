#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACT_DIR="$ROOT_DIR/artifacts/packages"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
export NUGET_PACKAGES="$WORK_DIR/.nuget/packages"
export NUGET_HTTP_CACHE_PATH="$WORK_DIR/.nuget/http-cache"
export NUGET_PLUGINS_CACHE_PATH="$WORK_DIR/.nuget/plugins-cache"

echo "Packing library packages into $ARTIFACT_DIR"
rm -rf "$ARTIFACT_DIR"
dotnet restore "$ROOT_DIR/ProgrammaticMcp.sln" >/dev/null
dotnet pack "$ROOT_DIR/src/ProgrammaticMcp/ProgrammaticMcp.csproj" --configuration Release --no-restore
dotnet pack "$ROOT_DIR/src/ProgrammaticMcp.Jint/ProgrammaticMcp.Jint.csproj" --configuration Release --no-restore
dotnet pack "$ROOT_DIR/src/ProgrammaticMcp.AspNetCore/ProgrammaticMcp.AspNetCore.csproj" --configuration Release --no-restore

APP_DIR="$WORK_DIR/package-smoke"
dotnet new console -n PackageSmoke --framework net10.0 --output "$APP_DIR" >/dev/null

cat > "$APP_DIR/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$ARTIFACT_DIR" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

dotnet add "$APP_DIR/PackageSmoke.csproj" package ProgrammaticMcp --version 0.1.0-preview.1 --no-restore >/dev/null
dotnet add "$APP_DIR/PackageSmoke.csproj" package ProgrammaticMcp.Jint --version 0.1.0-preview.1 --no-restore >/dev/null
dotnet add "$APP_DIR/PackageSmoke.csproj" package ProgrammaticMcp.AspNetCore --version 0.1.0-preview.1 --no-restore >/dev/null

cat > "$APP_DIR/Program.cs" <<'EOF'
using ProgrammaticMcp;
using ProgrammaticMcp.AspNetCore;
using ProgrammaticMcp.Jint;

var builder = new ProgrammaticMcpBuilder().AllowAllBoundCallers();
var options = new ProgrammaticMcpServerOptions
{
    ExecutorOptions = new JintExecutorOptions()
};

Console.WriteLine($"{builder.GetType().Name}|{options.GetType().Name}|{options.ExecutorOptions.GetType().Name}");
EOF

dotnet restore "$APP_DIR/PackageSmoke.csproj" --configfile "$APP_DIR/NuGet.Config" >/dev/null
dotnet build "$APP_DIR/PackageSmoke.csproj" --configuration Release --no-restore >/dev/null
OUTPUT="$(dotnet run --project "$APP_DIR/PackageSmoke.csproj" --configuration Release --no-build)"

if [[ "$OUTPUT" != "ProgrammaticMcpBuilder|ProgrammaticMcpServerOptions|JintExecutorOptions" ]]; then
  echo "Unexpected consumer output: $OUTPUT" >&2
  exit 1
fi

echo "Local package verification succeeded."
