#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
stage=$(mktemp -d artifacts/smoke.XXXXXXXX)
trap 'rm -rf -- "$stage"' EXIT
python3 infra/mikrus/archive.py artifacts/release.tar.gz "$stage"
# Docker is used only on the CI runner, never on Mikrus.
docker run --rm -v "$PWD/$stage:/release:ro" debian:trixie-slim bash -euc '
    apt-get update -qq
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends ca-certificates curl libgcc-s1 libgssapi-krb5-2 libicu76 libssl3t64 libstdc++6 zlib1g
    for binary in /release/api/PlanSafe.Api /release/api/libe_sqlite3.so; do
        dependencies=$(ldd "$binary")
        echo "$dependencies"
        [[ $dependencies != *"not found"* ]]
    done
    cd /release/api
    ASPNETCORE_URLS=http://127.0.0.1:5001 ./PlanSafe.Api >/tmp/api.log 2>&1 &
    pid=$!
    trap "kill $pid 2>/dev/null || true" EXIT
    for ((i=0; i<45; i++)); do
        if [[ $(curl --fail --silent --noproxy "*" http://127.0.0.1:5001/api/health) == OK ]]; then
            [[ $(curl --silent -o /dev/null -w "%{http_code}" http://127.0.0.1:5001/api/unknown) == 404 ]]
            exit 0
        fi
        kill -0 "$pid" || { cat /tmp/api.log; exit 1; }
        sleep 1
    done
    cat /tmp/api.log
    exit 1
'
