#!/usr/bin/env bash
# ==============================================================================
# deploy.sh - Local Deployment and Run Script for CrowdSim / PlanSafe
# ==============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Configuration defaults
PORT="${PORT:-5171}"
HOST="${HOST:-localhost}"
PID_FILE="$SCRIPT_DIR/.deploy.pid"
LOG_FILE="$SCRIPT_DIR/deploy.log"
PROJECT_DIR="$SCRIPT_DIR/src/PlanSafe.App"
DIST_DIR="$SCRIPT_DIR/dist"
WWWROOT_DIR="$DIST_DIR/wwwroot"

# Ensure .dotnet is in PATH if not already accessible
setup_dotnet_path() {
    if ! command -v dotnet >/dev/null 2>&1; then
        if [ -x "$HOME/.dotnet/dotnet" ]; then
            export DOTNET_ROOT="$HOME/.dotnet"
            export PATH="$HOME/.dotnet:$PATH"
        elif [ -x "/usr/share/dotnet/dotnet" ]; then
            export DOTNET_ROOT="/usr/share/dotnet"
            export PATH="/usr/share/dotnet:$PATH"
        fi
    fi

    if ! command -v dotnet >/dev/null 2>&1; then
        echo "❌ Error: 'dotnet' command not found in PATH or standard locations (~/.dotnet, /usr/share/dotnet)." >&2
        echo "Please install .NET 10 SDK or set DOTNET_ROOT." >&2
        exit 1
    fi
}

# Check Python 3 for static SPA serving
check_python() {
    if ! command -v python3 >/dev/null 2>&1; then
        echo "❌ Error: 'python3' is required to run the local SPA server." >&2
        exit 1
    fi
}

cmd_build() {
    setup_dotnet_path
    echo "🔨 Building PlanSafe.App (.NET 10 WebAssembly)..."
    dotnet build "$PROJECT_DIR/PlanSafe.App.csproj" -c "${CONFIG:-Debug}"
    echo "✅ Build completed successfully."
}

cmd_publish() {
    setup_dotnet_path
    echo "📦 Publishing PlanSafe.App in Release mode to '$DIST_DIR'..."
    dotnet publish "$PROJECT_DIR/PlanSafe.App.csproj" -c Release -o "$DIST_DIR"
    echo "✅ Publish complete. Static assets are in '$WWWROOT_DIR'."
}

cmd_dev() {
    setup_dotnet_path
    echo "🚀 Starting CrowdSim in Development mode (with Blazor DevServer)..."
    echo "🌐 URL: http://${HOST}:${PORT}"
    echo "Press Ctrl+C to stop."
    dotnet run --project "$PROJECT_DIR" --urls "http://${HOST}:${PORT}"
}

cmd_serve() {
    check_python
    if [ ! -d "$WWWROOT_DIR" ]; then
        echo "⚠️ Dist directory '$WWWROOT_DIR' not found. Running publish first..."
        cmd_publish
    fi
    echo "🌐 Serving production build on http://${HOST}:${PORT}..."
    echo "Press Ctrl+C to stop."
    python3 "$SCRIPT_DIR/scripts/spa_server.py" "$WWWROOT_DIR" "$PORT" "$HOST"
}

cmd_start() {
    check_python
    if [ -f "$PID_FILE" ]; then
        PID="$(cat "$PID_FILE" 2>/dev/null || true)"
        if [ -n "$PID" ] && kill -0 "$PID" 2>/dev/null; then
            echo "⚠️ Application is already running in background (PID: $PID) at http://${HOST}:${PORT}."
            return 0
        fi
        rm -f "$PID_FILE"
    fi

    if [ ! -d "$WWWROOT_DIR" ]; then
        echo "⚠️ Dist directory '$WWWROOT_DIR' not found. Publishing first..."
        cmd_publish
    fi

    echo "🚀 Starting background server on http://${HOST}:${PORT}..."
    if command -v setsid >/dev/null 2>&1; then
        setsid python3 "$SCRIPT_DIR/scripts/spa_server.py" "$WWWROOT_DIR" "$PORT" "$HOST" </dev/null >> "$LOG_FILE" 2>&1 &
    else
        nohup python3 "$SCRIPT_DIR/scripts/spa_server.py" "$WWWROOT_DIR" "$PORT" "$HOST" </dev/null >> "$LOG_FILE" 2>&1 &
    fi
    NEW_PID=$!
    disown "$NEW_PID" 2>/dev/null || true
    echo "$NEW_PID" > "$PID_FILE"

    sleep 1
    if kill -0 "$NEW_PID" 2>/dev/null; then
        echo "✅ Started successfully (PID: $NEW_PID)."
        echo "🌐 URL: http://${HOST}:${PORT}"
        echo "📄 Logs: $LOG_FILE"
    else
        echo "❌ Failed to start. Check $LOG_FILE for details." >&2
        rm -f "$PID_FILE"
        exit 1
    fi
}

cmd_stop() {
    if [ ! -f "$PID_FILE" ]; then
        echo "ℹ️ No PID file found ($PID_FILE). Checking for any lingering server on port $PORT..."
    else
        PID="$(cat "$PID_FILE" 2>/dev/null || true)"
        if [ -n "$PID" ]; then
            echo "🛑 Stopping server (PID: $PID)..."
            kill "$PID" 2>/dev/null || true
            sleep 1
            if kill -0 "$PID" 2>/dev/null; then
                kill -9 "$PID" 2>/dev/null || true
            fi
        fi
        rm -f "$PID_FILE"
    fi

    # Check if anything is still listening on PORT and kill if needed
    if command -v fuser >/dev/null 2>&1; then
        fuser -k "${PORT}/tcp" 2>/dev/null || true
    fi

    echo "✅ Server stopped."
}

cmd_status() {
    if [ -f "$PID_FILE" ]; then
        PID="$(cat "$PID_FILE" 2>/dev/null || true)"
        if [ -n "$PID" ] && kill -0 "$PID" 2>/dev/null; then
            echo "🟢 Server is RUNNING (PID: $PID)."
            echo "🌐 URL: http://${HOST}:${PORT}"
            if command -v curl >/dev/null 2>&1; then
                STATUS_CODE="$(curl -s -o /dev/null -w "%{http_code}" "http://${HOST}:${PORT}/" || echo "failed")"
                echo "📡 HTTP Status check: $STATUS_CODE"
            fi
            return 0
        else
            echo "🔴 Server is NOT running (stale PID file found)."
            rm -f "$PID_FILE"
            return 1
        fi
    else
        echo "⚪ Server is NOT running."
        return 1
    fi
}

cmd_restart() {
    echo "🔄 Restarting deployment..."
    cmd_stop
    cmd_start
}

cmd_clean() {
    cmd_stop || true
    echo "🧹 Cleaning build artifacts and logs..."
    setup_dotnet_path
    dotnet clean "$PROJECT_DIR/CrowdSim.csproj" || true
    rm -rf "$DIST_DIR" "$PROJECT_DIR/bin" "$PROJECT_DIR/obj" "$PID_FILE" "$LOG_FILE"
    echo "✅ Clean finished."
}

show_help() {
    cat <<EOF
Usage: $0 [command] [options]

Commands:
  dev       Run project in Development mode via dotnet run (foreground, port $PORT)
  build     Build the .NET 10 WebAssembly project
  publish   Publish project in Release mode into ./dist
  serve     Serve the published static build in foreground (port $PORT)
  start     Deploy & start the app as a background daemon (port $PORT)
  stop      Stop the background daemon
  restart   Restart the background daemon
  status    Show status of the local deployment
  clean     Clean build outputs, dist/, and log files
  help      Show this help message

Environment variables:
  PORT      Port to listen on (default: 5171)
  HOST      Host to bind to (default: localhost for dev, 0.0.0.0 for serve/start)

Examples:
  ./deploy.sh dev            # Run dev server with hot reload
  ./deploy.sh start          # Deploy & start in background
  PORT=8080 ./deploy.sh dev  # Run on custom port 8080
  ./deploy.sh status         # Check if app is active
  ./deploy.sh stop           # Terminate local background server
EOF
}

ACTION="${1:-dev}"

case "$ACTION" in
    dev|run)
        cmd_dev
        ;;
    build)
        cmd_build
        ;;
    publish)
        cmd_publish
        ;;
    serve)
        cmd_serve
        ;;
    start)
        cmd_start
        ;;
    stop)
        cmd_stop
        ;;
    restart)
        cmd_restart
        ;;
    status)
        cmd_status
        ;;
    clean)
        cmd_clean
        ;;
    help|-h|--help)
        show_help
        ;;
    *)
        echo "❌ Unknown command: $ACTION"
        show_help
        exit 1
        ;;
esac
