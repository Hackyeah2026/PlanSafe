#!/usr/bin/env bash
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "$0")" && pwd)
# shellcheck source-path=SCRIPTDIR
# shellcheck source=../mikrus/config.sh
source "$script_dir/../mikrus/config.sh"
validate_public_url
origin=${MIKRUS_PUBLIC_URL%/}
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
check() {
    local code
    # No redirects: a 3xx is a failure, never an insecure follow-up hop.
    code=$(curl --silent --show-error --proto '=https' --connect-timeout 5 --max-time 10 \
        -o "$work/web" -w '%{http_code}' "$origin/") || return 1
    if ! [[ $code == 200 ]] || ! grep -q 'PlanSafe' "$work/web" || ! grep -qi '<html' "$work/web"; then
        return 1
    fi
    code=$(curl --silent --show-error --proto '=https' --connect-timeout 5 --max-time 10 \
        -o "$work/health" -w '%{http_code}' "$origin/api/health") || return 1
    [[ $code == 200 && $(<"$work/health") == OK ]]
}
for ((attempt=1; attempt<=6; attempt++)); do
    if check; then echo 'Public HTTPS web/API smoke passed'; exit 0; fi
    if ((attempt < 6)); then sleep 5; fi
done
echo 'Public HTTPS check failed: inspect provider routing and MIKRUS_PUBLIC_URL. Locally healthy deployment is NOT rolled back.' >&2
exit 1
