#!/usr/bin/env bash
set -euo pipefail
# Run from a unique, root-owned incoming directory containing the verified bundle.
[[ $(id -u) == 0 && $# == 3 ]] || exit 2
id=$1
expected=$2
archive=$3
[[ $id =~ ^([0-9]+)-([0-9]+)-([a-f0-9]{40})$ && $expected =~ ^[a-f0-9]{64}$ ]] || exit 2
[[ $id =~ ^([0-9]+)-([0-9]+)-([a-f0-9]{40})$ ]]
run=${BASH_REMATCH[1]}
attempt=${BASH_REMATCH[2]}
root=/srv/plansafe
script_dir=$(cd -- "$(dirname -- "$0")" && pwd)
for path in "$root" "$root/releases" "$root/data" "$root/deploy.lock" "$root/deployed"; do
    [[ ! -L $path ]] || { echo "Refusing symlinked storage: $path" >&2; exit 1; }
done
install -d -m 0755 "$root"
exec 9>"$root/deploy.lock"
flock -x 9
for path in "$root/current.next" "$root/current.rollback" "$root/deployed.next"; do
    [[ ! -e $path && ! -L $path ]] || { echo "Unexpected activation temporary: $path" >&2; exit 1; }
done
if [[ -f $root/deployed ]]; then
    read -r previous_run previous_attempt < "$root/deployed"
    if ((run < previous_run || (run == previous_run && attempt <= previous_attempt))); then
        echo 'Skipping stale/already deployed run'
        exit 0
    fi
fi
[[ -f $archive && ! -L $archive ]] || exit 1
[[ $(sha256sum "$archive" | cut -d ' ' -f 1) == "$expected" ]] || { echo 'Integrity check failed' >&2; exit 1; }
# Install missing prerequisites before archive validation; no package upgrades.
# Python may not exist yet. Provision is transactional with config snapshots below.
install -d -m 0755 "$root/releases"
old=$(readlink "$root/current" || true)
[[ ! -e $root/current || -L $root/current ]] || exit 1
if [[ -n $old ]]; then
    [[ $old =~ ^releases/[0-9]+-[0-9]+-[a-f0-9]{40}$ && -d $root/$old && ! -L $root/$old ]] || exit 1
fi
release=$root/releases/$id
[[ ! -e $release && ! -L $release ]] || { echo 'Release already exists' >&2; exit 1; }
backup=$(mktemp -d "$root/.config.XXXXXXXX")
configs=(/etc/nginx/sites-available/plansafe.conf /etc/nginx/sites-enabled/plansafe.conf /etc/nginx/sites-enabled/default /etc/systemd/system/plansafe-api.service /etc/systemd/journald.conf.d/plansafe.conf)
for i in "${!configs[@]}"; do
    target=${configs[$i]}
    if [[ -e $target || -L $target ]]; then cp -a "$target" "$backup/$i"; fi
done
stage=
switched=0
success=0
installed=0
status_code() {
    curl --silent --show-error --noproxy '*' --max-time 3 -o /dev/null -w '%{http_code}' "http://127.0.0.1$1"
}
healthy() {
    [[ $(curl --fail --silent --noproxy '*' --max-time 3 http://127.0.0.1:5001/api/health) == OK ]] &&
        [[ $(status_code /api/health) == 200 && $(status_code /api/unknown) == 404 && $(status_code /api) == 404 && $(status_code /) == 200 && $(status_code /_framework/dotnet.js) == 200 && $(status_code /_framework/missing.js) == 404 ]]
}
wait_healthy() {
    for ((i=0; i<30; i++)); do healthy && return 0; sleep 1; done
    return 1
}
finish() {
    status=$?
    trap - EXIT
    if (( ! success )); then
        echo 'Deployment failed; restoring previous configuration/release' >&2
        for i in "${!configs[@]}"; do
            rm -f "${configs[$i]}"
            if [[ -e $backup/$i || -L $backup/$i ]]; then cp -a "$backup/$i" "${configs[$i]}"; fi
        done
        systemctl daemon-reload || true
        systemctl restart systemd-journald || true
        if ((switched)); then
            if [[ -n $old ]]; then
                ln -s "$old" "$root/current.rollback"
                mv -Tf "$root/current.rollback" "$root/current"
                if ! systemctl restart plansafe-api || ! nginx -t || ! systemctl reload-or-restart nginx || ! wait_healthy; then
                    echo 'ALERT: rollback health failed; inspect service manually' >&2
                fi
            else
                rm -f "$root/current"
                systemctl stop plansafe-api || true
                nginx -t && systemctl reload-or-restart nginx || true
            fi
        fi
    fi
    [[ -z $stage ]] || rm -rf -- "$stage"
    if ((installed && ! success)) && [[ $(readlink "$root/current" || true) != "releases/$id" ]]; then rm -rf -- "$release"; fi
    rm -rf -- "$backup"
    exit "$status"
}
trap finish EXIT
trap 'exit 1' HUP INT TERM
bash "$script_dir/provision.sh"
size=$(python3 "$script_dir/archive.py" "$archive")
available=$(df -Pk "$root" | awk 'NR==2 {print $4}')
((available * 1024 >= size + 200 * 1024 * 1024)) || { echo 'Insufficient disk (200 MiB reserve required)' >&2; exit 1; }
stage=$(mktemp -d "$root/releases/.stage.XXXXXXXX")
python3 "$script_dir/archive.py" "$archive" "$stage" >/dev/null
for binary in "$stage/api/PlanSafe.Api" "$stage/api/libe_sqlite3.so"; do
    dependencies=$(ldd "$binary")
    [[ $dependencies != *'not found'* ]] || { echo "$dependencies" >&2; exit 1; }
done
# Native app runs unprivileged; all release files remain root-owned and immutable to it.
find "$stage" -type d -exec chmod 0755 {} +
find "$stage" -type f -exec chmod 0644 {} +
chmod 0755 "$stage/api/PlanSafe.Api"
mv "$stage" "$release"
stage=
installed=1
ln -s "releases/$id" "$root/current.next"
switched=1
mv -Tf "$root/current.next" "$root/current"
nginx -t
systemctl restart plansafe-api
systemctl reload-or-restart nginx
wait_healthy || { echo 'Post-activation health/web check failed' >&2; exit 1; }
printf '%s %s\n' "$run" "$attempt" > "$root/deployed.next"
mv -Tf "$root/deployed.next" "$root/deployed"
success=1
# Delete only real, recognised release directories; retain current and one previous.
for item in "$root/releases"/*; do
    name=${item##*/}
    [[ $name =~ ^[0-9]+-[0-9]+-[a-f0-9]{40}$ && -d $item && ! -L $item ]] || continue
    [[ $item == "$release" || $item == "$root/$old" ]] || rm -rf -- "$item"
done
echo "Activated $id"
