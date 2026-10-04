#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
npm run typecheck
dotnet restore PlanSafe.slnx
dotnet build PlanSafe.slnx --no-restore -c Debug -p:RunAOTCompilation=false -p:PublishAot=false -p:TreatWarningsAsErrors=true
npm run format:check
