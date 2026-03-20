#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACT_DIR="$ROOT_DIR/artifacts/packages"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
export NUGET_PACKAGES="$WORK_DIR/.nuget/packages"
export NUGET_HTTP_CACHE_PATH="$WORK_DIR/.nuget/http-cache"
export NUGET_PLUGINS_CACHE_PATH="$WORK_DIR/.nuget/plugins-cache"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

grep_extended() {
  grep -E "$@"
}

read_zip_entry() {
  local archive="$1"
  local pattern="$2"
  local entry
  entry="$(zipinfo -1 "$archive" | grep_extended "$pattern" | head -n 1 || true)"
  if [[ -z "$entry" ]]; then
    echo "Missing archive entry matching '$pattern' in $archive" >&2
    exit 1
  fi

  unzip -p "$archive" "$entry"
}

verify_package_metadata() {
  local package_name="$1"
  local package_file
  package_file="$(find "$ARTIFACT_DIR" -maxdepth 1 -name "$package_name.[0-9]*.nupkg" | head -n 1)"
  if [[ -z "$package_file" ]]; then
    echo "Package not found for metadata verification: $package_name" >&2
    exit 1
  fi

  local nuspec
  nuspec="$(read_zip_entry "$package_file" '\.nuspec$')"
  local readme_path
  readme_path="$(printf '%s' "$nuspec" | grep_extended -o '<readme>[^<]+</readme>' | sed -E 's#</?readme>##g')"

  if [[ -z "$readme_path" ]]; then
    echo "Package $package_name is missing nuspec <readme> metadata." >&2
    exit 1
  fi

  [[ "$nuspec" == *"<id>$package_name</id>"* ]] || {
    echo "Package $package_name has unexpected nuspec id." >&2
    exit 1
  }

  [[ "$nuspec" == *"<version>0.1.0-preview.1</version>"* ]] || {
    echo "Package $package_name has unexpected nuspec version." >&2
    exit 1
  }

  printf '%s' "$nuspec" | grep_extended -q '<description>.+</description>' || {
    echo "Package $package_name is missing nuspec description metadata." >&2
    exit 1
  }

  unzip -p "$package_file" "$readme_path" >/dev/null || {
    echo "Package $package_name is missing the declared readme file $readme_path." >&2
    exit 1
  }

  zipinfo -1 "$package_file" | grep_extended -q '^lib/.+\.xml$' || {
    echo "Package $package_name is missing XML documentation output." >&2
    exit 1
  }
}

require_command dotnet
require_command unzip
require_command zipinfo

echo "Packing library packages into $ARTIFACT_DIR"
rm -rf "$ARTIFACT_DIR"
dotnet restore "$ROOT_DIR/ProgrammaticMcp.sln" >/dev/null
dotnet pack "$ROOT_DIR/src/ProgrammaticMcp/ProgrammaticMcp.csproj" --configuration Release --no-restore
dotnet pack "$ROOT_DIR/src/ProgrammaticMcp.Jint/ProgrammaticMcp.Jint.csproj" --configuration Release --no-restore
dotnet pack "$ROOT_DIR/src/ProgrammaticMcp.AspNetCore/ProgrammaticMcp.AspNetCore.csproj" --configuration Release --no-restore

verify_package_metadata ProgrammaticMcp
verify_package_metadata ProgrammaticMcp.Jint
verify_package_metadata ProgrammaticMcp.AspNetCore

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
