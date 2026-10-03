#!/usr/bin/env bash
set -euo pipefail
: "${MIKRUS_HOST:?}" "${MIKRUS_SSH_PORT:?}" "${MIKRUS_KEY:?}" "${MIKRUS_KNOWN_HOSTS:?}" "${RELEASE_ID:?}"
[[ $MIKRUS_HOST =~ ^[a-zA-Z0-9.:-]+$ && $MIKRUS_SSH_PORT =~ ^[0-9]+$ && $RELEASE_ID =~ ^[0-9]+-[0-9]+-[a-f0-9]{40}$ ]] || exit 2
ssh_dir=$(mktemp -d)
remote=
trap 'rm -rf -- "$ssh_dir"' EXIT
chmod 700 "$ssh_dir"
printf '%s\n' "$MIKRUS_KEY" > "$ssh_dir/key"
printf '%s\n' "$MIKRUS_KNOWN_HOSTS" > "$ssh_dir/known_hosts"
chmod 600 "$ssh_dir/"*
options=(-i "$ssh_dir/key" -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes -o "UserKnownHostsFile=$ssh_dir/known_hosts" -o ConnectTimeout=15)
remote=$(ssh "${options[@]}" -p "$MIKRUS_SSH_PORT" "root@$MIKRUS_HOST" 'set -eu; umask 077; test "$(df -Pk /root | awk '\''NR==2 {print $4}'\'')" -ge 262144; mktemp -d /root/plansafe.XXXXXXXX')
[[ $remote =~ ^/root/plansafe\.[a-zA-Z0-9]+$ ]] || exit 1
cleanup() {
    ssh "${options[@]}" -p "$MIKRUS_SSH_PORT" "root@$MIKRUS_HOST" "rm -rf -- '$remote'" || true
    rm -rf -- "$ssh_dir"
}
trap cleanup EXIT
bundle_hash=$(sha256sum artifacts/deploy-bundle.tar.gz | cut -d ' ' -f 1)
release_hash=$(sha256sum artifacts/release.tar.gz | cut -d ' ' -f 1)
scp "${options[@]}" -P "$MIKRUS_SSH_PORT" artifacts/deploy-bundle.tar.gz "root@[$MIKRUS_HOST]:$remote/bundle.tar.gz"
# The outer bundle is produced only from trusted main and verified before root extraction.
ssh "${options[@]}" -p "$MIKRUS_SSH_PORT" "root@$MIKRUS_HOST" bash -s -- "$remote" "$bundle_hash" "$RELEASE_ID" "$release_hash" <<'REMOTE'
set -euo pipefail
cd "$1"
[[ $(sha256sum bundle.tar.gz | cut -d ' ' -f 1) == "$2" ]]
tar -xzf bundle.tar.gz --no-same-owner
bash infra/deploy.sh "$3" "$4" "$PWD/release.tar.gz"
REMOTE
