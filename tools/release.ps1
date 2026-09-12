# Galbox 发布脚本
#
# 一条命令产出可分发的 Windows 程序：
#
#   pwsh -File tools\release.ps1
#
# 产物：
#   发布目录  $OutputRoot\Galbox-<version>-win-x64\
#   分发包    $OutputRoot\Galbox-<version>-win-x64.zip
#
# 第 3 步的实机验证默认让窗口**离屏**启动，不会在跑脚本的人的桌面上弹窗；
# 想亲眼看一次用户看到的样子加 -OnScreen。原因与全仓的规矩见 docs/DEVELOPER-GUIDE.md §6.1。
#
# 这个脚本把「发布 → 核对产物 → 实机验证 → 打包」四步固化下来，避免每次手敲命令时漏步。
# 每一步都会打印实测值，任一步失败即以非零码退出（不会带着坏产物继续往下走）。
#
# 背景：发布的根因曾是 resources.pri 从未生成 —— 未打包的 WinUI 3 应用连框架自身的资源
# 也要走应用的主资源映射解析，缺少合并 PRI 会在 Microsoft.UI.Xaml.dll 里 fail-fast
#（0xC000027B，native 层，托管 try/catch 抓不到）。详见
# _product/design/acceptance-runs/09-published-build-verified.txt。

[CmdletBinding()]
param(
    # 发布产物落地的根目录。默认是本次会话的工作目录，用户回头就能看到。
    [string] $OutputRoot = 'C:\Users\Jiang\Desktop\Tmp\dsh启动地址',

    # 跳过实机启动验证（只做发布与打包时用）。
    [switch] $SkipLaunchProbe,

    # 让发布出来的程序按用户看到的样子出现在屏幕上（默认离屏，见第 3 步的说明）。
    [switch] $OnScreen,

    # 保留已存在的输出目录内容（默认会先清理）。
    [switch] $NoClean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\Galbox.App\Galbox.App.csproj'

function Write-Step([string] $text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Ok([string] $text) { Write-Host "  [OK]   $text" -ForegroundColor Green }
function Write-Bad([string] $text) { Write-Host "  [FAIL] $text" -ForegroundColor Red }
function Fail([string] $text) { Write-Bad $text; exit 1 }

# ---------------------------------------------------------------- 前置检查
Write-Step '0. 前置检查'
if (-not (Test-Path $appProject)) { Fail "找不到应用项目：$appProject" }
Write-Ok "仓库根目录 : $repoRoot"

# 版本号取自 csproj，避免脚本与项目各写一份。
# 注意用正则而不是 $xml.Project.PropertyGroup.Version：csproj 有多个 PropertyGroup，
# 在 Set-StrictMode 下访问某个 PropertyGroup 上不存在的属性会直接抛错。
$versionMatch = Select-String -Path $appProject -Pattern '<Version>([^<]+)</Version>' |
    Select-Object -First 1
if (-not $versionMatch) { Fail 'csproj 里没有 <Version>，无法确定版本号' }
$version = $versionMatch.Matches[0].Groups[1].Value.Trim()
Write-Ok "版本号     : $version"

$packageName = "Galbox-$version-win-x64"
$stageDir = Join-Path $OutputRoot $packageName
$zipPath = Join-Path $OutputRoot "$packageName.zip"

# ---------------------------------------------------------------- 1. 发布
Write-Step '1. 发布（自包含，用户机器无需预装 .NET 与 Windows App Runtime）'
$publishArgs = @(
    'publish', $appProject,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:WindowsAppSDKSelfContained=true',
    '-v', 'q', '--nologo'
)
Write-Host "  dotnet $($publishArgs -join ' ')"
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish 退出码 $LASTEXITCODE" }
Write-Ok 'dotnet publish 成功'

$publishDir = Join-Path $repoRoot 'src\Galbox.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'
if (-not (Test-Path $publishDir)) { Fail "找不到发布目录：$publishDir" }

# ---------------------------------------------------------------- 2. 核对产物
Write-Step '2. 核对发布产物'
$allFiles = Get-ChildItem $publishDir -Recurse -File
$totalMb = [math]::Round((($allFiles | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Ok "文件数     : $($allFiles.Count)"
Write-Ok "总体积     : $totalMb MB"

# 这五个是「能启动」的必要条件，缺任何一个都直接失败，不进入下一步。
$mustExist = @{
    'Galbox.App.exe'                          = '主程序'
    'resources.pri'                           = '合并后的资源索引（缺失会导致启动 fail-fast）'
    'Microsoft.WindowsAppRuntime.Bootstrap.dll' = '框架自举'
    'Microsoft.ui.xaml.dll'                   = '自包含部署的标志'
}
foreach ($name in $mustExist.Keys) {
    $hit = Get-ChildItem $publishDir -Recurse -Filter $name -ErrorAction SilentlyContinue
    if (-not $hit) { Fail "产物缺少 $name（$($mustExist[$name])）" }
    Write-Ok "$name  ($($mustExist[$name]))"
}

$xbf = @(Get-ChildItem $publishDir -Recurse -Filter '*.xbf' -ErrorAction SilentlyContinue)
if ($xbf.Count -lt 10) { Fail "编译后的 XAML 只搬运了 $($xbf.Count) 个（应至少 10 个）" }
Write-Ok "已编译 XAML: $($xbf.Count) 个"

$pri = Get-Item (Join-Path $publishDir 'resources.pri')
Write-Ok "resources.pri: $([math]::Round($pri.Length / 1KB, 1)) KB"

# ---------------------------------------------------------------- 3. 实机验证
if (-not $SkipLaunchProbe) {
    Write-Step '3. 实机启动验证（窗口必须真的出现）'

    # 同名进程会互相干扰（本仓库有过「遗留 Galbox.App.exe 杀掉同名进程」的记录），
    # 所以先确认没有别的实例在跑，否则测量结果无法归因。
    $existing = @(Get-Process -Name 'Galbox.App' -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        Write-Host "  发现 $($existing.Count) 个已有的 Galbox.App 实例，先结束它们（否则本次测量无法归因）" -ForegroundColor Yellow
        $existing | ForEach-Object { try { $_.Kill() } catch {} }
        Start-Sleep -Milliseconds 800
    }

    $exe = Join-Path $publishDir 'Galbox.App.exe'

    # ---------------------------------------------------------------- 离屏启动（默认）
    #
    # 这个脚本由发布工程师在自己机器上反复运行，每跑一次就弹一次窗口正是要避免的打扰。
    # 所以探针默认让窗口落在所有显示器之外（docs/DEVELOPER-GUIDE.md §6.1 是同一条规矩）。
    #
    # 为什么这里也敢离屏 —— 本步的每一条断言都与窗口位置无关，全部实测过：
    #   * MainWindowHandle 非零：实测离屏后仍非零（.NET 找主窗口只要求无属主 + IsWindowVisible）
    #   * 窗口标题可读         ：实测仍是 "Galbox 1.0.0"
    #   * CloseMainWindow() 能正常退出：它按句柄投递 WM_CLOSE，与位置无关
    # 「必须看用户看到的那一版」这个理由在这里不成立：离屏只是 MainWindow 构造里一个
    # if (环境变量) 分支，启动路径上其它每一步（DI、迁移、缓存预热、建窗口、导航、进程监控）
    # 走的都是同一条代码。想亲眼看一次用户实际看到的样子，加 -OnScreen。
    #
    # ⚠️ 时序：这个变量由「被启动的那个 exe」决定要不要理会。本次发布之前的产物
    #（例如 Galbox-1.0.0-win-x64，来自 967ed5e）根本不认识它，会被静默忽略、窗口照样出现在屏幕上。
    # 所以「设置了变量但窗口还是出现了」不是 bug，而是那份产物还没有这个能力 —— 本改动要到
    # 下一次发布才生效。
    $probeOffscreen = -not $OnScreen
    $offscreenVariable = $null
    if ($probeOffscreen) {
        # 变量名与取值从应用源码里读出来，不在脚本里另抄一份 —— 抄的那份会随时间腐烂，
        # 而且腐烂的方式是「悄悄又开始弹窗」，正是这次要根除的问题。
        $mainWindowSource = Join-Path $repoRoot 'src\Galbox.App\MainWindow.xaml.cs'
        $source = Get-Content $mainWindowSource -Raw
        $nameMatch = [regex]::Match($source, 'OffscreenWindowVariable\s*=\s*"([^"]+)"')
        $valueMatch = [regex]::Match($source, 'OffscreenWindowEnabledValue\s*=\s*"([^"]+)"')
        if (-not $nameMatch.Success -or -not $valueMatch.Success) {
            Fail "在 $mainWindowSource 里找不到离屏开关的常量定义（OffscreenWindowVariable / OffscreenWindowEnabledValue）。发布探针默认离屏，找不到就不敢猜；传 -SkipLaunchProbe 或 -OnScreen 后重跑。"
        }

        $offscreenVariable = $nameMatch.Groups[1].Value
        [System.Environment]::SetEnvironmentVariable($offscreenVariable, $valueMatch.Groups[1].Value, 'Process')
        Write-Host "  探针以离屏方式启动（$offscreenVariable=$($valueMatch.Groups[1].Value)），不会打扰正在用这台机器的人；加 -OnScreen 可改回屏幕内" -ForegroundColor DarkGray
    } else {
        Write-Host "  探针以 -OnScreen 启动：窗口会真的出现在屏幕上（用户看到的那一版）" -ForegroundColor Yellow
    }

    try {
        $proc = Start-Process -FilePath $exe -WorkingDirectory $publishDir -PassThru
    } finally {
        if ($offscreenVariable) {
            [System.Environment]::SetEnvironmentVariable($offscreenVariable, $null, 'Process')
        }
    }
    Write-Host "  PID $($proc.Id) 于 $(Get-Date -Format 'HH:mm:ss.fff') 启动"

    $deadline = (Get-Date).AddSeconds(60)
    $handle = 0
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        try {
            $proc.Refresh()
            if ($proc.HasExited) { break }
            if ($proc.MainWindowHandle -ne 0) { $handle = $proc.MainWindowHandle; break }
        } catch { break }
    }

    try { $proc.Refresh() } catch {}
    if ($proc.HasExited) {
        Fail "进程已退出，退出码 $($proc.ExitCode) (0x$('{0:X8}' -f $proc.ExitCode))"
    }
    if ($handle -eq 0) { Fail '60 秒内没有出现主窗口' }

    Write-Ok "MainWindowHandle : $handle"
    Write-Ok "窗口标题         : $($proc.MainWindowTitle)"
    Write-Ok "启动耗时         : $([math]::Round(((Get-Date) - $proc.StartTime).TotalSeconds, 1)) 秒"

    # 正常关窗能退出，说明消息循环是好的（不只是「有窗口」）。
    $proc.CloseMainWindow() | Out-Null
    Start-Sleep -Seconds 3
    try { $proc.Refresh() } catch {}
    if ($proc.HasExited) { Write-Ok '正常关闭（消息循环正常）' }
    else { $proc.Kill(); Write-Host '  [WARN] 无法正常关闭，已强制结束' -ForegroundColor Yellow }
} else {
    Write-Step '3. 实机启动验证 —— 已按参数跳过'
}

# ---------------------------------------------------------------- 4. 落地与打包
Write-Step '4. 落地与打包'
if (-not (Test-Path $OutputRoot)) { New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null }
if (Test-Path $stageDir) {
    if ($NoClean) { Write-Host "  保留已存在的 $stageDir（-NoClean）" }
    else { Remove-Item $stageDir -Recurse -Force; Write-Ok '已清理旧的发布目录' }
}
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
Copy-Item (Join-Path $publishDir '*') $stageDir -Recurse -Force
Write-Ok "发布目录 : $stageDir"

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Ok "分发包   : $zipPath ($zipMb MB)"

# ---------------------------------------------------------------- 收尾说明
Write-Step '完成'
Write-Host @"
  给用户的三句话：
    1. 解压到任意位置（建议非中文路径），双击 Galbox.App.exe 即可运行
    2. 不需要安装、不需要管理员权限、不需要预装 .NET 或 Windows App Runtime
    3. 首次运行会在 %LocalAppData%\Galbox\ 下建立数据目录（数据库、日志、封面缓存）

  仍未做（需要产品决策，见 _product/design/DELIVERY-CHECKLIST.md）：
    - 代码签名（未签名的 exe 会被 SmartScreen 警告）
    - 安装包（当前是绿色版，无安装器与开始菜单项）
"@
exit 0
