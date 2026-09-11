# 更新日志

本仓库**目前没有对外版本号**，因此这里按"开发轮次"记录，而不是语义化版本号。
每条都能对应到代码或公开的缺陷档案（`_product/design/`）。

* 上一轮的缺陷取证与根因分析：`_product/design/defect-postmortems.md`
* 任务清单与完成状态：`_product/design/WORK-BACKLOG.md`
* 本轮末的验收结果见文末「验证」。

---

## 一、阻断级修复

### 1. 双击没反应：启动路径上的 `ConfigureAwait(false)`
应用进程活着、有 40+ 线程、有消息循环，**但永远没有窗口，也不报错**。
根因：`App.xaml.cs` 在 `new MainWindow()` 之前有 7 处 `await … .ConfigureAwait(false)`，
只要其中任意一个真正让出，后续代码就运行在线程池线程上，
`new MainWindow()` 抛 `COMException 0x8001010E (RPC_E_WRONG_THREAD)`，被 `catch` 静默吞掉。

**修复**：全部改为显式 `ConfigureAwait(true)`；启动期异常改为**落盘日志 + 必要时弹模态框**。
**证据**：A/B 对照（刮削缓存为空 → 有窗口；缓存有文件 → 无窗口；改回 `ConfigureAwait(true)` → 有窗口），
现已固化为验收检查 A9。
**位置**：`src/Galbox.App/App.xaml.cs`（`OnLaunched` / `PrepareDatabaseAsync` /
`InitializeDeferredServicesAsync`）、`src/Galbox.App/Services/StartupDiagnostics.cs`。

### 2. 双击没反应（第二因）：三个 P/Invoke 声明在错误的 DLL 上
`SetWindowSubclass` / `RemoveWindowSubclass` / `DefSubclassProc` 被声明在 `user32.dll`，
而这三个符号只由 `comctl32.dll` 导出；构造函数里第一次调用即抛 `EntryPointNotFoundException`，
同样被启动 catch 吞掉。

**修复**：改到 `comctl32.dll`。
**位置**：`src/Galbox.App/MainWindow.xaml.cs:25-32`（注释说明了为什么）。

### 3. 刮削从来没成功过：四重故障叠加
诊断结论是"一个功能从来没成功过，往往不是单个 bug"，四条独立故障同时存在：

| 编号 | 问题 | 修复要点 |
|---|---|---|
| D2 | Bangumi 调的是未公开的旧端点（返回 `{results,list}`），而模型绑定 `{data,total,limit,offset}` → 永远 0 条且 HTTP 200 不报错 | 改用 `api.bgm.tv` v0 端点（`BangumiApi.cs:30`） |
| D3/D4 | VNDB 请求体形状错误（`filters` 传成字符串、`fields` 含裸 `titles`）→ 服务端硬 400 | 修正请求体（`VndbApi.cs:51`） |
| D8 | 无空格的文件名与正确标题相似度只有 84.21%，低于 90% 阈值被丢弃 | 名称归一化匹配（`GameNameMatcher`，`GameScrapingService.cs:771`） |
| D1 | **零 UI 入口**：没有刮削页面、没有导航项，设置里的"添加游戏时自动刮削"没有任何代码读取 | 补 `ScrapingProgressPage` + 导航 key + 菜单项 + 详情页「刮削」按钮，并真正读取该设置 |

其余同轮修复：**D5/D6** Bangumi infobox 键名改为真实键（`BangumiApi.cs:253`）；
**D7** 只缓存成功且无错误的结果（避免一次网络抖动把某个名字锁死 7 天）；
**D9/D10** 阈值、四个源开关、源优先级改为从 `UserSettings` 读取（新增 `IScrapingSettingsProvider`）；
**D11** 低于阈值的候选仍然可手动应用；**D12/D17** 反序列化失败不再被吞成"没有结果"，
界面新增"数据源诊断"栏区分"上游报错"与"确实没这个游戏"；**D13** 进度事件订阅守卫写反导致进度永不更新；
**D15** VNDB 的 `released` 不再硬编码 `null`；**D16** 刮削服务改为 Singleton，使限流与 30 分钟内存缓存真正跨批次生效；
**D18/D19** infobox 键名与源内异常处理。

**本轮实机验证**：验收 A5 返回真实命中（`HasResults=true, BestMatch=Bangumi, total items=6`），
A7 用应用自己的 HTTP 流量确认上游持有该游戏。修复前 A5/A6 是 FAIL。

### 4. 启动器选错且不确定
游戏目录里同时存在 64 位与 32 位 exe 时，旧实现过滤掉 setup/patch 之类后直接 `FirstOrDefault()`，
**依赖操作系统的枚举顺序**。这个缺陷靠读代码几乎发现不了，是验收程序第一次运行时抓到的。

**修复**：改为确定性排序 —— 文件名与目录名归一化精确匹配优先 → 架构后缀降权 →
文件更大优先 → 名称排序兜底（同输入永远同输出）。
**位置**：`src/Galbox.App/Services/GameUtilityService.cs:68-98` 与 `ScoreExecutableCandidate`；
由验收检查 A1 守护。

---

## 二、诚实性修复（"会说谎的功能"）

| 位置 | 曾经的行为 | 现在 |
|---|---|---|
| `PatchCenterViewModel` | 生成 `stub_trans_*` **假补丁并真的写进用户数据库**（用"名字长度偶数"当概率） | 假数据生成整段删除；页面只显示数据库里真实存在的记录，空状态写明"尚未配置补丁源"（`PatchCenterViewModel.cs:11-19, 470-475`） |
| 补丁中心三个按钮 | `Command` 恒为 null（死按钮，点了没反应也不报错） | 死按钮与它们对应的假 `DownloadPatchAsync`/`InstallPatchAsync` 一并删除；「详情」按钮改为通过 `Tag` 转发（DataTemplate 有自己的 namescope，`ElementName=RootGrid` 永远解析不到） |
| 详情页「创建备份」 | 只往数据库写一行，`BackupPath` 是拼出来的假路径，磁盘上什么都没有 | 改为调用 `ISaveManagementService`，写出真实 ZIP；检测不到存档时如实报错（`GameDetailViewModel.cs:689-722`） |
| 详情页「恢复备份」 | 直接显示"存档备份恢复功能尚未实现" | 改为调用真实服务，解压还原并做完整性校验（同文件 748 行起） |
| `PatchCenterPage` | 唯一没有设置 `DataContext` 的页面 → 所有经典 `{Binding}` 解析为 null，补丁列表永久 Collapsed | 补上 `DataContext = ViewModel`（`PatchCenterPage.xaml.cs`，注释标为 W3） |

---

## 三、接线与入口（"已经写好但没有门"）

| 编号 | 事项 | 位置 |
|---|---|---|
| W1 | **DI 生存期重构**：全应用共用一个永不释放的 `DbContext`（"随机红条"的根因）→ 改为 `AddDbContextFactory` + scope 内上下文，并开启 `ValidateScopes` / `ValidateOnBuild` | `App.xaml.cs:75-101, 47-51` |
| W2 | 补 `ErrorReportPage` 与 `ScrapingProgressPage` 两个页面 + 导航 key + 菜单项（此前两个 ViewModel 已在 DI 注册但全工程零 XAML 引用） | `Views/`、`NavigationService.cs`、`MainWindow.xaml` |
| W4 | **启动进程监控**：`ProcessMonitorService.StartAsync()` 与 `RegisterGame()` 此前零调用者，老板键/截图/真实运行检测全是死的 → 现在启动时遍历游戏库注册并启动 | `Services/ProcessMonitorStartup.cs`，调用点在 `App.xaml.cs` 的 `OnLaunched` |
| W5 | 事件订阅守卫写反（`_eventsSubscribed = true` 起始）→ 进度永不更新 | `ScrapingProgressViewModel.cs:28-32, 63-88` |
| W7 | `RemoveWindowSubclass` 的 DLL 错误（与第 2 条同源） | `MainWindow.xaml.cs` |
| — | 设置页引用的转换器全部已注册（此前有一处未注册，是最可能的崩溃点） | 由验收 A8.4 覆盖，当前零失败 |
| — | 添加游戏时"自动刮削"设置真正生效，并跳转到刮削页面让过程可见 | `LibraryViewModel.cs:408-453`、`MainViewModel.cs:307` |

---

## 四、本轮新增能力

### 4.1 数据库迁移机制（取代 `EnsureCreated()` + 手工补列）
* 新增 `GalboxDatabaseInitializer`：四种模式（全新安装 / 老库基线标记 / 升级 / 已最新），
  **能识别由旧 `EnsureCreated()` 建出的库**（有表、无 `__EFMigrationsHistory`），
  在不动任何一行数据的前提下把基线迁移标记为已应用，再应用后续迁移；
* 新增 `BaselineSchema`（在一次性库里真跑一次基线迁移，取它生成的结构作为唯一期望来源）、
  `LegacySchemaRepair`（补齐老库缺失的表/列，模型不认识的历史列原样保留）、
  `TolerantMigrationRunner`（对"对象已存在"的独立操作生成可跳过计划，不安全则显式失败）、
  `SchemaIntegrityChecker`（模型 vs 真实库的一致性报告）；
* 迁移前自动生成 `galbox.db.pre-migration-<时间戳>.bak`（保留最近 5 份）；
* 新增设计时工厂 `GalboxDbContextFactory`：`dotnet ef` 命令默认连到临时目录，
  **永不触碰用户真实库**（可用 `GALBOX_DESIGNTIME_DB` 覆盖）；
* 新增取证工具 `tests/Galbox.Data.Migrations.Harness` 与一键脚本 `tools/verify-db-migration.ps1`：
  覆盖老库升级、全新安装、**幂等性**（迁移两次第二次不改动）、手工改过的库、对象已全部满足等场景，
  并在前后比对源库 SHA256 证明它没被修改。
* 本轮迁移：`20260911145649_InitialBaseline`（基线，已冻结）、
  `20260911145834_AddGameStatusAndSaveNodes`（游戏状态字段 + 存档节点/存档组表）。

### 4.2 Ren'Py 存档解析与存档节点（引擎侧完成，界面未接线）
* `Galbox.Core/Saves`：零执行 pickle 扫描（`PickleScanner`，不反序列化任意对象）、
  `.rpa` 归档读取、`persistent` 读取、剧本 label 索引、存档分析器；
* `Galbox.Data`：`SaveNode` / `SaveGroup` 实体与迁移，`GameInfo` 增加状态字段；
* `Galbox.Services/Saves`：`SaveNodeScanService`（分析 → 映射 → 幂等 upsert）、
  `RenpySaveNodeMapper`（所有产品判断集中在此，纯函数）、`SceneSetClusterer`
  （按已访问场景集合做 Jaccard 单链聚类，是"疑似路线"的唯一依据）；
* 三条硬规则写进了实现：路线名只能带 `疑似路线` 前缀；进度是"已解锁场景数/总场景数"
  且分母未知时保持 NULL；解析失败也要落库并写明原因（不静默跳过）。
* **注意**：`Galbox.App` 目前**不引用** `Galbox.Services`，`ISaveNodeScanService` 也未注册进 DI，
  因此这些能力在当前版本**没有界面入口**。

### 4.3 本机补丁安装器（引擎侧完成，界面未接线）
* `Galbox.Core/Patches`：沙箱解压 → 覆盖预览（新增/覆盖/冲突三级）→ 只备份将被覆盖的文件 →
  安装 + 逐文件哈希台账 → 回滚（`ByteIdenticalToPreInstall`）→ 断电恢复 →
  状态判定（明确区分 `Fact` / `Inference` / `None`，推测永不可能被包装成"已安装"）；
* 安全边界在代码里而非文档里：路径逃逸/绝对路径/UNC/符号链接/保留名/长路径/自解压 exe
  全部有明确处理，`type` 含 `save` 的包硬拒绝；
* 新增取证工具 `tools/Galbox.PatchVerifier`：造假游戏目录与恶劣压缩包，
  逐条打印预览清单、备份树、前后哈希、被拒条目与外部 7z/RAR 样本证据；
* `services.AddGalboxPatches()` 已实现，但**尚无调用者**，UI 尚未接入。

### 4.4 无界面验收程序
`tests/Galbox.Acceptance`：复刻 `App.xaml.cs` 的 DI 容器（只去掉必须依赖
`Microsoft.UI.Xaml.Controls` 的 `INavigationService` 与 ViewModel），驱动真实业务服务，
每项打印"期望值 / 实测值 / 原始证据 / 该检查期间的服务日志"，退出码 0 仅当全部通过。
当前 **A0–A9 共 10 项**，其中 A8 是"功能必须有门"的源码级守卫，A9 是唯一会启动真实 GUI 的检查。
它只使用隔离数据库 `%LocalAppData%\Galbox\acceptance\acceptance.db` 并每次重建，
且显式关闭刮削缓存以保证 A5 是真实网络查询。

### 4.5 启动诊断
`StartupDiagnostics`：每一步追加到 `%LocalAppData%\Galbox\logs\startup-YYYYMMDD.log`；
窗口尚未创建时失败还会弹一个 Win32 模态框写明日志路径。
日志里有两个稳定标记常量供自动化断言：`OnLaunched: startup sequence completed` 与
`EXCEPTION in OnLaunched`。

### 4.6 存档恢复的回滚
`RestoreBackupAsync` 现在会先把当前存档复制到临时目录，校验失败时
`RollbackRestoreAsync` 真的把文件还原回去并返回失败（此前"Attempting rollback"只有日志没有代码）。

---

## 五、验证

本轮结束时的实测（本机 Windows，游戏目录 `D:\GAME\Dreamin'_Her` 存在）：

```
dotnet build Galbox.sln -c Debug            → 成功，0 警告 0 错误
Galbox.Acceptance.exe                       → PASSED 10 / FAILED 0 / ERRORS 0，EXIT CODE 0
```

数据库迁移路径的历史取证记录（公开文件）：
`_bmad-output/progress/db-migration-verification-pristine-20260911.txt`、
`_bmad-output/progress/db-migration-verification-livedb-20260911.txt`。

---

## 六、本轮**没有**处理的事（如实记录）

* **`tests/Galbox.Tests` 仍是零断言的假绿灯**：它不在 `Galbox.sln` 里，
  `FullIntegrationTest` 逐步 `return true`，同目录的 `TEST_REPORT.md` 宣称"8/8 通过"。
  本轮**没有修它**，只是不再把它当验证依据。
* **快速切换的保护不完整**：`QuickSwitchSaveAsync` 里"先备份当前存档"若返回 null，
  代码仍会继续恢复目标备份（`SaveManagementService.cs:842-856` 缺 null 短路）。
* **恢复完整性校验仍带容差**（文件数 20%、大小 10%），因此**不能声称逐字节还原**。
* **部分引擎的存档位置会退化为游戏安装根目录**（KiriKiri / RPG Maker，
  `EngineSaveDetector.cs:452-454, 645-648`），恢复/回滚会在该目录整体操作。
* **删除游戏、安装目录丢失检测、封面图本地下载、窗口标题** 属于其它工作线，
  **尚未合并进本分支**（`src/` 中检索零命中）。
* **截图只有文件、没有界面**：退出时自动截图会把图片写进 `%LocalAppData%\Galbox\Screenshots\`，
  但 `ScreenshotCaptured` 事件没有订阅者，**没有任何代码把截图写进 `Screenshots` 表**，
  因此详情页的"截图"区恒为空；界面也没有手动截图按钮
  （`CaptureFocusedWindowScreenshotAsync` 无调用者）。
* **存档节点界面（时间线 / 快照 / 剧情进度 / CG 解锁率）** 与**补丁中心的安装接线**未完成。
* **流程图追踪、社区成就** 仍未实现（源码中无相关代码，需要自建服务端）。
* 设置页仍有一批"能存不能用"的项（见 `README.md` 的已知限制 §8.8）。
* 安装包与分发形态：见 `README.md` 的「安装与分发（待补）」。
