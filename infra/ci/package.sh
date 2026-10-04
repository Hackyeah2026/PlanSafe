#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
mkdir -p artifacts
stage=$(mktemp -d artifacts/package.XXXXXXXX)
trap 'rm -rf -- "$stage"' EXIT
dotnet publish src/PlanSafe.Api -c Release -r linux-x64 --self-contained true -p:PublishAot=true -o "$stage/api"
dotnet publish src/PlanSafe.App -c Release -p:RunAOTCompilation=true -o "$stage/app"
dotnet publish src/PlanSafe.Worker -c Release -r browser-wasm -p:RunAOTCompilation=true -o "$stage/worker"
# Validate Worker output, but do not expose it as an integrated browser feature.
test -n "$(find "$stage/worker" -name '*.wasm' -print -quit)"
mkdir "$stage/web"
cp -a "$stage/app/wwwroot/." "$stage/web/"
test -n "$(find "$stage/web/_framework" -maxdepth 1 -regextype posix-extended -type f -regex '.*/dotnet(\.[a-z0-9]{10,64})?\.js' -print -quit)"
test -n "$(find "$stage/web/_framework" -name '*.wasm' -print -quit)"
tar --owner=0 --group=0 --numeric-owner -czf artifacts/release.tar.gz -C "$stage" api web
python3 infra/mikrus/archive.py artifacts/release.tar.gz
# CI-only reference artifact; Worker is deliberately excluded from server releases.
tar -czf artifacts/worker-validation.tar.gz -C "$stage/worker" .
cp artifacts/release.tar.gz "$stage/release.tar.gz"
mkdir "$stage/infra"
cp infra/mikrus/{deploy.sh,provision.sh,config.sh,archive.py,nginx.conf,plansafe-api.service} "$stage/infra/"
tar -czf artifacts/deploy-bundle.tar.gz -C "$stage" release.tar.gz infra
du -h artifacts/{release,worker-validation,deploy-bundle}.tar.gz
