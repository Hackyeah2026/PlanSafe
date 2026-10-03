#!/usr/bin/env bash
# ==============================================================================
# redeploy.sh - Quick local redeployment script for PlanSafe / CrowdSim
# ==============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "🔄 Initiating local redeployment..."
"$SCRIPT_DIR/deploy.sh" stop || true
"$SCRIPT_DIR/deploy.sh" publish
"$SCRIPT_DIR/deploy.sh" start

echo "✨ Local redeployment completed."
"$SCRIPT_DIR/deploy.sh" status
