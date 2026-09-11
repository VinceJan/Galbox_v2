# For each navigation item: start a FRESH process, select exactly that item, then watch for 25s.
param([Parameter(Mandatory = $true)][string]$Exe)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$root = $AE::RootElement

function Start-App {
    $p = Start-Process -FilePath $script:Exe -WorkingDirectory (Split-Path $script:Exe) -PassThru
    $pc = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
    $w = $null
    for ($i = 0; $i -lt 40 -and -not $w; $i++) {
        Start-Sleep -Milliseconds 500
        if ($p.HasExited) { return @($p, $null) }
        $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pc)
    }
    Start-Sleep -Seconds 3   # let the startup sequence finish
    return @($p, $w)
}

$pages = @('主页', '游戏库', '存档管理', '补丁中心', 'Settings')
foreach ($pg in $pages) {
    $r = Start-App
    $p = $r[0]; $w = $r[1]
    if (-not $w) { Write-Host "[$pg] no window"; continue }
    $sel = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $pg)
    $el = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $sel)
    if (-not $el) { Write-Host "[$pg] not reachable in the tree"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; continue }
    $pat = $null
    $el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat) | Out-Null
    if ($pat) { $pat.Select() } else { Write-Host "[$pg] no selection pattern" }

    $died = -1
    for ($t = 1; $t -le 50; $t++) {
        Start-Sleep -Milliseconds 500
        if ($p.HasExited) { $died = $t; break }
    }
    if ($died -lt 0) {
        $p.Refresh()
        Write-Host ("[{0}] SURVIVED 25s   handle={1}" -f $pg, $p.MainWindowHandle)
    } else {
        Write-Host ("[{0}] DIED after {1:N1}s   exit=0x{2:X8}" -f $pg, ($died * 0.5), ($p.ExitCode -band 0xFFFFFFFF))
    }
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
}
