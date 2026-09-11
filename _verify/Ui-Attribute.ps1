# Attributes a crash to a specific navigation item: after each selection, watch the process for
# several seconds instead of a single fixed sleep.
param([Parameter(Mandatory = $true)][string]$Exe)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]

$p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
$root = $AE::RootElement
$procCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $null
for ($i = 0; $i -lt 40 -and -not $win; $i++) {
    Start-Sleep -Milliseconds 500
    if ($p.HasExited) { break }
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $procCond)
}
if (-not $win) { Write-Host 'NO WINDOW'; if (-not $p.HasExited) { $p.Kill() }; exit 1 }
Write-Host "window='$($win.Current.Name)'"

# Wait until the startup sequence has fully completed before touching anything.
$applog = Join-Path $env:LOCALAPPDATA 'Galbox\logs'
for ($i = 0; $i -lt 30; $i++) {
    $today = Join-Path $applog ("startup-{0}.log" -f (Get-Date -Format yyyyMMdd))
    if ((Test-Path $today) -and ((Get-Content $today -Raw) -match 'startup sequence completed')) { break }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 1

$listCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$names = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCond)) {
    $n = $e.Current.Name
    if (-not [string]::IsNullOrWhiteSpace($n) -and $names -notcontains $n) { $names += $n }
}
Write-Host "items: $($names -join ' | ')`n"

foreach ($n in $names) {
    if ($p.HasExited) { Write-Host "[$n] skipped - already dead"; continue }
    $sel = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $n)
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $sel)
    if (-not $el) { Write-Host "[$n] element not found (no action taken)"; continue }
    $pat = $null
    $how = 'selection'
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select() }
    else {
        $ip = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$ip)) { $ip.Invoke(); $how = 'invoke' }
        else { Write-Host "[$n] no usable pattern"; continue }
    }
    $dead = $false
    for ($t = 1; $t -le 6; $t++) {
        Start-Sleep -Milliseconds 500
        if ($p.HasExited) { $dead = $true; Write-Host "[$n] <-- PROCESS DIED ${t}x0.5s after $how, exit=$('0x{0:X8}' -f ($p.ExitCode -band 0xFFFFFFFF))"; break }
    }
    if (-not $dead) { Write-Host "[$n] ok ($how), still alive after 3s" }
}
Write-Host "`nfinal alive: $(-not $p.HasExited)"
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
