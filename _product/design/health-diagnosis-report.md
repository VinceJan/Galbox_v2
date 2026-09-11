# Galbox 游戏健康诊断：真实修复能力 + 文案中文化（交付报告）

工作区：`E:\tmp\Galbox_health`（`feat/health-diagnosis`，基于 `release/1.0.0` @ `88d9798`）
验收：`A70–A74`，原始输出见 `_product/design/acceptance-runs/09…11` 与同目录的 `README-health-diagnosis.md`。

---

## 1. 结论先行：8 个诊断项现在的分级

| # | 诊断项 | 严重度 | 解决方式 | 真实自动修复能力 | 落地形式 |
|---|---|---|---|---|---|
| 1 | 路径含中文字符 | Major | **Auto Fix** | ✅ **真的能修** | 一键把游戏目录改成 ASCII 名，同步更新数据库三处路径，失败回滚，可撤销 |
| 2 | 区域设置要求 | Critical | External Tool | ❌ 不修 | Locale Emulator 官方地址（github.com/xupefei/Locale-Emulator） |
| 3 | DirectX 缺失 | Major | External Tool | ❌ 不修 | 微软 DirectX End-User Runtime 官方页 |
| 4 | 视频解码器缺失 | Minor | External Tool | ❌ 不修 | K-Lite Codec Pack 官方页 |
| 5 | Windows 兼容性 | Critical | **Auto Fix** | ✅ **真的能修** | 写 HKCU 兼容性层（`~ WIN7RTM`），可撤销、会还原用户旧值 |
| 6 | 运行库缺失 | Critical | External Tool | ❌ 不修 | .NET Framework / VC++ Redistributable / Adoptium 官方页 |
| 7 | 权限问题 | Major | Manual Fix | ❌ 不修 | 管理员运行，或移出 Program Files |
| 8 | 杀软拦截 | Info | Manual Fix | ❌ 不修 | 把游戏目录加入杀软排除列表 |

**真正能自动修复的只有 2 项**（中文路径、Windows 兼容性）。这正是规格 §3.5「一键重命名目录」和 §4.4「兼容模式运行」明确承诺过、而仓库里从来没有实现的两项。

**另外两条由我独立判断、并已落进代码的调整：**

- **中文出现在上层目录时，降级为 Manual Fix。** 一键重命名只能改游戏自己的目录；如果中文在 `D:\游戏\` 这一层，重命名游戏目录解决不了问题，此时仍然显示「一键修复」就是这个项目历史上最典型的缺陷（有按钮没有门）。检测端因此会区分这两种情况，只在前者可修时才给按钮。
- **已设置兼容层的游戏不再重复报出该项。** 否则用户点完一键修复，列表里仍然挂着一条 Critical，按钮再点一次什么也不会发生。`ErrorCheckingService` 现在会先问 `IsCompatibilityModeApplied`。

---

## 2. 改动文件清单与职责

### 新增（生产代码）

| 文件 | 职责 |
|---|---|
| `src/Galbox.App/Services/IGameHealthFixService.cs` | 修复能力契约：`CanAutoFix`、重命名/撤销、写兼容层/撤销、`IsCompatibilityModeApplied` |
| `src/Galbox.App/Services/GameHealthFixService.cs` | 唯一实现。重命名（含冲突检测、进程占用检测、数据库提交与回滚）、HKCU 兼容层写入/撤销/旧值还原、改名时迁移已有兼容层、撤销日志 |
| `src/Galbox.App/Services/GamePathNaming.cs` | 中文检测、路径分段分析、ASCII 名生成（词表 → 拼音 → `u+码点` 兜底）、文件名净化 |
| `src/Galbox.App/Services/GameHealthFixJournal.cs` | 撤销日志（`%LocalAppData%\Galbox\health-fixes\applied-fixes.json`），使撤销在重启后仍然可用 |
| `src/Galbox.App/Models/GameHealthFixModels.cs` | `GameHealthFixResult` / `GameHealthFixKind` / `AutoFixBatchResult` |
| `src/Galbox.App/Models/DiagnosisText.cs` | 严重度 / 分类 / 解决方式的**唯一**中文显示名表（含数据库里存的枚举名字符串） |

### 修改

| 文件 | 改动 |
|---|---|
| `src/Galbox.App/Models/GameErrorInfo.cs` | 8 个工厂全部改中文；分级落定（2 个 AutoFix + 4 个 ExternalTool + 2 个 ManualFix）；中文路径工厂新增 `canAutoFix`；`SeverityDisplay`/`CategoryDisplay`/`SolutionTypeDisplay` 委托给 `DiagnosisText` |
| `src/Galbox.App/Services/ErrorCheckingService.cs` | 注入修复服务；`AttemptAutoFixAsync` 按类别分派到真实修复、其余类别如实返回失败；`XpVistaIndicatorPattern` 裸子串 `xp` 改词边界（修掉 Expansion 误报）；已设兼容层不再报出；中文路径区分「本目录/上层目录」；删除恒返回失败的 `FixPermissionIssueAsync` 死代码 |
| `src/Galbox.App/Services/IErrorCheckingService.cs` | `AutoFixResult` 增加 `Fix`（携带改了什么、旧值是什么、能否撤销） |
| `src/Galbox.App/ViewModels/ErrorReportViewModel.cs` | 一键修复/批量修复/撤销三个命令、`AppliedFixes`、按钮文案、筛选与状态文案中文化、历史记录行 `AutoFixAvailable` 由 `SolutionType` 推导、**修复后按 Id 重读游戏行再复检** |
| `src/Galbox.App/Views/ErrorReportPage.xaml(.cs)` | 「一键修复」只对可自动修复项出现；「一键修复所有可自动修复的问题（N 项）」；修复详情 + 逐条「撤销这次修复」；严重度/分类/方案中文化 |
| `src/Galbox.App/Converters/CommonConverters.cs` | 三个显示名转换器，全部复用 `DiagnosisText` |
| `src/Galbox.App/App.xaml.cs` | 注册 `IGameHealthFixService` |
| `tests/Galbox.Acceptance/AcceptanceContainer.cs` | 注册修复服务，并把撤销日志指向本次运行的隔离目录 |
| `tests/Galbox.Acceptance/ReflectionBridge.cs` | 增加 `Strings`（读字符串集合属性） |
| `tests/Galbox.Acceptance/Program.cs` | 只追加 A70–A74，不动既有项 |

### 新增（验收）

`A70HealthDiagnosisCheck.cs`、`A71ChinesePathRenameCheck.cs`、`A72CompatibilityModeCheck.cs`、`A73AutoFixHonestyCheck.cs`、`A74DiagnosisLocalizationCheck.cs`、`HealthCheckSupport.cs`。

---

## 3. 基线 FAIL → 实现后 PASS（原始命令与输出）

```powershell
cd E:\tmp\Galbox_final
git worktree add E:\tmp\Galbox_health -b feat/health-diagnosis release/1.0.0

cd E:\tmp\Galbox_health
dotnet build tests\Galbox.Acceptance\Galbox.Acceptance.csproj -c Debug
tests\Galbox.Acceptance\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.Acceptance.exe --timeout 600
```

### 基线（`src/` 回退到实现前、检查代码为最终版）

```
 PASSED : 18
 FAILED : 6
 ERRORS : 0
 TOTAL  : 24
   A70  FAIL   健康诊断：按触发条件准确报出 8 项、严重度符合规格、且不误报
   A71  FAIL   中文路径一键重命名：目录真的改名、数据库三个路径字段同步、失败可回滚
   A72  FAIL   Windows 兼容性一键写入：HKCU 兼容性层真的写入、且撤销后真的删除
   A73  FAIL   自动修复不虚标：标为可自动修复的项真的能修，其余项不得谎报成功
   A74  FAIL   诊断文案中文化：标题、说明、方案、分级与持久化记录均无英文残留
   A9   FAIL   （环境噪声，与本次改动无关，见 README-health-diagnosis.md）
 EXIT CODE: 1
```

基线的具体理由（原始输出节选）：

```
[FAIL] 误报 fixture（游戏名 Expansion，readme 无 OS 要求）
       类别=WindowsCompatibility 期望=无 实测=Critical
       Title="Windows Compatibility Issue" SolutionType=ManualFix AutoFix=False
[FAIL] 路径含中文字符   规格=AutoFix 实测=ManualFix   AutoFixAvailable=False
[FAIL] Windows 兼容性   规格=AutoFix 实测=ManualFix   AutoFixAvailable=False
FAIL REASON: 没有 IGameHealthFixService，兼容模式只能靠用户自己右键 → 属性 → 兼容性手工设置。
A74 ACTUAL: 63 处文案未通过（Title="Chinese Characters in Path" …）
```

### 实现后（最终输出）

```
 PASSED : 23
 FAILED : 1
 ERRORS : 0
 TOTAL  : 24
   A70  PASS   健康诊断：按触发条件准确报出 8 项、严重度符合规格、且不误报
   A71  PASS   中文路径一键重命名：目录真的改名、数据库三个路径字段同步、失败可回滚
   A72  PASS   Windows 兼容性一键写入：HKCU 兼容性层真的写入、且撤销后真的删除
   A73  PASS   自动修复不虚标：标为可自动修复的项真的能修，其余项不得谎报成功
   A74  PASS   诊断文案中文化：标题、说明、方案、分级与持久化记录均无英文残留
   A9   FAIL   （唯一失败项：同机其它 worktree 并发跑同一套验收导致的环境噪声）
 EXIT CODE: 1
```

过程中还记录到一次 **24/24、退出码 0** 的整套全绿运行（`11-…-24of24-intermediate.txt`）。

---

## 4. 自动修复的真实证据

### 4.1 中文路径 → 目录改名 + 数据库同步（A71 用例 1，走 `AttemptAutoFixAsync`，即界面按钮的调用）

```
诊断结论 : "游戏路径包含中文字符" 严重度=Major AutoFix=True 方案=AutoFix
改名前 InstallPath    : …\a71-…\case1\中文游戏目录
改名前 MainExecutable : …\case1\中文游戏目录\probe.exe
改名前 AlternativeExe : …\中文游戏目录\probe64.exe|…\中文游戏目录\probe.exe
改名前 文档路径        : …\中文游戏目录\readme.txt
改名前 CoverImagePath : …\中文游戏目录\cover.png
Message : 已把游戏目录改名为「Chinese-Game-Directory」，数据库路径已同步。
--- 改名后实测 ---
目录            : …\case1\Chinese-Game-Directory  (存在=True)
旧目录是否仍在   : False
新目录名        : Chinese-Game-Directory  纯 ASCII=True
新 InstallPath  : …\case1\Chinese-Game-Directory
新 MainExecutable: …\Chinese-Game-Directory\probe.exe   （文件存在=True）
新 AlternativeExe: …\Chinese-Game-Directory\probe64.exe|…\Chinese-Game-Directory\probe.exe
新 文档路径      : …\Chinese-Game-Directory\readme.txt
新 CoverImagePath: …\Chinese-Game-Directory\cover.png
复诊仍报中文路径 : False
```

### 4.2 失败可回滚（A71 用例 2：另一个游戏行已声明目标路径）

```
Success          : False
RolledBack       : True
Message          : 数据库更新失败（另一个游戏记录（Id=10「A71 占位行」）已经声明了目标路径 …），目录已回滚到原状，未做任何修改。
回滚后原目录存在 : True
回滚后新目录存在 : False
回滚后 InstallPath    : …\case2\中文游戏目录
回滚后 MainExecutable : …\case2\中文游戏目录\probe.exe
服务日志 [Error] Galbox.App.Services.GameHealthFixService: Committing the rename of game 9 failed; rolling the folder back
```

### 4.3 其它拒绝路径（A71 用例 3/4/5）

```
用例 3 目标目录已存在 → Success=False「目标目录已存在…本工具不会动它」，原目录与其中无关文件均未变、数据库未变
用例 4 游戏进程运行中 → Success=False「检测到游戏进程 Galbox.Acceptance 正在运行，已取消重命名」，目录仍在原处
用例 5 撤销           → 撤销后 InstallPath 回到 …\case5\中文游戏目录，目录存在=True，复诊重新报出中文路径=True
```

### 4.4 兼容模式写入与撤销（A72，真实注册表）

```
用例 1 写入前 : (不存在) → 写入后 : ~ WIN7RTM
       撤销后 : (不存在)；Layers 键仍存在 = True（没有删掉别的应用的设置）
用例 2 用户原有 ~ WINXPSP3 → 写入 ~ WIN7RTM（PreviousValue=~ WINXPSP3）→ 撤销后回到 ~ WINXPSP3
用例 3 主程序不存在 → Success=False，注册表未被写
用例 5 改名前的层设置 …\中文游戏目录\probe.exe=~ WINXPSP3
       → 旧路径上的值=(已删除)、新路径 …\Chinese-Game-Directory\probe.exe=~ WINXPSP3
用例 4 源码反证：不含 Registry.LocalMachine / ClassesRoot / Users；使用 Registry.CurrentUser
```

### 4.5 不虚标（A73）

```
分级全部与规格表一致；AutoFixAvailable 当且仅当 SolutionType=AutoFix
6 个非自动修复项调用修复接口一律 Success=False 并给出中文说明；
被强行标成 AutoFixAvailable=true 的 DirectX 项同样拒绝，不谎报成功
用例 3：ChineseDirectory 修复 Success=True；WindowsCompatibility 修复 Success=True；
        修复后复诊「仍报出的可自动修复项：无」
用例 4（真实 ViewModel + 真实服务，即界面上的批量按钮）：
        按钮文案 = "一键修复所有可自动修复的问题（2 项）"
        FixedCount=2，FixedItems=游戏路径包含中文字符 / 需要 Windows 兼容模式
        ManualItems=可能被安全软件拦截，FailedItems 为空
        原名目录仍在=False；改名后主程序存在=True；注册表兼容性层=~ WIN7RTM
```

### 4.6 撤销日志（真实文件内容，节选）

```json
{ "Kind": "RenameInstallPath", "GameId": 8,
  "OldPath": "…\\work\\a71-30688\\case1\\\u4E2D\u6587\u6E38\u620F\u76EE\u5F55",
  "NewPath": "…\\work\\a71-30688\\case1\\Chinese-Game-Directory" },
{ "Kind": "WindowsCompatibility", "GameId": 18,
  "ExecutablePath": "…\\a73-30688\\Chinese-Game-Directory\\probe.exe",
  "AppliedValue": "~ WIN7RTM" }
```

### 4.7 没有碰真实用户数据

- `%LocalAppData%\Galbox\galbox.db`：**内容未变**（192512 字节，与开工前一致；应用启动日志为 `mode=AlreadyUpToDate, applied=0`）。A9/A18/A19 本来就要拉起真实 `Galbox.App.exe`，它打开真实库只做初始化检查；mtime 变化来自 SQLite 打开 WAL 模式时生成的 `-wal(0 字节)/-shm` 边车文件，不是内容写入。
- `%LocalAppData%\Galbox\health-fixes`：**不存在**——验收跑动把撤销日志写进 `acceptance\run-<pid>\health-fixes-<pid>`。
- `D:\GAME\Dreamin'_Her`：mtime 仍是 2026-04-08，未改动。
- 注册表：`HKCU\…\AppCompatFlags\Layers` 下没有任何指向 `acceptance\work` 的残留（写入的都是本次运行自己创建的临时 exe，验证后已删除；空键也在原本不存在时删掉了）。

### 4.8 过程中被验收检查抓出的两个真实缺陷

1. **误报**：`XpVistaIndicatorPattern` 用裸子串 `xp` 匹配，游戏名 `Expansion Pack Probe` 被报成 **Critical 的 Windows 兼容性问题**（A70 抓出）。
2. **修复后复检用旧路径**：`CheckGameErrorsAsync` 用的是列表传进来的旧 `GameInfo`，重命名之后它的 `InstallPath` 还是中文路径，一次成功的修复被复检显示成「仍然有问题」（A73 用例 4 抓出）。已改为先按 Id 从数据库重读再检测。

---

## 5. 没做到的部分、降级为手动的项、以及只是「绕过」的地方

**明确没做的自动修复（都是有理由的，不是漏做）：**

| 项 | 为什么不做成自动修复 |
|---|---|
| 区域设置要求 | 每个进程临时改区域必须挂钩进程加载（Locale Emulator / NTLEA / AppLocale 一类工具），Galbox 无法在不开挂的情况下做到；改 `HKCU\Control Panel\International` 是全局的，会连带影响用户其它程序。工具未安装时给它写一个「一键配置」就是假按钮。 |
| DirectX / K-Lite / 运行库 | 自动修复意味着**下载并静默运行第三方安装包**，部分还需要管理员权限、会改系统组件。这与「不要静默动用户系统」冲突，失败也无法可靠回滚。给官方下载页是诚实的边界。 |
| 权限问题 | 需要的动作是移动游戏目录或改写 ACL，属于系统级改动，当前进程连 Program Files 都写不进去（实测 Medium 完整性/非提升）。 |
| 杀软拦截 | 各家杀软没有稳定公开的排除项接口，Windows 安全中心需要提升权限且接口未公开。 |
| 缺游戏专属依赖 | 规格 §4.4 自己写明「只有分类、没有实现」，我没有为它造检测。 |

**降低期望 / 绕过（如实说明）：**

- **拼音表不是完整的**。词表 + 约 500 个高频汉字 + `u+码点` 兜底。生僻字会转成 `u946b` 这种形式，并在修复详情里明确告诉用户「有 N 个汉字不在拼音表中，请自行确认」——这比猜一个错的读音诚实。
- **目标名冲突是「拒绝」而不是「自动加后缀」**。冲突时给出明确原因让用户决定，不会悄悄改成 `名字-2`；这也意味着两个同名中文游戏不能连续一键修复。
- **重命名只处理游戏自己的目录（叶子）**。中文在上层目录时降级为 Manual Fix，只给「移到纯英文路径」的说明。
- **`A71` 的回滚用例是用真实冲突触发的**，不是注入故障：提交阶段的冲突检查是有意放在提交时（`CommitRelocationAsync` 内、读新行之后），因为预检和提交之间存在竞态，只有提交时的检查是权威的；「另一个游戏行已声明目标路径」是真实会发生的场景（用户手动改过名、上次修复中断）。
- **`A72` 的「只写 HKCU」是运行时 + 源码级双重证明**：值本身读写得已验证，但「没有写别处」无法用运行时枚举证明，所以额外断言修复服务源码里不存在 `LocalMachine`/`ClassesRoot`/`Users` 访问。
- **A9 我没修**。它因为同机其它 worktree 并发跑同一套验收而稳定失败（根因是它一看到窗口句柄就 break，而 `startup sequence completed` 那行还差约 18 ms 落盘；另外它读的是全机共享的按天日志）。它在**实现前的 `src/` 上以完全相同的方式失败**，不是本次回归。修法写在 `README-health-diagnosis.md` 里，但改别人的检查文件超出我的任务范围，我没有动。
- **我没有做界面外观的截图验证**。A19 证明 `ErrorReportPage` 能在真实应用里加载（我的 XAML 改动会让它编译失败或被 A19 抓到），A73 用例 4 证明批量修复按钮背后的逻辑真的生效，但我没有肉眼看渲染出来的页面。

---

## 6. 诚实评估：这个功能现在能不能真正帮到用户

**能，但只对 2/8 的问题，而且这 2 项恰好是最高频、最烦人的那一类。**

用户视角现在会发生的事：

- 加进来的日文游戏在中文目录里 → 检测报出「游戏路径包含中文字符 / 重要」，点一次「一键修复」，目录被改成英文名，库里三处路径和目录内的文档/截图/存档备份引用一起跟着走；点「撤销」可以改回去。**这一条是真的省事，因为手工做要改名 + 回来改库里的路径，很容易漏。**
- XP 时代的老游戏 → 报「需要 Windows 兼容模式 / 严重」，一键写入 HKCU 兼容层，不用再右键→属性→兼容性→确定；而且如果之前手工设过 XP SP3，撤销会还原成 XP SP3 而不是一删了之。
- 其它 6 项，用户看到的是一条**中文**的、说明「为什么不能自动修」的条目 + 一个真的能打开的官方下载页或可执行的手工步骤。这比一个点了没反应的按钮有价值得多，但它终究只是「把该做的事说清楚」。

**仍然要清楚的风险与短板：**

1. **改名只覆盖叶子目录**。真实用户把游戏放在 `D:\游戏\` 这种顶层中文目录下是很常见的，这种情况下第一次点击会得到一个「做不到」的中文说明——正确，但用户会觉得「还是得自己动手」。
2. **生僻字转写会难看**（`u4e2d`），需要用户自己改名。词表/拼音表的覆盖率决定了体验的上限。
3. **撤销依赖日志文件**。日志被删（或换了机器/换了用户配置）之后，改名撤销会明确告知做不到；兼容模式撤销会退化为「只删除与本工具写入值相同的项」，绝不会误删用户手工设的值——这是有意的保守选择。
4. **两处修复都只影响本用户**（HKCU、用户目录），换 Windows 用户后兼容模式设置不在。这是刻意的取舍（不写 HKLM、不要管理员）。
5. **诊断本身仍是启发式**：解码器/DirectX/运行库三项依赖「游戏目录里有没有对应的 DLL / 视频文件」，装了 K-Lite 就不再报解码器项。它能减少瞎猜，但不能保证准确。

一句话：**从「8 项全是空话」变成「2 项真的动手、6 项说清楚且给到官方出口」**。这次改动把历史上那类「有按钮没有门」的缺陷在健康诊断这块关掉了，并且用真实文件系统、真实注册表、真实 SQLite 的断言把它锁住——但覆盖范围只有 2 项，凡是需要安装软件或系统级权限的问题，产品仍然只能把用户送到官方页面，这一点没有变、也不应该假装变了。
