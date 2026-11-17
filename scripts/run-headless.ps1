# Runs the HeadlessHarness suite headless (no GPU/window).
#
# It temporarily switches the game manifest to "Core + HeadlessHarness only" (plus any -PreMods and
# -ContentMods), launches StarMap with the KSA_HEADLESS_HARNESS=1 env var (so Mod.OnBeforeMain runs
# the harness and calls Environment.Exit BEFORE KSA Program.Main, i.e. before any Vulkan/GLFW init),
# then ALWAYS restores your manifest, prints the harness log, lists the result files, and exits with
# the harness exit code. The minimal manifest avoids other code mods whose ImmediateLoad hook the
# headless flow does not run.
#
# Concurrent sessions queue: the whole swap -> launch -> restore sequence runs under a machine-wide
# named mutex, so parallel invocations wait for each other instead of fighting over the shared game
# manifest and game process. Each run writes its own log file under %TEMP%\ksa-headless-harness\
# (nothing is overwritten; the harness prunes the folder to the newest 200 runs and keeps every run
# younger than 2 days), with <run>-results.json and <run>-junit.xml next to it; on a failed run the
# game's own log is archived next to it, but only when this run actually wrote it, which a headless
# run usually does not.
#
# Parameters:
#   -InstancePath  optional game profile folder to run in, for example a Borea instance. StarMap gets
#                it through STARMAP_INSTANCE_PATH, so the game, the harness and every mod use it in place
#                of Documents\My Games\Kitten Space Agency. The manifest swap and the vehicle saves are
#                then those of that folder, and the mods under test must be deployed into its mods folder.
#   -Vehicle     name of a save in the Vehicles folder of the profile for the flight
#                test (default "Test Vehicle 1"; pass '' to skip the flight test)
#   -Vehicles    optional comma-separated save list (KSA_HEADLESS_VEHICLES) overriding the candidate
#                set a multi-vehicle consumer test would otherwise resolve; separate from -Vehicle
#   -Tests       optional comma-separated test-name filter (KSA_HEADLESS_TESTS)
#   -System      optional star-system id (KSA_HEADLESS_SYSTEM), the Id attribute of a system XML, for
#                example SolSystemInterstellar. Empty loads the first system the game lists, Sol with Core.
#                An id that no enabled mod defines fails the run with exit 2. Without -ContentMods only
#                the Core systems can be selected.
#   -Rules       optional game rules to turn on (KSA_HEADLESS_RULES), comma-separated: radiation,
#                lifesupport, or settings to keep the rules of the profile's settings file. Empty turns
#                every rule off, so a run does not depend on the profile and has no random avionics upsets.
#   -ContentMods optional mod ids (separated by ',' or ';') the minimal manifest enables after Core and
#                HeadlessHarness, for content mods (parts, systems) the tests load. Each must be a folder in
#                the profile's mods folder (after -Deploy), or the run fails with exit 2.
#   -PreMods     optional mod ids enabled after Core and BEFORE HeadlessHarness. StarMap runs the
#                [StarMapBeforeMain] method of each mod in manifest order, and the harness brings the game
#                up inside its own, so a mod that has to patch the content load must come first.
#   -KeepManifest  run with the profile's own manifest instead of the minimal one, for a dedicated
#                instance whose manifest already enables what the run needs (HeadlessHarness included).
#                Cannot be combined with -ContentMods or -PreMods.
#   -Param       optional test parameters "name=value;name2=value2" (KSA_HEADLESS_PARAMS), read by
#                TestSupport.Param
#   -EnvVars     optional "NAME=value;NAME2=value2" environment variables for the game process, for
#                tests that read a variable of their own; each is put back to its earlier value afterwards
#   -Seed        optional integer (KSA_HEADLESS_SEED) a test seeds its random sources with; the harness
#                records it in the results file
#   -TestTimeoutSec  optional default wall-clock budget of each test (KSA_HEADLESS_TEST_TIMEOUT); a test
#                that outlives it is reported, the results are written, and the run exits with 3. 0 (the
#                default) leaves only the budgets the tests name themselves (IHarnessTest.TimeoutSeconds).
#   -Deploy      optional "<source folder>=<mod folder name>" pairs (separated by ';') copied into the
#                profile's mods folder after the run queue is taken and removed at the end, so a deploy never
#                changes the mods of a run that holds the queue. An existing target fails the run (exit 2).
#   -SaveSource  optional vehicle save folders (separated by ';') copied into the profile's Vehicles folder
#   -SaveName    as the names given here, in the same order, for this run only. A save of that name
#                that already exists is moved aside to Vehicles.harnessbak and put back at the end. The game
#                names a save by the name in its meta.toml, not by its folder, so keep the two the same.
#   -Build       build and deploy the harness (and the example consumer) inside the queue first, so a
#                build never races another session's running game holding the deployed DLL
#   -TimeoutSec  seconds before this script kills StarMap and reports a timeout (default 900, which fits a
#                large consumer suite). The harness gets a run budget 30 s below it
#                (KSA_HEADLESS_RUN_TIMEOUT), so a hung run normally ends itself, names the test it hung in
#                and still writes its results. The run-queue wait scales with it, so a run queued behind a
#                long run does not give up (exit 4) before the run ahead finishes.
#
# Exit codes: 0 = all tests passed, 1 = test failure(s), 2 = harness infrastructure failure,
# 3 = timeout (the harness watchdog ended the run, or this script killed StarMap), 4 = run-queue
# timeout, 5 = build failure.
#
# Usage: powershell -ExecutionPolicy Bypass -File "F:\Coding\KSA Modding\mods\HeadlessHarness\scripts\run-headless.ps1"

param(
    [string]$InstancePath = '',
    [string]$Vehicle = 'Test Vehicle 1',
    [string]$Vehicles = '',
    [string]$Tests = '',
    [string]$System = '',
    [string]$Rules = '',
    [string]$ContentMods = '',
    [string]$PreMods = '',
    [switch]$KeepManifest,
    [string]$Param = '',
    [string]$EnvVars = '',
    [string]$Seed = '',
    [ValidateRange(0, [int]::MaxValue)]
    [int]$TestTimeoutSec = 0,
    [string]$Deploy = '',
    [string]$SaveSource = '',
    [string]$SaveName = '',
    [switch]$Build,
    # Reject 0/negative up front: WaitForExit(0) would return at once and report a spurious timeout.
    [ValidateRange(1, [int]::MaxValue)]
    [int]$TimeoutSec = 900
)

$ErrorActionPreference = 'Stop'

$docs     = if ($InstancePath) { $InstancePath } else { Join-Path $env:USERPROFILE 'Documents\My Games\Kitten Space Agency' }
if ($InstancePath -and -not (Test-Path (Join-Path $InstancePath 'mods'))) {
    Write-Host "The instance folder $InstancePath has no mods folder."
    exit 2
}
$manifest = Join-Path $docs 'manifest.toml'
$backup   = Join-Path $docs 'manifest.toml.harnessbak'
$saveBackupDir = Join-Path $docs 'Vehicles.harnessbak'
$repoRoot = Split-Path $PSScriptRoot
$starmap  = 'F:\Coding\KSA Modding\refs\starmap\StarMap.exe'
$logDir   = Join-Path $env:TEMP 'ksa-headless-harness'
$runId    = "run-{0}-pid{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID
$log      = Join-Path $logDir "$runId.log"
$resultsFile = Join-Path $logDir "$runId-results.json"
$junitFile   = Join-Path $logDir "$runId-junit.xml"
$gameLog  = Join-Path $docs 'logs\KittenSpaceAgency.log'
# Wait for the run queue at least the base 10 minutes, but scale it with the run's own kill timeout
# so a run queued behind an equally long run does not give up before the run ahead can finish. The
# wait ceiling is 5x this run's own kill timeout (the default 900s run waits up to 4500s, a
# -TimeoutSec 3600 run up to 18000s).
$queueTimeoutSec = [Math]::Max(600, $TimeoutSec * 5)

# Every variable this script sets for the game process. They are cleared before the run, so an empty
# parameter means "unset" and not "whatever happened to be in the environment", and again after it.
$harnessVars = @(
    'KSA_HEADLESS_HARNESS', 'KSA_HEADLESS_LOG', 'KSA_HEADLESS_VEHICLE', 'KSA_HEADLESS_VEHICLES',
    'KSA_HEADLESS_TESTS', 'KSA_HEADLESS_SYSTEM', 'KSA_HEADLESS_RULES', 'KSA_HEADLESS_PARAMS',
    'KSA_HEADLESS_SEED', 'KSA_HEADLESS_TEST_TIMEOUT', 'KSA_HEADLESS_RUN_TIMEOUT', 'STARMAP_INSTANCE_PATH'
)

function Split-List([string]$value) {
    return @($value -split '[,;]' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Assert-LeafName([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value) -or $value -in @('.', '..') -or $value -match '[\\/]' -or
        $value.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        Write-Host "'$value' must be one folder name."
        exit 2
    }
}

$cleanupFailed = $false
function Invoke-Cleanup([scriptblock]$action) {
    try { & $action }
    catch {
        $script:cleanupFailed = $true
        Write-Host "Cleanup failed: $($_.Exception.Message)"
    }
}

$contentList = @(Split-List $ContentMods)
$preList = @(Split-List $PreMods)
foreach ($id in ($contentList + $preList)) { Assert-LeafName $id }
if ($KeepManifest -and ($contentList.Count -gt 0 -or $preList.Count -gt 0)) {
    Write-Host "-KeepManifest runs with the profile's own manifest and cannot be combined with -ContentMods or -PreMods."
    exit 2
}

# Parsed up front so a malformed value fails before the queue wait.
$extraEnv = [ordered]@{}
foreach ($pair in ($EnvVars -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
    $k, $v = $pair -split '=', 2
    $k = $k.Trim()
    if ($null -eq $v -or $k -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        Write-Host "-EnvVars entry '$pair' is not of the form NAME=value."
        exit 2
    }
    $extraEnv[$k] = $v
}
$savedEnv = @{}
foreach ($k in $extraEnv.Keys) { $savedEnv[$k] = [Environment]::GetEnvironmentVariable($k) }

$saveSources = @($SaveSource -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$saveNames = @($SaveName -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($saveSources.Count -ne $saveNames.Count) {
    Write-Host "-SaveSource names $($saveSources.Count) save(s) and -SaveName $($saveNames.Count); they must match."
    exit 2
}

# Core first, the pre mods, the harness, then the content mods; each id once.
$manifestIds = New-Object System.Collections.Generic.List[string]
foreach ($id in (@('Core') + $preList + @('HeadlessHarness') + $contentList)) {
    if (-not $manifestIds.Contains($id)) { $manifestIds.Add($id) }
}
$minimal = ($manifestIds | ForEach-Object { "[[mods]]`nid = `"$_`"`nenabled = true" }) -join "`n`n"

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
        dotnet build (Join-Path $repoRoot 'HeadlessHarness\HeadlessHarness.csproj') -v q -nologo -maxcpucount:4 "-p:ModDeployDir=$(Join-Path $docs 'mods/HeadlessHarness')/"
        if ($LASTEXITCODE -ne 0) { Write-Host "Harness build failed."; exit 5 }
        dotnet build (Join-Path $repoRoot 'examples\HarnessConsumerExample\HarnessConsumerExample.csproj') -v q -nologo -maxcpucount:4 "-p:ModDeployDir=$(Join-Path $docs 'mods/HarnessConsumerExample')/"
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
    # The same for vehicle saves a killed run moved aside.
    if (Test-Path $saveBackupDir) {
        foreach ($leftover in @(Get-ChildItem $saveBackupDir)) {
            $original = Join-Path $docs ("Vehicles\" + $leftover.Name)
            Write-Host "Found leftover save backup '$($leftover.Name)' from an aborted run - restoring it first."
            if (Test-Path $original) { Remove-Item $original -Recurse -Force }
            Move-Item $leftover.FullName $original
        }
        Remove-Item $saveBackupDir -Recurse -Force
    }

    if (-not $KeepManifest) {
        Copy-Item $manifest $backup -Force
        Write-Host "Manifest backed up to $backup"
    }
    $deployed = @()
    $copiedSaves = @()
    $movedSaves = @()
    try {
        foreach ($pair in ($Deploy -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
            $src, $name = $pair -split '=', 2
            if (-not $name) { Write-Host "-Deploy entry '$pair' is not of the form <source folder>=<mod folder name>."; exit 2 }
            Assert-LeafName $name
            $dst = Join-Path $docs "mods\$name"
            if (Test-Path $dst) { Write-Host "Deploy target $dst exists already - refusing to overwrite it."; exit 2 }
            $deployed += $dst
            Copy-Item $src $dst -Recurse -Force
            Write-Host "Deployed $src -> $dst"
        }
        for ($i = 0; $i -lt $saveSources.Count; $i++) {
            Assert-LeafName $saveNames[$i]
            $saveDst = Join-Path $docs ("Vehicles\" + $saveNames[$i])
            if (Test-Path $saveDst) {
                New-Item -ItemType Directory -Force $saveBackupDir | Out-Null
                Move-Item $saveDst (Join-Path $saveBackupDir $saveNames[$i])
                $movedSaves += $saveNames[$i]
            }
            $copiedSaves += $saveDst
            Copy-Item $saveSources[$i] $saveDst -Recurse -Force
            Write-Host "Copied save $($saveSources[$i]) -> $saveDst"
        }
        foreach ($id in ($preList + $contentList)) {
            if (-not (Test-Path (Join-Path $docs "mods\$id"))) {
                Write-Host "Mod '$id' is not in $docs\mods - deploy it there first."
                exit 2
            }
        }

        # The harness resolves bundled and profile saves after content loading.

        if ($KeepManifest) {
            $own = if (Test-Path $manifest) { Get-Content $manifest -Raw } else { '' }
            if ($own -notmatch 'id\s*=\s*"HeadlessHarness"') {
                Write-Host "Warning: $manifest does not seem to enable HeadlessHarness; the harness may not run."
            }
            Write-Host "Running with the profile's own manifest (-KeepManifest)."
        } else {
            # .NET writer => UTF-8 without BOM (Set-Content -Encoding utf8 would add a BOM in PS 5.1).
            [System.IO.File]::WriteAllText($manifest, $minimal)
            Write-Host "Wrote minimal manifest ($($manifestIds -join ' + '))."
        }

        New-Item -ItemType Directory -Force $logDir | Out-Null
        foreach ($name in $harnessVars) { Remove-Item "Env:\$name" -ErrorAction SilentlyContinue }
        if ($InstancePath) { $env:STARMAP_INSTANCE_PATH = $InstancePath }
        $env:KSA_HEADLESS_HARNESS = '1'
        $env:KSA_HEADLESS_LOG = $log
        if ($Vehicle) { $env:KSA_HEADLESS_VEHICLE = $Vehicle }
        if ($Vehicles) { $env:KSA_HEADLESS_VEHICLES = $Vehicles }
        if ($Tests) { $env:KSA_HEADLESS_TESTS = $Tests }
        if ($System) { $env:KSA_HEADLESS_SYSTEM = $System }
        if ($Rules) { $env:KSA_HEADLESS_RULES = $Rules }
        if ($Param) { $env:KSA_HEADLESS_PARAMS = $Param }
        if ($Seed) { $env:KSA_HEADLESS_SEED = $Seed }
        if ($TestTimeoutSec -gt 0) { $env:KSA_HEADLESS_TEST_TIMEOUT = "$TestTimeoutSec" }
        # Below the kill timeout, so the harness watchdog ends a hung run itself and writes its results.
        if ($TimeoutSec -ge 60) { $env:KSA_HEADLESS_RUN_TIMEOUT = "$($TimeoutSec - 30)" }
        foreach ($k in $extraEnv.Keys) {
            Set-Item -Path "Env:\$k" -Value $extraEnv[$k]
            Write-Host "Environment $k=$($extraEnv[$k])"
        }

        $systemNote = if ($System) { "system $System" } else { 'default system' }
        Write-Host "Launching StarMap headless ($systemNote, timeout ${TimeoutSec}s, log $log)..."
        # StarMap.exe is a console-subsystem app, so Start-Process would give it its own console
        # window that pops to the foreground and steals focus. CreateNoWindow keeps it windowless;
        # UseShellExecute=false lets it inherit this shell's environment (the KSA_HEADLESS_* vars set
        # above) and reuses the current console for output instead of allocating a new one.
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $starmap
        $psi.WorkingDirectory = Split-Path $starmap
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        # Stamped before the launch so the archive step below can tell a log this run wrote from the
        # leftover of the last windowed session.
        $launchedAt = Get-Date
        $p = [System.Diagnostics.Process]::Start($psi)
        if (-not $p.WaitForExit($TimeoutSec * 1000)) {
            Write-Host "Timeout reached - killing StarMap."
            # Awaited: Kill only signals, and Windows does not flush a file's timestamp while the
            # process still holds it open, so the game-log freshness check below would misread a log
            # this run did write.
            try { $p.Kill(); $p.WaitForExit() } catch {}
            $harnessExit = 3
        } else {
            $harnessExit = $p.ExitCode
            Write-Host "StarMap exited with code $harnessExit."
        }

        # On failure, keep the game's own log (overwritten by every game start) next to the run log.
        # Only when this run actually wrote it: KSA wires its file logger up inside Program..ctor,
        # which a headless run never reaches, so DefaultCategory output usually goes nowhere and the
        # file still holds the last windowed session. Archiving that unannounced sends whoever reads
        # it hunting through a log from a different run.
        # Best-effort: a copy failure (e.g. the file still locked by another process) must not
        # replace the meaningful harness exit code with a script error.
        if ($harnessExit -ne 0 -and (Test-Path $gameLog)) {
            try {
                if ((Get-Item $gameLog -ErrorAction Stop).LastWriteTime -lt $launchedAt) {
                    Write-Host "No game log from this run (KSA's file logger is not wired up headless); $gameLog is from an earlier session and was not archived."
                } else {
                    Copy-Item $gameLog (Join-Path $logDir "$runId.game.log") -Force -ErrorAction Stop
                    Write-Host "Archived game log to $logDir\$runId.game.log"
                }
            } catch {
                Write-Host "Could not archive the game log: $($_.Exception.Message)"
            }
        }
    }
    finally {
        if (-not $KeepManifest) {
            Invoke-Cleanup {
                Copy-Item $backup $manifest -Force
                Remove-Item $backup -Force
                Write-Host "Manifest restored."
            }
        }
        foreach ($d in $deployed) { Invoke-Cleanup { if (Test-Path $d) { Remove-Item $d -Recurse -Force }; Write-Host "Removed $d" } }
        foreach ($s in $copiedSaves) { Invoke-Cleanup { if (Test-Path $s) { Remove-Item $s -Recurse -Force }; Write-Host "Removed $s" } }
        foreach ($n in $movedSaves) {
            Invoke-Cleanup {
                $destination = Join-Path $docs ("Vehicles\" + $n)
                if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
                Move-Item (Join-Path $saveBackupDir $n) $destination
                Write-Host "Put back the save '$n'."
            }
        }
        Invoke-Cleanup { if ((Test-Path $saveBackupDir) -and @(Get-ChildItem $saveBackupDir).Count -eq 0) { Remove-Item $saveBackupDir -Force } }
        foreach ($name in $harnessVars) { Remove-Item "Env:\$name" -ErrorAction SilentlyContinue }
        foreach ($k in $extraEnv.Keys) {
            if ($harnessVars -contains $k) { continue }
            Invoke-Cleanup {
                if ($null -eq $savedEnv[$k]) { Remove-Item "Env:\$k" -ErrorAction SilentlyContinue }
                else { Set-Item -Path "Env:\$k" -Value $savedEnv[$k] }
            }
        }
    }
}
catch {
    Write-Host "Harness infrastructure failed: $($_.Exception.Message)"
    $harnessExit = 2
}
finally {
    if ($queued) { $queue.ReleaseMutex() }
    $queue.Dispose()
}

if ($cleanupFailed) { $harnessExit = 2 }

Write-Host ""
Write-Host "===== harness log: $log ====="
if (Test-Path $log) { Get-Content $log } else { Write-Host "(no harness log - Mod.OnBeforeMain may not have run)" }
Write-Host "(all run logs are kept in $logDir)"

# The harness writes the results and every data or report file as <run-log-basename>-<tag>.<ext>,
# i.e. sharing this run's id prefix.
if (Test-Path $resultsFile) {
    Write-Host ""
    Write-Host "===== results ====="
    try {
        $r = Get-Content $resultsFile -Raw | ConvertFrom-Json
        $t = $r.totals
        Write-Host ("{0} test(s): {1} pass, {2} fail, {3} skip, {4} infra, {5} timeout; exit {6} ({7})" -f `
            $t.tests, $t.pass, $t.fail, $t.skip, $t.infra, $t.timeout, $r.exitCode, $r.outcome)
    } catch {
        Write-Host "Could not read $resultsFile : $($_.Exception.Message)"
    }
    Write-Host $resultsFile
    if (Test-Path $junitFile) { Write-Host $junitFile }
}
$dataFiles = @(Get-ChildItem -Path $logDir -Filter "$runId-*" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -ne $resultsFile -and $_.FullName -ne $junitFile })
if ($dataFiles.Count -gt 0) {
    Write-Host ""
    Write-Host "===== data files produced by this run ====="
    foreach ($f in $dataFiles) { Write-Host $f.FullName }
}
exit $harnessExit
