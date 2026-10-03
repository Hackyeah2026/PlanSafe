<#
.SYNOPSIS
    Redeploy script for PlanSafe (App & API) for rapid development and testing.

.DESCRIPTION
    Stops existing instances, ensures environment prerequisites (.NET 10, Node/TS shim),
    builds the solution, starts the API (port 5001) and Blazor App (port 5000) in the background,
    and performs a health check.

.PARAMETER Stop
    Stops any running instances of PlanSafe and exits.

.PARAMETER Status
    Checks the status and health of PlanSafe services and exits.

.PARAMETER NoBuild
    Skips the build step before starting services.

.PARAMETER AppPort
    Port for the Blazor WebAssembly App (default: 5000).

.PARAMETER ApiPort
    Port for the backend API (default: 5001).

.PARAMETER TimeoutSec
    Maximum seconds to wait for services to become healthy (default: 35).
#>

[CmdletBinding()]
param(
    [switch]$Stop,
    [switch]$Status,
    [switch]$NoBuild,
    [int]$AppPort = 5000,
    [int]$ApiPort = 5001,
    [int]$TimeoutSec = 35
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
if (-not $repoRoot) {
    $repoRoot = (Get-Location).Path
}

$runDir = Join-Path $repoRoot ".run"
$logsDir = Join-Path $runDir "logs"
$appPidFile = Join-Path $runDir "app.pid"
$apiPidFile = Join-Path $runDir "api.pid"

function Ensure-Environment {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:DOTNET_NOLOGO = "1"

    # Check if dotnet is using SDK 10
    $needsDotnetPath = $true
    try {
        $sdkVer = (dotnet --version 2>$null)
        if ($sdkVer -and $sdkVer.StartsWith("10.")) {
            $needsDotnetPath = $false
        }
    } catch {
        $needsDotnetPath = $true
    }

    if ($needsDotnetPath) {
        $userDotnet = Join-Path $env:USERPROFILE ".dotnet"
        if (Test-Path (Join-Path $userDotnet "dotnet.exe")) {
            $env:PATH = "$userDotnet;$env:PATH"
            $env:DOTNET_ROOT = $userDotnet
        }
    }

    # Verify npm dependencies & Node 20 typescript compatibility shim
    $nodeModules = Join-Path $repoRoot "node_modules"
    if (-not (Test-Path $nodeModules)) {
        Write-Host "Installing npm dependencies (npm ci)..." -ForegroundColor Cyan
        npm ci --prefix "$repoRoot"
    }

    # If typescript binary is extensionless in node_modules, provide tsc.js shim for Node 20 compatibility
    $tsBinDir = Join-Path $nodeModules "typescript\bin"
    $tscNoExt = Join-Path $tsBinDir "tsc"
    $tscJs = Join-Path $tsBinDir "tsc.js"
    if ((Test-Path $tscNoExt) -and (-not (Test-Path $tscJs))) {
        Copy-Item $tscNoExt $tscJs -Force
    }

    $dotBinTscCmd = Join-Path $nodeModules ".bin\tsc.cmd"
    if (Test-Path $dotBinTscCmd) {
        $cmdContent = Get-Content $dotBinTscCmd -Raw
        if ($cmdContent -match 'bin\\tsc"') {
            $cmdContent = $cmdContent -replace 'bin\\tsc"', 'bin\tsc.js"'
            Set-Content -Path $dotBinTscCmd -Value $cmdContent -NoNewline
        }
    }
}

function Get-PortProcessId([int]$port) {
    try {
        $conns = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
        foreach ($conn in $conns) {
            if ($conn.OwningProcess -and $conn.OwningProcess -ne 0) {
                return $conn.OwningProcess
            }
        }
    } catch {
        $lines = netstat -ano | Select-String ":$port\s+.*LISTENING\s+(\d+)"
        foreach ($line in $lines) {
            if ($line.Matches[0].Groups[1].Value) {
                return [int]$line.Matches[0].Groups[1].Value
            }
        }
    }
    return $null
}

function Stop-ProcessesOnPort([int]$port) {
    try {
        $conns = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
        foreach ($conn in $conns) {
            $procId = $conn.OwningProcess
            if ($procId -and $procId -ne 0 -and $procId -ne $PID) {
                Write-Host "Stopping process $procId listening on port $port..." -ForegroundColor Yellow
                Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
            }
        }
    } catch {
        $lines = netstat -ano | Select-String ":$port\s+.*LISTENING\s+(\d+)"
        foreach ($line in $lines) {
            if ($line.Matches[0].Groups[1].Value) {
                $pidToKill = [int]$line.Matches[0].Groups[1].Value
                if ($pidToKill -and $pidToKill -ne $PID) {
                    Write-Host "Stopping process $pidToKill on port $port (netstat)..." -ForegroundColor Yellow
                    Stop-Process -Id $pidToKill -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }
}

function Stop-Services {
    Write-Host "Stopping running PlanSafe services..." -ForegroundColor Cyan

    if (Test-Path $appPidFile) {
        $savedPid = (Get-Content $appPidFile -ErrorAction SilentlyContinue | Out-String).Trim()
        if ($savedPid -match '^\d+$') {
            Stop-Process -Id ([int]$savedPid) -Force -ErrorAction SilentlyContinue
        }
        Remove-Item $appPidFile -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path $apiPidFile) {
        $savedPid = (Get-Content $apiPidFile -ErrorAction SilentlyContinue | Out-String).Trim()
        if ($savedPid -match '^\d+$') {
            Stop-Process -Id ([int]$savedPid) -Force -ErrorAction SilentlyContinue
        }
        Remove-Item $apiPidFile -Force -ErrorAction SilentlyContinue
    }

    Stop-ProcessesOnPort $AppPort
    Stop-ProcessesOnPort $ApiPort

    Start-Sleep -Milliseconds 800
    Write-Host "PlanSafe services stopped." -ForegroundColor Green
}

function Test-Endpoint([string]$url) {
    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
        return $true
    } catch {
        if ($_.Exception.Response -ne $null) {
            return $true
        }
        return $false
    }
}

function Show-Status {
    $appRunning = Test-Endpoint "http://127.0.0.1:$AppPort"
    $apiRunning = Test-Endpoint "http://127.0.0.1:$ApiPort"
    $appPid = Get-PortProcessId $AppPort
    $apiPid = Get-PortProcessId $ApiPort

    Write-Host ""
    Write-Host "================ PlanSafe Status ================" -ForegroundColor Cyan
    if ($appRunning) {
        Write-Host "  App (Frontend): RUNNING  -> http://127.0.0.1:$AppPort (PID: $appPid)" -ForegroundColor Green
    } else {
        Write-Host "  App (Frontend): STOPPED  (port $AppPort)" -ForegroundColor Red
    }

    if ($apiRunning) {
        Write-Host "  API (Backend):  RUNNING  -> http://127.0.0.1:$ApiPort (PID: $apiPid)" -ForegroundColor Green
    } else {
        Write-Host "  API (Backend):  STOPPED  (port $ApiPort)" -ForegroundColor Red
    }
    Write-Host "=================================================" -ForegroundColor Cyan
    Write-Host ""
}

function Start-DetachedProcess([string]$commandLine) {
    $res = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $commandLine }
    return $res.ProcessId
}

# --- Action handling ---

if ($Status) {
    Show-Status
    exit 0
}

Ensure-Environment
Stop-Services

if ($Stop) {
    exit 0
}

# Ensure directory structure
if (-not (Test-Path $logsDir)) {
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
}

if (-not $NoBuild) {
    Write-Host "Building PlanSafe solution..." -ForegroundColor Cyan
    $buildResult = & dotnet build (Join-Path $repoRoot "PlanSafe.slnx") -c Debug
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed with exit code $LASTEXITCODE." -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "Build succeeded." -ForegroundColor Green
}

# Prepare paths and environment command prefix for child processes
$dotnetPrefix = ""
$userDotnet = Join-Path $env:USERPROFILE ".dotnet"
if (Test-Path (Join-Path $userDotnet "dotnet.exe")) {
    $dotnetPrefix = "set DOTNET_ROOT=$userDotnet && set PATH=$userDotnet;%PATH% && "
}

# Start API in background
Write-Host "Starting PlanSafe.Api on http://127.0.0.1:$ApiPort..." -ForegroundColor Cyan
$apiOutLog = Join-Path $logsDir "api.log"
$apiErrLog = Join-Path $logsDir "api.err.log"
$apiCmd = "cmd /c ""cd /d `"$repoRoot`" && $dotnetPrefix dotnet run --project src/PlanSafe.Api --no-build --urls http://127.0.0.1:$ApiPort > `"$apiOutLog`" 2> `"$apiErrLog`""""
$apiLauncherPid = Start-DetachedProcess $apiCmd

# Start App in background
Write-Host "Starting PlanSafe.App on http://127.0.0.1:$AppPort..." -ForegroundColor Cyan
$appOutLog = Join-Path $logsDir "app.log"
$appErrLog = Join-Path $logsDir "app.err.log"
$appCmd = "cmd /c ""cd /d `"$repoRoot`" && $dotnetPrefix dotnet run --project src/PlanSafe.App --no-build --urls http://127.0.0.1:$AppPort > `"$appOutLog`" 2> `"$appErrLog`""""
$appLauncherPid = Start-DetachedProcess $appCmd

# Wait for healthy endpoints
Write-Host "Waiting for services to become ready (up to $TimeoutSec s)..." -ForegroundColor Cyan
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$appReady = $false
$apiReady = $false

while ((Get-Date) -lt $deadline) {
    if (-not $apiReady) {
        $apiReady = Test-Endpoint "http://127.0.0.1:$ApiPort"
    }
    if (-not $appReady) {
        $appReady = Test-Endpoint "http://127.0.0.1:$AppPort"
    }
    if ($appReady -and $apiReady) {
        break
    }
    Start-Sleep -Milliseconds 600
}

# Save actual listening PIDs
$actualApiPid = Get-PortProcessId $ApiPort
if ($actualApiPid) {
    $actualApiPid | Set-Content -Path $apiPidFile
} else {
    $apiLauncherPid | Set-Content -Path $apiPidFile
}

$actualAppPid = Get-PortProcessId $AppPort
if ($actualAppPid) {
    $actualAppPid | Set-Content -Path $appPidFile
} else {
    $appLauncherPid | Set-Content -Path $appPidFile
}

Write-Host ""
if ($appReady -and $apiReady) {
    Write-Host "==========================================================" -ForegroundColor Green
    Write-Host " PlanSafe successfully redeployed and ready for testing! " -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Green
    Write-Host "  * App (Frontend): http://127.0.0.1:$AppPort (PID: $actualAppPid)" -ForegroundColor Cyan
    Write-Host "  * API (Backend):  http://127.0.0.1:$ApiPort (PID: $actualApiPid)" -ForegroundColor Cyan
    Write-Host "  * Logs directory: $logsDir" -ForegroundColor Gray
    Write-Host "  * Check status:   .\redeploy.ps1 -Status" -ForegroundColor Gray
    Write-Host "  * Stop services:  .\redeploy.ps1 -Stop" -ForegroundColor Gray
    Write-Host "==========================================================" -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host "WARNING: Services took longer than $TimeoutSec seconds to respond." -ForegroundColor Yellow
    Write-Host "Current status:"
    Write-Host "  * App Ready: $appReady"
    Write-Host "  * API Ready: $apiReady"
    Write-Host "Check logs in $logsDir for details:"
    Write-Host "  - API Error Log: $apiErrLog"
    Write-Host "  - App Error Log: $appErrLog"
    exit 1
}
