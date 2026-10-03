# PlanSafe / CrowdSim - Deployment & Operations Guide for Agents

This document describes the local deployment environment, the deployment script (`deploy.sh`), and standard operating procedures for building, running, and testing the PlanSafe application locally.

---

## 1. Project Overview

- **Project Name:** PlanSafe / CrowdSim
- **Tech Stack:** Blazor WebAssembly (.NET 10.0)
- **Primary Source Directory:** `src/PlanSafe.App/`
- **Build Output / Release Artifacts:** `dist/` (`dist/wwwroot`)
- **Default Port:** `5171`

---

## 2. Environment Prerequisites

- **.NET 10 SDK**:
  The script automatically checks `PATH`, `$HOME/.dotnet/dotnet`, and `/usr/share/dotnet/dotnet`.
- **Python 3**:
  Used to host the local production SPA server (`scripts/spa_server.py`) with support for WebAssembly MIME types (`application/wasm`) and SPA route fallback to `index.html`.
- **Docker / Docker Compose** *(Optional)*:
  Available for containerized deployment via `Dockerfile` and `docker-compose.yml`.

---

## 3. Deployment Script (`deploy.sh`)

A unified script `./deploy.sh` is provided in the repository root. Ensure execution permissions are set:

```bash
chmod +x deploy.sh scripts/spa_server.py
```

### Available Commands

| Command | Description | Example |
| :--- | :--- | :--- |
| `dev` / `run` | Runs the app in development mode using `dotnet run` (with Blazor DevServer) in foreground | `./deploy.sh dev` |
| `build` | Builds the .NET project | `./deploy.sh build` |
| `publish` | Compiles an optimized Release build into `./dist/` | `./deploy.sh publish` |
| `serve` | Serves the production build (`./dist/wwwroot`) in the foreground | `./deploy.sh serve` |
| `start` | Builds/publishes (if needed) and starts the server in the **background** (daemon) | `./deploy.sh start` |
| `status` | Checks background daemon process status and performs an HTTP health check | `./deploy.sh status` |
| `stop` | Stops the running background server | `./deploy.sh stop` |
| `restart` | Restarts the background server | `./deploy.sh restart` |
| `clean` | Cleans build artifacts (`dist/`, `bin/`, `obj/`) and log/pid files | `./deploy.sh clean` |
| `help` | Displays usage and help info | `./deploy.sh help` |

### Redeploy Script (`redeploy.sh`)

A dedicated convenience script `./redeploy.sh` stops any currently running background instance, recompiles a fresh release build, restarts the background daemon, and prints the operational status:

```bash
chmod +x redeploy.sh
./redeploy.sh
```

---

## 4. Environment Variables

The deployment script supports the following environment variables:

- `PORT`: Port to listen on (default: `5171`).
- `HOST`: Host interface to bind to (default: `localhost` for dev, `0.0.0.0` for serve/start).
- `CONFIG`: Build configuration for `build` command (default: `Debug`).
- `DOTNET_ROOT`: Custom path to .NET SDK directory if non-standard.

### Examples

Run on custom port 8080:
```bash
PORT=8080 ./deploy.sh start
```

Run in dev mode bound to all network interfaces:
```bash
HOST=0.0.0.0 PORT=5000 ./deploy.sh dev
```

---

## 5. Background Deployment Workflow

To quickly deploy in background and verify:

1. **Start the server:**
   ```bash
   ./deploy.sh start
   ```
2. **Verify status:**
   ```bash
   ./deploy.sh status
   ```
3. **Inspect logs:**
   ```bash
   tail -f deploy.log
   ```
4. **Test endpoints:**
   ```bash
   curl -I http://localhost:5171/
   curl -I http://localhost:5171/evacuate
   ```
5. **Stop the server:**
   ```bash
   ./deploy.sh stop
   ```

---

## 6. Docker Deployment (Alternative)

If Docker is available on the host:

```bash
# Build and run container in background
docker compose up --build -d

# Check status
docker compose ps

# View container logs
docker compose logs -f plansafe-frontend

# Stop container
docker compose down
```

---

## 7. SPA Client-Side Routing & MIME Types

Blazor WebAssembly relies on:
1. `application/wasm` MIME type for `.wasm` binaries.
2. Single Page Application (SPA) routing fallback where non-file requests (such as `/evacuate`) return `index.html`.

The included helper `scripts/spa_server.py` handles both automatically for static hosting during local testing.

---

## 8. Version Control Policy for Agents (Local Git Commits)

> [!IMPORTANT]
> **All changes must be committed to local Git after changes are made.**
> Whenever any task, code edit, feature addition, or bug fix is finished:
> 1. Verify working directory status: `git status`
> 2. Stage modified/new project files: `git add <files>` (or `git add -A` for project files, respecting `.gitignore`)
> 3. Create a clean, descriptive commit: `git commit -m "<concise description of changes>"`
> 4. Ensure no uncommitted working tree changes remain before concluding the turn or moving to the next task.
