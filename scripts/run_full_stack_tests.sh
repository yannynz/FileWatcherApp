#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

echo "==> Running FileWatcherApp unit tests"
dotnet test "${ROOT_DIR}/tests/FileWatcherApp.Tests/FileWatcherApp.Tests.csproj" --nologo

echo "==> Running organizador-producao service tests"
pushd "${ROOT_DIR}/../organizador-producao" >/dev/null
./mvnw test -Dtest=OrganizadorProducaoApplicationTests
popd >/dev/null

echo "==> All suites finished"
