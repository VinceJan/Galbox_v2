# Galbox 主任务清单（Work Backlog）

> 编制时间：2026-09-11　｜　维护者：主对话
> 来源：四份调研报告 + 实测发现。**每一项都有证据来源，不是拍脑袋排的。**
> 状态标记：✅ 已完成 ｜ 🔄 进行中 ｜ ⬜ 待办 ｜ ⛔ 被产品决策阻塞

---

## 0. 项目当前的真实状态

**应用已经能正常启动，验收程序已从 8 项扩到 19 项（A0–A18）。** 核心差异化能力（存档节点）的底层与胶水层均已交付并经过独立复验。

**唯一客观的度量**：无界面验收程序（`tests\Galbox.Acceptance`）。

| 时间点 | 结果 |
|---|---|
| 首轮基线 | 5 PASS / 3 FAIL（A1/A5/A6 失败） |
| 第一轮修复后 | 8 PASS / 0 FAIL |
| 加 A8/A9 后 | 10 PASS / 0 FAIL |
| **现在（A0–A18 共 19 项）** | **待统一跑一次全量**（A11–A18 刚写入，尚未整体复验） |

### ⚠️ 当前唯一阻断项：**打包发布后无法运行**

| | 状态 |
|---|---|
| 从构建目录运行 | ✅ 完全正常（启动、刮削、存档、补丁全部可用） |
| **`dotnet publish` 后运行** | ❌ **崩溃**（`0xC000027B`，崩在 `Microsoft.UI.Xaml.dll`） |

已修的两个根因（**但仍崩**）：
1. publish 不搬运编译后的 XAML（`.xbf` 10 个文件丢失，因 `EnableCoreMrtTooling=false` 无 `resources.pri` 兜底）→ 已加 MSBuild target 补上
2. `Program.cs` 无条件调用 `Bootstrap.Initialize`（自包含部署不该调用，会混用两个运行时版本）→ 已用条件编译常量编译掉

**已确认的环境缺口**：`Microsoft.Build.Packaging.Pri.Tasks.dll` 全系统不存在（VS 装了但无 UWP/Appx 工作负载），所以 `EnableCoreMrtTooling=true` 会报 MSB4062。

**正在由专门代理攻坚**，并要求它：做不到就如实报告 + 给代价最小的替代方案，不许谎报成功。

### 合并预演结果（实测，未触碰工作区）
| 分支 | 合入主线 |
|---|---|
| `feat/patch-installer`（ae88ee9） | ✅ **零冲突** |
| `feat/save-node-integration`（1a8d855，含数据层+解析器+Services） | ⚠️ **2 处冲突**：`Galbox.sln`、`src\Galbox.App\App.xaml.cs`（解法见 §5.0） |


---

## 1. ✅ 已完成

| # | 事项 | 证据 |
|---|---|---|
| 1 | **阻断级修复**：应用启动弹不出窗口（三个 P/Invoke 声明在 user32.dll，实际由 comctl32.dll 导出） | 二进制扫描两 DLL + 实机 A/B：修复前 `MainWindowHandle=0`，修复后 `=2362014` |
| 2 | **无界面验收程序**（UI-free 复刻 DI 容器） | 已从 8 项扩到 **19 项（A0–A18）** |
| 3 | **四份调研报告** | `current-state-audit.md`(108KB) / `scraping-diagnosis.md`(35KB) / `renpy-save-analysis.md`(58KB) / `patch-source-research.md`(55KB) |
| 4 | **用户数据安全网** | 迁移前 DB 快照（26 列、无 VndbId）+ 公开仓库隐私隔离（私人文档已排除） |
| 5 | **产品知识包入库** | `_product\` 下 6 份文档已提交 |
| 6 | **线程亲和性修复**：UI 启动路径 7 处 `.ConfigureAwait(false)` → 续体落到线程池 → `new MainWindow()` 抛 `COMException 0x8001010E` 且被吞 | 修复为 `.ConfigureAwait(true)`；A/B 验证缓存空/非空两种情况 |
| 7 | **DI 生存期修复（W1）**：`AddDbContextFactory` + `ValidateScopes`/`ValidateOnBuild`；8 个 ViewModel 改造 | 启动日志出现 `Process monitor: started=True, registered=1/1` |
| 8 | **刮削链路修复（A5/A6）**：4 个叠加故障（零入口、Bangumi 旧端点返回结构不符、VNDB `filters` 非法、90% 阈值误杀） | `ScrapingCache\` 落盘 `search_dreamin'_her.json` 等；A5/A6 转 PASS；`魔女的夜宴 / Yuzusoft / v16044` |
| 9 | **启动器选择确定性化**：不再依赖 OS 枚举顺序 | 打分排序后不再误选 `dreaminher-32.exe` |
| 10 | **补丁安装器引擎** | 189/189 断言；回滚后目录逐字节一致 |
| 11 | **Ren'Py 存档解析器**（RPA 解包 + 零执行 pickle 扫描） | `ALL CHECKS PASSED`；label 22/22、CG 6/27 |
| 12 | **数据层 + 迁移机制** | 107/107；用老库快照验证迁移后数据一条不少 |
| 13 | **存档节点扫描服务** | 11/11 |
| 14 | **合并预演**：`merge/all-features` @ `0e051ea` | 冲突已解；**实测 10/10 PASS** |
| 15 | **W3 补丁中心页缺 DataContext** | 已补 `DataContext = ViewModel;`（此前补丁列表永久 Collapsed） |
| 16 | **W8 假补丁数据生成** | 生成代码已删除（仅剩说明性注释）；用户库未被污染（0 条 `stub_trans_*`） |
| 17 | **W9 详情页假备份/恢复** | 已改为调用真实 `SaveManagementService.CreateBackupAsync` / `RestoreBackupAsync` |
| 18 | **W10 死按钮** | `ElementName=RootGrid` 绑定已在详情页与补丁中心页清除 |

### 意外收获（不在原清单，但确实修好了）

| 事项 | 说明 |
|---|---|
| `UserSettings` 少 5 列 | `BangumiAuthMethod` / `BangumiRefreshToken` / `BangumiTokenExpiresAt` / `BangumiUsername` / `GameDirectoriesJson` 通过原始 SQL 证实缺失，已随迁移基线修复 |
| `VndbApi` 非法 `characters{}` 字段 | 详情请求 400 → `Developer` 恒为 null；已修 |
| 验收程序自身被改坏 | 接线代理改坏后由主对话修复 4 处（缺 `()`、缺 `using`、缺两个 P/Invoke） |

---

## 2. 🔄 进行中（每条线一个独立 worktree，避免 `CS2012 dll 被占用`）

| 工作线 | 目录 / 分支 | 目标 | 验收判据 |
|---|---|---|---|
| **观感 + 数据安全** | `Galbox_v2`（主工作区）/ `wip/2026-04-16-fixes` | W13–W21 + 验收 A11–A18 | 19 项全 PASS |
| **可分发打包攻坚** | `Galbox_verify` | 免安装绿色版（`dotnet publish` 后能跑） | 发布产物双击即起窗口 |
| **存档节点界面** | `Galbox_saveui` / `feat/save-node-ui` | 时间线 / 快照 / 剧情进度 / CG 解锁率 | A10 转 PASS + `PrintWindow` 截图 |
| **测试诚实性** | `Galbox_uitest` / `feat/honest-ui-tests` | W12：把 `tests\Galbox.Tests` 从"假绿灯"改成真检查 | 应用启动冒烟测试**先能复现失败、后能通过** |

### 已交付并复验完成的工作线（已合入 `merge/all-features`）

| 工作线 | 原目录 / 分支 | 判据达成情况 |
|---|---|---|
| 数据层 | `Galbox_data` / `feat/save-node-model` | ✅ 老库快照迁移验证 107/107 |
| 存档解析器 | `Galbox_parser` / `feat/renpy-parser` | ✅ label 22/22、CG 6/27、回滚哈希全等 |
| 补丁安装器 | `Galbox_patch` / `feat/patch-installer` | ✅ 189/189、Zip Slip 被拒、Shift-JIS 文件名正确 |
| 存档节点胶水层 | `Galbox_v2` 内 | ✅ 11/11 |

---

## 3. ⬜ 待办（按依赖顺序，不是按重要性）

### 3.1 接线类（"已经写好但没有门"）

| # | 事项 | 状态 / 证据 |
|---|---|---|
| W1 | DI 生存期修复 —— 全应用共用一个永不释放的 `DbContext`，是"随机红条"的根因 | ✅ **已修**（`AddDbContextFactory` + `ValidateScopes`/`ValidateOnBuild`，8 个 ViewModel 改造） |
| W2 | 补 `ErrorReportPage.xaml` + `ScrapingProgressPage.xaml` + 两个导航 key + 菜单项 | ✅ **已存在**（两页 XAML 已在仓库中，且已进入构建产物） |
| W3 | 给 `PatchCenterPage` 补 `DataContext = ViewModel;` | ✅ **已修** |
| W4 | 启动进程监控：`await processMonitor.StartAsync()` + 为每个游戏 `RegisterGame()` | ✅ **已修**（启动日志：`Process monitor: started=True, registered=1/1`） |
| W5 | 修事件订阅守卫：`_eventsSubscribed = true` → `false` | ✅ **已修** |
| W6 | 修 `SettingsPage.xaml` 未注册的转换器 | ✅ **已修** |
| W7 | 修 `RemoveWindowSubclass` 的同一个 DLL 错误 | ✅ **已修**（已随阻断级修复处理） |

### 3.2 诚实性类（"在说谎、在造假"的功能）

| # | 事项 | 状态 / 证据 |
|---|---|---|
| W8 | 删除假补丁数据生成（`stub_trans_*` 会真写进用户库） | ✅ **已修**（生成代码已删，仅剩说明性注释；用户库 0 条 `stub_trans_*`） |
| W9 | 详情页"创建备份/恢复"改为调真实服务 | ✅ **已修**（`GameDetailViewModel.cs:768/823` 现调用真实 `CreateBackupAsync` / `RestoreBackupAsync`） |
| W10 | 死按钮处理（`ElementName=RootGrid` → `Command` 恒 null） | ✅ **已修**（详情页与补丁中心页已无该绑定） |
| W11 | **26 个装饰性设置项**：要么接线，要么从 UI 移除 | ⬜ **待办（未派发）** —— 需等观感批次改完 `SettingsPage.xaml` 再做，否则冲突 |
| W12 | 纠正测试的"假绿灯" | 🔄 **已派发**（`Galbox_uitest` / `feat/honest-ui-tests`）：6 处 `return true`、零 `ProjectReference`、不在 `.sln` 里 |

### 3.3 产品能力类（用户能直接感知的缺失）—— 🔄 观感批次处理中

| # | 事项 | 证据 | 状态 |
|---|---|---|---|
| W13 | **删除游戏**功能 | 全仓 grep 只有"删除扫描目录设置"，**加错/扫错后永远无法移除** | 🔄 已写 `GameDeletionService.cs` + `A16DeleteGameCheck.cs` |
| W14 | **游戏文件夹丢失检测** | 用户库里唯一那条记录 `SabbatOfTheWitch` 指向的文件夹**已被删除**，界面上仍会显示 | 🔄 已写 `GameInstallationStatus.cs` + `A17MissingFolderCheck.cs` |
| W15 | **图片下载链路** | 刮削写 `CoverImageUrl`，但**全工程无任何下载代码**；UI 绑 `CoverImagePath` → 5 个页面封面永远空白 | 🔄 已写 `GameImageService.cs` + `A15CoverImageDownloadCheck.cs` |
| W16 | 窗口标题 | 只用 TextBlock 自绘，没设 `Window.Title` → 任务栏显示 "WinUI Desktop" | 🔄 已写 `A18WindowTitleCheck.cs` |
| W17 | 把扫描/添加游戏抽成可测试服务 | 逻辑在 `LibraryViewModel`（无法脱离 UI 测试）；**两份不一致实现**（首页版不识别引擎）；扫描只下探一层 | ⬜ **待办** |

### 3.4 数据安全类（可能导致真实损失）—— 🔄 观感批次处理中

| # | 事项 | 证据 | 状态 |
|---|---|---|---|
| W18 | 快速切换补 `currentBackup == null` 短路 | `SaveManagementService.cs:804-810` 缺检查 → 检测失败仍覆盖旧档并**报成功** | 🔄 已写 `A12RestoreRollbackCheck.cs` 等 |
| W19 | 恢复失败分支实现真回滚 | `:519/:534` 的 "Attempting rollback" **只有日志没有代码** | 🔄 已写 `A14RestoreVerificationCheck.cs` |
| W20 | 禁止把 `InstallPath` 当存档目录 | `EngineSaveDetector.cs:645-648` 配合"全删再拷回" → **可能永久损坏游戏安装** | 🔄 已写 `A13InstallRootSavePathCheck.cs` |
| W21 | 完整性校验去掉 20%/10% 容差 | `:563-606` 只比文件数且带容差 | 🔄 同上批次 |

### 3.5 产品灵魂类（差异化所在）

| # | 事项 | 依赖 | 状态 |
|---|---|---|---|
| W22 | **存档节点 UI**：时间线视图 / 快照 / 分支分组 | 依赖数据层 + 解析器 | 🔄 `Galbox_saveui` 处理中（已写 `A10SaveNodeScanCheck.cs`） |
| W23 | **CG 解锁率展示**（6/27=22.2%，并列出还差哪几张） | 依赖数据层 + 解析器 | 🔄 同上 |
| W24 | 剧情进度展示（已解锁场景数 / 总数，**不要用行号百分比**） | 同上 | 🔄 同上 |
| W25 | **补丁中心**：NextMoe API + BYOK + 跳转下载 + 下载文件夹自动接管 + 状态台账 | 依赖数据层（VndbId）+ 安装器引擎 | ⛔ **受产品决策阻塞**（引擎已交付，UI 未接） |
| W26 | 流程图追踪（预留功能） | 需要自建服务端 | ⛔ **受产品决策阻塞** |
| W27 | 社区成就（存档推断 + 内存监控两条路径） | 同上 | ⛔ **受产品决策阻塞** |

### 3.6 收尾类

| # | 事项 | 状态 |
|---|---|---|
| W28 | 打包成**免安装绿色版**（双击即跑，不依赖 .NET 运行时、不需管理员权限） | 🔄 `Galbox_verify` 攻坚中；**保底方案已验证可用**（整目录复制，需 .NET 8 运行时） |
| W29 | 全面回归：重跑验收程序（现为 A0–A18 共 19 项） | ⬜ 待三条线合并后执行 |
| W30 | 文档补齐（README / 用户手册 / 开发者指南）+ 推送到 GitHub | ⬜ 待办 |
| W31 | 合并各分支到主线 | ⬜ 已知冲突点与解法见 `DELIVERY-CHECKLIST.md` |

> **收尾专项行动依据**：`DELIVERY-CHECKLIST.md`（步骤顺序、冲突解法、交付时必须说明的诚实结论）

---

## 3.7 目标差距分析（2026-09-12 00:10 编制）

目标要求的五项能力 vs 当前真实状态。**"有类文件"不等于"能用的功能"**——本表按"用户能不能真的用到"判定。

| 目标要求 | 当前真实状态 | 判定 |
|---|---|---|
| **存档节点**：命名 / 时间线 / 快照 / 分支存档 | 数据层 + 解析器 + 扫描服务已交付并验证（107/107、ALL CHECKS、11/11）；**界面接线进行中**（`feat/save-node-ui`） | 🔄 底层完成，界面在途 |
| **补丁中心（moyu.moe 查询下载安装）** | 引擎完成并验证（189/189）；**界面此前一行都没有**（已在补，`feat/patch-center-ui`）；**moyu.moe 数据源此前从未被调研**（既有 55KB 调研研究的是别的源），专项调研中 | 🔄 三块并行补 |
| **刮削可靠性与四源接入** | **四源 = 2/4 真实**：Bangumi ✅ + VNDB ✅（A5/A6 覆盖）；**ymgal ❌ + cngal ❌ 是诚实的空壳**（代码自述 stub、明确返回失败，不假装能用）→ 已派 `feat/metadata-sources` 补齐 | 🔄 一半完成 |
| **游戏健康诊断的真实修复能力** | 规格定义 8 个诊断项、4 级严重度、4 种修复方式；**历史上 8 项全部标记"不可自动修复"**，且文案未本地化。真正可自动修复的是：**① 中文路径一键重命名目录 ② 兼容模式写注册表** | ⬜ **暂缓派发**（见下） |
| **流程图追踪 / 社区成就** | 只有接口设计，未实现 | ⛔ 需自建服务端，受产品决策阻塞 |

### 3.7.1 健康诊断为何暂缓派发（记录理由，不是遗忘）

两个硬约束：
1. **会直接冲突**：该功能必须改 `GameDetailPage.xaml` / `GameDetailViewModel.cs`，而观感批次（`Galbox_v2` 主工作区，35 个未提交改动）正在改同一批文件。
2. **资源已紧张**：当时有 8 条工作线同时构建，可用内存一度只剩 1 GB。再加线会拖慢全部。

**待观感批次落地后立即派发。** 派发时的范围：
- 新增独立服务（新文件，避免冲突）：路径检测、运行库检测、兼容性检测、修复执行器
- **可自动修复的两项要做成真的能修**：
  - 中文路径 → 重命名目录 + 原子性更新数据库中的 `InstallPath` / `MainExecutable` / `AlternativeExecutables`，失败可回滚
  - 兼容模式 → 写 `HKCU\...\AppCompatFlags\Layers`，**必须提供撤销**
- 其余项给出**可点击的官方下载/工具链接**，并明确标注"需外部工具"或"需手动处理"，**不得放点了没反应的假按钮**
- 8 个诊断项的标题与方案文案**中文化**（规格明确指出这是历史缺陷）
- 补验收检查，**先失败后通过**

---

## 4. ⛔ 被产品决策阻塞的项（已记录在 `OPEN-QUESTIONS.md`）| 决策 | 我的推荐 | 影响 |
|---|---|---|
| 补丁中心密钥模式 | **BYOK**（用户自铸密钥，软件不打包） | 决定 W25 怎么落地 |
| "一键下载"承诺怎么改 | **跳浏览器 + 监听下载文件夹自动接管** | 决定 W25 的交互形态 |
| 玩法状态字段口径 | 恢复 PRD 的 5 值持久化 + 自动推导作默认值 | 数据层已按此实施 |
| 18+ / 破解补丁是否纳入 | 默认不请求，放高级设置 + 年龄确认 | 合规风险 |

---

## 5. 已知的集成风险（合并时要注意）

### 5.0 合并排雷结果（2026-09-11 实测，用 git merge-tree，未触碰工作区）

| 分支 | 合并到主线 | 结果 |
|---|---|---|
| `feat/renpy-parser`（解析器，84315e3） | ✅ **可干净合并** | 无冲突 |
| `feat/save-node-model`（数据层，3995d24） | ⚠️ **1 处冲突** | `src/Galbox.App/App.xaml.cs` |
| `feat/patch-installer`（补丁安装器，1aedbfe） | 待测 | — |

**那处冲突的成因与解法（已明确，不用猜）**：
- **数据层**改了 `App.xaml.cs`：删掉手工 `ALTER TABLE` 补列循环，改为调用 `GalboxDatabaseInitializer`（正式迁移入口）
- **接线批次**也改了 `App.xaml.cs`：修 7 处 `ConfigureAwait`、DI 改 `AddDbContextFactory`/`ValidateOnBuild`、接入 `StartupDiagnostics`、拆出 `PrepareDatabaseAsync`/`InitializeDeferredServicesAsync`
- **解法**：保留接线批次的重构结构（线程亲和性 + DI + 启动诊断），**把其中的"手工补列循环"整段替换为 `await GalboxDatabaseInitializer.InitializeAsync(...)`**。两边的意图不矛盾——数据层本来就是来取代那段手工循环的。

### 5.1 其它


1. **两套 Schema 管理并存**：刮削代理在 `App.xaml.cs` 加了手工 `ALTER TABLE` 补列循环；数据层代理在建正式 EF 迁移。**合并时后者应取代前者**，否则会打架。
2. **`VndbId` 有两处定义**：已在实体与 DbContext 中定义（`[MaxLength(20)]`），数据层生成迁移时**不要重复定义语义不同的同名字段**。
3. **线上用户库已被手工改过**（27 列，含 VndbId）。**迁移验证请用老库快照**：
   `_product\backup\galbox-real-db-before-migration-20260911-230250.db`（26 列，干净）。
4. **四条分支都基于同一个提交 `e320726`**，各自只动不同目录，理论上可干净合并——但 `Galbox.sln` 与 `App.xaml.cs` 有被多方修改的可能，合并时优先人工确认这两个文件。
