# Runs the HeadlessHarness suite headless (no GPU/window).
#
# It temporarily switches the game manifest to "Core + HeadlessHarness only", launches StarMap with
# the KSA_HEADLESS_HARNESS=1 env var (so Mod.OnBeforeMain runs the harness and calls Environment.Exit
# BEFORE KSA Program.Main, i.e. before any Vulkan/GLFW init), then ALWAYS restores your manifest,
# prints the harness log, and exits with the harness exit code. The minimal manifest avoids other
# code mods whose ImmediateLoad hook the headless flow does not run.
#
# Parameters:
#   -Vehicle  name of a save in Documents\My Games\Kitten Space Agency\Vehicles for the flight test
#             (default "Test Vehicle 1"; pass '' to skip the flight test)
#   -Tests    optional comma-separated test-name filter (KSA_HEADLESS_TESTS)
#
# Exit codes: 0 = all tests passed, 1 = test failure(s), 2 = harness infrastructure failure,
# 3 = timeout (StarMap killed by this script).
#
# Usage: powershell -ExecutionPolicy Bypass -File "F:\Coding\KSA Modding\mods\HeadlessHarness\scripts\run-headless.ps1"

param(
    [string]$Vehicle = 'Test Vehicle 1',
    [string]$Tests = ''
)

$ErrorActionPreference = 'Stop'

$docs     = Join-Path $env:USERPROFILE 'Documents\My Games\Kitten Space Agency'
$manifest = Join-Path $docs 'manifest.toml'
$backup   = Join-Path $docs 'manifest.toml.harnessbak'
$starmap  = 'F:\Coding\KSA Modding\refs\starmap\StarMap.exe'
$log      = Join-Path $env:TEMP 'ksa-headless-harness.log'
$timeoutSec = 120

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

# A leftover backup means a previous run was hard-killed between the manifest swap and the restore,
# so manifest.toml on disk is the minimal one and the backup holds the user's real manifest. Restore
# it FIRST; backing up again here would overwrite the only surviving copy with the minimal manifest.
if (Test-Path $backup) {
    Write-Host "Found leftover manifest backup from an aborted run - restoring it first."
    Copy-Item $backup $manifest -Force
    Remove-Item $backup -Force
}

Copy-Item $manifest $backup -Force
Write-Host "Manifest backed up to $backup"
$harnessExit = 3
try {
    # .NET writer => UTF-8 without BOM (Set-Content -Encoding utf8 would add a BOM in PS 5.1).
    [System.IO.File]::WriteAllText($manifest, $minimal)
    Write-Host "Wrote minimal manifest (Core + HeadlessHarness)."

    if (Test-Path $log) { Remove-Item $log -Force }
    # Clear stale values from the parent shell first, so an empty parameter means "unset", not
    # "whatever happened to be in the environment".
    Remove-Item Env:\KSA_HEADLESS_VEHICLE -ErrorAction SilentlyContinue
    Remove-Item Env:\KSA_HEADLESS_TESTS -ErrorAction SilentlyContinue
    $env:KSA_HEADLESS_HARNESS = '1'
    if ($Vehicle) { $env:KSA_HEADLESS_VEHICLE = $Vehicle }
    if ($Tests) { $env:KSA_HEADLESS_TESTS = $Tests }

    Write-Host "Launching StarMap headless (timeout ${timeoutSec}s)..."
    $p = Start-Process -FilePath $starmap -WorkingDirectory (Split-Path $starmap) -PassThru
    if (-not $p.WaitForExit($timeoutSec * 1000)) {
        Write-Host "Timeout reached - killing StarMap."
        try { $p.Kill() } catch {}
        $harnessExit = 3
    } else {
        $harnessExit = $p.ExitCode
        Write-Host "StarMap exited with code $harnessExit."
    }
}
finally {
    Copy-Item $backup $manifest -Force
    Remove-Item $backup -Force
    Remove-Item Env:\KSA_HEADLESS_HARNESS -ErrorAction SilentlyContinue
    Remove-Item Env:\KSA_HEADLESS_VEHICLE -ErrorAction SilentlyContinue
    Remove-Item Env:\KSA_HEADLESS_TESTS -ErrorAction SilentlyContinue
    Write-Host "Manifest restored."
}

Write-Host ""
Write-Host "===== harness log: $log ====="
if (Test-Path $log) { Get-Content $log } else { Write-Host "(no harness log - Mod.OnBeforeMain may not have run)" }
exit $harnessExit
