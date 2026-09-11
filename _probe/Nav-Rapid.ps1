# Nav-Rapid.ps1 - rapid continuous navigation stress probe for Galbox.
#
# THE SAME SCRIPT IS RUN BEFORE AND AFTER THE FIX. Its intensity must not be lowered:
#   * navigation items are clicked with a fixed pacing delay (default 40 ms)
#   * the switch count is a hard target that is always attempted in full
#   * no retry, no backoff, no "slow down when it looks unhappy"
#
# Usage:
#   Nav-Rapid.ps1 -Switches 200 [-Exe <path>] [-StepMs 40] [-FirstChance]
#
# Exit code: 0 when the process survived the full run, 1 when it died (or could not start).
param(
    [Parameter(Mandatory = $true)][int]$Switches,
    [string]$Exe = 'E:\tmp\Galbox_crash\src\Galbox.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.App.exe',
    [int]$StepMs = 40,
    [switch]$FirstChance,
    [switch]$KeepAlive,
    [string]$ArtifactDir = 'E:\tmp\Galbox_crash\_probe\artifacts'
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]

New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$report = Join-Path $ArtifactDir "navrapid-$stamp.txt"
$navlog = Join-Path $ArtifactDir "nav-smoke-$stamp.log"
$fcLog  = "$env:LocalAppData\Galbox\logs\firstchance.log"

function Say {
    param([string]$Text)
    $line = $Text
    Write-Host $line
    Add-Content -Path $report -Value $line -Encoding UTF8
}

function Get-EventEvidence {
    param([int]$Count = 14)
    $out = @()
    $since = (Get-Date).AddMinutes(-6)
    Write-Host ''
    Write-Host '--- WINDOWS EVENT LOG (Application) ---'
    try {
        $evts = Get-WinEvent -FilterHashtable @{
            LogName   = 'Application'
            StartTime = $since
        } -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match 'Galbox' -or $_.ProviderName -match 'Application Error|\.NET Runtime|Windows Error Reporting' } |
            Select-Object -First $Count
    } catch { $evts = @() }

    foreach ($e in $evts) {
        $txt = "----- [{0}] {1} (Id={2}, Level={3}) -----`n{4}`n" -f $e.TimeCreated, $e.ProviderName, $e.Id, $e.LevelDisplayName, $e.Message
        $out += $txt
    }
    if ($evts.Count -eq 0) { $out += "(no matching events in the last 6 minutes)" }
    return $out
}

function Get-WerEvidence {
    $out = @()
    $base = Join-Path $env:LocalAppData 'Microsoft\Windows\WER'
    foreach ($sub in @('ReportArchive', 'ReportQueue')) {
        $p = Join-Path $base $sub
        if (-not (Test-Path $p)) { continue }
        $dirs = Get-ChildItem $p -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'Galbox' -and $_.LastWriteTime -gt (Get-Date).AddMinutes(-20) }
        foreach ($d in $dirs) {
            $out += "=== WER folder: $($d.FullName) ($($d.LastWriteTime)) ==="
            $werFile = Get-ChildItem $d.FullName -Filter '*.wer' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($werFile) { $out += (Get-Content $werFile.FullName -Raw -ErrorAction SilentlyContinue) }
            $out += ''
        }
    }
    if ($out.Count -eq 0) { $out += '(no fresh WER report folders for Galbox)' }
    return $out
}

# ------------------------------------------------------------------ start
Say "================================================================"
Say " GAlBOX RAPID NAVIGATION PROBE"
Say "================================================================"
Say " started            : $(Get-Date -Format 'u')"
Say " exe                : $Exe"
Say " target switches    : $Switches"
Say " pacing (StepMs)    : $StepMs"
Say " first-chance trace : $($FirstChance.IsPresent)"
Say " report file        : $report"

if (-not (Test-Path $Exe)) { Say "FATAL: executable not found"; exit 1 }

if ($FirstChance -and (Test-Path $fcLog)) { Remove-Item $fcLog -Force -ErrorAction SilentlyContinue }
Remove-Item $navlog -Force -ErrorAction SilentlyContinue

$env:GALBOX_NAVIGATION_SMOKE = 'Library,SaveManager,PatchCenter,Settings,Home'
$env:GALBOX_NAVIGATION_SMOKE_FILE = $navlog
if ($FirstChance) { $env:GALBOX_FIRSTCHANCE_TRACE = '1' } else { Remove-Item Env:\GALBOX_FIRSTCHANCE_TRACE -ErrorAction SilentlyContinue }

$p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
$pid_ = $p.Id
Say " process            : pid=$pid_"

$pc = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $pid_)
$root = $AE::RootElement
$w = $null
for ($i = 0; $i -lt 60 -and -not $w; $i++) {
    Start-Sleep -Milliseconds 500
    if ($p.HasExited) { break }
    $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pc)
}

if ($p.HasExited) {
    Say "FATAL: process exited during startup (exit=0x$('{0:X8}' -f ($p.ExitCode -band 0xFFFFFFFF)))"
    exit 1
}
if (-not $w) { Say 'FATAL: main window never appeared'; Stop-Process -Id $pid_ -Force -ErrorAction SilentlyContinue; exit 1 }

Say " window acquired after $($i * 500) ms -> handle=$($w.Current.NativeWindowHandle)"

# Give the startup sequence (database + deferred services + monitor) time to finish, exactly
# like the reference probe. This is startup settle time, NOT pacing between navigations.
Start-Sleep -Seconds 4

# ------------------------------------------------- resolve the navigation items
# Menu items only (TreeScope::Descendants over the whole window), resolved by tag/name.
$candidates = @('主页', '游戏库', '存档管理', '补丁中心', 'Settings')
$items = @{}
foreach ($c in $candidates) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $c)
    $el = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($el) {
        $pat = $null
        $ok = $el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)
        if ($ok -and $pat) {
            $items[$c] = $pat
            Say " nav item resolved : $c"
        } else {
            Say " nav item NO-SELECTION-PATTERN : $c"
        }
    } else {
        Say " nav item NOT FOUND : $c"
    }
}

if ($items.Count -lt 2) {
    Say 'FATAL: fewer than 2 navigation items are reachable; the probe cannot stress navigation.'
    Stop-Process -Id $pid_ -Force -ErrorAction SilentlyContinue
    exit 1
}

$order = @($items.Keys)
$names = @($order)

# ---------------------------------------------------------------- stress loop
Say ''
Say "--- rapid navigation: $Switches switches, $StepMs ms pacing ---"
$done = 0
$died = $false
$failedSelects = 0
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

for ($n = 0; $n -lt $Switches; $n++) {
    $name = $order[$n % $order.Count]
    try {
        $items[$name].Select()
    } catch {
        $failedSelects++
    }
    $done = $n + 1

    if ($n % 25 -eq 24) {
        Say ("  ... {0}/{1} switches at {2:N1}s" -f $done, $Switches, $stopwatch.Elapsed.TotalSeconds)
    }

    Start-Sleep -Milliseconds $StepMs

    if ($p.HasExited) { $died = $true; break }
}

$stopwatch.Stop()

Say ''
Say "--- RESULT ---"
Say " switches completed : $done / $Switches"
Say " select calls failed: $failedSelects"
Say " elapsed            : $($stopwatch.Elapsed.TotalSeconds) s"

if ($died) {
    $p.WaitForExit()
    $code = $p.ExitCode
    Say " PROCESS DIED       : YES"
    Say (" exit code          : 0x{0:X8} ({0})" -f ($code -band 0xFFFFFFFF))
    Say " died after switch  : $done"
    Say " last item clicked  : $($order[($done - 1) % $order.Count])"
    Say " next item would be : $($order[$done % $order.Count])"

    # survive-the-crash check a moment later, so late WER writes land
    Start-Sleep -Seconds 4

    Say ''
    Say '--- APPLICATION LOG (app-YYYYMMDD.log, tail) ---'
    $applog = Get-ChildItem "$env:LocalAppData\Galbox\logs" -Filter 'app-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($applog) {
        Say " file: $($applog.FullName)"
        Say ((Get-Content $applog.FullName -Tail 25 -ErrorAction SilentlyContinue) -join "`n")
    } else { Say ' (no app log)' }

    Say ''
    Say '--- NAVIGATION SMOKE RESULT FILE ---'
    if (Test-Path $navlog) { Say ((Get-Content $navlog -ErrorAction SilentlyContinue) -join "`n") } else { Say ' (no smoke result file)' }

    Say ''
    Say '--- STARTUP LOG (tail) ---'
    $stlog = Get-ChildItem "$env:LocalAppData\Galbox\logs" -Filter 'startup-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($stlog) {
        Say " file: $($stlog.FullName)"
        Say ((Get-Content $stlog.FullName -Tail 20 -ErrorAction SilentlyContinue) -join "`n")
    }

    Say ''
    Say '--- FIRST-CHANCE LOG (tail) ---'
    if (Test-Path $fcLog) { Say ((Get-Content $fcLog -Tail 60 -ErrorAction SilentlyContinue) -join "`n") } else { Say ' (no firstchance.log)' }

    foreach ($l in (Get-EventEvidence)) { Say $l }
    foreach ($l in (Get-WerEvidence)) { Say $l }

    Say ''
    Say 'PROBE VERDICT: CRASHED'
} else {
    Say " PROCESS DIED       : NO"
    Say " alive after        : $Switches switches / $($stopwatch.Elapsed.TotalSeconds) s"
    Say " window handle      : $($w.Current.NativeWindowHandle)"
    Say ''
    Say 'PROBE VERDICT: SURVIVED'
}

$exitCode = if ($died) { 1 } else { 0 }

if (-not $p.HasExited) {
    if ($KeepAlive) {
        Say " (keeping pid=$pid_ alive for inspection)"
    } else {
        Stop-Process -Id $pid_ -Force -ErrorAction SilentlyContinue
    }
}

Say " report saved to    : $report"
exit $exitCode
