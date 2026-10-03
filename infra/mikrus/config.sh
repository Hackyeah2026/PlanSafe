#!/usr/bin/env bash
# Shared validation; source before any local credentials or remote writes.
validate_http_port() {
    if [[ ${MIKRUS_HTTP_PORT:-} =~ ^[1-9][0-9]{3,4}$ ]] &&
        ((MIKRUS_HTTP_PORT >= 1024 && MIKRUS_HTTP_PORT <= 65535 && MIKRUS_HTTP_PORT != 5001)) &&
        [[ $MIKRUS_HTTP_PORT != "${MIKRUS_SSH_PORT:-22}" ]]; then
        return 0
    fi
    echo 'Invalid MIKRUS_HTTP_PORT: require 1024..65535, excluding API/SSH ports' >&2
    return 2
}
validate_public_url() {
    local label='[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?'
    local pattern="^https://$label(\\.$label)+(:([1-9][0-9]{0,4}))?/?$"
    [[ ${MIKRUS_PUBLIC_URL:-} =~ $pattern ]] || {
        echo 'Invalid MIKRUS_PUBLIC_URL: require an HTTPS DNS origin only' >&2
        return 2
    }
    local origin=${MIKRUS_PUBLIC_URL%/}
    local authority=${origin#https://}
    if [[ $authority == *:* ]]; then
        local port=${authority##*:}
        ((port <= 65535)) || return 2
    fi
}
