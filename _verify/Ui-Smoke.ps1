# UI Automation smoke test: drives the real NavigationView of the published app and confirms
# every page can be shown (i.e. every compiled .xbf resolves from resources.pri at runtime).
param([Parameter(Mandatory = $true)][string]$Exe)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
$t = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)

$win = $null
for ($i = 0; $i -lt 40 -and -not $win; $i++) {
    Start-Sleep -Milliseconds 500
    if ($p.HasExited) { break }
    $win = $t.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}
if (-not $win) { Write-Host "NO WINDOW"; if (-not $p.HasExited) { $p.Kill() }; exit 1 }
Write-Host "Window: '$($win.Current.Name)'  handle=$($win.Current.NativeWindowHandle)"

# NavigationView items surface as ListItem elements (SelectionItemPattern).
$listCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCond)
Write-Host "Navigation items found: $($items.Count)"

$results = @()
foreach ($it in $items) {
    $name = $it.Current.Name
    if ([string]::IsNullOrWhiteSpace($name)) { continue }
    $ok = $true; $err = ''
    try {
        $pat = $null
        if ($it.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) {
            $pat.Select()
        } else {
            $ip = $null
            if ($it.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$ip)) { $ip.Invoke() }
            else { $ok = $false; $err = 'no selectable/invoke pattern' }
        }
    } catch { $ok = $false; $err = $_.Exception.Message }
    Start-Sleep -Milliseconds 900
    if ($p.HasExited) { $ok = $false; $err = "process died (exit $($p.ExitCode))" ; break }
    $results += [pscustomobject]@{ Page = $name; Opened = $ok; Note = $err }
}

$results | Format-Table -AutoSize | Out-Host
$alive = -not $p.HasExited
Write-Host "PROCESS ALIVE AFTER NAVIGATION THROUGH $($results.Count) PAGE(S): $alive"
if ($alive) { $p.Refresh(); Write-Host "MainWindowHandle after nav: $($p.MainWindowHandle)" }
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
