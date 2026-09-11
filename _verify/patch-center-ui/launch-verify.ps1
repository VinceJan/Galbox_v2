#requires -Version 7
<#
  实机启动验证（补丁中心）。

  1. 启动 Release 版 Galbox.App.exe
  2. 等到真实顶层窗口（MainWindowHandle != 0）
  3. 用 UI Automation 点开 NavigationView 的「补丁中心」
  4. dump 页面自动化树，证明页面不是空白
  5. PrintWindow + 屏幕截图两条路径留证
  6. 关掉应用

  对真实游戏目录只读：这里不做任何安装。

  注意：脚本开头的 Stop-Process 只按可执行文件路径匹配本 worktree 的产物，
  避免误杀其它工作线正在跑的验收程序（A9 也会启动同一个 exe）。
#>
param(
    [string]$Exe = 'E:\tmp\Galbox_patchui\src\Galbox.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Galbox.App.exe',
    [string]$OutDir = 'E:\tmp\_galbox_patchui_scratch',
    [string]$LogFile = 'E:\tmp\_galbox_patchui_scratch\launch-verify.txt'
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type -Namespace Native -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

$transcript = [System.Collections.Generic.List[string]]::new()
function Say([string]$text) { $transcript.Add($text); Write-Host $text }

Say "=== GALBOX PATCH CENTRE - REAL MACHINE LAUNCH ==="
Say ("UTC started : " + [DateTime]::UtcNow.ToString('u'))
Say ("Executable  : " + $Exe)
Say ("Exists      : " + (Test-Path $Exe))

# 只杀本 worktree 的实例，不碰别的工作线正在跑的验收程序。
Get-Process Galbox.App -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $Exe } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700

$proc = Start-Process -FilePath $Exe -PassThru
Say ("Launched    : pid " + $proc.Id)

$handle = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
    $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if (-not $p) { break }
    $p.Refresh()
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $p.MainWindowHandle; break }
}

if ($handle -eq [IntPtr]::Zero) {
    Say "RESULT: FAIL - no top-level window appeared within 45s"
    $transcript | Set-Content -Path $LogFile -Encoding UTF8
    if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) { Stop-Process -Id $proc.Id -Force }
    exit 1
}

$p = Get-Process -Id $proc.Id
Say ("MainWindowHandle : 0x" + $handle.ToInt64().ToString('X'))
Say ("MainWindowTitle  : '" + $p.MainWindowTitle + "'")
Say ("Alive            : " + (-not $p.HasExited))

# ---------------------------------------------------------------- UI Automation
$root = [System.Windows.Automation.AutomationElement]::RootElement
$window = $null
for ($i = 0; $i -lt 25; $i++) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if ($window) { break }
    Start-Sleep -Milliseconds 400
}

if (-not $window) {
    Say "RESULT: FAIL - UI Automation could not find the window for pid $($proc.Id)"
    $transcript | Set-Content -Path $LogFile -Encoding UTF8
    Stop-Process -Id $proc.Id -Force
    exit 1
}

Say ("AutomationElement: '" + $window.Current.Name + "' class=" + $window.Current.ClassName)

function Get-Descendants($element) {
    return $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

function Dump-Names($element, [string]$label, [int]$max = 60) {
    Say ""
    Say "--- $label ---"
    $all = Get-Descendants $element
    $i = 0
    foreach ($e in $all) {
        $name = $e.Current.Name
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $i++
        if ($i -gt $max) { Say ("    ... (" + $all.Count + " elements total)"); break }
        Say ("    [" + $e.Current.ControlType.ProgrammaticName.Replace('ControlType.', '') + "] " + $name)
    }
    return $i
}

[void](Dump-Names $window "BEFORE navigation - window content" 40)

$patchItem = $null
foreach ($e in (Get-Descendants $window)) {
    if ($e.Current.Name -eq '补丁中心') { $patchItem = $e; break }
}
if (-not $patchItem) {
    foreach ($e in (Get-Descendants $window)) {
        if ($e.Current.Name -like '*补丁中心*') { $patchItem = $e; break }
    }
}

if (-not $patchItem) {
    Say ""
    Say "RESULT: FAIL - the 补丁中心 navigation item was not found in the automation tree"
    $transcript | Set-Content -Path $LogFile -Encoding UTF8
    Stop-Process -Id $proc.Id -Force
    exit 1
}

Say ""
Say ("NavItem found    : '" + $patchItem.Current.Name + "' type=" + $patchItem.Current.ControlType.ProgrammaticName)

$invoked = $false
try {
    $patchItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $invoked = $true
    Say "Invoked via      : SelectionItemPattern.Select()"
} catch { Say ("SelectionItemPattern failed: " + $_.Exception.Message) }

if (-not $invoked) {
    try {
        $patchItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $invoked = $true
        Say "Invoked via      : InvokePattern.Invoke()"
    } catch { Say ("InvokePattern failed: " + $_.Exception.Message) }
}

if (-not $invoked) {
    Say "RESULT: FAIL - could not activate the navigation item"
    $transcript | Set-Content -Path $LogFile -Encoding UTF8
    Stop-Process -Id $proc.Id -Force
    exit 1
}

Start-Sleep -Seconds 3

$p.Refresh()
Say ""
Say ("Alive after nav  : " + (-not $p.HasExited) + " (HasExited=" + $p.HasExited + ")")

[void](Dump-Names $window "AFTER navigation - patch centre page content" 90)

$pageText = ((Get-Descendants $window) | ForEach-Object { $_.Current.Name }) -join "`n"
$markers = @('本地补丁包（已下载）', '选择补丁压缩包', '在线补丁源：未实现', '刷新台账')
Say ""
Say "--- content markers ---"
$missing = 0
foreach ($m in $markers) {
    $found = $pageText -like ('*' + $m + '*')
    if (-not $found) { $missing++ }
    Say ("    [" + (& { if ($found) { 'FOUND' } else { 'MISS ' } }) + "] " + $m)
}

# ---------------------------------------------------------------- screenshot
$shot = Join-Path $OutDir 'patchcenter-printwindow.png'
try {
    [void][Native.Win]::ShowWindow($handle, 9)   # SW_RESTORE
    [void][Native.Win]::SetForegroundWindow($handle)
    Start-Sleep -Seconds 2

    [Native.Win+RECT]$rect = New-Object Native.Win+RECT
    [void][Native.Win]::GetWindowRect($handle, [ref]$rect)
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    Say ""
    Say ("Window rect      : " + $w + " x " + $h)

    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $gfx.GetHdc()
    $ok1 = [Native.Win]::PrintWindow($handle, $hdc, 2)   # PW_RENDERFULLCONTENT
    Start-Sleep -Milliseconds 400
    $ok2 = [Native.Win]::PrintWindow($handle, $hdc, 2)
    $gfx.ReleaseHdc($hdc)
    $gfx.Dispose()
    $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Say ("PrintWindow      : first=" + $ok1 + " second=" + $ok2)
    Say ("Screenshot       : " + $shot + " (" + (Get-Item $shot).Length + " bytes)")
} catch {
    Say ("Screenshot failed: " + $_.Exception.Message)
}

# 屏幕截图：WinUI 3 走 DirectComposition，PrintWindow 经常给出错位/被裁的帧，
# 所以这一张才是"页面长什么样"的可信证据。
$shot2 = Join-Path $OutDir 'patchcenter-screencopy.png'
try {
    [void][Native.Win]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 1200
    [Native.Win+RECT]$rect2 = New-Object Native.Win+RECT
    [void][Native.Win]::GetWindowRect($handle, [ref]$rect2)
    $w2 = $rect2.Right - $rect2.Left
    $h2 = $rect2.Bottom - $rect2.Top
    $bmp2 = New-Object System.Drawing.Bitmap($w2, $h2)
    $gfx2 = [System.Drawing.Graphics]::FromImage($bmp2)
    $gfx2.CopyFromScreen($rect2.Left, $rect2.Top, 0, 0, (New-Object System.Drawing.Size($w2, $h2)))
    $gfx2.Dispose()
    $bmp2.Save($shot2, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp2.Dispose()
    Say ("Screen capture   : " + $shot2 + " (" + (Get-Item $shot2).Length + " bytes)")
} catch {
    Say ("Screen capture failed: " + $_.Exception.Message)
}

Say ""
if ($missing -eq 0 -and -not $p.HasExited) {
    Say "RESULT: PASS - window present, patch centre opened, page contains the local-package workflow"
} else {
    Say ("RESULT: FAIL - missingMarkers=$missing exited=" + $p.HasExited)
}

$transcript | Set-Content -Path $LogFile -Encoding UTF8

Start-Sleep -Milliseconds 500
if (-not $p.HasExited) { Stop-Process -Id $proc.Id -Force }
Say "Process stopped."
$transcript | Set-Content -Path $LogFile -Encoding UTF8

if ($missing -eq 0) { exit 0 } else { exit 1 }
