# Runs the HeadlessHarness suite headless (no GPU/window).
#
# It temporarily switches the game manifest to "Core + HeadlessHarness only", launches StarMap with
# the KSA_HEADLESS_HARNESS=1 env var (so Mod.OnBeforeMain runs the harness and calls Environment.Exit
# BEFORE KSA Program.Main, i.e. before any Vulkan/GLFW init), then ALWAYS restores your manifest,
# prints the harness log, and exits with the harness exit code. The minimal manifest avoids other
# code mods whose ImmediateLoad hook the headless flow does not run.
#
# Concurrent sessions queue: the whole swap -> launch -> restore sequence runs under a machine-wide
# named mutex, so parallel invocations wait for each other instead of fighting over the shared game
# manifest and game process. Each run writes its own log file under %TEMP%\ksa-headless-harness\
# (nothing is overwritten); on a failed run the game's own log is archived next to it.
#
# Parameters:
#   -Vehicle  name of a save in Documents\My Games\Kitten Space Agency\Vehicles for the flight test
#             (default "Test Vehicle 1"; pass '' to skip the flight test)
#   -Tests    optional comma-separated test-name filter (KSA_HEADLESS_TESTS)
#   -Build    build and deploy the harness (and the example consumer) inside the queue first, so a
#             build never races another session's running game holding the deployed DLL
#
# Exit codes: 0 = all tests passed, 1 = test failure(s), 2 = harness infrastructure failure,
# 3 = timeout (StarMap killed by this script), 4 = run-queue timeout, 5 = build failure.
#
# Usage: powershell -ExecutionPolicy Bypass -File "F:\Coding\KSA Modding\mods\HeadlessHarness\scripts\run-headless.ps1"

param(
    [string]$Vehicle = 'Test Vehicle 1',
    [string]$Tests = '',
    [switch]$Build
)

$ErrorActionPreference = 'Stop'

$docs     = Join-Path $env:USERPROFILE 'Documents\My Games\Kitten Space Agency'
$manifest = Join-Path $docs 'manifest.toml'
$backup   = Join-Path $docs 'manifest.toml.harnessbak'
$repoRoot = Split-Path $PSScriptRoot
$starmap  = 'F:\Coding\KSA Modding\refs\starmap\StarMap.exe'
$logDir   = Join-Path $env:TEMP 'ksa-headless-harness'
$runId    = "run-{0}-pid{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID
$log      = Join-Path $logDir "$runId.log"
$gameLog  = Join-Path $docs 'logs\KittenSpaceAgency.log'
$timeoutSec = 120
$queueTimeoutSec = 600

# Only Core + HeadlessHarness are enabled. Consumer mods (deployed to the mods folder with a
# HeadlessHarness dependency in their mod.toml) are loaded by the harness itself, not StarMap.
$minimal = @"
[[mods]]
id = "Core"
enabled = true

[[mods]]
id = "HeadlessHarness"
enabled = true
"@

# The default vehicle is a convenience for this machine; on a machine without that save the flight
# test would FAIL (set-but-missing is an error by design), so fall back to skipping it unless the
# caller asked for a vehicle explicitly.
if ($Vehicle -and -not $PSBoundParameters.ContainsKey('Vehicle')) {
    if (-not (Test-Path (Join-Path $docs "Vehicles\$Vehicle"))) {
        Write-Host "Default vehicle save '$Vehicle' not found - the flight test will skip."
        $Vehicle = ''
    }
}

# Machine-wide run queue. The game manifest is exclusive state (exactly one run may own the
# swap/restore window), so the entire run serializes here; concurrent invocations simply wait their
# turn. The name differs from the harness's own in-process mutex (Global\KSA-HeadlessHarness) on
# purpose: this script holds its mutex while the game runs, so sharing the name would deadlock the
# game against its own launcher.
$queue = New-Object System.Threading.Mutex($false, 'Global\KSA-HeadlessHarness-Script')
$queued = $false
try {
    $queued = $queue.WaitOne(0)
    if (-not $queued) {
        Write-Host "Another harness run is in progress - queued (waiting up to ${queueTimeoutSec}s)..."
        try { $queued = $queue.WaitOne($queueTimeoutSec * 1000) }
        catch [System.Threading.AbandonedMutexException] { $queued = $true }
    }
} catch [System.Threading.AbandonedMutexException] { $queued = $true }
if (-not $queued) {
    Write-Host "Run queue still busy after ${queueTimeoutSec}s - giving up."
    $queue.Dispose()
    exit 4
}

$harnessExit = 3
try {
    if ($Build) {
        Write-Host "Building harness + example consumer (inside the queue)..."
        dotnet build (Join-Path $repoRoot 'HeadlessHarness\HeadlessHarness.csproj') -v q -nologo
        if ($LASTEXITCODE -ne 0) { Write-Host "Harness build failed."; exit 5 }
        dotnet build (Join-Path $repoRoot 'examples\HarnessConsumerExample\HarnessConsumerExample.csproj') -v q -nologo
        if ($LASTEXITCODE -ne 0) { Write-Host "Example consumer build failed."; exit 5 }
    }

    # A leftover backup means a previous run was hard-killed between the manifest swap and the
    # restore, so manifest.toml on disk is the minimal one and the backup holds the user's real
    # manifest. Restore it FIRST; backing up again here would overwrite the only surviving copy with
    # the minimal manifest. (Inside the queue: only ever a crash leftover, never a live run's backup.)
    if (Test-Path $backup) {
        Write-Host "Found leftover manifest backup from an aborted run - restoring it first."
        Copy-Item $backup $manifest -Force
        Remove-Item $backup -Force
    }

    Copy-Item $manifest $backup -Force
    Write-Host "Manifest backed up to $backup"
    try {
        # .NET writer => UTF-8 without BOM (Set-Content -Encoding utf8 would add a BOM in PS 5.1).
        [System.IO.File]::WriteAllText($manifest, $minimal)
        Write-Host "Wrote minimal manifest (Core + HeadlessHarness)."

        New-Item -ItemType Directory -Force $logDir | Out-Null
        # Clear stale values from the parent shell first, so an empty parameter means "unset", not
        # "whatever happened to be in the environment".
        Remove-Item Env:\KSA_HEADLESS_VEHICLE -ErrorAction SilentlyContinue
        Remove-Item Env:\KSA_HEADLESS_TESTS -ErrorAction SilentlyContinue
        $env:KSA_HEADLESS_HARNESS = '1'
        $env:KSA_HEADLESS_LOG = $log
        if ($Vehicle) { $env:KSA_HEADLESS_VEHICLE = $Vehicle }
        if ($Tests) { $env:KSA_HEADLESS_TESTS = $Tests }

        Write-Host "Launching StarMap headless (timeout ${timeoutSec}s, log $log)..."
        $p = Start-Process -FilePath $starmap -WorkingDirectory (Split-Path $starmap) -PassThru
        if (-not $p.WaitForExit($timeoutSec * 1000)) {
            Write-Host "Timeout reached - killing StarMap."
            try { $p.Kill() } catch {}
            $harnessExit = 3
        } else {
            $harnessExit = $p.ExitCode
            Write-Host "StarMap exited with code $harnessExit."
        }

        # On failure, keep the game's own log (overwritten by every game start) next to the run log.
        # Best-effort: a copy failure (e.g. the file still locked by another process) must not
        # replace the meaningful harness exit code with a script error.
        if ($harnessExit -ne 0 -and (Test-Path $gameLog)) {
            try {
                Copy-Item $gameLog (Join-Path $logDir "$runId.game.log") -Force -ErrorAction Stop
                Write-Host "Archived game log to $logDir\$runId.game.log"
            } catch {
                Write-Host "Could not archive the game log: $($_.Exception.Message)"
            }
        }
    }
    finally {
        Copy-Item $backup $manifest -Force
        Remove-Item $backup -Force
        Remove-Item Env:\KSA_HEADLESS_HARNESS -ErrorAction SilentlyContinue
        Remove-Item Env:\KSA_HEADLESS_LOG -ErrorAction SilentlyContinue
        Remove-Item Env:\KSA_HEADLESS_VEHICLE -ErrorAction SilentlyContinue
        Remove-Item Env:\KSA_HEADLESS_TESTS -ErrorAction SilentlyContinue
        Write-Host "Manifest restored."
    }
}
finally {
    if ($queued) { $queue.ReleaseMutex() }
    $queue.Dispose()
}

Write-Host ""
Write-Host "===== harness log: $log ====="
if (Test-Path $log) { Get-Content $log } else { Write-Host "(no harness log - Mod.OnBeforeMain may not have run)" }
Write-Host "(all run logs are kept in $logDir)"
exit $harnessExit
