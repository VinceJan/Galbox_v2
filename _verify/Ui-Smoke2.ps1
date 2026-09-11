# UI Automation smoke test v2: re-resolves each navigation element by name before selecting it,
# so a re-render between selections cannot invalidate the walk.
param([Parameter(Mandatory = $true)][string]$Exe)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
$root = [System.Windows.Automation.AutomationElement]::RootElement
$procCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)

$win = $null
for ($i = 0; $i -lt 40 -and -not $win; $i++) {
    Start-Sleep -Milliseconds 500
    if ($p.HasExited) { break }
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $procCond)
}
if (-not $win) { Write-Host 'NO WINDOW'; if (-not $p.HasExited) { $p.Kill() }; exit 1 }
Write-Host "Window: '$($win.Current.Name)'  handle=$($win.Current.NativeWindowHandle)"

$listCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$names = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCond)) {
    $n = $e.Current.Name
    if (-not [string]::IsNullOrWhiteSpace($n) -and $names -notcontains $n) { $names += $n }
}
Write-Host "Distinct navigation items: $($names.Count) -> $($names -join ' | ')"

$results = @()
foreach ($n in $names) {
    $sel = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $n)
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $sel)
    if (-not $el) { $results += [pscustomobject]@{ Page = $n; Opened = $false; Note = 'element not found' }; continue }
    $ok = $true; $err = ''
    try {
        $pat = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select() }
        else {
            $ip = $null
            if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$ip)) { $ip.Invoke() }
            else { $ok = $false; $err = 'no pattern' }
        }
    } catch { $ok = $false; $err = $_.Exception.Message }
    Start-Sleep -Milliseconds 1100
    if ($p.HasExited) { $results += [pscustomobject]@{ Page = $n; Opened = $false; Note = "process died (exit $($p.ExitCode))" }; break }
    $results += [pscustomobject]@{ Page = $n; Opened = $ok; Note = $err }
}

$results | Format-Table -AutoSize | Out-Host
Write-Host "PROCESS ALIVE AFTER WALKING $($results.Count) PAGE(S): $(-not $p.HasExited)"
if (-not $p.HasExited) { $p.Refresh(); Write-Host "MainWindowHandle: $($p.MainWindowHandle)"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
