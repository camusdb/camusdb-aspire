#!/usr/bin/env bash
#
# Pack the CamusDB Aspire integrations, and optionally push them to nuget.org:
#   CamusDB.Aspire.Hosting, CamusDB.Aspire.Client, CamusDB.Aspire.EntityFrameworkCore
#
# Usage:
#   scripts/pack-nuget.sh                               # run the unit tests, then pack <version>
#   VERSION=0.2.0 scripts/pack-nuget.sh                 # pack a different version
#   TESTS=0 scripts/pack-nuget.sh                       # pack without the unit tests
#   PUSH=1 NUGET_API_KEY=... scripts/pack-nuget.sh      # pack, then push to nuget.org
#
# The version is taken from <Version> in Directory.Build.props unless overridden. All three packages
# get the same version. The packages and their symbol packages (.snupkg) go to artifacts/packages.
#
# The unit tests do not need Docker. The functional tests do, and they take minutes, so this script
# does not run them; run them before a release with:
#   dotnet test --project tests/CamusDB.Aspire.Hosting.Tests --filter-trait "Category=Docker"

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

OUTPUT="$REPO_ROOT/artifacts/packages"
SOURCE="${SOURCE:-https://api.nuget.org/v3/index.json}"
PUSH="${PUSH:-0}"
TESTS="${TESTS:-1}"

PACKAGES=(
  CamusDB.Aspire.Hosting
  CamusDB.Aspire.Client
  CamusDB.Aspire.EntityFrameworkCore
)

if [[ -z "${VERSION:-}" ]]; then
  VERSION="$(grep -oE '<Version>[^<]+</Version>' "$REPO_ROOT/Directory.Build.props" | head -n1 | sed -E 's/<\/?Version>//g')"
fi

if [[ -z "$VERSION" ]]; then
  echo "error: could not determine version (set VERSION=...)" >&2
  exit 1
fi

echo "Packages: ${PACKAGES[*]}"
echo "Version:  $VERSION"
echo "Tests:    $TESTS"
echo "Push:     $PUSH"
echo

cd "$REPO_ROOT"

if [[ "$TESTS" == "1" ]]; then
  dotnet test --project tests/CamusDB.Aspire.Hosting.Tests --filter-not-trait "Category=Docker"
  dotnet test --project tests/CamusDB.Aspire.Client.Tests
fi

for package in "${PACKAGES[@]}"; do
  rm -f "$OUTPUT/$package.$VERSION.nupkg" "$OUTPUT/$package.$VERSION.snupkg"
done

# Version (not PackageVersion) also sets the assembly version, so the DLLs match the package.
# ContinuousIntegrationBuild makes the source paths in the symbols independent of this machine.
for package in "${PACKAGES[@]}"; do
  dotnet pack "src/$package/$package.csproj" -c Release \
    -p:Version="$VERSION" \
    -p:ContinuousIntegrationBuild=true \
    -o "$OUTPUT"
done

for package in "${PACKAGES[@]}"; do
  for expected in "$OUTPUT/$package.$VERSION.nupkg" "$OUTPUT/$package.$VERSION.snupkg"; do
    if [[ ! -f "$expected" ]]; then
      echo "error: expected $expected to exist after pack" >&2
      exit 1
    fi
  done
done

echo
echo "Built in $OUTPUT:"
for package in "${PACKAGES[@]}"; do
  echo "  $package.$VERSION.nupkg"
done

if [[ "$PUSH" != "1" ]]; then
  echo
  echo "PUSH=0: packages were not pushed."
  echo "To use them from a local folder: dotnet nuget add source \"$OUTPUT\" --name camusdb-aspire-local"
  exit 0
fi

if [[ -z "${NUGET_API_KEY:-}" ]]; then
  echo "error: NUGET_API_KEY is not set" >&2
  exit 1
fi

# The push of a .nupkg also pushes the .snupkg next to it. --skip-duplicate lets a rerun finish a
# push that failed part of the way, because nuget.org refuses a version that it already has.
for package in "${PACKAGES[@]}"; do
  dotnet nuget push "$OUTPUT/$package.$VERSION.nupkg" --source "$SOURCE" --api-key "$NUGET_API_KEY" --skip-duplicate
done

echo
echo "Done: ${PACKAGES[*]} $VERSION"
