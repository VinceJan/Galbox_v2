# Galbox

Windows 上的 Galgame（视觉小说）管理器：把散落在硬盘各处的游戏收进一个库，补上元数据，
并在"换存档""打补丁"这两件最容易出事的事情上提供可回滚的操作。

> **状态：功能已完成，尚未正式发布。** 本仓库里**没有预编译的下载包**，也没有打 tag，
> 所以想跑起来请按下面「构建」一节自行编译；但**打包链路是通的**——`tools/release.ps1`
> 一条命令就能产出自包含、免安装的绿色版，并且已经在实机验证过能运行
> （证据：`_verify/PACKAGING.md` 与 `_verify/Test-Launch.ps1`）。
> 版本号写在 `src/Galbox.App/Galbox.App.csproj` 的 `<Version>1.0.0</Version>`，
> 它目前只用于窗口标题与打包文件名，**不代表已经发布**。
> 各功能的真实完成度见「当前状态与已知限制」。
>
> ⚠️ **运行验收程序会启动真实 GUI，但窗口离屏**：A9 / A18 / A19 / A50 会启动 `Galbox.App.exe`，
> A50 会快速切换导航 200 次。窗口必须落在所有显示器之外，详见「五、运行验收程序」。

---

## 一、Galbox 是什么，给谁用

目标用户是**同时装了十几到几十部 Galgame、并且会在同一部游戏里反复换存档 / 打汉化补丁的人**。
它不替代游戏本身，也不做启动器的花活，而是解决三类具体麻烦：

1. **游戏散落在多个盘、多个目录里**，想找的时候不记得路径，也不记得装的是哪一部。
2. **元数据（中文名 / 原厂商 / 封面 / 评分）散在 Bangumi、VNDB 等站点上**，逐个手抄成本高。
3. **存档与补丁是高风险操作**：覆盖错了会把进度或游戏本体弄坏，而多数工具在这一步没有退路。

## 二、与"直接双击启动游戏"相比，Galbox 想多做什么

| 差异点 | 产品的意图 | 截至当前分支的真实状态 |
|---|---|---|
| **本地游戏库管理** | 扫描/添加游戏目录，识别引擎，刮削元数据，记录游玩时长，一键启动并监控进程 | **可用**。添加/批量扫描、库列表（网格与表格）、详情页、刮削页面、错误检测页面、进程监控（老板键、游玩时长）都有界面入口 |
| **存档节点标记** | 不止"备份一个文件夹"，而是把每个存档标记成"剧情走到哪"的节点，并能看出路线/进度/CG 解锁 | **已接通**。存档管理页里有真实的时间线视图、CG 图鉴、剧情进度、快照标记、疑似路线分组，以及诚实的空态与错误态（`src/Galbox.App/Views/SaveManagerPage.xaml`、`src/Galbox.App/ViewModels/SaveManagerViewModel.SaveNodes.cs`）；`ISaveNodeScanService` 已注册（`src/Galbox.App/App.xaml.cs:250`）。验收项 **A10** 在真实存档上跑通（12 个节点 / CG 6/27），并且要求界面真的绑到扫描服务上，注册一断就失败 |
| **补丁一键整合** | 用户下载好补丁包后，先沙箱解压 + 覆盖预览，再只备份会被覆盖的文件，装错了能逐字节回滚 | **已接通**。补丁中心页可以「选本地补丁包 → 沙箱解压 → 覆盖预览（覆盖 / 新增 / 冲突 / 未变化 / 被拒绝五类分开列）→ 安装（带进度、可取消）→ 逐文件结果 → 回滚 → 状态台账 → 中断恢复」（`src/Galbox.App/Views/PatchCenterPage.xaml`、`src/Galbox.App/ViewModels/PatchCenterViewModel.Patches.cs`；`AddGalboxPatches()` 与 `ILocalPatchService` 已注册，`src/Galbox.App/App.xaml.cs:293-294`）。验收项 **A30–A32** 覆盖接线、往返（安装后字节改变、回滚后逐字节还原）与被拒绝内容的可见性 |

## 三、系统要求

| 项 | 要求 | 依据 |
|---|---|---|
| 操作系统 | Windows 10 1809（build 17763）或更高，x64 | `src/Galbox.App/Galbox.App.csproj` 的 `TargetPlatformMinVersion` 与 `Platforms=x64` |
| 运行时 | .NET 8 桌面运行时 + **Windows App SDK 1.6 运行时** | 应用是 WinUI 3、**非打包（unpackaged）**、框架依赖；`Program.cs` 用 `Bootstrap.Initialize(0x00010006)` 显式加载 1.6 运行时 |
| 构建工具 | .NET SDK 8（`global.json` 指定 8.0.419，允许同 feature band 的补丁版本） | `global.json` |
| 网络 | 刮削元数据需要能访问四个源：`api.bgm.tv`、`api.vndb.org/kana`、`www.ymgal.games`、`api.cngal.org`。其中 **ymgal 与 cngal 都不需要你申请密钥** | `App.xaml.cs:108-139` 与 `Galbox.Core/Api/MetadataSourceOptions.cs`（ymgal 用官方文档公开的公共客户端，cngal 的 API 本身就无需 key） |

## 四、构建

```powershell
git clone https://github.com/VinceJan/Galbox_v2.git
cd Galbox_v2
dotnet build Galbox.sln -c Debug
```

`Galbox.sln` 包含 7 个项目：`Galbox.Core`、`Galbox.Data`、`Galbox.Services`、`Galbox.App`、
`tests/Galbox.Acceptance`、`tests/Galbox.Tests`、`tools/Galbox.MoyuVerifier`。
另外两个取证工具**不在解决方案里**，需要时单独构建：

| 项目 | 用途 | 为什么不进 sln |
|---|---|---|
| `tests/Galbox.Data.Migrations.Harness` | 数据库迁移的取证工具 | 只产出证据，不是产品代码；由 `tools/verify-db-migration.ps1` 调用 |
| `tools/Galbox.PatchVerifier` | 补丁安装器的取证工具 | 同上（csproj 内有明确注释） |

发布（绿色版）用 `tools/release.ps1`，详见「九、安装与分发」。

## 五、运行验收程序（本项目唯一可信的自动化验证）

`tests/Galbox.Acceptance` 是一个**无界面控制台程序**：它复刻 `App.xaml.cs` 的依赖注入容器
（只去掉两个必须依赖 `Microsoft.UI.Xaml.Controls` 的注册），驱动**真实的业务服务**，
打印每一项的"期望值 / 实测值 / 原始证据"，最后**全通过才返回退出码 0，否则返回 1**。

```powershell
# 1) 先构建整个解决方案（验收程序会去找 src\Galbox.App\bin 下的 Galbox.App.exe）
dotnet build Galbox.sln -c Debug

# 2) 运行
.\tests\Galbox.Acceptance\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.Acceptance.exe
```

> 单独 `dotnet build tests\Galbox.Acceptance\Galbox.Acceptance.csproj` 会把可执行文件输出到
> `bin\Debug\...`（没有 `x64` 这一段），路径容易踩空——这一点已实测确认。
> 所以「先 `dotnet build Galbox.sln`、再按上面那条带 `x64` 的完整路径运行」是必须的。

> ⚠️ **GUI 类检查会启动真实 `Galbox.App.exe`，但窗口必须离屏。** 52 项里有四项会启动应用：
> A9（带刮削缓存启动）、A18（查窗口标题）、A19（逐个导航页加载）、A50（**快速切换导航最多 200 次**）。
> 窗口落在所有显示器之外（见 §五 与 `docs/DEVELOPER-GUIDE.md` §6.1）；窗口若出现在屏幕上，那几项直接失败。
> A50 在低配机器或与其它验收进程并行时会跑满几十秒。**同一台机器不要并行跑多份验收。**

参数：

```
--game <folder>     作为真实测试素材的游戏目录（默认 D:\GAME\Dreamin'_Her）
--name <query>      用于构造测试记录与刮削关键词的名称（默认 Dreamin' Her）
--timeout <seconds> 整次运行的超时（默认 300）
--verbose, -v       打印捕获到的全部服务日志，而不只是 WARNING 及以上
```

使用前请确认：

* `--game` 指向的目录**真实存在且大于 900 MB**（A2 会拿这个下界校验文件夹大小计算，见
  `AcceptanceContext.cs` 的 `MinimumFolderSizeBytes`）；指一个空目录会让 A1–A3 失败。
* A5 / A7 / A40 / A41 / A42 是**真实联网查询**（刮削缓存被显式关闭），没有网络时这几项会失败。
* A9 / A18 / A19 / A50 会**真的启动 `Galbox.App.exe`**，需要 Windows App SDK 运行时已安装。
  窗口会**落在所有显示器之外、也不出现在任务栏里**（`dotnet test` 与 `tools\release.ps1` 同理），
  所以你跑验收时桌面上不会弹窗、不会自己翻页。这条规矩见
  [`docs/DEVELOPER-GUIDE.md` §6.1](docs/DEVELOPER-GUIDE.md)，并且**是可自动验证的**：
  窗口若与任何显示器相交，那几项检查会直接判失败。
* 它使用**每次运行独立的隔离数据库** `%LocalAppData%\Galbox\acceptance\run-<pid>\acceptance.db`
  （可用环境变量 `GALBOX_ACCEPTANCE_DIR` 指定到别处），补丁与图片也落在同一个隔离根下，
  **不读写你的真实游戏库**（`AcceptanceContainer.cs`）。
* **同一台机器上不要并行跑多份验收程序。** 它们都会启动、关闭**同名**的 `Galbox.App.exe`。
  实测后果是 GUI 类检查被拖慢（A50 从约 13 秒变成 250 秒，触到下限就提前停），也可能互相影响判定。
  仓库里三处「按名字杀**全部**实例」的脚本已清掉，但并行仍然不可取。

当前检查项（`tests/Galbox.Acceptance/Program.cs` 中注册的顺序即为执行顺序，共 **52** 项。
编号是分段的：A0–A19、A30–A32、A40–A42、**A50**、A60–A67、A70–A74、**A80**、A90–A92、**A100**、**A110–A116**。
**权威来源始终是那个注册数组**，本表可能滞后）：

| 编号 | 检查内容 |
|---|---|
| A0 | 验收数据库与真实库隔离；EF 模型能从零建表；`GameInfo` 能写入并读回；再重置一次证明可重复 |
| A1 | `FindExecutableInFolder` 在同时存在 64 位与 32 位 exe 的目录里选中正确的主程序 |
| A2 | `CalculateFolderSize` 报出真实目录大小（与独立递归遍历交叉验证） |
| A3 | `DetectSaveLocationAsync` 能找到 Ren'Py 存档目录 |
| A4 | `CheckGameAsync` 能跑完，结论字段完整，并恰好持久化与结论条数相同的记录行 |
| A5 | **真实联网刮削**：四个数据源各自的结果、错误、命中数与耗时 |
| A6 | A5 命中的元数据是否可用（标题、评分、封面 URL 等） |
| A7 | 上游契约探针：打印应用自己发出的 HTTP 流量，再用"修正后的请求"打同一上游，判断故障在 Galbox 还是在上游 |
| A8 | **"功能必须有门"**：导航 key ↔ 页面 ↔ ViewModel ↔ 转换器 ↔ 菜单项 的一致性（源码级检查） |
| A9 | 在刮削缓存非空的前提下启动真实 GUI，进程存活且拥有可见顶层窗口 |
| A10 | **存档节点**：真实存档扫描产出时间线数据（12 个节点、标签、CG 6/27），并断言界面确实绑在扫描服务上 |
| A11 | 快速切换在"当前存档无法备份"时中止并如实报错，而不是继续切换 |
| A12 | 恢复失败时把存档目录回滚到恢复前状态，并留下记录 |
| A13 | 存档位置探测**拒绝**把游戏安装目录本身当成存档目录 |
| A14 | 恢复校验拒绝"大小相同内容不同"和"缺文件"两类坏恢复 |
| A15 | 刮削到的封面与角色图真的下载成图片文件 |
| A16 | 删除游戏只删库里的记录，磁盘上的游戏文件保持不动 |
| A17 | 游戏目录缺失能被检出、在界面上说明，并带原因阻止启动 |
| A18 | 窗口标题是产品名，而不是 `WinUI Desktop` |
| A19 | 每一个导航目标都能在运行中的应用里加载出来 |
| A30 | 补丁中心接线：DI 能解析 `IPatchEngine` / `ILocalPatchService`，且 ViewModel 真的绑到它 |
| A31 | 补丁往返：预览 → 安装 → 字节确实改变 → 回滚 → 与原文件逐字节一致 |
| A32 | 被拒绝的补丁内容仍然可见：Zip Slip 条目与存档类包被拒绝、被报告、且一个字都没写入 |
| A40 | **ymgal 真实搜索**并能取回该游戏详情 |
| A41 | **cngal 真实搜索**并能取回该游戏详情 |
| A42 | 四个元数据源能区分"未配置 / 连不上 / 查无结果"三种状态 |
| A50 | **快速连续导航**（目标 200 次，下限 120 次）后进程仍然存活 |
| A60 | `MoyuApi` 能从 DI 解析，且用的是官方 `/v2/moyu` 的 HttpClient 配置 |
| A61 | 未配置 `nmk_` 密钥时如实报成"没配密钥"，而不是伪装成"查无结果" |
| A62 | `nmk_` 密钥以 DPAPI 加密落盘，任何失败/日志面上都不出现明文 |
| A63 | **合规断言**：任何 `/api` 路径都不可达（依据 moyu.moe `robots.txt` 的 `Disallow: /api`） |
| A64 | 体积字符串按二进制单位解析；游戏锚点只接受 `vndb:` / `catalog:` |
| A65 | 下载文件夹监视器能认领新文件，也能干净地超时退出 |
| A66 | 浏览器跳转只接受 HTTPS 的 moyu 补丁页，拒绝 `http`、`/api`、API/CDN 主机与其它域名 |
| A67 | 遵守 `429` 的 `Retry-After`，并在 `304` 时复用缓存文档 |
| A70 | 健康诊断：8 个诊断项按触发条件准确报出、严重度符合规格、且不误报 |
| A71 | 中文路径一键重命名：目录真的改名、数据库三个路径字段同步、冲突与运行中拒绝、失败可回滚 |
| A72 | Windows 兼容性一键写入：`HKCU` 兼容性层真的写入、撤销时还原旧值，且全程只写 HKCU |
| A73 | 自动修复不虚标：标为可自动修复的项真的能修，其余项如实拒绝而不是谎报成功 |
| A74 | 诊断文案中文化：标题、说明、方案、分级与持久化记录均无英文残留 |
| A90 | 预留接口层按文档形状存在（流程图 + 社区成就的接口、数据模型、功能开关） |
| A91 | 两个预留功能默认关闭，且前端没有任何入口（无导航 key、无页面、无菜单项、无按钮） |
| A92 | 预留功能**没有实现、没有假实现、没有占位实体**；容器解析为 null；存档节点 → 剧情位置的投影真的可用 |
| A80 | **真实游戏全链路**：添加 → 刮削 → 定位存档 → 扫描节点 → 时间线 → 备份 → 恢复，每步断言真实产物 |
| A100 | 存档只在备用路径时，备份仍能取到、验证、并记下实际来源文件夹 |
| A110 | 补丁中心 ViewModel 真的持有 moyu 服务；界面不再写「在线补丁源：未实现」；查询会打到 `/v2/moyu/patches` |
| A111 | 未配置 `nmk_` 密钥时显示明确的「未配置密钥」，不是空列表，且发出 0 个请求 |
| A112 | 没有 vndb id 时显示「无法查询」，发出 0 个请求 |
| A113 | 「没有找到补丁」与「查询失败」是两种状态、两套话术 |
| A114 | 经由补丁中心的新路径也不会碰到 `/api`（合规护栏仍在运行时生效） |
| A115 | 打开补丁页（dry-run 不启动浏览器）与接管下载（超时会点名下载文件夹） |
| A116 | 密钥可在补丁中心填写：非法值被拒绝、合法值真的进客户端、且不回显 |

本文件写作时的实测结果（Windows + .NET SDK 8.0.31，游戏目录 `D:\GAME\Dreamin'_Her` 存在）：

```
PASSED : 52
FAILED : 0
ERRORS : 0
TOTAL  : 52
EXIT CODE: 0
```

完整原始输出留档在 `_verify/acceptance-runs/moyuui-52-after-a115.txt`。
A50 实测 `survived 200 rapid switch(es)`，窗口矩形 `(-32000, -32000)`，不在任何显示器上。

关于 `tests/Galbox.Tests`：它已经被重写成诚实的测试（**在 `Galbox.sln` 里**、有项目引用、
断言真实存在），当前 `dotnet test` 的结果是 **2 通过 / 5 跳过**，每个跳过项都写了原因。
旧的「8/8 通过 100%」结论已由该项目的 `TEST_REPORT.md` 自己撤回。详见「已知限制」8.11。

## 六、项目结构

```
Galbox.sln
├── src/
│   ├── Galbox.Core/          # 无 UI、无数据库依赖的引擎层
│   │   ├── Api/              # Bangumi / VNDB / ymgal / cngal 客户端 + 名称匹配
│   │   ├── Saves/            # Ren'Py 存档解析（含零执行 pickle 扫描、RPA 解包）
│   │   └── Patches/          # 本机补丁安装器（沙箱解压 / 覆盖预览 / 备份 / 回滚 / 状态台账）
│   ├── Galbox.Data/          # EF Core 实体 + 迁移
│   │   ├── Entities/         # GameInfo / SaveNode / SaveGroup / PatchRecord / UserSettings ...
│   │   └── Migrations/       # GalboxDatabaseInitializer 与迁移文件、兼容性修复
│   ├── Galbox.Services/      # 需要"解析器 + 数据库"两边的应用服务（存档节点扫描、预留接口的投影）
│   └── Galbox.App/           # WinUI 3 应用：Views / ViewModels / Services / Converters
├── tests/
│   ├── Galbox.Acceptance/    # 无界面验收程序（52 项，本项目唯一可信的自动化验证）
│   ├── Galbox.Data.Migrations.Harness/  # 迁移取证工具
│   └── Galbox.Tests/         # FlaUI 启动冒烟 + 逐步审计后的功能测试（2 通过 / 5 跳过）
├── tools/
│   ├── Galbox.PatchVerifier/ # 补丁安装器取证工具
│   ├── Galbox.MoyuVerifier/  # moyu 补丁源服务层取证工具
│   ├── verify-db-migration.ps1
│   └── release.ps1           # 一条命令产出可分发绿色版（发布 → 核对 → 实机验证 → 打包）
├── docs/                     # 本文档目录（见文末索引）
└── _product/                 # 产品知识包：调研、缺陷档案、任务清单
```

分层的取舍（为什么 `Galbox.Services` 单独存在、为什么 `Galbox.Core` 不引用 `Galbox.Data`）
写在 [docs/DEVELOPER-GUIDE.md](docs/DEVELOPER-GUIDE.md)。

## 七、数据都放在哪

| 内容 | 位置 | 依据 |
|---|---|---|
| 数据库 | `%LocalAppData%\Galbox\galbox.db` | `App.xaml.cs` 的 `GetAppDataPath()` |
| 启动日志 | `%LocalAppData%\Galbox\logs\startup-YYYYMMDD.log` | `StartupDiagnostics.LogDirectory` |
| 存档备份（ZIP） | `%LocalAppData%\Galbox\SaveBackups\` | `SaveManagementService` 构造函数 |
| 刮削缓存 | `%LocalAppData%\Galbox\ScrapingCache\` | `ScrapingCacheService` |
| 封面 / 背景 / 角色图 | `%LocalAppData%\Galbox\Covers\`、`Backgrounds\`、`Characters\` | `GameImageService` 构造函数 |
| 截图 | `%LocalAppData%\Galbox\Screenshots\`（可在设置中改） | `ProcessMonitorService` |
| 补丁备份与台账 | `%LocalAppData%\Galbox\patchbak\<gameId>\<installId>\` | `Galbox.Core/Patches/README.md` |

## 八、当前状态与已知限制

下面每一条都对着代码核过；没有把握的地方会明说"未验证"。

### 8.1 存档解析只支持 Ren'Py

`Galbox.Core/Saves` 下的解析器（`RenpySaveAnalyzer`、`RenpyPersistentReader`、
`RenpyScriptIndexBuilder`、`Rpa.RpaArchive`）全部是 Ren'Py 专用。存档节点扫描服务在遇到
其它引擎时**不会猜测**，而是直接返回 `UnsupportedEngine` 并写明"当前已实现 Ren'Py"
（`src/Galbox.Services/Saves/SaveNodeScanService.cs:119-130`）。

实测覆盖的版本只有两个维度：**Ren'Py 7.4.11 / Python 2.7**（参考游戏 `Dreamin'_Her`，12 个真实存档）。
其它 Ren'Py 版本、其它引擎（TyranoScript、KiriKiri 等）**未验证**。

需要区分两件事：**存档目录的"位置探测"**支持 Ren'Py / KiriKiri / Tyrano / VNM / Unity / RPG Maker
六种启发式判断（`src/Galbox.App/Services/EngineSaveDetector.cs:103-131`），但**存档内容的解析**
（剧情位置、CG、进度）目前只有 Ren'Py。

### 8.2 路线/分支分组只能给"疑似"结论

Ren'Py 只序列化**本局被改动过**的变量，而这批实测存档里一个路线变量都读不到
（`RenpySaveNodeMapper.cs:105` 的注释记录了 12/12 缺失）。因此路线名只在"场景集合聚类"
产出 2 个以上同组成员的存档时才写，且**永远带 `疑似路线` 前缀**
（`RenpySaveNodeMapper.SpeculativeRoutePrefix`，同文件 92 行）；单独一个存档的 `RouteName` 留空。
`SaveGroup` 行也**故意不自动创建**——给分支命名交给用户决定。

### 8.3 没有"章节"概念，进度用"已解锁场景数 / 总场景数"

参考游戏的剧本是 262 个扁平 label，没有任何章节字段。所以 `ChapterProgressPercent`
的语义是"已解锁场景数 ÷ 总场景数"，并且在分母未知时**保持 NULL**（`NULL ≠ 0%`）。
依据：`RenpySaveNodeMapper.cs:155` 与 317 行、`_product/design/save-node-integration-plan.md` §5。

### 8.4 四个元数据源全部真实可用（ymgal / cngal 已实现）

`src/Galbox.Core/Api/YmgalCngalApi.cs` 里的两个客户端都是**对着线上服务做出来的真实实现**：

* **ymgal**（月幕Galgame）走官方 OAuth2 `client_credentials`，端点是
  `/oauth/token`、`/open/archive/search-game`、`/open/archive`；
  它使用官方开发者文档里**公开的公共客户端**（`MetadataSourceOptions.cs:78-82`），
  所以**不需要用户申请密钥**——开箱即用。想换成自己的 client，可以用环境变量
  `GALBOX_YMGAL_CLIENT_ID` / `GALBOX_YMGAL_CLIENT_SECRET` 覆盖（同文件 144-159 行：
  两个都没设时用公共客户端；只设了一个则如实报"未配置"，而不是偷偷回退）。
* **cngal**（CnGal）走 `https://api.cngal.org`，**这个 API 本身就是全开放的：无 key、无 token、无账号**
  （`MetadataSourceOptions.cs:171-198`）。

接线在 `App.xaml.cs:135-139`，两个 `HttpClient` 的基础地址集中在
`Galbox.Core/Api/HttpClientWrappers.cs:107-145`，保证应用与验收程序不会各配一份。
验收项 **A40（ymgal 真实搜索）/ A41（cngal 真实搜索）/ A42（区分"未配置 / 连不上 / 查无结果"）**。

**已知的上游不稳定（不是 Galbox 缺陷）**：ymgal 偶发返回**空 body 的 HTTP 302**，
大约 1/3 概率让 A40 失败，**重跑即过**。断言没有被放宽，也不打算放宽——放宽就等于把上游抖动
改写成"通过"。

### 8.5 补丁中心：本地安装链路与 moyu 在线源都已接通

**已经能用的部分（用户下载好补丁包之后）：** 补丁中心页上的「本地补丁包（已下载）」
就是完整链路——选包 → 沙箱解压 → 覆盖预览（**覆盖 / 新增 / 冲突 / 未变化 / 被拒绝**五类分开列，
其中"冲突"必须你逐个确认）→ 安装（带进度、可取消）→ 逐文件结果 → 回滚 → 状态台账 → 中断恢复。
界面在 `src/Galbox.App/Views/PatchCenterPage.xaml`（预览与五类清单 626-720 行、取消安装 736 行、
状态台账 807 行、中断恢复 853 行、回滚 886 行），后端在
`src/Galbox.App/ViewModels/PatchCenterViewModel.Patches.cs` 与 `src/Galbox.Core/Patches`；
`AddGalboxPatches()` 与 `ILocalPatchService` 都已注册（`src/Galbox.App/App.xaml.cs:293-294`）。
验收项 **A30–A32** 覆盖接线、安装→回滚的逐字节往返、以及被拒绝内容仍然可见。

**在线补丁源（moyu.moe）服务层与界面都已接通：**

* 服务层只允许访问官方 NextMoe 的 `/v2/moyu` 面，密钥由用户自带（`nmk_`，
  DPAPI 加密落盘、绝不明文、绝不出现在错误信息里），另有速率限制、`429`/`304` 处理、
  下载文件夹监视与浏览器跳转白名单。证据：`src/Galbox.Core/Api/Moyu*.cs`、
  `App.xaml.cs:150-190`、验收项 **A60–A67**（其中 A63 是**可自动执行的合规断言**：
  任何 `/api` 路径都不可达，依据 moyu.moe 的 `robots.txt` 写着 `Disallow: /api`）。
* 补丁中心页上的「在线补丁源：moyu.moe」可以填密钥、查补丁、打开补丁页、接管下载文件夹
  里刚下好的文件，再交给本机补丁引擎做预览与安装。四种结果（没密钥 / 没 vndb id / 查无结果 /
  查询失败）是四种状态，不会互相冒充。验收项 **A110–A116**。

**仍然不提供"程序化下载补丁包"**：Galbox 不下载补丁压缩包的字节。它读的是补丁元数据，
然后**把补丁页面交给浏览器**让用户自己下载（A66 断言这个跳转只接受 `https://www.moyu.moe/` 下的
补丁页，拒绝 `http`、`/api`、API/CDN 主机与其它域名）；下载完成后由 `MoyuDownloadWatcher`
在下载文件夹里认领文件（A65）。补丁备份与台账落在
`%LocalAppData%\Galbox\patchbak\<gameId>\<installId>\`。

### 8.6 流程图追踪 / 社区成就：只有预留接口层，默认关闭且前端隐藏（这是规格要求）

产品规格对这两个 P2 功能的原文是"**第一版只预留接口、前端隐藏**"，实现与之一致：

* 仓库里有**接口 + 数据模型 + 功能开关**：`src/Galbox.Core/Community/Flowcharts/IFlowchartProvider.cs`、
  `.../Achievements/IAchievementProvider.cs`、`.../ReservedFeatures.cs`；
* 两个开关是**编译期常量 `false`**（`ReservedFeatures.cs:224-240`），改开关必须改代码；
* 容器里**只注册了一个** `IReservedFeatureCatalog`（它只负责回答"这个功能不可用，因为缺什么"，
  `App.xaml.cs:256-274`），三个 provider 接口**故意不注册**，解析结果是 `null`；
* 界面上**没有任何入口**：没有导航 key、没有页面、没有菜单项、没有按钮，`Views/`、
  `ViewModels/`、`MainWindow.xaml` 里对这两个功能的提及数为 **0**；
* **没有实现、也没有假实现**：没有类型实现那三个接口，预留源文件里没有
  `NotImplementedException` / `TODO` / 假 `HttpClient`，EF 模型也没有为它们加实体。

验收项 **A90（接口层形状）/ A91（默认关闭且前端隐藏）/ A92（不假装可用）** 就是这三条断言的机器版本。
唯一的"缝"是被真实实现的：`Galbox.Services/Community/SaveNodePositionProjector.cs` 能把存档节点
投影成剧情位置（A92 逐字段校验）——那是这两个功能将来要接的地方。

### 8.7 存档节点标记的界面已接通

数据层（`SaveNode` / `SaveGroup` 实体与迁移）、解析层（`Galbox.Core/Saves`）、
编排层（`Galbox.Services/Saves/SaveNodeScanService`）、**界面层**都在：

* `src/Galbox.App/Galbox.App.csproj` 引用 `Galbox.Services`；
* `ISaveNodeScanService` 已注册（`src/Galbox.App/App.xaml.cs:245-251`），
  ViewModel 通过自己创建的 scope 解析它，避免把 scoped 服务注入根解析的 ViewModel；
* 存档管理页里有真实可点的界面：「扫描存档」按钮（`SaveManagerPage.xaml:406-417`，
  提示文案是"扫描这个游戏的存档目录，把每个存档解析成剧情节点"）、时间线视图（445-500 行）、
  **CG 图鉴**（560-635 行）、**剧情进度**（637-665 行）、**快照标记 / 清除**（668-713 行），
  以及分支分组文本与错误提示；
* 空态与错误态是**诚实的**：还没扫描过时显示"扫描后这里会按存档时间画出一条时间线……"
  （同文件 431-442 行的空态文案），失败时弹出 `InfoBar`「存档节点扫描未完成」并附带具体原因
  （424-429 行，标题对"不支持的引擎"会另作区分，使"没有数据"不会被误读成"没有存档"）；
  遇到未实现的引擎，界面照抄服务返回的原因说"暂不支持"（`SaveNodeScanService.cs:119-130`）。

验收项 **A10** 用真实存档跑完整链路（12 个节点、标签、CG 6/27），并且**如果注册被摘掉就会失败**——
它测的正是"界面有没有真的绑上扫描服务"，而不是只测服务本身。

### 8.8 一组设置项还只是"能存不能用"

设置页里的部分开关会被写进 `UserSettings` 并能读回显示，但**全仓库没有任何消费方**：

| 设置项 | 现状 |
|---|---|
| 自动备份（退出时）/ 自定义备份路径 | 只有设置页读写（`SettingsViewModel` 与 `SettingsPage.xaml`），无业务读取方 |
| 启动时自动扫描游戏目录 | 同上；`GameDirectoriesJson` 只被设置页自己读回 |
| 库默认视图（网格/表格） | 同上；库页有自己的视图切换按钮 |
| 主题 / 语言 | 同上，没有应用它们的代码 |
| Bangumi 访问令牌 | 能保存，`BangumiAuthService` 也会在启动时初始化，但**没有任何消费方**：`GameScrapingService` 只依赖四个 API 客户端，`BangumiHttpClient` 未加认证头 |

**真正接了线的设置**：刮削匹配阈值、四个数据源的开关与优先级、添加游戏后自动刮削
（经 `IScrapingSettingsProvider` 被刮削链路读取），以及老板键与截图相关配置
（经 `SettingsViewModel.ApplyRuntimeSettings()` 应用到进程监控服务）。

### 8.9 存档管理：一个正在修的已知缺陷，加上一处保留问题

* **【已知缺陷，正在修】存档只落在 `AlternativePaths` 的游戏，备份会失败。**
  这类游戏的存档位置探测结果是"主路径为空、备选路径里有十几个存档"，
  而 `CreateBackupAsync` 在 `PrimarySavePath` 为空时**直接返回 `null`**
  （`src/Galbox.App/Services/SaveManagementService.cs:172-178`），
  界面于是报"创建备份失败：未检测到存档文件"
  （`SaveManagerViewModel.cs:565`、`GameDetailViewModel.cs:774`）——
  **明明检测到了 12 个存档，却告诉你一个都没找到**。
  收集待备份文件的逻辑其实已经会把 `AlternativePaths` 算进去（同文件 227-233 行），
  卡住的是它前面那道提前返回。**另一条工作线正在修**
  （分支 `fix/save-backup-from-alternative-paths`），修好前请把这条当成真实故障读。
* **"自动备份"开关目前没有触发器**：`SaveManagerPage` 上的开关只改变
  `SaveManagementService.AutoBackupEnabled` 这个内存属性
  （`SaveManagerViewModel.cs:451-453` → `SaveManagementService.cs:60`），
  而 `CreateAutoBackupAsync` 在全仓库**没有任何调用者**；重启后开关还会回到默认值。

以下两条过去是本节的限制，**现在已经不是**——再次核对过代码，所以改写成事实记录：

* **恢复的完整性校验是零容差**：`VerifyRestoreIntegrityAsync`
  （`SaveManagementService.cs:671-682`）逐个条目比对 SHA-256 内容、精确大小与精确文件数，
  并且在该方法的注释里明确写了"两个比较对象都是本服务自己写出来的，没有任何理由留容差"。
  恢复失败会走真实回滚（`TryRollbackRestoreAsync` :954，失败路径调用点 :581 / :634）。
  验收项 **A14** 用"同大小不同内容"和"缺文件"两类坏恢复证明它会拒绝。
* **存档位置不会再退化成游戏安装根目录**：探测结果离开探测器之前会经过一道闸门
  （`src/Galbox.App/Services/EngineSaveDetector.cs:142-144` 调用 `RejectInstallRootAsSavePath`，
  拒绝理由写在同文件 224-243 行："把安装目录本身当作存档目录会在恢复时清空整个游戏目录，因此已拒绝"）。
  验收项 **A13** 分别用 RPG Maker / KiriKiri 的"存档就在安装根目录"场景证明它被拒绝，
  同时证明正常子目录仍然被接受。

### 8.10 截图会落盘，但不会出现在界面里

开启"退出时自动截图"后，`ProcessMonitorService` 会在游戏退出时把图片写入截图目录
（默认 `%LocalAppData%\Galbox\Screenshots\`），**文件是真的**。但：

* 触发写入后抛出的 `ScreenshotCaptured` 事件**没有任何订阅者**，
  **没有任何代码把截图记录写进 `Screenshots` 表**；
* 游戏详情页的"截图"区读的正是那张表（`GameDetailViewModel.cs:393-402`），因此它**恒为空**；
* 界面上也**没有手动截图按钮**：`CaptureFocusedWindowScreenshotAsync` 没有调用者。

要看截图，请直接打开那个目录。

### 8.11 `tests/Galbox.Tests` 已经被重写成诚实的测试，但它仍不是验收依据

它**在 `Galbox.sln` 里**，并且通过项目引用真的引用了 `Galbox.App`
（`tests/Galbox.Tests/Galbox.Tests.csproj`）。旧版本那套"每步 `return true`、按一个在 WinUI 3 里
并不存在的控件名去找导航项"的假测试**已被重写**（`GalboxFunctionalTests.cs:17` 记录了这次审计）。
现在 `dotnet test` 的结果是 **2 通过 / 5 跳过**，5 个跳过项都带着书面原因
（需要交互式文件夹选择器、控件级 UIA 在本机不稳定、需要真实游戏进程与全局热键等）。
该项目的 `TEST_REPORT.md` 现在是一份**诚实报告**：它自己撤回了旧版"8/8 通过 100%"的结论，
并记录了故意破坏 → 检出的对照实验。

两条注意事项：

* **`dotnet test` 也会开真实窗口**：`AppLaunchSmokeTests` 会启动 `Galbox.App.exe`
  并断言窗口标题不是启动失败提示。
* 判断"这个项目能不能用"**仍然以 `tests/Galbox.Acceptance` 为准**：
  它是唯一覆盖数据安全、接线与上游契约的门禁，`Galbox.Tests` 只是补充。

### 8.12 未验证的部分（明确声明）

* **打包/分发形态**：`tools/release.ps1` 这条链路在 `feat/distributable-packaging` 工作线上
  做过实机验证（证据 `_verify/PACKAGING.md`、`_verify/Test-Launch.ps1`），
  但**没有在 `release/1.0.0` 上重新跑一遍**，所以"绿色版能运行"这句目前属于
  "有证据、但不是本分支当场复验"。
* **未做代码签名**：产出的 exe 是未签名的，首次运行会有 SmartScreen 警告；仓库里没有签名证书。
* Ren'Py 之外的引擎存档解析、Ren'Py 其它版本：未做任何实测。
* 文档中所有"可用"的结论都来自**代码阅读 + 验收运行**，并且每条都注明了检查编号或
  `file:line`；但**界面交互本身（点击、折叠、对话框）没有自动化覆盖**——
  A8/A30/A91 这类源码级检查只能证明"门存在/门是连上的"，不能证明"门后一定好用"。
* A50（快速导航压力检查）在本轮全量验收中通过：200 次快速切换全部存活，窗口离屏。

## 九、安装与分发

**本仓库不提供预编译的安装包，也没有打 tag 或发布 Release**；分发包要自己出，
但这一步已经被固化成一个脚本：

```powershell
pwsh -File tools\release.ps1
```

它把「发布 → 核对产物 → 实机验证 → 打包」四步连起来，每一步都打印实测值，
任一步失败即以非零码退出（不会带着坏产物往下走）。产物是

```
Galbox-<version>-win-x64\          # 免安装目录
Galbox-<version>-win-x64.zip       # 分发包
```

默认落在**当前工作目录**，可以用 `-OutputRoot <dir>` 改；`-SkipLaunchProbe` 跳过实机启动验证，
`-NoClean` 保留已存在的输出目录。版本号取自 `src/Galbox.App/Galbox.App.csproj` 的 `<Version>`。

形态与实测数据（细节见 `_verify/PACKAGING.md`）：

| 形态 | 命令要点 | 体积（实测） | 目标机器需要预装 |
|---|---|---|---|
| **自包含绿色版**（推荐交付物） | `--self-contained true -p:WindowsAppSDKSelfContained=true` | 325 文件 / 168.92 MB | **什么都不用装**（免装 .NET 与 Windows App Runtime） |
| 框架依赖便携版 | `--self-contained false` | 78 文件 / 47.02 MB | .NET 8 桌面运行时 + Windows App Runtime 1.6 |

打包曾经出不来，根因是**未打包（unpackaged）的 WinUI 3 应用缺一个合并后的 `resources.pri`**：
缺了它进程会在 `Microsoft.UI.Xaml.dll` 里 fail-fast（`0xC000027B`，native 层，托管 `try/catch` 抓不到），
而且**任何输出目录的配置都受影响**。现在 `src/Galbox.App/Galbox.App.csproj` 里有一个
`GenerateAppResourcePriFile` 目标，用 `MakePri.exe` 生成并合并它。排查过程写在
`_verify/PACKAGING.md`。

**未做代码签名**：exe 没有签名，第一次运行时 Windows SmartScreen 会拦一次
（"Windows 已保护你的电脑"），要点"更多信息 → 仍要运行"。仓库里没有代码签名证书，
所以这件事只能由分发者自备证书解决。

## 十、文档

| 文档 | 内容 |
|---|---|
| [docs/USER-MANUAL.md](docs/USER-MANUAL.md) | 逐步的用户操作说明（每一步都对应真实的 XAML / ViewModel） |
| [docs/DEVELOPER-GUIDE.md](docs/DEVELOPER-GUIDE.md) | 分层架构、依赖注入规则、数据库迁移机制、如何扩展元数据源/验收检查、调试入口 |
| [CHANGELOG.md](CHANGELOG.md) | 本轮开发修了什么、加了什么（按缺陷档案逐条对照代码） |
| [_product/](_product/) | 产品知识包：缺陷档案、调研报告、任务清单（公开） |

> `docs/USER_MANUAL.md`、`docs/DEVELOPER_GUIDE.md`、`docs/CHANGELOG.md`、`docs/README.md`、
> `docs/API_DOCUMENTATION.md` 是早期生成的文档（注意是下划线命名的旧版）。它们**两个方向都过期**：
> 既有"写了却没实现"的描述（例如补丁下载、打包安装步骤），也有"已经实现但文档说没实现"的说法。
> **以 README 与上面三个文件为准**，其余仅供参考。

## 十一、许可证

本仓库当前**没有附带许可证文件**（仓库根目录下不存在 `LICENSE`）。
在补上之前，请不要假定它采用任何开源许可证。
