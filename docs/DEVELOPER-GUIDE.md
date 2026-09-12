# Galbox 开发者指南

面向要改这份代码的人。内容包括：分层与职责、依赖注入规则、**数据库迁移机制**、
如何新增一个元数据源、如何给验收程序加检查、以及出问题时的调试入口。

> 读这份指南时请配合一个习惯：**任何结论都要能找到代码**。
> 本项目的旧文档有过"宣称 8/8 通过、实际测试项目零断言"的记录，
> 所以这里描述每个机制时都会指出它所在的文件。

---

## 1. 先建立验证习惯

改完代码，唯一的自动化判据是：

```powershell
dotnet build Galbox.sln -c Debug
.\tests\Galbox.Acceptance\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.Acceptance.exe
```

退出码 0 = 全部通过。这个程序**会真的联网刮削、真的启动一次 GUI**，
所以它的通过比"编译成功"强得多。

`tests/Galbox.Tests` **不是**验证依据：它不在解决方案里，`FullIntegrationTest` 逐步
`return true`、零断言。不要用它的结论。

---

## 2. 分层与职责

```
Galbox.Core     引擎：解析器（Ren'Py）、外部 API 客户端、本机补丁安装器
                依赖：Microsoft.Extensions.DependencyInjection + System.Text.Json + SharpCompress
                不依赖：EF Core、SQLite、任何 UI

Galbox.Data     持久化：EF Core 实体 + 迁移 + 数据库初始化器
                依赖：Microsoft.EntityFrameworkCore(.Sqlite/.Design)

Galbox.Services 需要"解析器 + 数据库"的应用服务（当前：存档节点扫描）
                依赖：Galbox.Core + Galbox.Data

Galbox.App      WinUI 3 界面：Views / ViewModels / Services / Converters
                依赖：Galbox.Core + Galbox.Data（**目前不引用 Galbox.Services**）
```

依赖方向是单向的：`App → Services → (Core, Data)`，`Core` 永不回头看 `Data`。

**为什么要有 `Galbox.Services`**（而不是把扫描服务塞进 Core 或 Data）：

* 塞进 `Galbox.Core` 就得让它引用 `Galbox.Data`，于是"只解析一个存档文件"的调用方
  （导入、修复、独立工具）也要拖着 EF Core + SQLite 起来；
* 塞进 `Galbox.Data` 会让实体层知道 `RenpySaveSlot` 这类引擎概念，而数据层的立场是
  "它从不自己解析存档"（`SaveNode.ParseStatus` 的注释就是这么写的）；
* 单独一个 csproj 把"编排 + 存储"这条依赖显式化，一眼能看见。

代价是：**`Galbox.App` 需要显式加一个 `ProjectReference` 才能真正用上它**。
这个引用已经加上了（`src/Galbox.App/Galbox.App.csproj`），`ISaveNodeScanService` 也已在
`App.xaml.cs` 注册（第 245-251 行），所以存档节点功能是有界面入口的——存档管理页上的
时间线、CG 图鉴、剧情进度与快照标记就是它（验收项 A10 在真实存档上跑通）。

---

## 3. 依赖注入

所有注册集中在 `src/Galbox.App/App.xaml.cs` 的 `ConfigureServices`。

### 3.1 生存期规则

| 类型 | 生存期 | 理由 |
|---|---|---|
| `IDbContextFactory<GalboxDbContext>` | Singleton（`AddDbContextFactory`） | 工厂本身必须活到进程结束；EF Core 自己管理连接池 |
| `GalboxDbContext`（`AddScoped(sp => factory.CreateDbContext())`） | Scoped | 给那些显式 `using var scope = Services.CreateScope()` 的代码路径用（启动迁移、`BangumiAuthService`、`AutoScrapingService`、`ScrapingSettingsProvider`、后台游玩时长写入） |
| 业务服务（`ISaveManagementService` / `IProcessMonitorService` / `IGameScrapingService` / `IErrorCheckingService` / `IGameUtilityService` / `IScrapingSettingsProvider` / `IAutoScrapingService` / `INavigationService` / `IScrapingCacheService` / `IBangumiAuthService`） | Singleton | 长驻、无状态或自带并发保护；**它们必须通过 `IDbContextFactory` 拿上下文，绝不能持有 `DbContext` 字段** |
| API 客户端（`BangumiApi` / `VndbApi` / `YmgalApi` / `CngalApi`） | Transient | 轻量包装，随 `HttpClient` 走 |
| ViewModel | 除 `SettingsViewModel`（Singleton）外均为 Transient | 页面从**根容器**解析它们（`App.Services.GetRequiredService<XxxViewModel>()`），所以它们同样不能依赖 scoped 服务 |

**这个规则是踩过坑换来的。** 早期用 `AddDbContext<GalboxDbContext>()`（默认 Scoped）却从不建作用域，
整个应用共用一个永不释放的上下文；任何两个重叠操作都会抛
"A second operation was started on this context instance"，在界面上表现为随机红条；
而 `ISaveManagementService`、`IErrorCheckingService` 这类 Singleton **捕获了它**，
于是这个共享上下文永久存在。现在的写法把这条路径彻底掐断。

容器构建时开启了两个开关（`App.xaml.cs:47-51`）：

```csharp
new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }
```

* `ValidateScopes`：从根容器解析 scoped 服务会**直接抛异常**，而不是悄悄给你一个单例；
* `ValidateOnBuild`：注册无法构造时**启动即失败**，而不是等用户点开某个页面才崩。

验收程序的容器（`tests/Galbox.Acceptance/AcceptanceContainer.cs`）用**同样的开关**复刻这套注册，
只删掉两个必须依赖 `Microsoft.UI.Xaml.Controls` 的注册（`INavigationService` 与各 ViewModel），
并把数据库换成每次运行独立的
`%LocalAppData%\Galbox\acceptance\run-<pid>\acceptance.db`（`GALBOX_ACCEPTANCE_DIR` 可改）。

### 3.2 加一个新服务

1. 接口放 `App/Services/IXxxService.cs`（如果要放 Core/Services 层，先想清楚依赖方向）；
2. 实现里需要数据库就注入 `IDbContextFactory<GalboxDbContext>`，每个操作 `using var db = factory.CreateDbContext();`；
3. 在 `ConfigureServices` 注册，选生存期（能 Singleton 就 Singleton，**前提是不持有 DbContext**）；
4. 如果它是一个用户可见的能力，**顺手把页面/导航入口也补上**：验收检查 A8 会把
   "注册进 DI 的 ViewModel 却没有视图引用"判为失败。

### 3.3 UI 线程亲和性

WinUI 的窗口与 UI 对象只能在拥有 dispatcher 的线程上创建。启动路径
（`App.OnLaunched`：准备数据库 → 初始化延迟服务 → `new MainWindow()`）上**所有 `await`
都用 `ConfigureAwait(true)`**，并且是显式写出来的。

这不是洁癖：任何一次 `ConfigureAwait(false)` 只要真的让出，后续代码就跑到线程池上，
`new MainWindow()` 抛 `COMException 0x8001010E (RPC_E_WRONG_THREAD)`；
旧代码的 `catch` 只写 `AddDebug`，于是用户看到的是"进程活着但永远没有窗口"。

---

## 4. 数据库迁移机制

### 4.1 为什么需要一套自己的初始化器

应用早期用 `Database.EnsureCreated()` 建库：**有表、但从来没有 `__EFMigrationsHistory`**。
这类库一旦直接 `Database.Migrate()`，EF 会试图重建已存在的表而失败（或留下半成品结构）。
而这样的库真实存在于用户机器上（`%LocalAppData%\Galbox\galbox.db`），里面有游戏库和设置。

### 4.2 入口：`GalboxDatabaseInitializer.InitializeAsync`

调用点在 `App.PrepareDatabaseAsync()`（启动时、建窗口之前），
传一个 `DatabaseInitializationOptions { Logger = ... }`。它返回
`DatabaseInitializationResult`，里面记录了模式、baseline id、前后已应用迁移、修复过的列、
冲突跳过的操作、schema 一致性报告等，`Describe()` 可打印成一段人类可读的总结
（`GalboxDatabaseInitializer.cs:148-182`）。

**四种模式**（`DatabaseInitializationMode`）：

| 模式 | 触发条件 | 行为 |
|---|---|---|
| `FreshInstall` | 库里**一张表都没有** | 正常跑迁移，从零建库 |
| `LegacyBaselineStamped` | 有表，但迁移历史里没有 baseline | 先确认这是 Galbox 库（缺 `Games`/`UserSettings` 直接拒绝，绝不动陌生 SQLite 文件），再用 `BaselineSchema` 把老库补到 baseline 应有的样子，最后把 baseline **只写进历史表、不执行它的 DDL**，剩下的迁移再正常应用 |
| `Upgraded` | 有历史且缺迁移 | 跑剩余迁移 |
| `AlreadyUpToDate` | 有历史且无待应用 | 什么都不做（第二次启动必然是这个结果） |

关键细节：

* **baseline 的期望结构来自 baseline 迁移本身**（`BaselineSchema.CaptureAsync`：在一个一次性数据库里
  真的执行 baseline 迁移，再读回结构），**不是**来自当前 EF 模型。原因很实际：
  如果这里按当前模型补列，补到的是**还没应用的迁移**要建的列，那个迁移稍后就会
  `duplicate column name` 失败。
* 老库与 baseline 之间的差异由 `LegacySchemaRepair` 补齐（缺的表、缺的列）；模型不认识的历史列
  会被记进 `UnknownColumns` **原样留下**，不删。
* 待应用的迁移如果遇到"目标对象已经存在"（典型是有人手工 `ALTER TABLE` 过），
  `TolerantMigrationRunner` 会先生成执行计划，把这类**独立对象**的操作标记为可跳过；
  若冲突不是可安全跳过的类型，就**显式失败**，绝不半途应用。
* 迁移前会做一次文件副本 `galbox.db.pre-migration-<时间戳>.bak`，
  只保留最近 5 份（`TryCreatePreMigrationBackupAsync`）；复制失败只记警告，不阻断启动。
* 最后 `SchemaIntegrityChecker.CheckAsync` 对比"EF 模型 vs 真实库"，
  不一致时写进日志（不静默，但也**不是致命错误**——真正致命的是带着过期结构继续写数据，
  所以日志里必须看得见）。

### 4.3 新增一个迁移

`Galbox.Data` 里已经有 EF Core 的 Design 包，且有无宿主的**设计时工厂**
`GalboxDbContextFactory`，所以命令行可以直接生成：

```powershell
# 默认连到 %TEMP%\galbox-designtime\galbox.db，绝不会碰用户真实库
dotnet ef migrations add <MigrationName> --project src\Galbox.Data

# 需要针对某个特定库时用环境变量指向一个副本
$env:GALBOX_DESIGNTIME_DB = 'E:\tmp\galbox-copy.db'
```

生成的迁移落在 `src/Galbox.Data/Migrations/`。当前已有两个：

* `20260911145649_InitialBaseline` —— **基线，被冻结**。不要编辑它；
* `20260911145834_AddGameStatusAndSaveNodes` —— 游戏状态字段 + 存档节点/存档组表。

**约束**：

1. **不要修改 baseline 迁移**。它同时承担"老 `EnsureCreated()` 库的结构参照"这个职责，
   改它等于让所有历史判断失效（`BaselineSchema` 的注释写明了这一点）。
2. 不要用 `Database.Migrate()` 取代初始化器，也不要在启动路径里写手工 `ALTER TABLE`
   补列循环——那正是这套机制要取代的东西，两者并存会互相打架。
3. 手改数据库（比如为了救用户数据）会让"迁移想建的对象已存在"，此时 `TolerantMigrationRunner`
   会兜住独立的列/表/索引；但**改之前最好先备份**，并把这个事实写进提交信息。

### 4.4 验证迁移

```powershell
# 默认对 %LocalAppData%\Galbox\galbox.db 的副本做验证（只读源库，前后比对 SHA256）
pwsh -File tools\verify-db-migration.ps1

# 更推荐：指向一份"迁移前的旧库快照"，证明老库能干净升级
pwsh -File tools\verify-db-migration.ps1 -LiveDatabasePath <旧库快照路径> -WorkDirectory E:\tmp\verify-migration
```

这个脚本会：复制源库 → 构建并运行 `tests/Galbox.Data.Migrations.Harness` → 打印全部原始证据 →
再次哈希源库证明它没被改过；全部场景通过才返回 0。

Harness 覆盖的场景（`tests/Galbox.Data.Migrations.Harness/Program.cs`）：
老库升级、全新安装、**幂等性**（同一份库迁两次，第二次必须什么都不改）、
手工改过的库（待应用迁移的对象已存在）、以及迁移对象已全部满足的情况。
它**不在 `Galbox.sln` 里**，是取证工具而不是产品代码。

已有的运行记录（公开，可直接读）：`_bmad-output/progress/db-migration-verification-pristine-20260911.txt`
与 `...-livedb-20260911.txt`。

---

## 5. 如何新增一个元数据源

以"加一个源 `Xxx`"为例，需要动的地方（按依赖顺序）：

1. **枚举**：`src/Galbox.App/Services/IGameScrapingService.cs` 的 `ScraperSource` 加成员。
2. **客户端**：在 `src/Galbox.Core/Api/` 新增 `XxxApi : ApiClient`，以及它在
   `HttpClientWrappers.cs` 里的 typed wrapper（`XxxHttpClient`）。
   请求与反序列化的错误**必须冒泡成错误信息，不能吞成空结果**——`ApiClient` 的注释记录了
   这个教训：一次反序列化失败被吞掉，界面上就变成"这个游戏搜不到"。
3. **DI**：`App.xaml.cs` 里注册 `AddHttpClient<XxxHttpClient>()`（配 `BaseAddress`、`User-Agent`、
   `Timeout`）与 `AddTransient<XxxApi>()`；
   同步更新 `tests/Galbox.Acceptance/AcceptanceContainer.cs`（它逐行复刻这份注册，加上录音 handler）。
4. **接入刮削链路**：`GameScrapingService.SearchFromSourceAsync` 的 `switch` 加分支；
   如需请求限流，照 `_bangumiRateLimitLock` / `_vndbRateLimitLock` 的写法加一个信号量。
5. **设置开关与优先级**：`UserSettings` 加 `EnableXxx` 列（→ 需要迁移），
   `ScrapingSettingsProvider.BuildEnabledSources` / `MapSourceName` / `DefaultPriority` 加成员，
   `SettingsViewModel.Sources` 与设置页的开关列表自然会出现。
6. **名称匹配**：如果该源的标题风格特殊，考虑改 `GameNameMatcher` 而不是在源里特判。
7. **验收**：A5 会打印每个源的结果与错误，A6 校验元数据字段，A7 打印实际 HTTP 流量。
   如果你的源上线后 A5 里那一行是空的，先看 A7 的请求/响应原文，再判断是 Galbox 还是上游。
8. **缓存**：不要为"空结果或部分失败"写缓存（`SearchGameAsync` 只缓存
   `HasResults && Errors.Count == 0` 的结果，理由见注释）。

> 现状提醒：`YmgalApi` / `CngalApi` **现在是四源里两个真实可用的实现**，不再是空壳。
> ymgal 走 OAuth2 `client_credentials`（使用官方文档公开的公共客户端，用户无需申请密钥，
> 可用 `GALBOX_YMGAL_CLIENT_ID` / `GALBOX_YMGAL_CLIENT_SECRET` 换成自己的），
> cngal 的 API 本身无需 key；两个 `HttpClient` 的基础地址集中在
> `Galbox.Core/Api/HttpClientWrappers.cs`（`MetadataHttpClientDefaults`），
> 配置对象在 `MetadataSourceOptions.cs`。A40 / A41 / A42 是它们的验收项。
> 照抄这个模板来加第五个源仍然是最省事的做法——但请注意它们已经从"诚实占位"
> 变成"要限流、要区分未配置/连不上/查无结果"的真实客户端了。

**反面教材**：这个仓库历史上出现过"界面在、逻辑是桩"的源（返回固定失败 + `"integration pending"`）。
新增源时不要那样做——宁可让开关如实报"未配置"，也不要让用户以为在查。

---

## 6. 如何给验收程序加一条检查

1. 在 `tests/Galbox.Acceptance/Checks/` 新建 `A<编号><名字>Check.cs`，实现 `IAcceptanceCheck`：

   ```csharp
   public sealed class A10SomethingCheck : IAcceptanceCheck
   {
       public string Id => "A10";
       public string Title => "一句话说明这项检查在量什么";

       public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
       {
           var expected = "写清楚期望的行为";
           // ... 真的去驱动服务 ...
           return CheckResult.Pass(Id, Title, expected, $"实测值：{measured}")
               .With(details.ToArray());   // 原始证据，逐行打印
       }
   }
   ```

2. 把它加进 `Program.cs` 的 `checks` 数组（顺序即执行顺序）。
3. 三条硬规则：
   * **实测值放 `Actual` / `Details`，不要藏进 `Title`**——写不出"我量到了什么"的检查不是检查；
   * 失败要用 `CheckResult.Fail`，异常交给 runner 记成 `ERROR`（它已经统一处理）；
   * **只许在 `tests/` 下加文件**。检查失败要改的是 `src/`。唯一的例外是"让被测应用配合检查"的
     测试开关——它必须默认关闭、只有显式设置才生效，且不得改变正常启动的任何行为（见 §6.1）。
4. 需要检查**源码结构**（"功能有没有门"这类运行期看不见的问题）时，用
   `RepoLocator.FindRepoRoot()` 往上找 `Galbox.sln`，再读 `src/Galbox.App/Views`、
   `App.xaml.cs`、`NavigationService.cs`——A8 就是这么做的。
5. 需要启动真实 GUI 时参考 A9：它准备了"刮削缓存非空"这个前置状态，
   启动 `Galbox.App.exe`，用 `MainWindowHandle` + `EnumWindows` 双重确认窗口存在，
   失败时附上最新的启动日志。注意它依赖 `src/Galbox.App/bin` 下已构建好的 exe。
   **启动真实 GUI 的检查必须先读 §6.1，窗口必须离屏。**

现有 **43 项**检查，编号是分段的：`A0–A19`（基础与数据安全）、`A30–A32`（补丁中心接线与往返）、
`A40–A42`（ymgal / cngal / 四源状态区分）、`A50`（快速导航存活）、`A60–A67`（moyu 补丁源服务层与合规）、
`A70–A74`（游戏健康诊断与修复）、`A90–A92`（预留接口层）。
每项量什么，见 `tests/Galbox.Acceptance/README.md` 与本仓库 README 的表格；
**权威来源始终是 `tests/Galbox.Acceptance/Program.cs` 里的注册数组**（顺序即执行顺序）。
**新增检查后请更新这两处的清单**，否则清单会像旧文档一样过期。

---

## 6.1 GUI 类检查不得打扰开发者的桌面（硬性规定）

**任何会启动真实 `Galbox.App.exe` 的检查，都必须让窗口落在所有显示器之外。**

这不是洁癖，是真实发生过的事。当前有四项检查会启动真实 GUI：

| 检查 | 会启动真实应用做什么 |
|---|---|
| A9 | 启动真实 GUI，要求进程存活 + 拥有可见顶层窗口 + 启动日志报完成 |
| A18 | 从真实窗口句柄读窗口标题 |
| A19 | 在真实运行的应用里逐个加载全部 8 个导航目的地 |
| A50 | 在真实应用里快速连续切换导航 200 次（40 ms 一次） |

其中 A19 会自己翻 8 页，A50 会自己翻 200 页。开发期间验收会被反复运行，多条工作线并行时更是如此，
而这些检查跑在**开发者正在使用的那台机器**上——于是桌面上就不停地弹窗口、自己翻页。

做法（两侧配合，缺一不可）：

* **应用侧**：`MainWindow.OffscreenWindowVariable`，即环境变量
  `GALBOX_TEST_OFFSCREEN_WINDOW=1`。窗口在被显示之前就移动到 `(-32000, -32000)`。
  该移动发生在 `CalculateInitialWindowSize` 的 DPI / 工作区夹取**之前**，并在 `Activate()`
  之后重新确认一次，所以启动路径上没有任何一步能把它拉回屏幕内。
  开关**默认关闭**，且只有字面量 `1` 才生效：未设置 / 空 / `0` / `false` 时启动行为与改动前完全一致
  （尺寸、位置、标题、导航、进程监控都不变）。
* **验收侧**：`Checks/OffscreenWindow.cs`。
  `OffscreenWindow.Request(startInfo)` 给子进程设上这个变量；
  `OffscreenWindow.Measure(hwnd)` 用 `GetWindowRect` × `EnumDisplayMonitors` **实测**窗口矩形、
  `IsWindowVisible` 与"是否与任何显示器相交"。

**为什么这不削弱断言**：Win32 与窗口位置无关。屏幕外的窗口 `IsWindowVisible` 仍然为 `TRUE`、
`Process.MainWindowHandle` 仍然非零、标题仍然可读、仍然挂在 UI Automation 根节点下，
页面仍然真的加载、导航仍然真的切换（A50 实测仍是 200/200）。四项检查原有的条件一个字都没有改，
只是**各多加了一条**：窗口矩形不得与任何显示器相交（`OffscreenWindow.WouldBeVisibleReason`）。
这条是实测的，不是假设的——万一开关在某台机器上失效，检查会变红，而不是"安静地开始闪窗口"。

变量名由 `OffscreenWindow.Variable` 直接引用应用的常量
（`Galbox.App.MainWindow.OffscreenWindowVariable`），编译器保证两侧不会写歪。

**扩展套件时请照做**：新增任何启动真实应用的检查，先调 `OffscreenWindow.Request(startInfo)`，
再把 `placement.IsVisibleAndOffscreen` 放进通过与失败判定里。做不到就别在检查里启动 GUI。

实测对照（2026-09-12，双屏 3840×2160 @150% + 2560×1440，单位是物理像素）：

| 场景 | `GetWindowRect` | `IsWindowVisible` | `MainWindowHandle` | 与显示器相交 |
|---|---|---|---|---|
| 改动前 `release/1.0.0`（`b4c8086`） | `(342, 342, 2142, 1542)` | True | 非零 | **是** |
| 改动后，开关未设置（正常启动） | `(380, 380, 2180, 1580)` | True | 非零 | **是**（这就是开发者的日常） |
| 改动后，`GALBOX_TEST_OFFSCREEN_WINDOW=1` | `(-32000, -32000, -30200, -30800)` | True | 非零 | 否 |

原始记录（含独立看门狗进程对整个验收过程的采样）：
[`tests/Galbox.Acceptance/evidence/gui-checks-must-not-flash.md`](../tests/Galbox.Acceptance/evidence/gui-checks-must-not-flash.md)。

**残余影响（已知，未处理）**：窗口虽然不在任何显示器上，任务栏里仍可能出现它。
没有顺手去掉是因为唯一的手段——`WS_EX_TOOLWINDOW`（`AppWindow.IsShownInSwitchers = false`
内部用的就是它）——有让 `Process.MainWindowHandle` 归零的风险，而"`MainWindowHandle` 非零"
正是 A9 的核心断言之一。用断言的安全换一次任务栏闪烁，不划算。

**验证这个开关本身**：它失效时检查会红，但想主动确认时可以用独立探针——
`OffscreenWindow.Measure` 打印的那一行就是证据，`GetWindowRect` 必须是负的大坐标、
`IsWindowVisible` 必须是 `True`、`intersects a monitor` 必须是 `False`。三个都要有，
少一个就说明"离屏"被换成了"隐藏"。

---

## 7. 本机补丁安装器

引擎在 `src/Galbox.Core/Patches`，纯本地：不联网、不碰数据库、不依赖 UI。
输入是"用户已下载的补丁包 + 游戏目录"，输出是可序列化对象。

它自己的文档（调用地图、状态判定表、安全边界）在
[`src/Galbox.Core/Patches/README.md`](../src/Galbox.Core/Patches/README.md)，
先读它再读代码。要点：

* **先预览后安装**：`PreviewAsync` 只解压到沙箱并算出每条目的最终落点，
  什么都不写进游戏目录；用户确认冲突后才 `InstallAsync`；
* **只备份会被覆盖的文件**，备份根在 `%LocalAppData%\Galbox\patchbak\<gameId>\<installId>\`，
  且拒绝把备份根放进游戏目录；
* **状态判定区分事实与推测**：`PatchStatusReport` 带 `Evidence`（Fact/Inference/None），
  "推测"永远不可能被包装成 `Installed`；
* 硬拒绝 `type` 含 `save` 的包、拒绝路径逃逸/符号链接/自解压 exe（只提示手动运行）。

验证：

```powershell
dotnet run --project tools\Galbox.PatchVerifier -c Release -- --scratch E:\tmp\_galbox_patch_scratch
```

它会造一个假游戏目录与一批恶意压缩包，逐条打印原始证据（预览清单、备份树、前后哈希、
被拒条目、journal、外部 7z/RAR 样本），并把报告写到 `<scratch>\verify-report.txt`。
退出码 0 表示全部断言通过。

**接线现状**：`services.AddGalboxPatches()` 在 `App.xaml.cs:285-294` 被调用，
`ILocalPatchService` 也已注册，补丁中心页已经把它接到了界面上——
选包 → `OverwritePreview`（覆盖 / 新增 / 冲突 / 未变化 / 被拒绝五类分开列）→ 用户确认冲突 →
`InstallAsync`（带进度、可取消）→ 逐文件结果与 `PatchStatusReport` 的 `Explanation` 原文 →
回滚 → 状态台账 → 中断恢复。验收项 A30–A32 覆盖这条链路。
**唯一还没接上的是在线补丁源（moyu）的界面**：服务层已实现（A60–A67），
但 `PatchCenterViewModel.Patches.cs` 目前仍如实写着"在线补丁源：未实现"。

---

## 8. 调试入口

| 想查什么 | 去哪里 |
|---|---|
| 启动过程每一步 | `%LocalAppData%\Galbox\logs\startup-YYYYMMDD.log`（`StartupDiagnostics`） |
| 启动成功/失败的判定标记 | 常量 `StartupDiagnostics.StartupCompletedMarker`（`OnLaunched: startup sequence completed`）与 `StartupFailureMarker`（`EXCEPTION in OnLaunched`），A9 也是按这两个字符串判定的 |
| 启动失败弹窗 | 窗口还没建起来时会弹一个 Win32 MessageBox，标题 `Galbox 启动失败`，里面写着日志路径 |
| 数据库 | `%LocalAppData%\Galbox\galbox.db`（要改先复制） |
| 验收用的隔离库 | `%LocalAppData%\Galbox\acceptance\run-<pid>\acceptance.db`（每次运行新建并重建；可用 `GALBOX_ACCEPTANCE_DIR` 改到别处） |
| 刮削缓存 | `%LocalAppData%\Galbox\ScrapingCache\search_*.json` |
| 存档备份 | `%LocalAppData%\Galbox\SaveBackups\` |
| 补丁备份与台账 | `%LocalAppData%\Galbox\patchbak\` 与 `<gameRoot>\.galbox\patch-manifest.json` |
| 服务内部日志 | 只在 Debug 输出（`builder.AddDebug()`）；验收程序额外挂了一个收集器，可以 `-v` 全量打印 |

调试启动问题的推荐顺序：

1. 看最新 `startup-*.log` 的最后几行——正常会以 `startup sequence completed` 结束；
2. 没有日志文件 → 进程可能根本没起来，或日志目录不可写；
3. 日志里有 `EXCEPTION in OnLaunched` → 看完整堆栈（`LogException` 会把 inner exception
   与 `AggregateException` 逐层展开）；
4. 怀疑是"窗口没建出来"而不是"程序没跑" → 用 A9 同样的办法查顶层窗口。

---

## 9. 提交代码前请自检

* [ ] `dotnet build Galbox.sln -c Debug` 无警告无错误；
* [ ] 验收程序退出码为 0（或你已经清楚为什么某项失败并写进了提交信息）；
* [ ] 新增的 ViewModel 有对应视图，新增的页面有导航 key 与菜单项（否则 A8 会失败）；
* [ ] 新增的服务在 `App.xaml.cs` **和** `AcceptanceContainer.cs` 都注册了；
* [ ] UI 线程路径上没有 `ConfigureAwait(false)`；
* [ ] **验收期间没有人会看见窗口**：新增或改动了启动真实 GUI 的检查时，它必须走
      `OffscreenWindow`（§6.1），并且报告的 `GetWindowRect` 与所有显示器都不相交；
* [ ] 没有留下"假实现"：不写只 sleep 然后改状态列的按钮，不生成假数据写进用户库，
      绑定不上的 `Command` 宁可把按钮删掉——这类"会说谎的功能"是本项目历史上最严重的缺陷类型
      （见 `_product/design/defect-postmortems.md` 缺陷 #5）；
* [ ] 文档若声称"某某已实现"，你能指出实现文件与它的界面入口。
