#!/usr/bin/env bash
set -euo pipefail
[[ $(id -u) == 0 && $(uname -m) == x86_64 ]] || exit 1
# shellcheck source=/dev/null
source /etc/os-release
[[ $ID:$VERSION_ID == debian:13 ]] || {
  echo 'Requires Debian 13' >&2
  exit 1
}
for path in /srv/plansafe /srv/plansafe/releases /srv/plansafe/data; do
  [[ ! -L $path ]] || { echo "Refusing symlinked storage: $path" >&2; exit 1; }
done
script_dir=$(cd -- "$(dirname -- "$0")" && pwd)
# Full Python standard library is needed for tarfile/hashlib; minimal alone is insufficient.
packages=(nginx curl python3 ca-certificates libc6 libgcc-s1 libgssapi-krb5-2 libicu76 libssl3t64 libstdc++6 zlib1g)
missing=()
for package in "${packages[@]}"; do
  [[ $(dpkg-query -W -f='${Status}' "$package" 2>/dev/null || true) == 'install ok installed' ]] || missing+=("$package")
done
if ((${#missing[@]})); then
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "${missing[@]}"
fi
id plansafe >/dev/null 2>&1 || useradd --system --user-group --home-dir /srv/plansafe/data --shell /usr/sbin/nologin plansafe
install -d -o root -g root -m 0755 /srv/plansafe /srv/plansafe/releases
install -d -o plansafe -g plansafe -m 0700 /srv/plansafe/data
# Do not recurse over persistent data or modify SSH/root policy.
install -o root -g root -m 0644 "$script_dir/plansafe-api.service" /etc/systemd/system/plansafe-api.service
install -o root -g root -m 0644 "$script_dir/nginx.conf" /etc/nginx/sites-available/plansafe.conf
# Only remove Debian's default site. Other sites are not silently deleted.
rm -f /etc/nginx/sites-enabled/default
ln -sfn /etc/nginx/sites-available/plansafe.conf /etc/nginx/sites-enabled/plansafe.conf
install -d -m 0755 /etc/systemd/journald.conf.d
printf '[Journal]\nSystemMaxUse=32M\nRuntimeMaxUse=8M\nMaxRetentionSec=7day\n' >/etc/systemd/journald.conf.d/plansafe.conf
nginx -t
systemctl daemon-reload
systemctl enable nginx plansafe-api
systemctl restart systemd-journald
# Activation, not provisioning, reloads nginx and restarts the API.
