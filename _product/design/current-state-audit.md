# Galbox 现状清单（诚实验收审计报告）

**审计对象**：`E:\tmp\Galbox_v2` · 分支 `wip/2026-04-16-fixes`
**审计基线**：审计开始时 HEAD = `7884836`（"补入产品知识包（重构唯一事实来源）"），工作树**干净**。
**审计方式**：**纯只读**。未构建、未运行、未跑测试、未修改任何源码、未提交任何 git 操作。
**代码规模**：`src` 下 60 个 .cs/.xaml 文件，共 **25,413** 物理行。
**行号口径**：任务书给出的行数是"非空行"计数，与磁盘实际不符。本报告**所有行号以物理行为准**（例：`ProcessMonitorService.cs` = 1851 行、`ErrorCheckingService.cs` = 1012、`SaveManagementService.cs` = 1468、`PatchCenterViewModel.cs` = 797、`SettingsPage.xaml` = 958、`UserSettings.cs` = 399）。

> ⚠️ **审计期间工作树发生了变化（非本审计所为）**：审计开始 `git status` 为空；结束时出现 `M Galbox.sln`、未跟踪的 `tests/Galbox.Acceptance/`（16 个文件 + 已构建的 bin/obj，mtime 2026-09-11 22:47–22:53）与 `_product/design/scraping-diagnosis.md`。**有另一个代理正在同一仓库并行工作**。本报告的行号结论基于 `src` 与 `tests/Galbox.Tests` 的已提交内容，不受该并行工作影响；新出现的 `tests/Galbox.Acceptance/` 已在 §2.2 单独说明。

---

## 0. 一句话结论

**这是一个"看起来有 12 个模块、6 个完整页面、25000 行代码"的应用，但真实可用的只有「添加/扫描游戏 → 游戏库列表 → 启动游戏进程」这一条主线。**

三个决定性事实：

1. **可能连窗口都打不开（阻断级）**：`MainWindow.xaml.cs:20-27` 把 `SetWindowSubclass`/`RemoveWindowSubclass`/`DefSubclassProc` 三个 P/Invoke 声明在 **`user32.dll`**，而这三个符号**只由 `comctl32.dll` 导出**（本次已对两个 DLL 的导出名表做二进制扫描核实：user32.dll 中三个都 **NOT FOUND**，comctl32.dll 中三个都 FOUND；同文件的 `RegisterHotKey` 在 user32.dll 中正常 FOUND）。构造函数 `:65 → :88` 会触发 `EntryPointNotFoundException`，被 `App.xaml.cs:224-229` 的 `catch` 静默吞掉（日志只配了 `AddDebug`，用户不可见）→ **进程活着、消息循环活着、但主窗口永远不出现，也没有任何报错**。（唯一例外是 `:82-85` 的 `WindowHandle == IntPtr.Zero` 提前返回——但该分支下 subclass 同样装不上，老板键照样死。"DLL 里没有这个导出"是确定事实；"因此启动失败"是高置信推断，本轮受只读约束未实机验证。Git 证据：这段 subclass 代码是 `cc540e7`（2026-04-14 12:11 "编译通过，准备进行测试"）之后新增的，而唯一一次"应用成功启动"的记录是 `tests/Galbox.Tests/TEST_REPORT.md:28`（2026-04-14 13:06，当时还没有这段代码）→ **含该代码的版本从未被人运行过**。）

2. **入口缺失比逻辑缺失更严重**：`错误报告` 与 `刮削进度` 两个模块**根本没有页面、没有导航项、没有按钮**（`Views/` 只有 6 个页面；`NavigationService.cs:20-28` 只有 6 个 key；`MainWindow.xaml:43-72` 只有 4 个菜单项 + 内置设置项）。两个 ViewModel 在 DI 里注册了（`App.xaml.cs:140-141`），但**全工程没有任何地方构造它们**，全仓 `*.xaml` grep 两者姓名**零命中**。而它们的底层引擎反而是本次审计中最"真"的部分。

3. **图片链路从根上断了**：`CoverImageUrl`/`BackgroundImageUrl` 会被刮削写入，但**全工程没有任何代码把 URL 下载成本地文件**（grep `GetByteArrayAsync|WriteAllBytes|DownloadImage|DownloadFile` 在 `src` 下零命中），而所有 `<Image>` 都绑定 `CoverImagePath`（经 `PathToImageConverter`，要求 `File.Exists`）。→ **首页、游戏库网格、详情页、存档管理、补丁中心的封面/背景图，永远不可能显示。**

> 交叉印证：项目自己的《产品知识总纲》`_product/Galbox-产品知识总纲.md:804` 也写下了同一结论——"v2 最终交付的，实际上退化成了一款'带刮削功能的游戏库 + 存档备份工具'——恰好就是它要打败的 PotatoVN 的水平"。

**一个必须先说的好消息**：底层并不像"占位"那么糟。`EngineSaveDetector`（896 行，6 引擎 + generic 全部是真实文件系统探测）、`SaveManagementService`（真 `ZipFile` 写盘 + 真解压覆盖）、`ErrorCheckingService`（8 项真实文件系统检查）、`AutoScrapingService`（真批处理循环 + 真 HTTP + 真 `SaveChanges`）、`ProcessMonitorService`（1851 行真轮询代码）都是**真实实现**。问题几乎全部出在**接线、入口、UI 绑定、DI 生存期**这四类"最后一公里"上。这意味着：**修好接线能一次性点亮大量功能**。

---

## 1. 功能总表

档位：**可用** = 逻辑完整、接真实数据源/文件系统、能跑通 · **半成品** = 有实现但缺关键环节 · **空壳** = 有界面/有数据模型但无真实逻辑（占位/硬编码/Stub） · **缺失** = 完全没有

### 1.1 游戏库（`LibraryPage` / `LibraryViewModel`）

| 功能 | 档位 | 证据（文件:行号） | 用户实际看到什么 |
|---|---|---|---|
| 添加游戏（单个） | **可用** | `ViewModels\LibraryViewModel.cs:325-410`：真校验目录、真 `FindExecutableInFolder`、真 `CalculateFolderSize`、真 `Games.Add`+`SaveChangesAsync:385` | 选文件夹 → 游戏入列表，提示"已将 'X' 添加到游戏库 (引擎: Krkr)" |
| 扫描文件夹（批量） | **可用（有缺陷）** | `LibraryViewModel.cs:417-525`：真遍历一级子目录 `:435`、真逐目录查 exe、真 `SaveChangesAsync:489` | 提示"扫描完成：添加 N 个游戏，跳过 M 个文件夹"，但界面被 loading 遮罩挡住 **5 秒**（`:511 await Task.Delay(5000)` 后才 `IsLoading=false`） |
| 扫描深度 | **半成品** | `:435 SearchOption.TopDirectoryOnly` + `:442` 只认子目录根部 `*.exe` | **三层以上嵌套的游戏目录永远漏扫**（`D:\GAME\X\X\game.exe`），静默计入"跳过" |
| 搜索 | **可用** | `:188-199` 对 `DisplayName/Developer/NameOriginal/NameCn/TagsJson` 做 `Contains`；`:264 OnSearchQueryChanged` 实时 | 输入即过滤，标题显示"过滤数/总数 个游戏" |
| 状态筛选 | **半成品（语义错误）** | `:222-236`：`Completed => LaunchCount > 0 && TotalPlayTimeSeconds > 3600` → **"已完成"被定义为"玩过且超 1 小时"**；`:634-643 GetGameStatus` 同样 | 玩 61 分钟就显示"已完成"（总纲 `:812/:565` 已自承此退化） |
| 排序（下拉框） | **半成品（真 bug）** | `CommonConverters.cs:440-459` 是裸 `(SortOption)index`，而 `LibraryPage.xaml:292-299` 只有 8 项，`LibraryViewModel.cs:719-733` 枚举索引 5/6/7 = `ReleaseDateAsc/PlayTimeDesc/PlayTimeAsc` → **选"游玩时间"实际按发布日期升序排；选"最后游玩"实际按游玩时间降序；选"评分"实际按游玩时间升序** | 后三个排序项的排序结果与标签完全不符 |
| 排序（表头点击） | **半成品** | `LibraryPage.xaml:420-483` + `LibraryViewModel.cs:649-663` 逻辑正确，但**不回写 ComboBox**；表头可把 `SortOption` 设为索引 8–11，而 ComboBox 只有 8 项 → 越界 | 点表头后排序下拉框变空白 |
| 网格/表格切换 | **可用** | `LibraryPage.xaml:225-246` + `LibraryViewModel.cs:288-293` | 正常切换 |
| **批量操作**（多选/批量刮削/批量删除/批量加标签） | **缺失** | `LibraryPage.xaml:489 SelectionMode="None"`；`LibraryViewModel` 只有 `ToggleView(288)/NavigateToGame(298)/Refresh(314)/QuickLaunchGame(530)` 四个命令 | 无法多选，**没有任何批量按钮** |
| **从库中删除游戏** | **缺失** | 全 `src` grep `Games.Remove|RemoveGame|DeleteGame` → 只命中 `SettingsViewModel.cs:725 RemoveGameDirectory`（删"设置里的扫描目录"） | 一旦加错游戏（或扫进一堆非游戏 exe），**永远无法从库里移除** |
| 快速启动（卡片悬停钮） | **可用** | `LibraryPage.xaml:375-388` → `LibraryPage.xaml.cs:167-180` → `LibraryViewModel.cs:531-629`：真 `Process.Start:562`、真写 `LaunchCount/LastSessionTime:568-577`、后台 `WaitForExitAsync` 累计时长 `:589-603`（用独立 scope 取 DbContext，写法正确） | 悬停出现播放圆钮 → 点下去**游戏真的启动**，玩完自动累计时长 |
| 刷新 | **可用** | `LibraryPage.xaml.cs:91-94` → `LibraryViewModel.cs:314-318` | 重新读库 |
| 封面缩略图 | **缺失** | `LibraryPage.xaml:63-68/334-339/513-517` 全绑 `CoverImagePath`，而全工程**只读不写**它（仅 `GameDetailViewModel.cs:251`、`SaveManagerViewModel.cs:190` 两处读取） | 网格视图每张卡片都是**空白占位块 + 游戏名** |
| 空状态提示 | **可用** | `LibraryPage.xaml:578-601` | 正常 |

### 1.2 首页 / 快速启动（`MainPage` / `MainViewModel`）

| 功能 | 档位 | 证据 | 用户实际看到什么 |
|---|---|---|---|
| **首页"启动"按钮** | **空壳** | `MainPage.xaml:177-187` 绑的是 `{Binding NavigateToGameCommand}` + `CommandParameter=QuickLaunchGame` → 只**跳转详情页**。`MainViewModel.cs:176-186` 的 `QuickLaunch()` **在 XAML 里从未被绑定**（全仓 grep `QuickLaunchCommand` 只命中定义处），而且它也只是 `NavigateToGame` | 点"启动"→ 跳到详情页，**游戏没有启动** |
| 首页能否启动游戏进程？ | **否** | 真 `Process.Start` 只有两处：`LibraryViewModel.cs:562`（库存卡片悬停钮）与 `GameDetailViewModel.cs:424`（详情页启动钮）。**首页两个入口都不含进程启动** | 必须先跳详情页再点一次启动 |
| 正在游玩/收藏/最近游玩 三区块 | **可用** | `MainViewModel.cs:100-141` 真 LINQ（收藏 top5、最近 top10、7 天内 top10）；`MainPage.xaml:206-370` | 有数据时正常；无数据时整块隐藏 |
| 首页"添加游戏" | **半成品（与库页不一致）** | `MainPage.xaml:102-111` → `MainViewModel.cs:229-309`：**第二份独立实现**——用 `Path.GetFileNameWithoutExtension(exe)` 当游戏名（库页用**文件夹名**），且**完全不识别引擎**（无 `EngineSaveDetector` 调用 → `EngineType` 恒 `Unknown`） | 同一游戏从首页加 vs 从库页加，**名字不一样、引擎一个有内容一个是 Unknown** |
| "正在游玩"的真实含义 | **半成品** | `MainViewModel.cs:126-133` 注释 `// games played in last 7 days`；`:130 LastSessionTime >= lastWeek` | 是**"7 天内玩过"**，与进程监控无关（进程监控从未启动） |

### 1.3 游戏详情页（`GameDetailPage` / `GameDetailViewModel`）

| 区块 | 档位 | 数据从哪来 | 用户实际看到什么 |
|---|---|---|---|
| 封面 | **缺失** | `GameDetailPage.xaml:99-107` 绑 `CoverImagePath` ← `GameDetailViewModel.cs:251` ← DB 字段 `GameInfo.cs:77`。**无任何写入方**；刮削只写 `CoverImageUrl`（`AutoScrapingService.cs:515-518`）；**无下载代码** | 空白图框，永远为空 |
| 背景图（模糊铺底） | **缺失** | `GameDetailPage.xaml:49-59` 绑 `BackgroundImagePath` ← `:252`，同样只读不写 | 无模糊背景 |
| 简介 | **空壳** | `:219-228` ← `:250` ← DB；唯一写入方 `AutoScrapingService.cs:510-513`，该服务**不可达** | 整块被 `EmptyStringToVisibilityConverter` 隐藏 |
| 角色 | **空壳** | `:231-269` ← `:313-321` ← `Include(g => g.Characters)`；唯一写入方 `AutoScrapingService.cs:547-564`，不可达 | 永远隐藏。即便有数据，`:560` 只写 `ImageUrl` 不写 `ImagePath`，而 `:247` 绑的是 `ImagePath` → 头像也会空白 |
| 标签 | **空壳** | `:144-162` ← `:288-310` 反序列化 `TagsJson`；唯一写入方 `AutoScrapingService.cs:543`，不可达 | 永远隐藏。即便有标签，点标签也**只写日志**（`GameDetailViewModel.cs:731-742` 只有 `_logger.LogInformation`，注释承认 `// Navigation would be handled by the page or a navigation service`） |
| **截图** | **空壳** | `:344-380` ← `:343-352` ← `Include(g => g.Screenshots)`。**全工程 `new GameScreenshot` / `Screenshots.Add` 零命中**（表定义在 `GalboxDbContext.cs:45`） | 区块永远隐藏（0 行数据） |
| **文档** | **空壳** | `:312-341` ← `:323-331`。**`new GameDocument` / `Documents.Add` 零命中**（表 `GalboxDbContext.cs:35`） | 永远隐藏（"扫描游戏目录里的 txt/readme 并可预览"这个功能从未实现） |
| **媒体文件** | **空壳** | `:383-422` ← `:333-341`。**`new GameMediaFile` / `MediaFiles.Add` 零命中** | 永远隐藏 |
| 游玩统计 | **可用** | `:272-309`；数据来自 `GameInfo.cs:209-240` 计算属性，由启动流程真实累计 | 正常显示 |
| 收藏切换 | **可用** | `:204-214` → `GameDetailViewModel.cs:521-547`：真翻转 + 真 `SaveChangesAsync:534` + 失败回滚 `:544-545` | 正常 |
| 启动游戏 | **可用（UI 锁死全程）** | `GameDetailViewModel.cs:382-499`：真 `Process.Start:424`、真累计时长 `:440-452`。**缺陷**：`await _runningProcess.WaitForExitAsync():440` 在命令内等待 → `IsLaunching` 直到游戏退出才复位，而 `GameDetailPage.xaml:176` 的启动按钮 `IsEnabled` 绑 `IsLaunching` 取反 | 游戏能启动；但启动按钮**直到游戏关闭都保持灰色** |
| "其他方式"多启动下拉 | **空壳** | `GameDetailPage.xaml:186-193` 可见性要求 `Executables.Count > 1`；`GameDetailViewModel.cs:264-285` 只塞 1 个主程序 + 拆 `AlternativeExecutables`，而**该字段全工程只读不写**（grep 仅 `GameInfo.cs:65` 定义 + `:272` 读取） | 下拉按钮永远隐藏 |
| **创建备份** | **空壳（假记录）** | `GameDetailPage.xaml:430-434` → `GameDetailViewModel.cs:652-686`：**只做 `SaveBackups.Add` + `SaveChangesAsync:674`**。`:662` 注释自认 `// This is a stub - actual implementation would need to identify save locations`；`:668 BackupPath = Path.Combine(Game.InstallPath,"Backups",backupName)` 是拼出来的假路径 | 列表凭空多一条"Backup_20260416_120000 / 自动创建的备份"，**磁盘上既无目录也无 zip** |
| **恢复备份** | **空壳（有硬编码文案）** | `GameDetailViewModel.cs:692-716`：`:702 // This is a stub - actual implementation would restore files`；`:706 ErrorMessage = "存档备份恢复功能尚未实现";` | 点"恢复"→ 红条**"存档备份恢复功能尚未实现"**（最容易复现的铁证） |
| 删除备份 | **缺失（死按钮）** | `GameDetailPage.xaml:471-474`：`<Button Grid.Column="2" Content="删除" CornerRadius="4"/>` —— **无 Command、无 Click**；`GameDetailPage.xaml.cs` grep 无任何 Click/Backup 代码 | 点了毫无反应 |
| 文档/截图/媒体 三个打开命令 | **可用（无数据可点）** | `GameDetailViewModel.cs:553-584/590-615/621-646` 都是真 `Process.Start(UseShellExecute=true)` + `File.Exists` | 命令正确，但三个集合恒空 → 永远点不到 |

### 1.4 存档管理（`SaveManagerPage` / `SaveManagerViewModel` / `SaveManagementService`）

| 功能 | 档位 | 证据（文件:行号） | 用户实际看到什么 |
|---|---|---|---|
| 游戏列表 / 搜索 / 刷新 | **可用** | `SaveManagerViewModel.cs:164-218/236-260/686-690`；真 DB 查询（N+1：每游戏 2 次） | 正常；并发时偶发"加载游戏失败：A second operation was started..." |
| 点游戏 → 加载备份列表 | **半成品** | `:274-311` → `Task.Run:288` → `:327 DispatcherQueue.GetForCurrentThread()` **在线程池线程返回 null** → 走 `:354-374` 在**后台线程**改 `ObservableCollection` | 大概率红条"加载备份失败：..."或列表空白（**未运行时验证**） |
| **创建备份** | **可用（真写盘）** | `SaveManagementService.cs:1146 ZipFile.Open(zipPath, ZipArchiveMode.Create)`、`:1166 CreateEntry`、`:1169-1171 CopyToAsync`、`:1064 Directory.CreateDirectory`；DB 行 `:232-244` | 真备份成功；无档/无权限 → "创建备份失败。未检测到存档文件。" |
| **恢复备份** | **半成品（数据丢失风险）** | `:1222-1223 File.Create + CopyToAsync` 真覆盖；但 `:519/:534` 的 `"Attempting rollback after cancellation/error"` **只有日志、没有回滚代码**；普通恢复的临时副本在 `finally :542-556` 被 `Directory.Delete` 删掉且不建 DB 备份；`VerifyRestoreIntegrityAsync:563-606` 只比文件数/总大小且带 20%/10% 容差 → 形同虚设；`restorePath` 失效时 `:457-460` 造"幽灵目录"仍报成功 | 成功→"备份恢复成功：X"；失败→只有"恢复备份失败"（不说是文件丢失/占用/空间/校验失败）；**当前存档被覆盖且无任何可回退副本** |
| **快速切换** | **半成品（致命缺口）** | `:804 CreateBackupAsync` → `:813 RestoreBackupAsync`，**但 `:804-810` 之后没有 `if (currentBackup == null)` 判断** → 当前存档检测失败时仍继续覆盖，`:818-826` 依然 `Success = true` | 报"已快速切换到：X"，**但旧档已被覆盖且无备份可回退**；切换期间进度条恒 0% |
| 删除备份 | **可用** | `:734-742` 真删文件 + 真删记录 | 正常 |
| "自动备份"开关（页内） | **空壳** | `SaveManagerPage.xaml:309-313` → `SaveManagerViewModel.cs:389-393` → `SaveManagementService.cs:48-49` **只写内存字段** `:19 _autoBackupEnabled = true`；`CreateAutoBackupAsync:290-308` **全工程零调用者** | 开关能拨、当次会话保持、重启复位、**对行为零影响** |
| **"检测存档位置"** | **缺失（UI 层）** | 全仓 grep `检测存档\|存档位置\|DetectSave` 在 `Views/*.xaml` **零命中**；`DetectSaveLocationAsync` 只被服务内部 `:156`（备份时）与 `:364`（恢复兜底）调用 | **没有这个按钮**。更糟：`:193 HasSaves = backupCount > 0` + `:801 StatusText => "未检测到存档"` → 对**所有没做过备份**的游戏都显示"未检测到存档"，与磁盘上是否真有存档无关 |
| 引擎探测（底层） | **可用（真逻辑）** | `EngineSaveDetector.cs:88-143` 按引擎分派到 7 个真实探测方法（`:345/415/462/485/546/609/661`），全部真 `Directory.Exists`/`GetFiles`，**无硬编码路径、无按文件夹名返回** | 底层诚实，只是用户点不到 |
| RPG Maker / Krkr 存档定位 | **半成品（危险）** | `EngineSaveDetector.cs:645-648 result.PrimarySavePath = game.InstallPath;`（根目录发现 `*.rvdata2` 时）、`:452-456` 同理 → 把**整个游戏安装目录**当"存档目录"；配合 `RollbackRestoreAsync:621-633` 的"先 `GetFiles(AllDirectories)` 全删再拷回" | 备份会打包整个游戏（数 GB）；**恢复/回滚可能永久损坏游戏安装** |
| 备份存储路径 | **缺失（设置无效）** | `SaveManagementService.cs:36-40` 硬编码 `%LOCALAPPDATA%\Galbox\SaveBackups` → 无视 `UserSettings.DefaultBackupPath`/`UseCustomBackupPath`（无消费方） | 设置页"自定义备份路径"是装饰 |
| 压缩/去重/哈希 | 半成品 | 压缩真（`ZipFile`）；去重只有路径字符串级（`EngineSaveDetector.cs:788`、`SaveManagementService.cs:1126`）；**哈希零命中**（grep `MD5\|SHA1\|SHA256\|ComputeHash` 全仓 0） | "验证备份完整性"名不副实 |

### 1.5 补丁中心（`PatchCenterPage` / `PatchCenterViewModel`）

**根因**：`PatchCenterPage.xaml.cs:23-32` **没有 `DataContext = ViewModel;`**（其他 5 个页面都有：`LibraryPage.xaml.cs:37`、`GameDetailPage.xaml.cs:35`、`MainPage.xaml.cs:34`、`SaveManagerPage.xaml.cs:32`、`SettingsPage.xaml.cs:34`）→ 所有 `{Binding}` 失效 → **承载补丁列表的 `ScrollViewer`（`PatchCenterPage.xaml:389`，`Visibility="{Binding SelectedGame, ...NullToVisibilityConverter}"`）永远 Collapsed**。

| 界面控件 | 档位 | 点击后实际发生什么 |
|---|---|---|
| 刷新按钮 | **可用（刷的是假数据）** | `RefreshCommand` 真实在 x:Bind 集合中（`obj\...\Views\PatchCenterPage.g.cs`）→ 真的重新加载 |
| 搜索框 / 类型下拉 | **半成品** | 真实内存过滤；但结果区被 Collapsed 包住，用户看不到效果 |
| 游戏列表项点击 | **半成品（竞态）** | 真逻辑；但一次点击触发**两条路径**（`PatchCenterPage.xaml.cs:60` 的 UI 线程 + `PatchCenterViewModel.cs:458-474` 的 `Task.Run`），两条都访问**同一个 DbContext** → EF 并发异常风险，且可能各自 `Add` 一批相同 stub 补丁 |
| **补丁列表** | **缺失（不可见）** | 用户**永远看不到任何补丁卡片**，也看不到下面的下载/安装/详情按钮 |
| "查看游戏" | **空壳（静默 no-op）** | `PatchCenterPage.xaml:367-368`：Command 用 `x:Bind`（真实），但 `CommandParameter="{Binding SelectedGame}"` 走 DataContext（null）→ `PatchCenterViewModel.cs:761-765 _logger.LogWarning("No game to navigate to"); return;` |
| **"下载"** | **空壳（死按钮）** | `PatchCenterPage.xaml:462` `{Binding ViewModel.DownloadPatchCommand, ElementName=RootGrid}` —— `RootGrid` 是 `Grid`，**没有 `ViewModel` 属性** → `Button.Command == null` → **点击什么都不发生，不崩溃**。铁证：生成的 `PatchCenterPage.g.cs` 的 x:Bind 集合里**没有** `DownloadPatchCommand`。实现本身也是假的：`PatchCenterViewModel.cs:586 await Task.Delay(200)`、`:597 patch.LocalPath = $"C:\\Galbox\\Patches\\{patch.Name}.zip"; // Stub path` |
| **"安装"** | **空壳（死按钮）** | `PatchCenterPage.xaml:477` 同上（`InstallPatchCommand` 不在 x:Bind 集合）。实现：`:660 // Simulate installation (stub implementation)`，无解压/无复制/连 `InstallPath` 都不读 |
| **"详情"** | **空壳（死按钮）** | `PatchCenterPage.xaml:505` 同类；`PatchCenterPage.xaml.cs:73-77 ShowPatchDetailsFlyout` **零调用者** |
| **补丁数据来源** | **空壳（假数据真入库）** | `PatchCenterViewModel.cs:157-158 await GenerateStubPatchDataAsync();` → `:230-275` → **`:269-274 _dbContext.Patches.Add(patch); await _dbContext.SaveChangesAsync();`** —— 假补丁（"{游戏名} 汉化补丁"、`https://moyu.moe/patches/example`、`ExternalId=stub_trans_*`）**被真的写进用户 SQLite 库**；`:323 if (game.DisplayName.Length % 2 == 0)`（注释却写 "50% chance"）、`:342 if (game.DisplayName.Contains(" "))`（注释写 "30% chance"）；`DownloadUrl` 全仓**只写不读** |
| 启用/禁用、删除/卸载、优先级/排序、导入本地补丁、更新版本 | **缺失** | 代码里完全不存在（无 UI、无 VM 成员、无 DB 字段） |
| 版本/兼容性校验、完整性(hash) | **缺失** | 无版本比较、无适配字段、无 hash 字段、无 `File.Exists` |
| 会崩溃的按钮？ | **0 个** | 3 个是死按钮（Command 为 null），2 个（刷新、查看游戏）都有 null 守卫 |

### 1.6 设置页（`SettingsPage` / `SettingsViewModel`）

详见 **§3 设置项虚实对照表**。摘要：

| 分组 | 档位 | 用户实际感受 |
|---|---|---|
| 刮削（4 源开关 / 来源优先级 / 匹配阈值 / 自动刮削 / 默认来源） | **空壳** | 能改能存，**对刮削零影响**（阈值被 `GameScrapingService.cs:47 const AutoAcceptThreshold = 90.0` 硬编码取代） |
| Bangumi 登录 / API 密钥 | **半成品** | OAuth 弹"将来实现"；API 密钥**明文存库、不校验、不设置账号名**，且 token **永不进入刮削请求** |
| 启动行为 / 退出行为 / 退出时自动备份 | **空壳** | 能改能存；启动游戏时应用既不最小化也不退出；`AutoBackupOnExit` 无消费方 |
| 老板键设置（启用/组合键/最小化到托盘/显示通知） | **半成品** | 组合键显示正常，**全局热键永远不会被注册**（`StartAsync` 无调用者）；托盘/通知连实现都没读 |
| 存档路径（自定义备份路径） | **空壳** | "选择文件夹"能选能存，备份服务不读 |
| 进程监控（高级监控/退出截图/间隔/格式/JPG 质量） | **半成品** | 真的写进 `ProcessMonitorService.Config` 且服务真的会读，**但服务从未启动** |
| 外观（主题/语言/库视图） | **空壳** | 三项都能选能存；**界面主题、语言、库页视图全都不变**（全仓 `RequestedTheme` 零命中） |
| 游戏库（启动时自动扫描 / 默认刮削源 / 游戏目录列表） | **空壳 + 半成品** | 目录 CRUD 完全可用且持久化，**但没有任何功能消费该列表**；"启动时自动扫描"拨了不扫 |
| 关于 / 数据库位置 | 半成品 / **可用** | `SettingsPage.xaml:917` 引用了**未注册的转换器** `SettingsVersionTextConverter`（见 §5 崩溃清单） |

### 1.7 错误报告（`ErrorReportViewModel` + `ErrorCheckingService`）—— 详见独立诊断

| 功能 | 档位 | 证据 | 用户实际看到什么 |
|---|---|---|---|
| **整个模块的 UI** | **缺失** | `Views/` 无 `ErrorReportPage.xaml`；`NavigationService.cs:20-28` 无 key；`MainWindow.xaml:43-72` 无菜单项；全仓 `*.xaml` grep `ErrorReportViewModel` **零命中**；`App.xaml.cs:141` 是唯一站外引用 | **什么都没有** |
| 检测引擎（8 项） | **半成品（真逻辑，不可达）** | `ErrorCheckingService.cs:100 → :114-126` 并行 8 个真检查：中文路径 `:318-341`、locale 需求 `:346-409`、DirectX `:414-483`、K-Lite `:488-588`、Windows 兼容性 `:593-669`、运行时依赖 `:674-761`、权限 `:865-906`（**真往游戏目录写 `.permission_test`**）、杀软 `:911-951` | 看不到。逻辑本身是真的 |
| 未检测的内容 | — | grep `Microsoft.Win32\|Registry\.` **零命中**（全项目无注册表代码）；grep `CultureInfo\|GetSystemDefaultLocaleName` **零命中**（从不读系统 locale） | 所谓"Locale 需求检测"从不读当前系统区域设置 |
| 结果持久化 | **半成品** | `:1002-1008` 真 `Add`+`SaveChangesAsync`；**但无去重** → 重复检测无限堆积；返回对象 `Id` 恒为 0 | 表会膨胀 |
| 结果展示 | **缺失** | 无 ListView/Infobar/Dialog。严重级数据本身真实（`IErrorCheckingService.cs:124-145` 4 级；`MatchesSeverityFilter:512-522`），但**没有任何东西渲染它** | 什么都不显示 |
| **一键修复** | **缺失（零真实动作）** | 三层封锁：① `GameErrorInfo.cs:145/168/192/217/238/263/309/331` 8 个工厂全部 `AutoFixAvailable = false`；② `ErrorReportViewModel.cs:313-317` 拦截 → `:325` 的服务调用**不可达**；③ `ErrorCheckingService.cs:238-245` 再拦，`switch:251-259` 只处理 `PermissionIssue`，唯一实现 `FixPermissionIssueAsync:964-978` 内容是 `// Note: This cannot actually fix permissions programmatically in most cases` + `Success = false`。**没有写文件、没有改注册表、没有改 locale、没有下载组件** | 无 UI。若接上：所有问题只会显示"Auto fix not available" |
| 死字段/死分支 | — | `GameErrorInfo.cs:58 FixAction` 从未赋值；`SolutionType.AutoFix` 从未被产生；`AutoFixResult.UpdatedError` 从未赋值 → `ErrorReportViewModel.cs:330-337` 死分支 | — |
| 标记已解决 | **半成品** | `ErrorCheckingService.cs:224-227` 真 EF 写库；**但对刚检测出的问题必然失败**（`Id = 0`）→ `:218-222` 返回 false | 只有对历史记录条目有效 |
| 严重级排序 bug | **真 bug** | `ErrorCheckingService.cs:307 .OrderByDescending(e => e.Severity)` —— `Severity` 是**字符串** → 字母倒序得 `Minor > Major > Info > Critical`，**Critical 排最后** | — |

### 1.8 刮削（简表；白盒诊断另有专门报告）

| 功能 | 档位 | 证据 | 用户实际看到什么 |
|---|---|---|---|
| **整个模块的 UI** | **缺失** | `Views/` 无 `ScrapingProgressPage.xaml`；`NavigationService` 无 key；全仓 `*.xaml` grep `ScrapingProgressViewModel` **零命中** | **什么都没有** |
| 批量刮削引擎 | **半成品（真引擎，不可达）** | `AutoScrapingService.cs:116` 真 while 循环、`:120` 批 5、`:130 Task.WhenAll`、`:441-447 ApplyMetadataToGame + SaveChangesAsync`。**唯一触发器** `ScrapingProgressViewModel.cs:240/253/259` 所在 VM 从不被实例化；`EnqueueGame:57` **无其他调用者** → **队列永远为空** | 无法触发 |
| 进度报告 | **半成品（服务真、UI 断线）** | 服务侧真：`Stopwatch:94`、真计数器 `:136-160`、ETA `:167-169`、除零守卫 `IAutoScrapingService.cs:127`、真取消 `:93/116/226-233`。**但订阅断了**：`ScrapingProgressViewModel.cs:27 private bool _eventsSubscribed = true;` + `:56-61 if (_eventsSubscribed) { return; }` → `:63-65` 三个 `+=` **永不执行** | 即便接上 UI 也会**永远 0%、结果列表永远为空**。另：`:268 ErrorMessage = "刮削失败：{ex.Message}";` **漏了 `$`**，用户会看到字面量 |
| **Bangumi 数据源** | **半成品（永远 0 条）** | `BangumiApi.cs:34-35` 真实存活 URL（实测 `GET https://api.bgm.tv/search/subject/CLANNAD?type=4&responseGroup=large` → HTTP 200，响应体 `{"results":15,"list":[...]}`），**但 `:85-88` 写的是 `[JsonPropertyName("data")]`** → `Data` 恒 null → `Items` 恒空 → `GameScrapingService.cs:176 Success = false` → `FindBestMatch:731` 跳过该源 | **第一优先级数据源事实上死亡**（修一处键名即可解锁） |
| VNDB | **可用（字段窄）** | `VndbApi.cs:13 https://api.vndb.org/kana`、真 POST `:41-44`、完整 JSON 模型 `:81-223`。缺陷：`:38` 搜索字段串不含 description/developers/tags/characters；`:160-165 GetReleaseDate()` 是 `return null;` 硬编码桩 | 能出结果（若有入口），标签/角色/开发商恒空 |
| Ymgal / Cngal | **缺失（纯桩，无 HTTP）** | `YmgalCngalApi.cs:38-52 await Task.CompletedTask; // No-op for stub implementation` + 硬编码 `Success = false`；`:61-67 return null; // Stub - no implementation yet`；`:101-114/:123-129` 同款；`BaseUrl` 被注释（`:14-15/:77-78`）；HttpClient 无 BaseAddress（`App.xaml.cs:87-101`） | 永远无结果 |
| 刮削缓存 | **可用（搜索半部分）** | `ScrapingCacheService.cs:255-267` 真目录 `%LOCALAPPDATA%\Galbox\ScrapingCache`、`:291-308` 真命中计数、7 天过期 `:232`、启动真加载（`App.xaml.cs:206-210`）、真被使用（`GameScrapingService.cs:84/129`） | 真实生效。**详情缓存** `:312-339/:370-394` **零调用者** → 永不写入；`:397-413 InvalidateGameCache` 忽略 `gameId` 且无调用者 |
| 角色/标签富化路径 | **缺失（死码）** | `GameScrapingService.cs:220-262 GetGameDetailsAsync` **无生产调用者** → 连带 `GetBangumiDetailsAsync(476-525)`、`GetVndbDetailsAsync(527-583)` 全死 | 标签和角色永远为空 |
| 设置页刮削项 | **空壳** | `UserSettings.cs:83/88/231` 只被 `SettingsViewModel.cs:542-543/582` **写入**，全仓**无读取者**；真实阈值硬编码 `GameScrapingService.cs:47`（`AutoScrapingService.cs:30` 还有一份同名**完全未使用**的 const） | 能改能存、零影响 |
| "登录 Bangumi 以获得更好的刮削结果" | **空壳** | `SettingsPage.xaml:290` 这句**无任何实现支撑**：`Bearer` 只加在 `BangumiAuthService` 内部（`:479/606/650`），刮削用的 `BangumiHttpClient` 只配了 `User-Agent`（`App.xaml.cs:71-77`） | 填了也没用 |

### 1.9 老板键 / 截图 / 进程监控 / 游玩时长

| 能力 | 档位 | 证据 | 用户实际看到什么 |
|---|---|---|---|
| **老板键** | **空壳** | 默认 `Alt+Shift+H`（`ProcessState.cs:237/242`）；`RegisterHotKey` 实现正确（`ProcessMonitorService.cs:1182`）；**但 `RegisterBossKey()` 三个入口全被锁死**：`:563`（在 `StartAsync` 内，而 **`StartAsync` 全仓零调用**）、`:222`（`_isRunning` 恒 false）、`:1249`（同）。即使对上，WM_HOTKEY 通道的 subclass 也用错 DLL（见 §0）。隐藏逻辑本身真实（`:1263-1295 HideAllWindows` 真 `ShowWindow(SW_HIDE)`；`:1312-1352` 按原状态恢复），**但 `_processStates` 只由 `RegisterGame`（零调用）与监控循环（从未启动）填充 → 实际隐藏 0 个窗口** | 设置里勾选、选键、显示"Alt+Shift+H"，按下去**毫无反应**；"最小化到托盘""显示通知"连实现都不存在 |
| **截图** | **空壳** | 捕获是真的：`ProcessMonitorService.cs:1599-1604 graphics.CopyFromScreen(...)`（GDI+，非桩）、PNG/JPG 真编码 `:1644-1666`、目录真创建 `:246-263`（`%LOCALAPPDATA%\Galbox\Screenshots`）、真写盘 `:1485` + `:1499 new FileInfo(filePath).Length`。**但：无任何 UI 入口**（无按钮、无截图热键、无托盘菜单）；唯一触发点是退出时自动截图 `:767-782`（默认 false 且依赖从未启动的循环）；**DB 零落库**（全仓搜不到 `new GameScreenshot`/`Screenshots.Add`）；`SettingsPage.xaml` 里**根本没有截图路径输入框** → `SetScreenshotDirectory` 永不执行 | 设置里能选格式/质量/自动截图，但**永远不会有截图产生**；详情页截图画廊永远空白 |
| **进程监控 / 游戏运行检测** | **空壳** | 1851 行真实轮询：`ProcessMonitorService.cs:692-721 MonitoringLoop`、`:703 Task.Delay(_config.MonitoringIntervalMs)`（默认 1000ms）、`:815 Process.GetProcessesByName`、`:1072-1097 EnumWindows`、`:988 WorkingSet64`、`:949 PerformanceCounter`、`:936 GetForegroundWindow`。**但 `StartAsync`（`:549`）全仓零调用**（grep 只命中接口 `IProcessMonitorService.cs:130` 与实现本身）；`RegisterGame` 同样零调用；6 个事件（`:1767-1782`）**零订阅**；服务层**零 DB**（构造只注入 `ILogger`，1851 行无 `SaveChanges`） | 没有任何真实运行态显示；设置页"高级监控/监控间隔"改了**没有任何东西在跑** |
| **游玩时长统计** | **半成品** | 真正在跑的是 ViewModel 旁路：`LibraryViewModel.cs:589 WaitForExitAsync` → `:601 TotalPlayTimeSeconds += ...` → `:602 SaveChangesAsync`（用独立 scope，写法正确）；`GameDetailViewModel.cs:440-450` 同构 | 从 Galbox 内启动的游戏**时长确实会增加**；从桌面/Steam 启动的**永远 0 分钟**；游玩中关掉 Galbox 这段时长**丢失** |
| GPU 监控 | **缺失（但 UI 有承诺）** | `ProcessMonitorService.cs:1006-1008 state.GpuUsagePercent = null;`（注释 `// would need NvAPI`），而 `SettingsPage.xaml:581` 宣称"CPU/内存/**GPU** 跟踪" | 承诺大于实现 |

### 1.10 应用启动 / 导航 / 基础设施

| 功能 | 档位 | 证据 | 用户实际看到什么 |
|---|---|---|---|
| **应用启动** | **阻断级缺陷** | `MainWindow.xaml.cs:20-27`（DllImport 用错 DLL，见 §0）+ `:65 SetupWindowSubclass()` + `App.xaml.cs:213-229`（异常被 catch 后只 `LogError`，日志仅 `AddDebug`） | **可能双击后没有任何窗口，也没有任何报错**（详见 §0 置信度说明） |
| 页面导航 | **可用** | `MainWindow.xaml:34-75`（NavigationView + 4 菜单项 + 内置设置）、`NavigationService.cs:20-28`（6 key）、`:80-129` 真 `Frame.Navigate` | 五个入口都能进（若不考虑 §0） |
| 窗口标题 | **半成品** | `MainWindow.xaml:24-30` 只用 `TextBlock Text="Galbox"` 自绘标题栏，**没有设置 `Window.Title`** | 任务栏/Alt-Tab 显示 "WinUI Desktop"（`TEST_REPORT.md:28` 实测确认；总纲 `:819` 已收录此缺陷） |
| SQLite 数据库 | **可用** | `App.xaml.cs:55-59`（`%LOCALAPPDATA%\Galbox\galbox.db`）、`GalboxDbContext.cs:20-60`（9 张表）、`OnModelCreating:65-268`（索引/关系/默认值齐全） | 数据真实持久化 |
| 数据库迁移策略 | **缺失** | `App.xaml.cs:174 EnsureCreatedAsync()`（非 Migration），只手工补了 `Games.EngineType` 一列（`:176-197`）；全仓**无 Migrations 目录** | 后续实体加列**不会自动迁移**（原文档已列为技术债） |
| **DI 生存期** | **严重缺陷** | `App.xaml.cs:56 AddDbContext<GalboxDbContext>(...)` 默认 **Scoped** + `:39 Services = services.BuildServiceProvider();`（**未开 `ValidateScopes`**）+ 所有调用点从**根容器**解析（`App.xaml.cs:139/142`、`LibraryPage.xaml.cs:34`、`GameDetailPage.xaml.cs:32`、`SaveManagerPage.xaml.cs:29`、`PatchCenterPage.xaml.cs:28`、`SettingsPage.xaml.cs:31`、`MainPage.xaml.cs:31`）→ **全应用共享同一个永不释放的 `GalboxDbContext`**；且 `:112 services.AddSingleton<ISaveManagementService, SaveManagementService>();`（Singleton 吃 Scoped）在开启 `ValidateScopes` 后会直接抛异常 | 表现为**随机红条** "A second operation was started on this context instance before a previous operation completed"；因为所有调用点都有 catch，所以不硬崩，只是功能偶发失败 |
| **自动化测试** | **空壳（假绿灯）** | `tests\Galbox.Tests\Galbox.Tests.csproj` **没有任何 `<ProjectReference>`** → 测不到任何应用代码；`GalboxFunctionalTests.cs:354-413 FullIntegrationTest` 每步只 `_output.WriteLine` 后 `:419 return true`（**零断言**）；`TEST_REPORT.md:14-16` 宣称"总测试数 8 / 通过 8 / 通过率 100%"，但 `:62-84` 明确 Step4 刮削 SKIPPED、Step5 详情页 SKIPPED | 报告的"100% 通过"**不构成任何质量证据**；报告 `:159-161` 还宣称"游戏库管理 完整实现""存档管理 完整实现""补丁中心 完整实现"，与代码事实全面矛盾 |
| 测试工程是否在解决方案里 | **缺失** | `Galbox.sln` **只含 Galbox.Core / Galbox.Data / Galbox.App** → `dotnet build Galbox.sln` **不会编译任何测试** | — |

---

## 2. 九个必答问题的逐条回答

### Q1（★重点）"添加游戏 / 扫描文件夹"这条链路

**逻辑具体写在哪个文件哪一行？**

| 入口 | 实现位置 |
|---|---|
| 库页"添加单个游戏"按钮 | `Views\LibraryPage.xaml:214-222` → `Views\LibraryPage.xaml.cs:99-128`（`FolderPicker` + `InitializeWithWindow`）→ **`ViewModels\LibraryViewModel.cs:325-410 AddGameAsync`** |
| 库页"批量扫描"按钮 | `Views\LibraryPage.xaml:203-211` → `Views\LibraryPage.xaml.cs:133-162` → **`ViewModels\LibraryViewModel.cs:417-525 ScanFolderAsync`** |
| 首页"添加游戏"按钮 | `Views\MainPage.xaml:102-111` → `Views\MainPage.xaml.cs:71-74` + `:79-108` → **`ViewModels\MainViewModel.cs:229-309 AddGameAsync`**（第二份独立实现） |

**它做了什么？**

`AddGameAsync`（`LibraryViewModel.cs:325-410`）：
1. 校验目录存在（`:327`），失败 → `ErrorMessage = "无效的文件夹路径"`；
2. 未传 exe 时调 `_gameUtilityService.FindExecutableInFolder(folderPath)`（`:342`；实现 `Services\GameUtilityService.cs:25-82`：真 `Directory.GetFiles("*.exe")` + 关键词黑名单过滤 setup/install/uninstall/config/launcher/patch/update/crack/keygen）；
3. 找不到 exe → `"文件夹中没有找到可执行文件"`（`:347`）并返回；
4. 真算目录大小 `CalculateFolderSize`（`:352`；`GameUtilityService.cs:87-114` 真 `EnumerateFiles("*", AllDirectories).Sum(Length)`，权限异常吞掉返回 0）；
5. 用**文件夹名**当 `NameOriginal`（`:355-360`），`AddedTime/UpdatedTime = UtcNow`、`SizeBytes`、`IsScraped = false`；
6. **真做引擎识别**：`game.EngineType = EngineSaveDetector.DetectEngineType(game)`（`:370`）；
7. 查重：`FirstOrDefaultAsync(g => g.InstallPath == folderPath)`（`:374-375`），重复 → `"该游戏文件夹已在游戏库中"` 并返回；
8. **真写库**：`_dbContext.Games.Add(game); await _dbContext.SaveChangesAsync();`（`:384-385`）；
9. 写内存集合 `AllGames.Add` + `ApplyFilters()`（`:388-392`）；
10. `SuccessMessage = $"已将 '{game.DisplayName}' 添加到游戏库 (引擎: {game.EngineType})"`（`:394`），`await Task.Delay(3000)` 后清空（`:398-399`）。

`ScanFolderAsync`（`LibraryViewModel.cs:417-525`）：
1. 校验根目录（`:419`）；
2. **真遍历**：`Directory.GetDirectories(rootFolder, "*", SearchOption.TopDirectoryOnly)`（`:435`）——**只下探一层**；
3. 逐子目录：查根部 `*.exe`（`:442`），无则 `skippedCount++`（`:443-448`）；
4. 找最佳 exe（`:451`）；查重（`:459-465`）；算大小（`:468`）+ 引擎识别（`:484`）；
5. **逐个游戏单独 `SaveChangesAsync`**（`:489`，无事务、无批量）；
6. 内层 try/catch 吞掉单目录失败（`:497-501`），只记 Warn + `skippedCount++`；
7. 汇总 `SuccessMessage = $"扫描完成：添加 {addedCount} 个游戏，跳过 {skippedCount} 个文件夹"`（`:507`）+ `await Task.Delay(5000)`（`:511`）+ 返回 `addedCount`。

**是否真的遍历文件系统？** ✅ **是**。真 `Directory.GetDirectories`/`GetFiles`/`DirectoryInfo.EnumerateFiles`，无 mock、无硬编码列表。
**是否真的识别引擎？** ✅ **是**。`Services\EngineSaveDetector.cs:33-80 DetectEngineType` 依次真实探测：Renpy（`:147-187`：`renpy/` 目录、`game/script.rpy`、`*.rpy`）、Krkr（`:189-212`：`*.xp3`、`savedata/`）、Tyrano（`:214-236`：`tyrano/`、`data/scenario`）、VNM（`:238-255`）、Unity（`:257-306`：`UnityPlayer.dll`、`*_Data`、`sharedassets*.assets`）、RPG Maker（`:308-339`：`js/rpg_core.js`、`*.rvdata2`），全部真 `Directory.Exists`/`File.Exists`/glob。
**是否真的写入数据库？** ✅ **是**。真 EF `Add` + `SaveChangesAsync`（`:385`/`:489`）。

**它是否被写在 ViewModel 里（即无法脱离 UI 测试）？** ⚠️ **是，这是本次审计最需要修的结构问题。** 五层障碍：

1. **业务逻辑与 UI 显示状态耦合**：直接写 `ErrorMessage`/`SuccessMessage`（`:329/347/379/394/507`），而这些是绑到 `InfoBar` 的显示状态（`LibraryPage.xaml:132-150`）。
2. **业务逻辑直接操作 UI 集合**：`AllGames.Add`（`:388`）、`FilteredGames.Clear()/Add`（`:183/208-211`）。
3. **依赖 UI 服务**：构造函数注入 `INavigationService`（`:120`），而 `INavigationService`（`Services\INavigationService.cs`）签名里就是 `Microsoft.UI.Xaml.Controls.Frame` / `NavigationView` → 任何依赖它的类都无法在无 UI 宿主里构造。
4. **程序集边界**：`LibraryViewModel` 在 `src\Galbox.App`，其 csproj 是 `net8.0-windows10.0.19041.0` + `<UseWinUI>true</UseWinUI>` + `Microsoft.WindowsAppSDK 1.6.250205002`（`src\Galbox.App\Galbox.App.csproj`）→ 无头程序引用它就得扛起整个 Windows App SDK 与 XAML 运行时。
5. **副作用污染**：`:398 Task.Delay(3000)` / `:511 Task.Delay(5000)` —— 任何调用者（含测试）被迫等 3–5 秒。

**附带发现（同一条链路上的 4 个真问题）**：
- **两条添加路径行为不一致**：首页版（`MainViewModel.cs:229-309`）用 **exe 文件名**当游戏名（`:258`）且**完全不做引擎识别** → `EngineType` 恒 `Unknown`；库页版用**文件夹名**且识别引擎。`grep EngineSaveDetector\.` 全仓只有 `LibraryViewModel.cs:370/484`、`SaveManagementService.cs:86/94/418/576/868/1111/1119` 和仓库根一个**游离的 `test_engine.cs:18/32`**（38 行、**不属于任何 csproj**、永不参与编译——恰好证明"引擎检测只靠手写脚本验证过"）。
- **扫描只下一层**：`:435 TopDirectoryOnly` + `:442` 要求 exe 在子目录根部 → 三层以上嵌套的游戏目录永远漏扫。
- **无 `CancellationToken`**：无法中断一个几万目录的扫描。
- **查重是竞态检查**：`FirstOrDefaultAsync` 后再 Add，无唯一约束兜底。

**如果要把它抽成一个独立的、可被无界面程序调用的服务，需要动哪些地方（方案，不实施）：**

**目标**：`IGameLibraryService.AddGameAsync(folder)` / `ScanFolderAsync(root, ct)` 可在纯控制台/单测中被调用，返回结果对象，不触碰任何 UI 状态。

**A. 落地位置**
- 放进 **`src\Galbox.Core`**（或新建 `src\Galbox.Services`）。选 `Galbox.Core` 的理由：它已是 `<TargetFramework>net8.0-windows</TargetFramework>`、**无 WinUI / 无 WindowsAppSDK 依赖**（`src\Galbox.Core\Galbox.Core.csproj`），`net8.0-windows` 的测试项目可直接引用。
- **必须同时改 `Galbox.Core.csproj`：加 `<ProjectReference Include="..\Galbox.Data\Galbox.Data.csproj" />`** —— 当前 `Galbox.Core` **不引用** `Galbox.Data`，而服务需要 `GameInfo`/`GalboxDbContext`。`Galbox.Data` 不引用 `Galbox.Core`，**无循环依赖风险**。

**B. 必须一起搬走的 3 个 UI-free 依赖（目前错放在 `Galbox.App`）**
1. `Services\IGameUtilityService.cs`（23 行）+ `Services\GameUtilityService.cs`（115 行）：只依赖 `ILogger`，**零 WinUI 引用**，可原样平移。
2. `Services\EngineSaveDetector.cs`（896 行）+ `SaveLocationResult`：静态类，只有 `public static ILogger? Logger { get; set; }`（`:26`），**零 WinUI 引用**，可原样平移（同步更新 `SaveManagementService.cs` 的 `using`）。
3. `Models\GameEngineType` 已在 `Galbox.Data.Entities`，无需搬。

**C. 接口设计（关键是切断 UI 依赖）**
```csharp
// src\Galbox.Core\Services\IGameLibraryService.cs
Task<AddGameResult>  AddGameAsync(string folderPath, string? executablePath = null, CancellationToken ct = default);
Task<ScanFolderResult> ScanFolderAsync(string rootFolder, CancellationToken ct = default);
```
- `AddGameResult` / `ScanFolderResult` 为**纯 POCO 结果对象**：`{ AddGameStatus Status; int? GameId; GameEngineType EngineType; string? ErrorMessage; int AddedCount; int SkippedCount; }`，`AddGameStatus` 枚举 = `Added / InvalidPath / NoExecutableFound / AlreadyExists / Failed`。
- **删掉**：`ErrorMessage`/`SuccessMessage` 赋值（改为返回枚举 + 文本）、`Task.Delay(...)`（消息生命周期交给 UI 层）、`ObservableCollection` 直接写入（返回结果对象，由 VM 投影）。
- **不注入** `INavigationService` 与 `IServiceProvider`：`_serviceProvider` 在 `LibraryViewModel` 里只被 `QuickLaunchGameAsync` 用（`:594-595`，为后台时长累计新建 scope）→ 把启动/计时拆成独立的 `IGameLauncherService`，或改用 UI-free 的 `IServiceScopeFactory` 代替 `IServiceProvider`。
- 注入：`GalboxDbContext` + `IGameUtilityService` + `ILogger<GameLibraryService>`，全部 UI-free。

**D. 需要修改的既有文件（清单）**

| 文件 | 改动 |
|---|---|
| `src\Galbox.Core\Galbox.Core.csproj` | 加 `ProjectReference` → `Galbox.Data` |
| 新增 `src\Galbox.Core\Services\IGameLibraryService.cs`、`GameLibraryService.cs`、`AddGameResult.cs`、`ScanFolderResult.cs` | 服务实现（从 VM 搬运逻辑） |
| `src\Galbox.App\Services\IGameUtilityService.cs` / `GameUtilityService.cs` → 移到 Core | 平移 + 改 namespace |
| `src\Galbox.App\Services\EngineSaveDetector.cs` → 移到 Core | 平移 + 改 namespace |
| `src\Galbox.App\ViewModels\LibraryViewModel.cs:325-410 / 417-525` | 删除实现体，改为调用 `IGameLibraryService` + 把结果投影进 `AllGames/FilteredGames` + 设置消息 |
| `src\Galbox.App\ViewModels\MainViewModel.cs:229-309` | **删除重复实现，改调同一个服务**（顺带修掉"两条路径不一致"） |
| `src\Galbox.App\App.xaml.cs:110-143` | 注册 `services.AddSingleton<IGameLibraryService, GameLibraryService>();` |
| `src\Galbox.App\Services\SaveManagementService.cs:86/94/418/576/868/1111/1119` | 更新 `EngineSaveDetector` 的 namespace（机械改动） |
| 新增 `tests\Galbox.UnitTests\Galbox.UnitTests.csproj` | `net8.0-windows` + xUnit + `ProjectReference` → `Galbox.Core`、`Galbox.Data`；用 SQLite `Data Source=:memory:` 建 DbContext |
| 删除仓库根 `test_engine.cs` | 游离文件，不属于任何项目 |

**E. 顺带要修的接缝**
- `ScanFolderAsync` 加 `CancellationToken`；
- 递归深度与"找到 exe"的判定做成参数/策略（现在硬编码 `TopDirectoryOnly`）；
- 每游戏一次 `SaveChangesAsync`（`:489`）改为批量一次提交 + 事务；
- 给 `InstallPath` 加唯一约束，消除竞态查重（`:459-460`）；
- 消息生命周期从服务里移出（去掉 `Task.Delay`）。

---

### Q2. 首页 / 快速启动：有没有真的能启动游戏进程？

**没有。首页没有任何地方能启动游戏进程。**

- 首页"启动"按钮（`MainPage.xaml:177-187`）绑的是 `{Binding NavigateToGameCommand}` + `CommandParameter="{Binding QuickLaunchGame}"` → `MainViewModel.cs:160-171 NavigateToGame` 的全部内容是 `_navigationService.NavigateTo("GameDetail", game.Id);` —— **只是导航**，没有 `Process.Start`。
- 首页"快速启动卡片"整块（`MainPage.xaml:127-189`）同上。
- `MainViewModel.cs:176-186` 确实有一个名字对得上的 `[RelayCommand] private void QuickLaunch()`，**但它也不启动进程**（`:185` 只是 `NavigateToGame(QuickLaunchGame)`），而且**它在整个 XAML 里从未被绑定**（全仓 grep `QuickLaunchCommand` 只命中定义处）。
- 全工程真正 `Process.Start` 游戏的地方只有两处：
  - `LibraryViewModel.cs:555-562`（库页卡片悬停的圆形播放钮，经 `LibraryPage.xaml.cs:167-180` 可达）；
  - `GameDetailViewModel.cs:410-424`（详情页"启动"按钮，经 `GameDetailPage.xaml:175` 可达）。
  **首页的两个入口都不在其中。**

**用户实际路径**：首页点"启动"（或点快速启动卡片）→ 跳到详情页 → **再点一次详情页的"启动"** → 游戏才启动。或者回游戏库页 → 鼠标悬停卡片 → 点中央播放钮。

---

### Q3. 游戏详情页各区块的数据从哪来？真实数据还是空集合？

| 区块 | 绑定链 | 数据来源 | 真实数据还是空集合？ |
|---|---|---|---|
| 封面 | `GameDetailPage.xaml:99-107` ← `GameDetailViewModel.cs:251` ← `game.CoverImagePath` | DB 字段 `GameInfo.cs:77` | **永远空**。全工程 `CoverImagePath` 只有读取（`GameDetailViewModel.cs:251`、`SaveManagerViewModel.cs:190`），**没有任何写入**。刮削写的是 `CoverImageUrl`（`AutoScrapingService.cs:515-518`），且**没有任何下载 URL→本地文件 的代码**（grep `GetByteArrayAsync\|WriteAllBytes\|DownloadImage\|DownloadFile` 在 `src` 下零命中）。`PathToImageConverter.Convert`（`CommonConverters.cs:209-234`）还要求 `System.IO.File.Exists(path)` 才返回 `Uri` |
| 背景图 | `:49-59` ← `:252` ← `BackgroundImagePath` | 同上 | **永远空**（`BackgroundImageUrl` 同理只写不下载） |
| 简介 | `:219-228` ← `:250` ← `Description` | `AutoScrapingService.cs:510-513` 是唯一写入方 | **空集合**：唯一写入方不可达 → 整块被 `EmptyStringToVisibilityConverter` 隐藏 |
| 角色 | `:231-269` ← `:313-321` ← `Include(g => g.Characters)` | 唯一写入方 `AutoScrapingService.cs:547-564` | **永远空**（DB 里不会有行）。即便有行，`:560` 只写 `ImageUrl` 不写 `ImagePath`，而 `:247` 绑 `ImagePath` → 头像也空白 |
| 标签 | `:144-162` ← `:288-310` 反序列化 `TagsJson` | 唯一写入方 `AutoScrapingService.cs:543` | **永远空** |
| 截图 | `:344-380` ← `:343-352` ← `Include(g => g.Screenshots)` | **`new GameScreenshot` / `Screenshots.Add` 全工程零命中** | **永远空集合**（表 `GalboxDbContext.cs:45` 从未被插入过一行） |
| 文档 | `:312-341` ← `:323-331` ← `Include(g => g.Documents)` | **`new GameDocument` / `Documents.Add` 零命中** | **永远空集合**（"扫描游戏目录 txt/readme 并可预览"从未实现） |
| 媒体文件 | `:383-422` ← `:333-341` ← `Include(g => g.MediaFiles)` | **`new GameMediaFile` / `MediaFiles.Add` 零命中** | **永远空集合** |
| 存档备份 | `:424-486` ← `:354-362` ← `Include(g => g.SaveBackups)` | `GameDetailViewModel.cs:664-673`（**只写 DB 行、不复制文件**）+ `SaveManagementService.cs:232-243`（真文件） | 从详情页创建的备份是**假记录**；从存档管理页创建的才是真的 |
| 游玩统计 | `:272-309` ← `FormattedPlayTime/LaunchCount/LastSessionTime/FormattedSize` | 启动流程真实累计（`LibraryViewModel.cs:568-603`、`GameDetailViewModel.cs:433-447`） | **真实数据** ✅ |

**结论**：详情页 10 个区块里，**截图 / 文档 / 媒体文件 三个是"表建了、UI 画了、永远没有数据"的死区块**；封面与背景图因为缺"下载"这一环，**即使刮削成功也永远显示不出来**；简介 / 角色 / 标签依赖一条不可达的刮削路径。

---

### Q4. 存档管理：备份/恢复/快速切换是否真操作文件？"检测存档位置"是否真调引擎探测？

| 操作 | 真的操作文件？ | 真操作数据库？ | 结论 |
|---|---|---|---|
| 创建备份 | ✅ **真写 zip**：`SaveManagementService.cs:1146 ZipFile.Open(zipPath, ZipArchiveMode.Create)`、`:1166 CreateEntry`、`:1169-1171 fileStream.CopyToAsync(entryStream)`、`:1064 Directory.CreateDirectory` | ✅ `:232-244` | **可用** |
| 恢复 | ✅ **真解压覆盖**：`:1222-1223 File.Create(entryPath)` + `entryStream.CopyToAsync(fileStream)` | ✅ 读记录 | **半成品**：覆盖前确实把当前文件拷进临时目录（`:415-447`），但 **`finally :542-556` 把它 `Directory.Delete` 掉**且不写 `GameSaveBackup` 记录 → **普通"恢复"没有任何可回退副本**；`:519/:534` 的 `"Attempting rollback after cancellation/error"` **只有日志、没有回滚代码** |
| 快速切换 | ✅ 真的先备份再恢复（`:804 CreateBackupAsync` → `:813 RestoreBackupAsync`） | ✅ | **半成品（致命缺口）**：`:804-810` 之后**没有 `if (currentBackup == null)` 判断** → 当前存档检测失败时仍继续覆盖，`:818-826` 依然 `Success = true` → **旧档静默丢失且无备份** |
| 删除备份 | ✅ 真删文件 + 真删记录（`:734-742`） | ✅ | **可用** |
| 详情页"创建备份" | ❌ **完全不碰文件** | ✅ 只写 DB 行（`GameDetailViewModel.cs:673-674`） | **空壳**（`BackupPath` 指向从未创建的目录 `:668`） |
| 详情页"恢复备份" | ❌ | ❌ | **空壳**，`:706 ErrorMessage = "存档备份恢复功能尚未实现";` |
| 详情页"删除" | ❌ | ❌ | **缺失**（`GameDetailPage.xaml:471-474` 无 Command/无 Click） |
| 自动备份 | ❌ `CreateAutoBackupAsync`（`:290-308`）**全工程零调用者**；页内开关只写内存字段 `:19`；设置页 `AutoBackupOnExit`（`UserSettings.cs:113`）**无消费方**；全仓 grep `FileSystemWatcher` **零命中**；无计时器；无 `ProcessMonitor` 集成 | — | **空壳（整条链路死代码）** |
| **"检测存档位置"** | **底层真的调用引擎探测，但 UI 层没有这个功能** | — | **缺失（UI 层）**。`DetectSaveLocationAsync` 只被服务内部 `:156`（备份时）与 `:364`（恢复兜底）调用；`Views/*.xaml` grep `检测存档\|存档位置` **零命中**。底层 `EngineSaveDetector.DetectSaveLocation`（`:88-143`）真的按引擎分派到 7 个真实文件系统探测方法。**更糟的是误导**：页面用 `HasSaves = backupCount > 0`（`SaveManagerViewModel.cs:193`）→ `:801 "未检测到存档"`，它只表示"没做过备份" |
| 压缩 / 去重 / 哈希 | 压缩真（`ZipFile`）；去重只有路径字符串级（`EngineSaveDetector.cs:788`、`SaveManagementService.cs:1126`）；**哈希零命中**（`MD5\|SHA1\|SHA256\|ComputeHash` 全仓 0）→ "验证备份完整性"实际只比文件数/总大小且带 20%/10% 容差（`:563-606`） | — | — |
| 备份存储路径 | **硬编码** `%LOCALAPPDATA%\Galbox\SaveBackups`（`:36-40`），**无视** `UserSettings.DefaultBackupPath`/`UseCustomBackupPath` | — | 设置页"备份位置"是装饰 |

**引擎支持矩阵（`EngineSaveDetector.cs`）**

| 引擎 | 类型判定 | 存档定位 | 占位/缺陷 |
|---|---|---|---|
| Renpy | `:147-187` 真（`renpy/`、`game/script.rpy`、`*.rpy`） | `:345-413` 扫 `%APPDATA%`，`:367-368` 用**安装文件夹名**匹配 → 重命名/打包目录几乎必然失败 | `RenpySavePatterns` 含 `*.rpy/*.py`（`:15`）疑似误收集 |
| Krkr | `:189-212` 真（`*.xp3`、`savedata/`） | `:415-460` install/savedata + `%APPDATA%` 目录名 `Contains(game.Developer)`（`:433-434`，Developer 空则失效） | `:192 executable.EndsWith(".xp3")` 对 exe 无意义（噪音） |
| Tyrano | `:214-236` 真 | `:462-483` data/save + tyrano/save | 无 |
| VNM | `:238-255` 真但弱 | `:485-544` 解析 project.json `SavePath` | `:888-893 VnmProjectData.SavePath` **非 VNM 真实 schema 字段 → 恒 null 的走过场** |
| Unity | `:257-306` 真 | `:546-607` 扫 LocalLow | `:604 result.ExtendedInfo["HasRegistryPrefs"] = true;` **硬编码假数据**；`:552-553` 注释声称"Registry (PlayerPrefs) - handled separately"是**假声明**（全项目无注册表代码） |
| RPG Maker | `:308-339` 真 | `:609-659` www/save、save/ | `:645-648 PrimarySavePath = game.InstallPath` → **把整个游戏目录当存档目录**（严重） |
| Generic | — | `:661-689` 猜目录名 `{save,saves,savedata,...}` + 根目录 `*.sav/*.save/*.dat/*.bak` | 只填 `SaveFiles` 不填 `PrimarySavePath` 时被 `SaveManagementService.cs:158` 判死 → 检测到了也报"未检测到存档文件" |

---

### Q5. 补丁中心：从界面能点到的每一个按钮，点下去会发生什么？

**根因**：`PatchCenterPage.xaml.cs:23-32` **没有 `DataContext = ViewModel;`**（其他 5 个页面都有）→ 所有 `{Binding}` 失效 → 补丁列表容器（`PatchCenterPage.xaml:389`）**永久 Collapsed**。

| 界面控件 | XAML 位置 | 点下去发生什么 |
|---|---|---|
| 刷新 | x:Bind `RefreshCommand`（已编译进 `PatchCenterPage.g.cs`） | ✅ 真逻辑：重新加载（但加载出来的是 stub 假数据） |
| 搜索框 | `:198-203` x:Bind `SearchQuery` | ✅ 真内存过滤 |
| 游戏列表项 | `:206-252` + `PatchCenterPage.xaml.cs:53-68` | ✅ 真逻辑；**但同时触发 `PatchCenterViewModel.cs:444-482` 的 `Task.Run` 路径，两条路径共用同一 DbContext** → EF 并发异常风险 |
| 类型下拉 | `:303-314` x:Bind | ✅ 真过滤（作用于用户看不见的列表） |
| **查看游戏** | `:364-370` | ❌ 静默无反应：`CommandParameter="{Binding SelectedGame}"` 走 null DataContext → `PatchCenterViewModel.cs:761-765` 打一条 warning 就 return |
| **下载** | `:461-473` | ❌ **死按钮**：`{Binding ViewModel.DownloadPatchCommand, ElementName=RootGrid}` —— RootGrid 无 `ViewModel` 属性 + DataContext 为 null → `Command == null` → **点击零反应、不崩溃**（生成的 `PatchCenterPage.g.cs` 无该命令，是铁证）。实现也是假的：`:586 Task.Delay(200)`、`:597` 硬编码 `C:\Galbox\Patches\*.zip` |
| **安装** | `:476-489` | ❌ **死按钮**（`InstallPatchCommand` 不在 x:Bind 集合）。实现：`:660 // Simulate installation`，无解压/无复制/不读 `InstallPath` |
| **详情** | `:504-516` | ❌ **死按钮**；`PatchCenterPage.xaml.cs:73-77 ShowPatchDetailsFlyout` **零调用者** |
| 进度条 | `:449-455` | ❌ 永远 Collapsed（`Visibility` 用 `PatchStatusToVisibilityConverter` 但**未传 `ConverterParameter`** → `CommonConverters.cs:536` 返回 Collapsed） |
| **会崩溃的按钮？** | — | **当前 0 个**。3 个死按钮 + 2 个有 null 守卫。**假补丁（`stub_trans_*`/`stub_fix_*`/`stub_adult_*`）会被真的写进用户数据库**（`:269-274`） |

---

### Q6. 设置页：有哪些设置项？每一项是否真的被业务代码读取使用？

**完整逐项表见 §3。** 结论要点：

- `UserSettings` 共 **37 个属性**（`UserSettings.cs:15-246`）。
- 整个 `src` 树里 `UserSettings` 只有**两个消费方**：`SettingsViewModel*` 与 `BangumiAuthService`。
- **真实被业务代码消费的只有 3 个**：`BangumiAccessToken`（`BangumiAuthService.cs:695-699` 启动加载 + `:479` 带 Bearer 调 `/v0/me` 校验）、`BangumiUserId`（`:698`），以及**一条无 UI 入口的半通链路** `ScreenshotPath`（`SettingsViewModel.cs:624-627 SetScreenshotDirectory` → `ProcessMonitorService.cs:234`，但设置页**没有任何截图路径输入框**）。
- 进程监控组 5 个字段（`EnableAdvancedMonitoring`/`AutoScreenshotOnExit`/`MonitoringIntervalMs`/`ScreenshotFormat`/`JpgQuality`）**确实被服务读取**（`ProcessMonitorService.cs:572/767/703/1480/1485`），**但服务从未启动** → 等效无效。
- 老板键组：只有 `EnableBossKey` 名义上被服务消费（`:222/563/1249`），但三处都要求 `_isRunning == true`；`MinimizeToTrayOnBossKey`（`ProcessState.cs:277`）与 `ShowBossKeyNotification`（`:282`）**连实现都不读**。
- **26 个设置是"能改、能存、对行为零影响"的装饰品**，其中 `Theme`/`Language` 全仓无任何应用代码（grep `RequestedTheme` 在 `Galbox.App` **零命中**）。
- **启动时不加载也不应用任何设置**：`App.xaml.cs:165-231 OnLaunched` 只做 `EnsureCreatedAsync` + 两个 `InitializeAsync` + 建窗口，无任何 `UserSettings` 读取。设置只在用户**导航到设置页**时加载（`SettingsPage.xaml.cs:49` → `SettingsViewModel.LoadSettingsAsync:257`）。
- **生效时机**：只有点"保存设置"时应用的 `ApplyRuntimeSettings`（`:604-630`）那 10 个 config 字段 + `ScreenshotPath`；**"重启后生效"的项 = 0 个**（因为重启也不应用）。
- **永不回读的 4 个字段**：`BangumiRefreshToken`(`:49`)、`BangumiTokenExpiresAt`(`:54`)、`BangumiUsername`(`:66`)、`BangumiAuthMethod`(`:71`) 由 `BangumiAuthService.cs:734-741` 写入，而 `LoadStoredCredentialsAsync:686-717` **只回读 `BangumiAccessToken` + `BangumiUserId`** → 这 4 个字段写后即死（`RefreshTokenAsync` 永远走 `:508-513` "No refresh token" 分支）。
- **绑定方向无坑**：用户可编辑控件**全部是 `Mode=TwoWay`**，没有"改了进不到 VM"的 OneWay 陷阱。

---

### Q7. 错误报告：检查结果怎么展示？有没有"一键修复"的真实实现？

- **检查结果怎么展示？→ 什么都不展示（缺失）。** 没有 `ErrorReportPage.xaml`、没有导航项、没有对话框、没有 ListView。`App.xaml.cs:141 services.AddTransient<ErrorReportViewModel>();` 是 `ErrorReportViewModel.cs` 之外**唯一**的引用；全仓 `*.xaml` grep `ErrorReportViewModel` **零命中**。各页面上的 `InfoBar`（如 `MainPage.xaml:61-66`）只绑各自的 `ErrorMessage`，**永远不会显示检测出的问题**。严重级数据本身是真的（`IErrorCheckingService.cs:124-145` 4 级 + `MatchesSeverityFilter:512-522` 筛选逻辑），**但没有任何东西渲染它**。
- **有没有"一键修复"？→ 没有，零真实动作。** 三层封锁：
  ① `GameErrorInfo.cs` 8 个工厂全部 `AutoFixAvailable = false`（`:145/168/192/217/238/263/309/331`）；
  ② `ErrorReportViewModel.cs:313-317` 直接拦截并显示 `"Auto fix not available for this error. Follow manual instructions."` → `:325` 的服务调用**不可达**；
  ③ 即便到达 `ErrorCheckingService.cs:238-245` 同样拦截，`switch:251-259` 只处理 `PermissionIssue`，而唯一的"修复实现" `FixPermissionIssueAsync:964-978` 内容是 `// Note: This cannot actually fix permissions programmatically in most cases` / `// We can provide guidance only` + `Success = false`。
  **没有写文件、没有改注册表、没有改 locale、没有下载组件、也没有假成功。** 唯一能改变持久化状态的是**用户手动**"标记已解决"（`MarkResolvedAsync:224-227` 真写库），且对刚检测出的条目因 `Id = 0` **必然失败**（`:218-222` 返回 false）。
- **检测引擎本身是真的**（8 项真实文件系统检查），只是**不可达**，且**有副作用**：`ErrorCheckingService.cs:886-890` 会往用户游戏目录写 `.permission_test` 做权限探测。
- **没有检查的东西**：注册表（全项目零注册表代码）、真实系统 locale/代码页（零 `CultureInfo` 使用）、进程状态、缺 crack/缺补丁（只看 exe 文件名关键词）、DLL 可加载性。
- **真 bug**：`:307 .OrderByDescending(e => e.Severity)` 对**字符串**排序 → `Minor > Major > Info > Critical`，**Critical 排最后**。

### Q8. 刮削进度页：批量刮削是否真实可用？

**否 —— 从用户角度完全不可用；底层引擎却是真的。**（本模块已有独立白盒诊断：`_product/design/scraping-diagnosis.md`，此处只给档位与关键证据）

1. **没有任何页面/入口**：无 `ScrapingProgressPage.xaml`，`NavigationService` 无 key，`App.xaml.cs:140` 是唯一站外引用。
2. **即便有入口，进度也永远不动**：`ScrapingProgressViewModel.cs:27 private bool _eventsSubscribed = true;` + `:56-61 if (_eventsSubscribed) { return; }` → `:63-65` 三个事件订阅**永不执行**。另有真笔误 `:268 ErrorMessage = "刮削失败：{ex.Message}";` **漏了 `$`**。
3. **引擎是真的**：真批处理循环 `AutoScrapingService.cs:116/120/130`、真 HTTP（`BangumiApi.cs:34-35`、`VndbApi.cs:41-44`、`ApiClient.cs:59/92`）、真 JSON（`ApiClient.cs:69/102`）、真落库（`:441-447`）、真计数器与 ETA（`:94/136-169`）、真取消（`:93/116/226-233`）—— **不是假定时器**。
4. **但数据源大面积残废**：**Bangumi 永远 0 条**（`BangumiApi.cs:85` 用 `[JsonPropertyName("data")]`，实测端点返回 `list`）；**Ymgal/Cngal 是纯桩且完全不发 HTTP**；VNDB 真实但搜索字段窄，能写标签/角色的详情路径 `GameScrapingService.cs:220-262` **无生产调用者**。
5. **0 个游戏是安全的**：队列空时循环体不执行（`:116`），`ProgressPercentage` 有除零守卫（`IAutoScrapingService.cs:127`），`avgTimePerGame` 除法在 `completedCount++` 之后（`:167`）→ **不挂起、不除零**。
6. **缓存是真的**（搜索部分）：`%LOCALAPPDATA%\Galbox\ScrapingCache`、7 天过期、真命中计数、启动真加载、真被读写；详情缓存零调用者。

### Q9. 所有硬编码假数据、Stub、TODO、NotImplementedException、永远返回空的实现、被注释掉的关键逻辑

**见 §4 完整清单（按文件分组，90+ 条，每条给 `文件:行号` + 一句话说明）。**

**先给三条"避免误报"的说明**：
1. `Converters\CommonConverters.cs` 里 **20 处 `NotImplementedException`（`:103/119/139/159/180/201/238/274/302/322/346/371/388/409/477/541/579/755/777/797`）全部位于 `ConvertBack` 方法内** —— 这是单向 `IValueConverter` 的**惯用写法，不是桩**。且没有任何 `TwoWay` 绑定依赖它们的 `ConvertBack`。
2. `ErrorCheckingService.cs:2 using System.Diagnostics;` **是被使用的**（`:647 FileVersionInfo.GetVersionInfo`），不要误判为未使用。
3. `SaveManagerViewModel.cs:82 [NotifyPropertyChangedFor(nameof(SelectedBackup))]` 挂在 `_selectedGame` 上是复制粘贴残留，**无害**。

---

## 3. 设置项虚实对照表（完整 37 项）

判定口径：命中 `SettingsViewModel*` / `UserSettings.cs` / `GalboxDbContext.cs`（映射）/ `SettingsPage.xaml(.cs)` / `CommonConverters.cs`（仅做 SelectedIndex 转换）= **仅"存了字段"**；只有出现在**业务服务/业务 VM** 里的才算"真的被用"。

| # | 设置属性 | 类型/默认 | 声明 file:line | 真实消费方？ | 消费方 file:line（或"仅存储"） | 档位 |
|---|---|---|---|---|---|---|
| 1 | `Id` | int / 1 | `UserSettings.cs:15` | 主键 | `GalboxDbContext.cs:224,266` | — |
| 2 | `EnableBangumi` | bool / true | `:22` | ❌ | 仅 `SettingsViewModel.cs:297,536`、`.Sources.cs:96`、`DbContext:249` | 空壳 |
| 3 | `EnableVndb` | bool / true | `:27` | ❌ | 仅 `SettingsViewModel.cs:298,537`、`.Sources.cs:97`、`DbContext:250` | 空壳 |
| 4 | `EnableYmgal` | bool / true | `:32` | ❌ | 仅 `SettingsViewModel.cs:299,538`、`.Sources.cs:98`、`DbContext:251` | 空壳 |
| 5 | `EnableCngal` | bool / true | `:37` | ❌ | 仅 `SettingsViewModel.cs:300,539`、`.Sources.cs:99`、`DbContext:252` | 空壳 |
| 6 | `BangumiAccessToken` | string?/null | `:43` | ✅ 真（但只用于自校验） | `BangumiAuthService.cs:695-699` 加载 + `:479` 发 `Authorization: Bearer` 调 `/v0/me` | 半成品 |
| 7 | `BangumiRefreshToken` | string?/null | `:49` | ❌ **只写不读** | 仅写 `BangumiAuthService.cs:735`；`LoadStoredCredentialsAsync:686-717` 不回读 → `RefreshTokenAsync` 永远走 `:508-513` 失败分支 | 半成品 |
| 8 | `BangumiTokenExpiresAt` | DateTime?/null | `:54` | ❌ **只写不读** | 仅写 `:736` | 半成品 |
| 9 | `BangumiUserId` | string?/null | `:60` | ✅ 真 | 读 `BangumiAuthService.cs:698` → `_userId`；UI 显示 `SettingsPage.xaml:298` | 半成品 |
| 10 | `BangumiUsername` | string?/null | `:66` | ❌ **只写不读** | 仅写 `:738`，全仓无读取 | 空壳 |
| 11 | `BangumiAuthMethod` | enum/ApiKey | `:71` | ❌ **只写不读** | 仅写 `:739-741`、`DbContext:238` | 空壳 |
| 12 | `SourcePriorityJson` | string? | `:77` | ❌ | 仅 `SettingsViewModel.cs:307,546` + `.Sources.cs:29-84`；真实优先级**硬编码**（`GameScrapingService.cs:11,122` 注释 + `FindBestMatch:298` 只按 MatchScore 取最高） | 空壳 |
| 13 | `MatchThresholdPercent` | int / 90 | `:83` | ❌ **被常量取代** | 真值 = `GameScrapingService.cs:47 const double AutoAcceptThreshold = 90.0`（用于 `:306`）；`AutoScrapingService.cs:30` 另有一份**同名完全未使用**的 const | 空壳 |
| 14 | `AutoScrapeOnAdd` | bool / true | `:88` | ❌ | 仅 `SettingsViewModel.cs:304,543`；`EnqueueGame` 唯一调用点 `ScrapingProgressViewModel.cs:253`，该命令**无任何 XAML 绑定** | 空壳 |
| 15 | `OnLaunchBehavior` | enum/Minimize | `:98` | ❌ | 仅 `SettingsViewModel.cs:310,550` + `.Display.cs:71-84`（只改提示文案）；`GameDetailViewModel.LaunchGameAsync:382-461` 直接 `Process.Start`，**不读设置** | 空壳 |
| 16 | `OnExitBehavior` | enum/MaximizeToDesktop | `:108` | ❌ | 仅 `SettingsViewModel.cs:314,553` | 空壳 |
| 17 | `AutoBackupOnExit` | bool / false | `:113` | ❌ | 仅 `SettingsViewModel.cs:315,554`；另一同名内存开关在 `SaveManagementService.cs:19,46,300`，其入口 `CreateAutoBackupAsync:290` **无人调用** | 空壳 |
| 18 | `EnableBossKey` | bool / true | `:122` | 部分（**不可达**） | 写 `SettingsViewModel.cs:610` → 读 `ProcessMonitorService.cs:222,563,1249`（三处均要求 `_isRunning`/`StartAsync`，而 `StartAsync` 零调用） | 半成品 |
| 19 | `BossKeyModifiers` | flags / Alt\|Shift | `:127` | 部分（**不可达**） | `SettingsViewModel.cs:611` → `ProcessMonitorService.cs:1179` | 半成品 |
| 20 | `BossKeyVirtualKey` | uint / 0x48 | `:132` | 部分（**不可达**） | `SettingsViewModel.cs:612` → `ProcessMonitorService.cs:1180` | 半成品 |
| 21 | `MinimizeToTrayOnBossKey` | bool / true | `:137` | ❌ **实现都不读** | 仅 `SettingsViewModel.cs:321,560,613` 拷进 `ProcessMonitorConfig:277` 后无人读；全项目**无托盘图标代码** | 空壳 |
| 22 | `ShowBossKeyNotification` | bool / true | `:142` | ❌ **实现都不读** | 仅 `SettingsViewModel.cs:322,561,614` → `ProcessMonitorConfig:282` 后无人读；**无任何通知 API 调用** | 空壳 |
| 23 | `DefaultBackupPath` | string / "" | `:153` | ❌ | 仅 `SettingsViewModel.cs:325,564` + `SettingsPage.xaml.cs:161`；备份服务**硬编码** `%LOCALAPPDATA%\Galbox\SaveBackups`（`SaveManagementService.cs:36-40`） | 空壳 |
| 24 | `UseCustomBackupPath` | bool / false | `:158` | ❌ | 仅 `SettingsViewModel.cs:326,565`；XAML `:530,536` 只控制面板显隐 | 空壳 |
| 25 | `ScreenshotPath` | string / "" | `:165` | ✅ 真（**但无 UI 入口**） | `SettingsViewModel.cs:327,566` → `:624-627 SetScreenshotDirectory` → `ProcessMonitorService.cs:234-244`；**设置页没有任何截图路径控件**（grep `ScreenshotPath\|截图路径` 在 `Views` 下 0 命中）→ 恒空 | 缺失 |
| 26 | `EnableAdvancedMonitoring` | bool / false | `:174` | ✅ 真（依赖未启动的循环） | `SettingsViewModel.cs:330,569,615` → `ProcessMonitorService.cs:572,941` | 半成品 |
| 27 | `AutoScreenshotOnExit` | bool / false | `:179` | ✅ 真（依赖未启动的循环） | `SettingsViewModel.cs:331,570,616` → `ProcessMonitorService.cs:767` | 半成品 |
| 28 | `MonitoringIntervalMs` | int / 1000 | `:185` | ✅ 真（依赖未启动的循环） | `SettingsViewModel.cs:332,571,617` → `ProcessMonitorService.cs:703` | 半成品 |
| 29 | `ScreenshotFormat` | enum/Png | `:190` | ✅ 真（依赖未启动的循环） | `SettingsViewModel.cs:333,572,618` → `ProcessMonitorService.cs:1480,1485,1648` | 半成品 |
| 30 | `JpgQuality` | int / 90 | `:195` | ✅ 真（依赖未启动的循环） | `SettingsViewModel.cs:334,573,619` → `ProcessMonitorService.cs:1485,1657` | 半成品 |
| 31 | `Theme` | enum/Default | `:205` | ❌ **完全没有** | 仅 `SettingsViewModel.cs:337,576`；全仓 `RequestedTheme`/`ElementTheme` **零命中** | 空壳 |
| 32 | `Language` | enum/ChineseSimplified | `:211` | ❌ **完全没有** | 仅 `SettingsViewModel.cs:338,577`；**无任何 i18n/资源机制**，XAML 文案全部硬编码中文 | 空壳 |
| 33 | `LibraryViewMode` | enum/Grid | `:216` | ❌ | 仅 `SettingsViewModel.cs:339,578` + `CommonConverters.cs:680-699`；`LibraryViewModel` **不读它**（`IsTableView` 硬编码 `false`，`:66`） | 空壳 |
| 34 | `AutoScanOnStartup` | bool / false | `:225` | ❌ | 仅 `SettingsViewModel.cs:342,581`；扫描唯一入口是库页文件夹选择器（`LibraryPage.xaml.cs:154` → `LibraryViewModel.ScanFolderAsync:417`） | 空壳 |
| 35 | `DefaultScrapingSource` | string / "Bangumi" | `:231` | ❌ | 仅 `SettingsViewModel.cs:343,582` + 专用转换器 `CommonConverters.cs:805-837` | 空壳 |
| 36 | `GameDirectoriesJson` | string? / "[]" | `:237` | ❌ | `SettingsViewModel.cs:346,585`（Load `:352-379` / Save `:384-387`）；**无任何扫描逻辑消费该列表** | 半成品 |
| 37 | `LastModified` | DateTime / UtcNow | `:246` | ❌ 只写 | 写 `SettingsViewModel.cs:505`、`BangumiAuthService.cs:742`；从不读 | 空壳 |

**统计**：真实业务消费者 **3**（`BangumiAccessToken`、`BangumiUserId`、`ScreenshotPath`-但无 UI）；**服务真的会读但整条链路未启动** **5**（进程监控组）；**名义上接进 config 但被 `_isRunning` 挡死** **3**（老板键组）；**空壳（能改能存零影响）** **20**；**半成品/不可达** **2**；元数据 **1**。

**这解释了设置页的真实观感**：**除"打开数据文件夹 / 选择文件夹 / Bangumi API 密钥入库"外，没有任何一项真正改变应用行为。**

---

## 4. 模块名中英文对照表

| 中文模块名 | English / 代码名 | 路由 key | 实现文件 | 现状 |
|---|---|---|---|---|
| 主页 / 首页 | Home / Main Page（**无 HomePage，首页 = MainPage**） | `"Home"` | `Views\MainPage.xaml(.cs)`、`ViewModels\MainViewModel.cs` | 可用（但"启动"不启动进程） |
| 游戏库 | Library / Game Library | `"Library"` | `Views\LibraryPage.xaml(.cs)`、`ViewModels\LibraryViewModel.cs` | 主线可用 |
| 游戏详情页 | Game Detail | `"GameDetail"` | `Views\GameDetailPage.xaml(.cs)`、`ViewModels\GameDetailViewModel.cs` | 半数区块空壳 |
| 存档管理 | Save Manager / Save Management | `"SaveManager"` | `Views\SaveManagerPage.xaml(.cs)`、`ViewModels\SaveManagerViewModel.cs`、`Services\SaveManagementService.cs`、`ISaveManagementService.cs`、`Services\EngineSaveDetector.cs` | 备份/删除可用；恢复类半成品 |
| 补丁中心 | Patch Center | `"PatchCenter"` | `Views\PatchCenterPage.xaml(.cs)`、`ViewModels\PatchCenterViewModel.cs`、`Data\Entities\PatchRecord.cs` | 空壳 |
| 设置 | Settings（**NavigationView 内置齿轮，非独立菜单项**） | `"Settings"` | `Views\SettingsPage.xaml(.cs)`、`ViewModels\SettingsViewModel.cs` + `.Display.cs` + `.Sources.cs`、`Data\Entities\UserSettings.cs` | 26/37 项为装饰 |
| 错误报告 | Error Report / Error Check | **无路由** | `ViewModels\ErrorReportViewModel.cs`、`Services\ErrorCheckingService.cs`、`IErrorCheckingService.cs`、`Models\GameErrorInfo.cs`、`Data\Entities\GameErrorRecord.cs` | **UI 完全缺失** |
| 刮削进度 | Scraping Progress / Auto Scraping | **无路由** | `ViewModels\ScrapingProgressViewModel.cs`、`Services\AutoScrapingService.cs`、`IAutoScrapingService.cs` | **UI 完全缺失** |
| 刮削 / 元数据抓取 | Scraping / Metadata | — | `Services\GameScrapingService.cs`、`IGameScrapingService.cs`、`Services\ScrapingCacheService.cs`、`Core\Api\*` | 引擎真、入口无 |
| 老板键 | Boss Key / Panic Key | — | `Services\ProcessMonitorService.cs`、`Services\ProcessState.cs`、`Services\IProcessMonitorService.cs`、`MainWindow.xaml.cs`（WM_HOTKEY 子类化） | 空壳 |
| 截图 | Screenshot / Capture | — | `Services\ProcessMonitorService.cs`（`SetScreenshotDirectory`、`SaveBitmapAsync`、`CaptureScreenshotAsync`） | 空壳 |
| 进程监控 / 游玩时长 | Process Monitor / Play Time Tracking | — | `Services\ProcessMonitorService.cs`、`ProcessState.cs` | 空壳 / 半成品 |
| 数据源（元数据源） | Metadata Source / Scraping Source | — | `Core\Api\VndbApi.cs`、`BangumiApi.cs`、`YmgalCngalApi.cs`（**合并类**，无独立 YmgalApi.cs/CngalApi.cs）、`ApiClient.cs`、`HttpClientWrappers.cs` | VNDB 可用 / Bangumi 恒 0 条 / Ymgal+Cngal 缺失 |
| 缓存 | Cache / Scraping Cache | — | `Services\ScrapingCacheService.cs` | 搜索可用、详情缺失 |
| 导航服务 | Navigation Service | — | `Services\NavigationService.cs`、`INavigationService.cs` | 可用 |
| 游戏工具服务 | Game Utility Service | — | `Services\GameUtilityService.cs`、`IGameUtilityService.cs` | 可用（**产品文档未描述过的代码侧新增**） |
| 数据库 | Database / DbContext | — | `Data\Entities\GalboxDbContext.cs`、`GameInfo.cs`（**含 5 个子实体：GameCharacter/GameDocument/GameMediaFile/GameScreenshot/GameSaveBackup**）、`UserSettings.cs`、`PatchRecord.cs`、`GameErrorRecord.cs` | 可用（无 Migrations） |
| 依赖注入 / 应用启动 | DI Container / App Bootstrap | — | `App.xaml.cs`、`App.xaml`、`Program.cs`、`MainWindow.xaml(.cs)` | **阻断级缺陷 + DI 生存期缺陷** |
| 转换器 | Converters | — | `Converters\CommonConverters.cs` | 可用（`ConvertBack` 抛异常为惯用写法） |
| 批量操作 | Batch Operations | — | — | **缺失** |
| 删除游戏 | Delete Game | — | — | **缺失** |
| 导入 / 导出库 | Import / Export Library | — | — | **缺失**（文档层面也只有"复制 %LocalAppData%\Galbox 目录"这一种数据迁移说法） |
| 自动备份 / 存档监视 | Auto Backup / Save Watching | — | `Services\SaveManagementService.cs:290-308`（死码） | **空壳** |
| 无界面验收程序 | Acceptance / Headless host | — | `tests\Galbox.Acceptance\`（**审计期间由并行代理新增、未跟踪**，`Galbox.Acceptance.csproj` 带 `ProjectReference` → `Galbox.App`，含 A0–A6 七项检查） | **未审计**（超出本次只读范围） |

**文档与代码的文件级错位（供对齐用）**：`docs`/`_product` 里点名的 `Galbox.Core/Engines/{RenpyParser,KrkrParser,TyranoParser}.cs`、`GameScannerService.cs`、`ScraperService.cs`、`SaveParser.cs`、`PatchService.cs`、`MoyuApi.cs`、`ProcessMonitor.cs`、`YmgalApi.cs`、`CngalApi.cs`、`Galbox.Data/Database/` **全部不存在**；5 个实体类也不在各自独立文件里，而**全在 `GameInfo.cs` 一个文件中**（`:246/278/308/350/381`）。

---

## 5. 全部 Stub / 假数据 / TODO / 死代码 / 空 catch 清单

### 5.1 显式自称 Stub / 占位（26 条）

| # | 文件:行 | 说明 |
|---|---|---|
| 1 | `ViewModels\GameDetailViewModel.cs:662` | `// This is a stub - actual implementation would need to identify save locations` |
| 2 | `ViewModels\GameDetailViewModel.cs:664-676` | 构造 `GameSaveBackup` + `SaveBackups.Add`，**全程零文件复制** |
| 3 | `ViewModels\GameDetailViewModel.cs:668` | `BackupPath = Path.Combine(Game.InstallPath, "Backups", backupName)` **假路径**，从未创建 |
| 4 | `ViewModels\GameDetailViewModel.cs:670` | `Description = "自动创建的备份"` 假描述 |
| 5 | `ViewModels\GameDetailViewModel.cs:702` | `// This is a stub - actual implementation would restore files` |
| 6 | `ViewModels\GameDetailViewModel.cs:706` | `ErrorMessage = "存档备份恢复功能尚未实现";` |
| 7 | `ViewModels\PatchCenterViewModel.cs:13` | 类注释自认 `from moyu.moe API (stub)` |
| 8 | `ViewModels\PatchCenterViewModel.cs:157-158` | `await GenerateStubPatchDataAsync();` |
| 9 | `ViewModels\PatchCenterViewModel.cs:227-275` | `GenerateStubPatchDataAsync` 造演示补丁 |
| 10 | `ViewModels\PatchCenterViewModel.cs:269-274` | **把假数据真写进 DB**（`Patches.Add` + `SaveChangesAsync`） |
| 11 | `ViewModels\PatchCenterViewModel.cs:315/333/352` | 写死 `https://moyu.moe/patches/example*`（全仓只写不读） |
| 12 | `ViewModels\PatchCenterViewModel.cs:559/586` | `// Downloads a patch (stub implementation with simulated progress)` / `await Task.Delay(200)` |
| 13 | `ViewModels\PatchCenterViewModel.cs:597` | `patch.LocalPath = $"C:\\Galbox\\Patches\\{patch.Name}.zip"; // Stub path` |
| 14 | `ViewModels\PatchCenterViewModel.cs:634/660` | `// Installs a downloaded patch (stub implementation)` / `// Simulate installation` |
| 15 | `Core\Api\YmgalCngalApi.cs:8-9/14` | `Placeholder API client` / `// Placeholder - needs research for actual API endpoint` |
| 16 | `Core\Api\YmgalCngalApi.cs:38-52` | Ymgal 搜索纯桩：`await Task.CompletedTask; // No-op for stub implementation` + 硬编码 `Success = false` |
| 17 | `Core\Api\YmgalCngalApi.cs:61-67` | `return null; // Stub - no implementation yet` |
| 18 | `Core\Api\YmgalCngalApi.cs:101-114/123-129` | Cngal 同款纯桩 |
| 19 | `Core\Api\YmgalCngalApi.cs:14-15/77-78` | 两个 `BaseUrl` 被**注释掉** |
| 20 | `Core\Api\YmgalCngalApi.cs:17-21/80-84` | 两个桩类里声明后**从未使用**的 `JsonSerializerOptions` |
| 21 | `Services\GameScrapingService.cs:244/248` | `// Stub - return null`（Ymgal/Cngal 详情） |
| 22 | `Services\GameScrapingService.cs:418/452` | `// Stub - ymgal API needs research` / `// Stub - cngal API needs research` |
| 23 | `Services\BangumiAuthService.cs:225-226` | `OAuthClientId = "bgm_galbox"; // Placeholder - needs real client ID` / `OAuthClientSecret = ""; // Placeholder` |
| 24 | `Services\BangumiAuthService.cs:299/319-324` | `// Note: This is a stub - real OAuth requires registered client` / `// Stub: Simulate successful auth for development` / 直接 `result.Success = false` |
| 25 | `Services\BangumiAuthService.cs:519` | `// Stub - would need actual OAuth client credentials` |
| 26 | `Views\SettingsPage.xaml.cs:84-92` | `// TODO: Implement OAuth login flow for Bangumi` + 弹"will be implemented in a future update" |

### 5.2 硬编码假数据 / 永远返回空 / 无消费方（11 条）

| # | 文件:行 | 说明 |
|---|---|---|
| 27 | `Services\EngineSaveDetector.cs:604` | `result.ExtendedInfo["HasRegistryPrefs"] = true;` **硬编码假标志**，从未探测注册表 |
| 28 | `Services\EngineSaveDetector.cs:552-553` | 注释 `// Registry (PlayerPrefs) - handled separately` 是**假声明**（全项目零注册表代码） |
| 29 | `Services\EngineSaveDetector.cs:888-893` | `VnmProjectData.SavePath` 非 VNM 真实 schema 字段 → **恒 null 的走过场探测** |
| 30 | `Core\Api\VndbApi.cs:160-165` | `GetReleaseDate()` → `// This would need to be expanded based on actual API response` + `return null;` |
| 31 | `Core\Api\BangumiApi.cs:85-88` | `[JsonPropertyName("data")]` 与实测响应键 **`list`** 不符 → Bangumi 恒 0 条 |
| 32 | `Services\SaveManagementService.cs:708` | `return new List<GameSaveBackup>();`（查询异常）→ 与"真的没有备份"**不可区分** |
| 33 | `Models\GameErrorInfo.cs:145/168/192/217/238/263/309/331` | 8 个工厂全部 `AutoFixAvailable = false` |
| 34 | `Models\GameErrorInfo.cs:58` | `public string? FixAction { get; set; }` **全仓从未赋值** |
| 35 | `ViewModels\ErrorReportViewModel.cs:602` | `AutoFixAvailable = false // From database records, auto fix is typically not available` |
| 36 | `Services\ErrorCheckingService.cs:164-165` | `Description = $"This game requires {requiredLocale}..."`，而调用方三处全传字面量 `"Japanese (Shift-JIS)"`（`:368/387/400`） |
| 37 | `Services\ScrapingCacheService.cs:397-413` | `InvalidateGameCache` **忽略 gameId**（注释 `// For now, we just clear the search cache`）且无调用者 |

### 5.3 死代码 / 完整实现但零调用者（23 条）

| # | 文件:行 | 说明 |
|---|---|---|
| 38 | `ViewModels\ErrorReportViewModel.cs`（整文件 660 行） | 只被 `App.xaml.cs:141` 注册，**无页面、无 XAML 引用** → 从不被实例化 |
| 39 | `ViewModels\ScrapingProgressViewModel.cs`（整文件 917 行） | 只被 `App.xaml.cs:140` 注册，**无页面** → 从不被实例化；连带整条批量刮削触发器死掉 |
| 40 | `Services\ProcessMonitorService.cs`（整服务 1851 行） | `StartAsync`（`:549`）**全仓零调用** → `_isRunning` 恒 false、热键永不注册、监控循环永不运行、6 个事件零订阅 |
| 41 | `Services\ProcessMonitorService.cs:270` | `RegisterGame` **全仓零调用** → `_processStates` 恒空 → 老板键隐藏 0 个窗口 |
| 42 | `Services\ErrorCheckingService.cs:174-195` | `CheckCategoryAsync` 完整实现但**零调用者** |
| 43 | `Services\SaveManagementService.cs:290-308` | `CreateAutoBackupAsync` 真实现但**零调用者** → 自动备份整条链路死 |
| 44 | `Services\SaveManagementService.cs:853-897` | `GetSaveMetadataAsync` 零调用者 → 连带 `:1303-1456`（约 150 行：`ExtractEngineMetadataAsync`/`ExtractRenpyMetadata`/`ExtractRpgMakerMetadataAsync`/`TryExtractJsonMetadataAsync`）全部不可达 |
| 45 | `ViewModels\SaveManagerViewModel.cs:398-435` | `LoadBackupsForGameAsync` 死码（只有 Safe 版被调用） |
| 46 | `Services\GameScrapingService.cs:220-262` | `GetGameDetailsAsync` 无生产调用者 → `GetBangumiDetailsAsync(476-525)`、`GetVndbDetailsAsync(527-583)` 整条标签/角色富化路径死掉 |
| 47 | `Services\ScrapingCacheService.cs:312-339/370-394` | `GetCachedDetails`/`CacheDetails` 零调用者 → 详情缓存永不写入 |
| 48 | `Services\ScrapingCacheService.cs:416-449/452-489` | `ClearAllCache`/`ClearExpiredEntries` 真实现但无调用者 |
| 49 | `Services\AutoScrapingService.cs:236-253` | `GetCurrentProgress()` 零调用者 |
| 50 | `Views\PatchCenterPage.xaml.cs:73-77` | `ShowPatchDetailsFlyout` 无调用者 |
| 51 | `ViewModels\MainViewModel.cs:176-186` | `QuickLaunch()` 命令在 XAML 里**从未被绑定** |
| 52 | `Helpers`/`Services\BangumiAuthService.cs:559-589` | `LogoutAsync` 无调用者，UI 也无登出按钮 |
| 53 | `Services\BangumiAuthService.cs:376-454` | `AuthenticateWithApiKeyAsync` **全仓无人调用**（设置页直接给 VM 赋值，绕过服务） |
| 54 | `ViewModels\SaveManagerViewModel.cs:713` | `BackupPhase.Verifying => "正在验证备份完整性..."` **不可达**（服务从不 Report Verifying） |
| 55 | `E:\tmp\Galbox_v2\test_engine.cs`（38 行） | 仓库根游离控制台入口，**不属于任何 csproj**，永不参与编译 |
| 56 | `Services\ErrorCheckingService.cs:48-55` | `KnownDirectXDlls`（20 条列表）声明后**从未被引用** |
| 57 | `Services\ErrorCheckingService.cs:3` | `using System.Runtime.InteropServices;` 未使用 |
| 58 | `Services\ErrorCheckingService.cs:726-730` | `if (IsRpgMakerGame(gamePath)) { }` **空块**，判定结果被丢弃 |
| 59 | `Services\GameScrapingService.cs:30-32` | `SpecialCharsPattern` 声明后**从未使用** |
| 60 | `Services\AutoScrapingService.cs:30` | `private const double AutoAcceptThreshold = 90.0;` 在本文件内**从未使用** |

### 5.4 界面/绑定层的死件（9 条）

| # | 文件:行 | 说明 |
|---|---|---|
| 61 | `Views\PatchCenterPage.xaml.cs:23-32` | **缺 `DataContext = ViewModel;`**（其他 5 页都有）→ 页面所有 `{Binding}` 全废 |
| 62 | `Views\PatchCenterPage.xaml:389` | 承载整个补丁列表的 `ScrollViewer` 因 DataContext 为 null **永久 Collapsed** |
| 63 | `Views\PatchCenterPage.xaml:462/477/505` | 下载/安装/详情三按钮用 `{Binding ViewModel.XCommand, ElementName=RootGrid}` → **`Command` 恒为 null**（生成的 `.g.cs` 中无这三个命令） |
| 64 | `Views\PatchCenterPage.xaml:375-380` | "从列表中选择游戏以查看可用补丁"误用 `NullToVisibilityConverter`（应为 `InverseNullToVisibilityConverter`，后者已在 `CommonConverters.cs:110-121` 存在）→ 永久不可见 |
| 65 | `Views\PatchCenterPage.xaml:449-455` | 进度条 `Visibility` 未传 `ConverterParameter` → 转换器返回 Collapsed → **永远不显示**（`CommonConverters.cs:530-534` 的 `"Downloading"` 分支成为死分支） |
| 66 | `Views\PatchCenterPage.xaml:44-50` | 4 个补丁颜色画刷定义在 **Page.Resources**，而 `PatchTypeToColorConverter` 从 **`Application.Current.Resources`** 取值（`CommonConverters.cs:556-569`）→ 永远取不到，永远走硬编码 fallback |
| 67 | `Views\GameDetailPage.xaml:471-474` | 「删除」按钮**无 Command、无 Click** |
| 68 | `Data\Entities\PatchRecord.cs`（整类） | **不实现 `INotifyPropertyChanged`** → `patch.Status/DownloadProgress` 变更不驱动 UI 刷新（`XAML:453/464/479/496` 绑定形同一次性快照） |
| 69 | `Views\SettingsPage.xaml:917` | 引用 **未注册的转换器** `SettingsVersionTextConverter`（见 §5.6 崩溃项） |

### 5.5 空 catch / 静默失败 / 吞异常（20 条）

| # | 文件:行 | 说明 |
|---|---|---|
| 70 | `Services\SaveManagementService.cs:512-523` | `:519 _logger.LogInformation("Attempting rollback after cancellation");` + `:522 throw` → **只有日志，没有回滚代码** |
| 71 | `Services\SaveManagementService.cs:524-538` | `:534 "Attempting rollback after error"` → 同样**无回滚代码**，`:537 return false` → 半覆盖状态留给用户 |
| 72 | `Services\SaveManagementService.cs:1297-1300` | `CleanupOldBackupsAsync` 外层 catch 全吞 → 旧备份可能既不删文件也不删记录 |
| 73 | `Services\SaveManagementService.cs:953-957` | `catch { ... return true; // Assume sufficient space }` → 空间不足被静默放过 |
| 74 | `Services\SaveManagementService.cs:931-936` | 无法确定盘根 → `return true` |
| 75 | `Services\SaveManagementService.cs:544-555` | 临时目录清理失败仅 Warn → `TempRestore\backup_*` 残留 |
| 76 | `Services\SaveManagementService.cs:1392-1395/1444-1447` | `catch (JsonException) { /* Ignore parsing errors */ }` |
| 77 | `Services\EngineSaveDetector.cs:873-879` | `GetFilesWithSizes` 吞 `UnauthorizedAccessException` → **备份"成功"但内容不完整**（最危险的一条） |
| 78 | `Services\EngineSaveDetector.cs:805-816/829-840` | `CollectSaveFiles`/`CollectAllSaveFiles` 吞权限/IO 异常 → 上层只报"未检测到存档文件" |
| 79 | `Services\ErrorCheckingService.cs:535-538/635-638/657-660/817-820` | 4 处空 catch（跳过不可访问目录/不可读文档/取不到版本信息/目录访问错误） |
| 80 | `Services\AutoScrapingService.cs:635-638` | `DeserializeTags` 的 `catch { return new List<string>(); }` 无日志吞异常 |
| 81 | `Services\ProcessMonitorService.cs:437/447/499/845/856/890/971/1815/1827/1839` | 10 处 `catch { }` 空吞 |
| 82 | `Services\SaveManagementService.cs:125-130` | `DetectSaveLocationAsync` 外层 catch → 返回 `Success=false`，诊断只剩日志 |
| 83 | `Services\SaveManagementService.cs:600-605` | `VerifyRestoreIntegrityAsync` catch → 把"异常"记为"校验失败" |
| 84 | `Views\LibraryPage.xaml.cs:124-127/158-161` | 添加/扫描失败 → 只把异常塞进 `ErrorMessage`（无日志） |
| 85 | `Views\MainWindow.xaml.cs:93` | `Debug.WriteLine("Failed to set window subclass...")` → **生产不可见**（且这行根本执行不到，因为 P/Invoke 在进入 native 前就抛了） |
| 86 | `Services\ProcessMonitorService.cs:1464` | 日志 `"Failed to capture window using PrintWindow..."` —— **日志与实现不符**（实际用 `CopyFromScreen`，`PrintWindow` 从未被调用） |
| 87 | `ViewModels\SettingsViewModel.cs:604-635` | `ApplyRuntimeSettings` 整段 `try/catch` → 失败只 warning，UI 仍显示"设置已保存" |
| 88 | `App.xaml.cs:193-197` | 数据库迁移失败只 `Debug.WriteLine` 后继续 |
| 89 | `App.xaml.cs:224-229` | **启动期异常只 `logger?.LogError`**，而日志仅 `AddDebug` → 用户看不到任何失败（这是 §0 那个阻断级缺陷"无声"的原因） |

### 5.6 被"注释掉 / 未接线"的关键逻辑（8 条）

| # | 文件:行 | 说明 |
|---|---|---|
| 90 | `Services\BangumiAuthService.cs:326+` | OAuth 完整实现被整块注释掉，`:320-324` 改为 `Success = false` |
| 91 | `Services\SaveManagementService.cs:1353-1354` | Renpy pickle 存档解析明确放弃 |
| 92 | `Services\SaveManagementService.cs:1320-1324` | `// Other engines may require specific parsing` → default 分支只做 JSON 猜测 |
| 93 | `App.xaml.cs:87-93/95-101` | Ymgal/Cngal 的 `HttpClient` **无 `BaseAddress`**（注释 `// BaseAddress to be configured when API implementation is complete`） |
| 94 | `Services\SaveManagementService.cs:290-308` + `UserSettings.cs:113` + `SaveManagerPage.xaml:309-313` | **自动备份整条链路未接线**：服务方法零调用者、页内开关只写内存、设置项无消费方、无 `FileSystemWatcher`、无计时器、无 `ProcessMonitor` 集成 |
| 95 | `Services\SaveManagementService.cs:19-20/36-40` | 硬编码 `_autoBackupEnabled = true`、`_maxBackupsPerGame = 10`、备份根目录 → **不读 `UserSettings`** |
| 96 | `ViewModels\ScrapingProgressViewModel.cs:27` + `:56-61` | `_eventsSubscribed = true` 让三个事件订阅永不执行（`:63-65` 死码） |
| 97 | `ViewModels\MainViewModel.cs:229-309` | 与 `LibraryViewModel.cs:325-410` **重复且行为不一致**的添加游戏实现（不识别引擎） |

### 5.7 会崩溃 / 高风险运行期问题（7 条）

| # | 严重度 | 文件:行 | 说明 |
|---|---|---|---|
| 98 | 🔴 **阻断** | `MainWindow.xaml.cs:20-27` + `:65` + `:88` | `SetWindowSubclass`/`RemoveWindowSubclass`/`DefSubclassProc` 声明在 **user32.dll**，实际只由 **comctl32.dll** 导出（本次已二进制核实）→ 构造函数抛 `EntryPointNotFoundException`，被 `App.xaml.cs:224` 静默吞掉 → **主窗口不出现且无报错**。`RemoveWindowSubclass` 同错（`:123-131`），若 subclass 曾装上，`Closed` 事件里会再抛一次（那次是**真正未捕获**的进程级崩溃） |
| 99 | 🔴 高 | `App.xaml.cs:56/39/112` + 各页面 `GetRequiredService` | **全应用共享一个 Scoped DbContext**（未开 `ValidateScopes` + 全部从根容器解析）→ 并发时 EF 抛 `A second operation was started on this context instance...`；且 `ISaveManagementService` 注册为 Singleton 却消费 Scoped（开 `ValidateScopes` 后直接抛） |
| 100 | 🔴 高 | `Views\SettingsPage.xaml:917` + `CommonConverters.cs:784` | `SettingsVersionTextConverter` **从未注册进任何 ResourceDictionary**（App.xaml 与 Page.Resources 都没有）→ 生成的 `SettingsPage.g.cs:933 LookupConverter("SettingsVersionTextConverter")` 返回 null → `.Convert(...)` 触发 `NullReferenceException` → **极可能"导航到设置页即崩溃"**（未实机验证） |
| 101 | 🟠 中 | `ViewModels\SaveManagerViewModel.cs:288/327/354-374/378` | `Task.Run` 线程池线程里 `DispatcherQueue.GetForCurrentThread()` 返回 null → 走 fallback 在**后台线程**改绑定的 `ObservableCollection` + 写 `ErrorMessage` → COMException 风险（被 catch 吞成红条） |
| 102 | 🟠 中 | `Views\SaveManagerPage.xaml.cs:82/97/116/135`（`async void`） | `AsyncRelayCommand` 不吞异常，catch 内再解引用（`SaveManagerViewModel.cs:555` 无 `?.`、`:672` 解引用 `SelectedGame`）→ 二次 NRE = **进程级未捕获异常** |
| 103 | 🟠 中 | `Services\ProcessMonitorService.cs:1115-1119` | `GetWindowText/GetWindowTextLength`（`:49-53`）**未指定 CharSet**（默认 Ansi）却按 UTF-16 解码（`PtrToStringUni`）→ 窗口标题乱码（高置信推断） |
| 104 | 🟠 中 | `Services\ProcessMonitorService.cs:1546-1560` | `GetScreenshotsForGame` 的 `Directory.GetFiles` **无 try/catch** → 目录被删即抛 |

### 5.8 假绿灯 / 工程卫生（6 条）

| # | 文件:行 | 说明 |
|---|---|---|
| 105 | `tests\Galbox.Tests\Galbox.Tests.csproj` | **没有任何 `<ProjectReference>`** → 测不到任何应用代码 |
| 106 | `tests\Galbox.Tests\GalboxFunctionalTests.cs:354-413` | `FullIntegrationTest` 每步只 `_output.WriteLine` 后 `:419 return true` → **零断言**；`RunTestStep:415-429` 只要不抛异常就记 PASS |
| 107 | `tests\Galbox.Tests\GalboxFunctionalTests.cs:114-144` | Step2 标题写 "Add Game Directory"，实际只点了设置导航项，末尾自述 `Step 2 PARTIAL` |
| 108 | `tests\Galbox.Tests\TEST_REPORT.md:14-16` | 宣称"总测试数 8 / 通过 8 / 通过率 100%"，但 `:62-84` 明确 Step4 刮削 **SKIPPED**、Step5 详情页 **SKIPPED** |
| 109 | `tests\Galbox.Tests\TEST_REPORT.md:159-161,196-198` | 宣称"游戏库管理 完整实现""存档管理 完整实现""补丁中心 完整实现"、"当前无需要修复的问题" —— 与本次代码审计结论全面矛盾；`:177` 还把老板键写成 **Ctrl+Shift+H**（与默认 Alt+Shift+H 矛盾） |
| 110 | `Galbox.sln` / git 历史 | 测试工程**不在解决方案里**（`dotnet build Galbox.sln` 不编译测试）；`cc540e7` 曾把整个 `bin\x64\Debug\...` 的 dll/pdb/exe **提交进版本库**；审计期间工作树被并行代理改动（`M Galbox.sln` + 未跟踪 `tests/Galbox.Acceptance/` + `_product/design/scraping-diagnosis.md`） |

---

## 6. 要把这个产品补完，按重要性排序必须先做的 5 件事

> 排序依据：**"卡住其它所有工作的依赖"优先于"单个功能的完整度"**；每条给出可验证的完成判据。

### 第 1 件：先让应用能稳定启动 —— 修 `MainWindow` 的 P/Invoke 与启动期异常可见性

**证据**：`MainWindow.xaml.cs:20-27` 三个 subclass P/Invoke 声明在 **user32.dll**，而本次二进制核实：`SetWindowSubclass`/`RemoveWindowSubclass`/`DefSubclassProc` 在 user32.dll 中 **NOT FOUND**、在 comctl32.dll 中 **FOUND**（同文件 `RegisterHotKey` 在 user32.dll 正常 FOUND）。构造函数 `:65 → :88` 触发 `EntryPointNotFoundException`，被 `App.xaml.cs:224-229` 吞掉，而日志只配了 `AddDebug`（`:48-52`）→ **无声失败**。Git 证据显示这段代码是 `cc540e7`（"编译通过，准备进行测试"）之后加的，而唯一"成功启动"记录早于它 → **该代码从未被运行验证过**。

**必须做**：
1. 三处 `DllImport` 改为 `comctl32.dll`（并在 `app.manifest` 声明 Common-Controls 6.0 依赖）；
2. **把启动期异常的兜底从"只打 Debug 日志"改成"可见的错误 UI"**（`ContentDialog`/`Infobar` 或落盘日志），否则任何启动失败都不可诊断；
3. 顺手修 `:123-131 RemoveWindowSubclass` 的同一个 DLL 错误（否则关闭窗口时会真正崩溃）。

**完成判据**：应用启动后主窗口稳定出现；故意让构造抛异常时能看到错误提示而不是"静默无窗口"。**这是唯一一件"不做就无法验证其它任何事"的任务。**

### 第 2 件：修 DI 生存期 —— 全应用共享一个 `GalboxDbContext`

**证据**：`App.xaml.cs:56 AddDbContext<GalboxDbContext>(...)` 默认 **Scoped** + `:39 services.BuildServiceProvider();`（**未开 `ValidateScopes`**）+ 所有页面/服务从**根容器**解析（`App.xaml.cs:139/142`、`LibraryPage.xaml.cs:34`、`GameDetailPage.xaml.cs:32`、`SaveManagerPage.xaml.cs:29`、`PatchCenterPage.xaml.cs:28`、`SettingsPage.xaml.cs:31`、`MainPage.xaml.cs:31`）→ 根作用域缓存了同一个实例；`App.xaml.cs:112` 还把 `ISaveManagementService` 注册为 **Singleton** 却消费 Scoped 的 `DbContext`。

**必须做**：
1. 改为 `services.AddDbContextFactory<GalboxDbContext>(...)`，业务代码用 `IDbContextFactory` 每次操作一个短生存期 context；**或**所有从根容器解析的地方改为 `CreateScope()`（`App.xaml.cs:172` 已经示范了正确写法，只是别处没用）；
2. 把**长生存期**服务（`SaveManagementService`/`AutoScrapingService`/`ErrorCheckingService`/`BangumiAuthService`/`ScrapingCacheService`）改为注入 `IDbContextFactory` 或 `IServiceScopeFactory`，不再直接注入 `DbContext`；
3. 开发期在 `Program.cs` 打开 `new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }` —— 这样第 2 条的遗漏会在**启动时**立刻炸出来，而不是变成运行期随机红条。

**完成判据**：`ValidateScopes/ValidateOnBuild` 打开后能正常启动；连点"刷新"与"点游戏"并发不出现 `A second operation was started on this context instance`。

### 第 3 件：把"添加游戏 / 扫描文件夹"抽成可测试服务，并消灭两条不一致的添加路径

**为什么排第三**：这是**唯一一条能跑通的主线**的入口，也是后续所有功能（刮削、存档、错误检测、补丁）共同的前置动作。当前它无法脱离 UI 测试（见 Q1 五层障碍），导致后面每一件事都无法用回归测试保护；而且它有**两个行为不一致的实现**（`LibraryViewModel.cs:325-410` vs `MainViewModel.cs:229-309`，后者不识别引擎）和**一个深层目录漏扫 bug**（`:435 TopDirectoryOnly`），以及**完全没有的"删除游戏"与"批量操作"**。

**必须做**：按 Q1 的 A–E 方案落地 —— `IGameLibraryService` 放进 `Galbox.Core`（`Galbox.Core.csproj` 加 `ProjectReference` → `Galbox.Data`），连带把 `IGameUtilityService`/`GameUtilityService`/`EngineSaveDetector` 一起搬出 `Galbox.App`；两个 ViewModel 都改为调用同一个服务；补 `CancellationToken`；删掉仓库根的游离 `test_engine.cs`；顺手补上**删除游戏**与**批量操作**（当前都没有）。

**完成判据**：新增的 `tests\Galbox.UnitTests`（`net8.0-windows`，**带 `ProjectReference`**）能在无 UI 进程里：给定临时目录树 → 断言扫到几个游戏、引擎类型是什么、DB 多了几行、重复扫描幂等。

### 第 4 件：修通图片链路 —— 没有它，产品"看起来就是坏的"

**证据**：全工程 `CoverImagePath`/`BackgroundImagePath` **只读不写**；刮削只写 `CoverImageUrl`/`BackgroundImageUrl`（`AutoScrapingService.cs:515-522`）；**没有任何下载代码**（grep `GetByteArrayAsync|WriteAllBytes|DownloadImage|DownloadFile` 在 `src` 下零命中）；而所有 `<Image>` 都经 `PathToImageConverter`（`CommonConverters.cs:209-234`）要求 `File.Exists` → **首页、游戏库网格、详情页、存档管理、补丁中心的封面全部是空白块**。这是"用户第一眼判断这个软件能不能用"的地方。

**必须做**：实现图片下载器（`HttpClient.GetByteArrayAsync` + 落盘到 `%LOCALAPPDATA%\Galbox\Covers\{gameId}.jpg` + 回写 `CoverImagePath`），并在刮削 `ApplyMetadataToGame` 后调用；同时把 `GameCharacter.ImageUrl` 也落成 `ImagePath`（`AutoScrapingService.cs:559` 只写了 URL，而 `GameDetailPage.xaml:247` 绑的是 `ImagePath`）。

**完成判据**：任选一个有 `CoverImagePath` 的游戏，重启应用后首页/库/详情页都能看到封面。

### 第 5 件：给已经写好却"没有门"的模块装上门，并停止制造假数据

**证据**：
- **两个模块零 UI**：`错误报告`/`刮削进度` 无页面、无导航项（`Views/` 只有 6 页；`NavigationService.cs:20-28` 只有 6 key；`App.xaml.cs:140-141` 是它们唯一的站外引用），而它们的引擎是本次审计中最真的部分；
- **一行就能解锁的两处**：`BangumiApi.cs:85` 的 `data` → `list`（实测服务端返回 `list`）；`ScrapingProgressViewModel.cs:27 _eventsSubscribed = true` → `false`（否则订阅永不发生）；另有 `:268` 漏了 `$` 的插值笔误；
- **补丁中心在造假数据**：`PatchCenterViewModel.cs:269-274` 把 `stub_trans_*`/`stub_fix_*`/`stub_adult_*` 假补丁**真写进用户数据库**，而 UI 因缺 `DataContext`（`PatchCenterPage.xaml.cs:23-32`）连看都看不到；
- **详情页在说谎**：`GameDetailViewModel.cs:664-676` 只写 DB 行不复制文件（假备份）；`:706` 直接返回"存档备份恢复功能尚未实现"；`GameDetailPage.xaml:471-474` 是死按钮；
- **存档有真实数据丢失风险**：`SaveManagementService.cs:804-810` 快速切换缺 `currentBackup == null` 短路 → 当前存档检测失败时仍静默覆盖旧档并报成功；`:519/:534` 的"Attempting rollback"只有日志没有回滚；`EngineSaveDetector.cs:645-648` 把 RPG Maker/Krkr 的**整个游戏目录**当存档目录，配合 `RollbackRestoreAsync:621-633` 的"全删再拷回"可能永久损坏游戏安装。

**必须做**：
1. **死按钮/假数据二选一**：详情页的假"创建备份/恢复"改为调用 `ISaveManagementService`（真实现已存在）或直接移除按钮；`GenerateStubPatchDataAsync` 整段删掉（**绝不能让假数据入库**）；`GameDetailPage.xaml:471`、`PatchCenterPage.xaml:462/477/505` 的死按钮要么绑对命令、要么删掉；
2. **补上 3 个缺失的页面接线**：`ErrorReportPage.xaml`/`ScrapingProgressPage.xaml` + `NavigationService._pageMapping` 两个 key + `MainWindow.xaml` 两个 `NavigationViewItem` + **给 `PatchCenterPage` 补 `DataContext = ViewModel;`**；
3. **修存档安全网**：快速切换补 `currentBackup == null` 短路；恢复异常分支实现真回滚（或改为"恢复前强制创建一份 DB 备份记录"）；禁止 `EngineSaveDetector` 把 `InstallPath` 当存档目录；
4. **顺便修一个设置页崩溃点**：`SettingsPage.xaml:917` 的 `SettingsVersionTextConverter` 未注册（注册它或改用普通格式化）；
5. **启动进程监控**：`App.xaml.cs:221` 之后补 `await processMonitor.StartAsync();` + 在添加/扫描流程里为每个游戏调 `RegisterGame(game)`（两者实现都已就绪，只缺调用）—— 这一条能让"老板键 + 截图 + 真实游玩时长/运行检测"三个模块同时从空壳变可用；
6. **设置页做二选一**：对 **26 个"只存不用"** 的项，要么接上真实消费方（优先级最高：`MatchThresholdPercent`、4 个源开关、`SourcePriorityJson`、`OnLaunchBehavior`/`OnExitBehavior`、`AutoBackupOnExit`、`DefaultBackupPath`、`LibraryViewMode`、`AutoScanOnStartup`），要么**从 UI 移除**；特别是 `Theme`/`Language` 必须拿掉或实现（否则等于对用户撒谎）；
7. **停止用假绿灯评估质量**：给 `tests\Galbox.Tests` 补上 `ProjectReference` 并把 `FullIntegrationTest` 里的 `return true` 换成真断言（至少断言"添加游戏后库里有 1 条"、"创建备份后磁盘上出现 zip"）；同时把 `TEST_REPORT.md` 里"补丁中心 完整实现""当前无需要修复的问题"这类结论纠正，避免后续验收继续被误导。

**完成判据**：`galbox.db` 的 `Patches` 表不再出现 `stub_*` / `moyu.moe/patches/example`；详情页点"创建备份"后磁盘上真的出现文件（或该按钮消失）；导航栏能进"错误报告"与"刮削"且进度真的从 0 走到 100%；设置页里每一个可见开关都能被指到一段读取它的业务代码。

---

## 附录 A：本次审计的方法、复现路径与边界

### A.1 做了什么
完整通读 `src` 下 60 个文件中的 40+ 个（约 20,000 行）；对每个"看起来实现了"的方法追到真实方法体；对**每个 `UserSettings` 属性**做全仓引用计数并区分"仅存储"与"真消费"（§3）；对每个 `IValueConverter` 的 `NotImplementedException` 核实是否在 `ConvertBack`；对每个 `[RelayCommand]` 核实是否真被 XAML 绑定（并交叉验证编译产物 `obj\...\*.g.cs` 的 x:Bind 集合）；对每个服务核实是否真的从可达路径被调用；用 `GetDirectories/GetFiles` 核实"真遍历文件系统"；**对 `user32.dll` / `comctl32.dll` 的导出名表做二进制扫描**核实 P/Invoke 目标存在性；对 Bangumi 搜索端点做了一次只读 HTTP GET 实证响应键名；核对 `git log`/`git show --stat`/`git reflog` 与 `tests/` 的断言强度。

### A.2 三条成本最低、证据最硬的复现路径
1. **详情页 → 点"恢复"** → 立刻看到红条 **"存档备份恢复功能尚未实现"**（`GameDetailViewModel.cs:706`）。
2. **补丁中心 → 选一个游戏** → 数据库 `Patches` 表出现 `stub_trans_*` 等假补丁（`PatchCenterViewModel.cs:269-274`），且**界面上什么都不会显示**（缺 `DataContext`）。
3. **游戏库 → 添加一个嵌套两层的游戏目录** → 扫描结果里它被计入"跳过"（`LibraryViewModel.cs:435/442`）。

### A.3 没有做（受"纯只读"约束）
没有构建、没有运行应用、没有跑测试、没有连接 `%LOCALAPPDATA%\Galbox\galbox.db` 查实际数据、没有对真实游戏目录做引擎探测命中率实测。因此报告中所有"运行时会怎样"（如 §0 的启动失败、跨线程更新集合抛 COMException、EF 并发异常的具体表现、设置页 NRE）均为**静态推断**，已在相应位置逐条标注置信度与"未验证"。

### A.4 与任务书不一致之处
任务书给出的行号为"非空行"计数（本报告全部改用物理行号）；任务书称 `ProcessMonitorService` 1557 行、实际 **1851**；`ErrorCheckingService` 标 880、实际 **1012**；`SaveManagementService` 标 1261、实际 **1468**；`PatchCenterViewModel` 标 690、实际 **797**；`UserSettings` 标 323、实际 **399**；`SettingsPage.xaml` 标 904、实际 **958**。

### A.5 未审计 / 未深挖
- `tests\Galbox.Acceptance\`（审计期间出现的无界面验收程序，含 A0–A6 七项检查，带 `ProjectReference` → `Galbox.App`）—— **未审计**。
- 刮削链路的更深入白盒结论见并行代理的 `_product/design/scraping-diagnosis.md`（本报告对刮削只给档位与关键证据）。
- `BangumiAuthService.cs`（845 行）与 `ProcessMonitorService.cs`（1851 行）的内部细节按模块抽查而非逐行复述。
