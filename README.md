# Galbox

Windows 上的 Galgame（视觉小说）管理器：把散落在硬盘各处的游戏收进一个库，补上元数据，
并在"换存档""打补丁"这两件最容易出事的事情上提供可回滚的操作。

> **状态：开发中，尚无正式发布版本。** 本仓库当前没有可下载的安装包，也没有对外版本号。
> 想跑起来请按下面「构建」一节自行编译。各功能的真实完成度见「当前状态与已知限制」。

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
| **存档节点标记** | 不止"备份一个文件夹"，而是把每个存档标记成"剧情走到哪"的节点，并能看出路线/进度/CG 解锁 | **引擎与数据模型已完成，界面入口尚未接线**。Ren'Py 存档解析器、存档节点扫描服务、`SaveNode`/`SaveGroup` 表与迁移都在仓库里，但在当前分支上**没有任何页面/按钮能触达它们**（详见下文限制） |
| **补丁一键整合** | 用户下载好补丁包后，先沙箱解压 + 覆盖预览，再只备份会被覆盖的文件，装错了能逐字节回滚 | **本地安装器引擎已完成，界面入口尚未接线**。`Galbox.Core/Patches` 是完整实现，`tools/Galbox.PatchVerifier` 是它的证据生成器；补丁中心页面目前只展示数据库中真实存在的补丁记录 |

## 三、系统要求

| 项 | 要求 | 依据 |
|---|---|---|
| 操作系统 | Windows 10 1809（build 17763）或更高，x64 | `src/Galbox.App/Galbox.App.csproj` 的 `TargetPlatformMinVersion` 与 `Platforms=x64` |
| 运行时 | .NET 8 桌面运行时 + **Windows App SDK 1.6 运行时** | 应用是 WinUI 3、**非打包（unpackaged）**、框架依赖；`Program.cs` 用 `Bootstrap.Initialize(0x00010006)` 显式加载 1.6 运行时 |
| 构建工具 | .NET SDK 8（`global.json` 指定 8.0.419，允许同 feature band 的补丁版本） | `global.json` |
| 网络 | 首次刮削元数据需要能访问 `api.bgm.tv` 与 `api.vndb.org` | `App.xaml.cs` 中两个 `HttpClient` 的 `BaseAddress` |

## 四、构建

```powershell
git clone https://github.com/VinceJan/Galbox_v2.git
cd Galbox_v2
dotnet build Galbox.sln -c Debug
```

`Galbox.sln` 只包含 5 个项目：`Galbox.Core`、`Galbox.Data`、`Galbox.Services`、`Galbox.App`、
`tests/Galbox.Acceptance`。另外三个工具项目**不在解决方案里**，需要时单独构建：

| 项目 | 用途 | 为什么不进 sln |
|---|---|---|
| `tests/Galbox.Data.Migrations.Harness` | 数据库迁移的取证工具 | 只产出证据，不是产品代码；由 `tools/verify-db-migration.ps1` 调用 |
| `tools/Galbox.PatchVerifier` | 补丁安装器的取证工具 | 同上（csproj 内有明确注释） |
| `tests/Galbox.Tests` | FlaUI 界面自动化脚本 | 见「已知限制」——它当前不能作为验证依据 |

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
> `bin\Debug\...`（没有 `x64` 这一段），路径容易踩空。

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
* A5/A7 是**真实联网查询**（刮削缓存被显式关闭），没有网络时这两项会失败。
* A9 会**真的启动一次 `Galbox.App.exe`**，检查窗口是否出现；它需要 Windows App SDK 运行时已安装。
  窗口会**落在所有显示器之外**（A18/A19/A50 同理，`dotnet test` 与 `tools\release.ps1` 也是），
  所以你跑验收时桌面上不会弹窗、不会自己翻页——这条规矩见
  [`docs/DEVELOPER-GUIDE.md` §6.1](docs/DEVELOPER-GUIDE.md)。
* 它只使用 `%LocalAppData%\Galbox\acceptance\acceptance.db`，并在每次运行时删除重建，
  **不会读写你的真实游戏库**（`AcceptanceContainer.cs`）。

当前检查项（`tests/Galbox.Acceptance/Program.cs` 中注册的顺序即为执行顺序）：

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

编写本文件时的实测结果（Windows + .NET SDK 8.0.425，游戏目录 `D:\GAME\Dreamin'_Her` 存在）：

```
PASSED : 10
FAILED : 0
ERRORS : 0
TOTAL  : 10
EXIT CODE: 0  (all checks PASS)
```

**这个数字来自本机实际运行，不是从旧文档抄来的。** 旧的测试报告（`tests/Galbox.Tests/TEST_REPORT.md`
所宣称的"8/8 通过 100%"）建立在一个零断言、且不在解决方案里的项目上，不可采信。

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
│   ├── Galbox.Services/      # 需要"解析器 + 数据库"两边的应用服务（当前是存档节点扫描）
│   └── Galbox.App/           # WinUI 3 应用：Views / ViewModels / Services / Converters
├── tests/
│   ├── Galbox.Acceptance/    # 无界面验收程序（唯一的自动化验证手段）
│   ├── Galbox.Data.Migrations.Harness/  # 迁移取证工具
│   └── Galbox.Tests/         # 历史遗留的 FlaUI 脚本，不可作为验证依据
├── tools/
│   ├── Galbox.PatchVerifier/ # 补丁安装器取证工具
│   └── verify-db-migration.ps1
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
| 截图 | `%LocalAppData%\Galbox\Screenshots\`（可在设置中改） | `ProcessMonitorService` |
| 补丁备份与台账 | `%LocalAppData%\Galbox\patchbak\<gameId>\<installId>\` | `Galbox.Core/Patches/README.md` |

## 八、当前状态与已知限制

下面每一条都对着代码核过；没有把握的地方会明说"未验证"。

### 8.1 存档解析只支持 Ren'Py

`Galbox.Core/Saves` 下的解析器（`RenpySaveAnalyzer`、`RenpyPersistentReader`、
`RenpyScriptIndexBuilder`、`Rpa.RpaArchive`）全部是 Ren'Py 专用。存档节点扫描服务在遇到
其它引擎时**不会猜测**，而是直接返回 `UnsupportedEngine` 并写明"当前已实现 Ren'Py"
（`src/Galbox.Services/Saves/SaveNodeScanService.cs:120-130`）。

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

### 8.4 元数据源里 ymgal / cngal 是空壳

`src/Galbox.Core/Api/YmgalCngalApi.cs` 里两个客户端的 `SearchAsync` 直接返回
`Success = false`、`Message = "…integration pending - needs API research"`，`GetGameAsync` 返回
`null`；`App.xaml.cs` 给这两个 `HttpClient` 注册时也**故意没有配 `BaseAddress`**。
设置页里它们的开关能拨动，但没有真实查询发生。

可用的源是 **Bangumi（`api.bgm.tv` v0 端点）与 VNDB（`api.vndb.org/kana`）**，这一点由 A5/A7 在联网实测中确认。

### 8.5 补丁中心不提供直链下载

上游（moyu / NextMoe 那条线）没有给出官方直链下载契约，因此**程序化下载既无实现也无计划**，
只能跳到浏览器让用户自行下载。补丁中心的"下载/安装"两个按钮**已随其假实现一并删除**
（`PatchCenterViewModel.cs:470-475` 的说明注释）；现在这个页面只显示数据库里真实存在的补丁记录，
没有记录时显示"尚未配置补丁源"的空状态。

真正完成的是**用户把补丁包下载下来之后**的那一段：沙箱解压 → 覆盖预览 → 只备份被覆盖文件 →
安装 → 回滚，全部在 `src/Galbox.Core/Patches`，用 `tools/Galbox.PatchVerifier` 出证据。
**这一段目前没有界面入口**（`AddGalboxPatches()` 在 `App.xaml.cs` 中没有任何调用者，
也没有 ViewModel 引用 `IPatchEngine`）。

### 8.6 流程图追踪、社区成就只有设想，未实现

在 `src/` 全目录检索 `Achievement` / `Flowchart` / `成就` / `流程图` **零命中**。
这两个功能需要自建服务端，目前只停留在产品文档里，没有接口实现。
（`_product/Galbox-产品知识总纲.md` 对它们的判定同样是"仅停在文档"。）

### 8.7 存档节点标记没有界面入口（本分支）

数据层（`SaveNode` / `SaveGroup` 实体与迁移）、解析层（`Galbox.Core/Saves`）、
编排层（`Galbox.Services/Saves/SaveNodeScanService`）都在仓库里，但：

* `src/Galbox.App/Galbox.App.csproj` **不引用** `Galbox.Services`；
* `AddGalboxPatches()` 与 `ISaveNodeScanService` 在 `App.xaml.cs` 里**都没有注册**；
* `SaveManagerPage.xaml` 是**备份管理器**（创建/恢复/快速切换/删除备份），没有任何节点、时间线或进度的界面元素。

所以从用户视角看：**这些能力现在还摸不到**。

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

### 8.9 存档管理的两处保留问题

* **"自动备份"开关目前没有触发器**：`SaveManagerPage` 上的开关只改变
  `SaveManagementService.AutoBackupEnabled` 这个内存属性，而 `CreateAutoBackupAsync`
  在全仓库**没有任何调用者**；重启后开关还会回到默认值。
* **恢复的完整性校验带容差**：`VerifyRestoreIntegrityAsync` 允许文件数 20%、总大小 10% 的偏差
  （`SaveManagementService.cs:598-610`），因此它**不能证明"逐字节还原"**。
  失败路径会执行真实回滚（同文件 499-515 行）。
* **部分引擎的存档位置会退化为游戏安装根目录**：KiriKiri / RPG Maker 在找不到专用存档子目录时，
  会把 `PrimarySavePath` 设为 `game.InstallPath`（`EngineSaveDetector.cs:452-454`、`645-648`）。
  对这类游戏执行恢复/回滚会在该目录下整体操作，动手前请自行确认。

### 8.10 截图会落盘，但不会出现在界面里

开启"退出时自动截图"后，`ProcessMonitorService` 会在游戏退出时把图片写入截图目录
（默认 `%LocalAppData%\Galbox\Screenshots\`），**文件是真的**。但：

* 触发写入后抛出的 `ScreenshotCaptured` 事件**没有任何订阅者**，
  **没有任何代码把截图记录写进 `Screenshots` 表**；
* 游戏详情页的"截图"区读的正是那张表（`GameDetailViewModel.cs:366-375`），因此它**恒为空**；
* 界面上也**没有手动截图按钮**：`CaptureFocusedWindowScreenshotAsync` 没有调用者。

要看截图，请直接打开那个目录。

### 8.11 `tests/Galbox.Tests` 不能作为验证依据

它**不在 `Galbox.sln` 里**，没有项目引用；`GalboxFunctionalTests.cs` 的
`FullIntegrationTest` 逐步调用 UI 自动化方法后一律 `return true`（该文件 364-410 行），
即只在抛异常时失败、**不做任何断言**。同目录的 `TEST_REPORT.md` 宣称"8/8 通过 100%"，
该结论不可采信。**要判断项目是否可用，请只运行 `tests/Galbox.Acceptance`。**

### 8.12 未验证的部分（明确声明）

* 打包/分发形态：见下一节（待补）。
* Ren'Py 之外的引擎存档解析、Ren'Py 其它版本：未做任何实测。
* 文档中所有"可用"的结论都来自**代码阅读 + 一次本机验收运行**；
  界面交互（点击、折叠、对话框）本身**没有**自动化覆盖，A8 只能证明"门存在"，不能证明"门后一定好用"。

## 九、安装与分发（待补）

> 本节留空：打包与分发方案正在进行中，结论落定后再补写。
> **在此之前请不要引用任何版本号、下载地址或安装步骤**——仓库里现有的旧文档中出现的
> Release 链接与安装说明均为未经实现的描述。

## 十、文档

| 文档 | 内容 |
|---|---|
| [docs/USER-MANUAL.md](docs/USER-MANUAL.md) | 逐步的用户操作说明（每一步都对应真实的 XAML / ViewModel） |
| [docs/DEVELOPER-GUIDE.md](docs/DEVELOPER-GUIDE.md) | 分层架构、依赖注入规则、数据库迁移机制、如何扩展元数据源/验收检查、调试入口 |
| [CHANGELOG.md](CHANGELOG.md) | 本轮开发修了什么、加了什么（按缺陷档案逐条对照代码） |
| [_product/](_product/) | 产品知识包：缺陷档案、调研报告、任务清单（公开） |

> `docs/USER_MANUAL.md`、`docs/DEVELOPER_GUIDE.md`、`docs/CHANGELOG.md`、`docs/README.md`、
> `docs/API_DOCUMENTATION.md` 是早期生成的文档，其中包含未实现功能的描述（例如补丁下载、
> 打包安装步骤）。**以 README 与上面三个文件为准**，其余仅供参考。

## 十一、许可证

本仓库当前**没有附带许可证文件**（仓库根目录下不存在 `LICENSE`）。
在补上之前，请不要假定它采用任何开源许可证。
