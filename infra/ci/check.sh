#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
npm ci
bash infra/ci/check-basic.sh
# No empty test project. Run .NET tests when the repository has real test projects.
if grep -rl --include='*.csproj' '<IsTestProject>true</IsTestProject>\|Microsoft.NET.Test.Sdk' src tests 2>/dev/null; then
    dotnet test PlanSafe.slnx --no-build --no-restore -c Debug -p:RunAOTCompilation=false -p:PublishAot=false
fi
npm run test:potential
npm run test:camera
npm run test:gpu
shellcheck infra/ci/*.sh infra/mikrus/*.sh
python3 infra/mikrus/test_deploy.py
python3 infra/ci/test_upload.py
