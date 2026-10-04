#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
dotnet test tests/PlanSafe.Api.Tests/PlanSafe.Api.Tests.csproj -c Debug \
    -p:PublishAot=false -p:TreatWarningsAsErrors=true \
    --logger 'trx;LogFileName=api.trx' --results-directory artifacts/test-results/api
