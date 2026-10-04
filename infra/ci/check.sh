#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
npm ci
bash infra/ci/check-basic.sh
dotnet test tests/PlanSafe.Tests/PlanSafe.Tests.csproj --no-build --no-restore -c Debug \
    -p:RunAOTCompilation=false -p:PublishAot=false \
    --logger 'trx;LogFileName=unit.trx' --results-directory artifacts/test-results/unit
npm run test:unit
shellcheck infra/ci/*.sh infra/mikrus/*.sh
python3 infra/mikrus/test_deploy.py
python3 infra/ci/test_upload.py
