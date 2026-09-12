# Nav-Rapid-Cdb.ps1 - the SAME rapid navigation stress as Nav-Rapid.ps1, with a live debugger
# attached so the WinUI fail-fast is caught with the real thread stacks.
#
# Order of operations (this matters):
#   1. application starts normally, UI Automation finds its window
#   2. cdb attaches and immediately breaks (process is frozen, but the window already exists)
#   3. the navigation items are resolved while frozen - UI Automation finds them in the
#      already-built visual tree even though the UI thread is stopped
#   4. cdb is told to `g` (resume) and the stress loop runs at the identical pacing
#   5. WinUI fail-fasts -> cdb breaks on the faults and logs the stacks
param(
    [Parameter(Mandatory = $true)][int]$Switches,
    [string]$Exe = 'E:\tmp\Galbox_crash\src\Galbox.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.App.exe',
    [int]$StepMs = 40,
    [string]$ArtifactDir = 'E:\tmp\Galbox_crash\_probe\artifacts',

    # DANGEROUS, and deliberately off by default.
    #
    # This script used to begin with `Get-Process Galbox.App | Stop-Process -Force`, which kills
    # EVERY Galbox instance on the machine - including one somebody is using, and including the
    # application another test is measuring. That is how a concurrent run got its window closed
    # under it: an acceptance run on 2026-09-12 01:16 recorded A50 failing after 63 switches with
    # exit code 0x00000000, i.e. a clean exit rather than a crash, while this script was running
    # elsewhere on the same box. See
    # _product/design/acceptance-runs/16-known-flakiness-gui-checks.txt
    #
    # Leftover instances are now only reported. Pass -KillExisting when you really mean it and
    # nothing else on the machine is using Galbox.
    [switch]$KillExisting
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]

New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$report = Join-Path $ArtifactDir "navrapid-cdb-$stamp.txt"
$cdbLog = Join-Path $ArtifactDir "cdb-attached-$stamp.txt"
$cf     = Join-Path $ArtifactDir "cdb-cmds-$stamp.txt"
$dbg    = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe'

function Say { param([string]$T) Write-Host $T; Add-Content -Path $report -Value $T -Encoding UTF8 }

# The command file is executed in order. Everything up to the first `g` happens while the
# process is frozen at the debugger break; the probe resumes it by writing "g" to cdb's stdin.
# cdb's own log file stays buffered while the debugger holds it open, so the freeze point is
# signalled through a sentinel file that the debuggee's shell creates for us.
$sentinel = Join-Path $ArtifactDir "cdb-ready-$stamp.flag"
Remove-Item $sentinel -Force -ErrorAction SilentlyContinue
@"
.symfix E:\tmp\symcache
.reload
sxe -c "g" eh
sxe -c "g" clr
sxe -c "g" c000027b
`.shell -ci "cmd /c echo ready > `"$sentinel`"" 
.echo ########## BROKEN: waiting for the probe to resolve nav items ##########
"@ | Set-Content -Path $cf -Encoding ascii

Say "================================================================"
Say " RAPID NAVIGATION PROBE + LIVE DEBUGGER (freeze / configure / resume)"
Say "================================================================"
Say " started         : $(Get-Date -Format 'u')"
Say " target switches : $Switches"
Say " pacing (StepMs) : $StepMs"
Say " cdb log         : $cdbLog"
Say " report          : $report"

$existing = @(Get-Process Galbox.App -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) {
    if ($KillExisting) {
        Say " killing $($existing.Count) existing Galbox.App instance(s) (-KillExisting was passed)"
        $existing | ForEach-Object { try { $_.Kill() } catch {} }
        Start-Sleep -Seconds 1
    } else {
        Say " WARNING: $($existing.Count) Galbox.App instance(s) already running:"
        $existing | ForEach-Object { Say "          pid $($_.Id)  started $($_.StartTime.ToString('HH:mm:ss'))" }
        Say "          This script will NOT touch them (a machine-wide kill used to be the first"
        Say "          thing it did, and that closed another test's window under it)."
        Say "          Attribute your measurements accordingly, or pass -KillExisting if you are"
        Say "          certain nothing else on this machine is using Galbox."
    }
}

$p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
$appPid = $p.Id
Say " app pid         : $appPid"

$pc = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $appPid)
$root = $AE::RootElement
$w = $null
for ($i = 0; $i -lt 60 -and -not $w; $i++) {
    Start-Sleep -Milliseconds 500
    if ($p.HasExited) { break }
    $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pc)
}
if ($p.HasExited) { Say "FATAL: app exited during startup"; exit 1 }
if (-not $w) { Say 'FATAL: no window'; Stop-Process -Id $appPid -Force; exit 1 }
Say " window          : handle=$($w.Current.NativeWindowHandle) after $($i * 500) ms"

# Wait for the startup sequence to settle BEFORE attaching, so the settle time is not
# swallowed by the debugger break. Same 4 s as Nav-Rapid.ps1.
Start-Sleep -Seconds 4

# Attach: cdb breaks immediately, then runs the command file up to the `.echo BROKEN` line.
$cdb = Start-Process -FilePath $dbg -ArgumentList @('-g','-G','-p', $appPid, '-logo', $cdbLog, '-cf', $cf) -PassThru -WindowStyle Hidden
Say " cdb attached    : cdb pid=$($cdb.Id)"

# Wait until cdb signals that it has finished configuring and is frozen at the break.
$broken = $false
for ($i = 0; $i -lt 180; $i++) {
    Start-Sleep -Milliseconds 500
    if ($cdb.HasExited) { break }
    if (Test-Path $sentinel) { $broken = $true; break }
}
Say " cdb broken      : $broken (after $($i * 500) ms)"
if (-not $broken) { Say 'FATAL: cdb did not reach the freeze point'; Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue; exit 1 }
Start-Sleep -Seconds 1   # let the sentinel write settle before UIA round-trips

# ------------------------------------------- resolve nav items while the UI thread is frozen
$candidates = @('主页', '游戏库', '存档管理', '补丁中心', 'Settings')
$items = [ordered]@{}
foreach ($c in $candidates) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $c)
    $el = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($el) {
        $pat = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat) -and $pat) {
            $items[$c] = $pat
            Say " nav item resolved : $c"
        } else {
            Say " nav item resolved : $c (no selection pattern)"
        }
    } else {
        Say " nav item NOT FOUND : $c"
    }
}
if ($items.Count -lt 2) { Say 'FATAL: <2 nav items'; Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue; exit 1 }
$order = @($items.Values); $names = @($items.Keys)
Say " nav items ready : $($names -join ', ')"

# ------------------------------------------------------------------ resume and stress
Say ' resuming under debugger...'
[void]$cdb.StandardInput.WriteLine('g')
$cdb.StandardInput.Flush()

Say ''
Say "--- rapid navigation: $Switches switches, $StepMs ms pacing ---"
$done = 0; $died = $false
$sw = [System.Diagnostics.Stopwatch]::StartNew()
for ($n = 0; $n -lt $Switches; $n++) {
    try { $order[$n % $order.Count].Select() } catch { }
    $done = $n + 1
    if ($n % 25 -eq 24) { Say ("  ... {0}/{1} at {2:N1}s" -f $done, $Switches, $sw.Elapsed.TotalSeconds) }
    Start-Sleep -Milliseconds $StepMs
    if ($p.HasExited) { $died = $true; break }
}
$sw.Stop()
Say ''
if ($died) {
    $p.WaitForExit()
    Say (" PROCESS DIED : YES  exit=0x{0:X8} after switch {1} (last='{2}')" -f ($p.ExitCode -band 0xFFFFFFFF), $done, $names[($done-1) % $names.Count])
} else {
    Say " PROCESS DIED : NO   survived $done switches in $($sw.Elapsed.TotalSeconds)s"
}

for ($i = 0; $i -lt 90 -and -not $cdb.HasExited; $i++) { Start-Sleep -Seconds 1 }
if (-not $cdb.HasExited) { Say ' (cdb still running - stopping it)'; Stop-Process -Id $cdb.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 }
Say " cdb finished    : exited=$($cdb.HasExited)  log size=$((Get-Item $cdbLog -ErrorAction SilentlyContinue).Length)"
if (-not $p.HasExited) { Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue }
Say " report          : $report"
exit $(if ($died) { 1 } else { 0 })
