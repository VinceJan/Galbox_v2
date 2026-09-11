# Launch harness: starts a Galbox.App.exe, observes window/exit, captures diagnostics.
# Usage: .\Test-Launch.ps1 -Exe <path> -Tag <label>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Tag,
    [int]$WaitSeconds = 20
)

$ErrorActionPreference = 'Continue'
$logDir = "E:\tmp\Galbox_verify\_verify\logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$appLogDir = Join-Path $env:LOCALAPPDATA 'Galbox\logs'

$report = [ordered]@{
    tag            = $Tag
    exe            = $Exe
    exeExists      = (Test-Path $Exe)
    startTime      = (Get-Date).ToString('s')
    launched       = $false
    exited         = $false
    exitCode       = $null
    windowHandle   = 0
    windowTitle    = ''
    verdict        = 'UNKNOWN'
}

if (-not (Test-Path $Exe)) { $report.verdict = 'EXE-MISSING'; $report | ConvertTo-Json | Out-Host; exit 1 }

# Mark the app log so we can slice only the lines this run produced.
$logBefore = @()
$appLogNow = Join-Path $appLogDir ("startup-{0}.log" -f (Get-Date).ToString('yyyyMMdd'))
if (Test-Path $appLogNow) { $logBefore = @(Get-Content $appLogNow) }
$beforeCount = $logBefore.Count

$startT = Get-Date
$p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
$report.launched = $true
$report.pid = $p.Id

$deadline = $startT.AddSeconds($WaitSeconds)
$handle = 0
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
    $p.Refresh()
    if ($p.HasExited) { break }
    try {
        $proc = Get-Process -Id $p.Id -ErrorAction Stop
        if ($proc.MainWindowHandle -ne 0) { $handle = $proc.MainWindowHandle; break }
    } catch { break }
}

$p.Refresh()
if ($p.HasExited) {
    $report.exited = $true
    $report.exitCode = $p.ExitCode
    $report.exitCodeHex = ('0x{0:X8}' -f ($p.ExitCode -band 0xFFFFFFFF))
} else {
    try {
        $proc = Get-Process -Id $p.Id -ErrorAction Stop
        $report.windowHandle = $proc.MainWindowHandle
        $report.windowTitle = $proc.MainWindowTitle
        if ($handle -ne 0) { $report.windowHandle = $handle }
    } catch { }
    # give it a moment, then re-read the title (message boxes appear late)
    Start-Sleep -Seconds 1
    try {
        $proc = Get-Process -Id $p.Id -ErrorAction Stop
        if ($proc.MainWindowHandle -ne 0) {
            $report.windowHandle = $proc.MainWindowHandle
            $report.windowTitle = $proc.MainWindowTitle
        }
    } catch { }
}

# Verdict
if (-not $report.exited -and $report.windowHandle -ne 0 -and $report.windowTitle -ne 'Galbox 启动失败') {
    $report.verdict = 'OK - window visible'
} elseif (-not $report.exited -and $report.windowTitle -eq 'Galbox 启动失败') {
    $report.verdict = 'FAIL - startup error dialog'
} elseif ($report.exited) {
    $report.verdict = "FAIL - process exited ($($report.exitCodeHex))"
} else {
    $report.verdict = 'FAIL - process alive but no window'
}

# Collect startup log lines produced by this run
$newLines = @()
if (Test-Path $appLogNow) {
    $all = @(Get-Content $appLogNow)
    if ($all.Count -gt $beforeCount) { $newLines = $all[$beforeCount..($all.Count - 1)] }
}
$report.newLogLines = $newLines
Set-Content -Path (Join-Path $logDir "$Tag.applog.txt") -Value ($newLines -join "`n") -Encoding UTF8

# Collect event log entries written since the start
$evText = ''
try {
    $evs = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $startT } -ErrorAction Stop |
        Where-Object { $_.Message -match 'Galbox' -or $_.ProviderName -eq 'Application Error' -or $_.ProviderName -eq '.NET Runtime' }
    foreach ($e in $evs) {
        $evText += "=== [$($e.ProviderName)] id=$($e.Id) $($e.TimeCreated) ===`n$($e.Message)`n`n"
    }
} catch { $evText = "(no events / $($_.Exception.Message))" }
Set-Content -Path (Join-Path $logDir "$Tag.events.txt") -Value $evText -Encoding UTF8

# Kill it if still alive
if (-not $p.HasExited) {
    try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
    Start-Sleep -Milliseconds 800
}
# Reap any leftover children
Get-Process -Name 'Galbox.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$report | ConvertTo-Json -Depth 4 | Out-Host
