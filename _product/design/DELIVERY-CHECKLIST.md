# Galbox 交付清单（Endgame Checklist）

> 编制时间：2026-09-11　｜　维护者：主对话
> 用途：**收尾阶段的唯一行动依据**。三条并行工作线完成后，按本清单逐项执行。

---

## 0. 用户要的三件事（原话）

1. **直接推进并完成现有的 V2 仓库**，自己完成所有测试等一系列工作，并打包好
2. **完成之后，把相关内容补齐，推送到 GitHub 仓库**
3. **最终给我一个完成度非常高、能够运行的 Windows 程序**

---

## 1. 当前状态总览

| 项 | 状态 | 证据 |
|---|---|---|
| 应用能否启动 | ✅ **能** | `MainWindowHandle` 非 0、标题非错误提示；启动日志逐步完整 |
| 验收程序 | ✅ **19 项（A0–A18）** | 10/10 时已跑通；新增 9 项待统一跑一次 |
| 产品灵魂（存档节点） | 🔄 界面接线中 | 数据层 + 解析器 + 扫描服务已交付（107/107、ALL CHECKS、11/11） |
| 补丁引擎 | ✅ 已交付 | 189/189 断言 |
| 刮削链路 | ✅ 已修复并验证 | A5/A6 转 PASS，缓存文件落盘，中文标题正确 |
| 合并 | ✅ **沙箱已验证 10/10** | `merge/all-features` @ `0e051ea` |
| 可分发打包 | ✅ **已解决并独立验证** | 发布产物 2.1 秒弹出真实窗口，`MainWindowHandle=6491884`，自包含 168.5 MB，无需预装 .NET。证据见 `E:\tmp\Galbox_verify\_verify\PACKAGING-VERIFIED.txt` || 观感 + 数据安全 | 🔄 进行中 | 新增 6 个检查 A11–A16 + `GameDeletionService` / `GameImageService` / `GameInstallationStatus` |

---

## 2. 收尾步骤（按顺序）

### 步骤 1：等三条工作线交付
| 工作线 | 目录 | 交付什么 |
|---|---|---|
| 观感 + 数据安全 | `E:\tmp\Galbox_v2` | W13–W21 + 验收 A11–A18 |
| 打包攻坚 | `E:\tmp\Galbox_verify` | 可分发绿色版 + 确切打包命令 |
| 存档节点界面 | `E:\tmp\Galbox_saveui` | 游玩时间线 + A10 |

### 步骤 2：合并到主线
已知冲突与解法：

| 冲突文件 | 成因 | 解法 |
|---|---|---|
| `Galbox.sln` | 各方分别新增项目 | **取并集**（Acceptance / Services / 以及各方新增） |
| `src\Galbox.App\App.xaml.cs` | 数据层改迁移入口；接线批次改 DI/线程；观感批次加注册；UI 批次加 `ISaveNodeScanService` | **保留主线结构 + 逐项合并注册**；手工补列循环替换为 `GalboxDatabaseInitializer` |
| `tests\Galbox.Acceptance\Program.cs` | 两个批次各自注册新检查 | **取并集**（A0–A18 全在） |

**顺序建议**：先合两个"只新增文件"的（冲突最少），最后合改 `App.xaml.cs` 与 `Program.cs` 的。

### 步骤 3：统一跑一次全量验收
- `dotnet build Galbox.sln -c Debug` → **0 错误**
- 跑 `tests\Galbox.Acceptance` → **A0–A18 全部 PASS、退出码 0**
- 把汇总表存档到 `_product\design\acceptance-runs\05-final-all-pass.txt`
  （`04-user-data-integrity.txt` 已存在：用户真实数据完整性验证记录）
- **实机启动一次**，`MainWindowHandle` 非 0 且标题不是错误提示

#### 3.1 验收检查编号分配表（最终版，合并时的关键）

多条工作线并行往同一个 `Program.cs` 检查数组里追加，**编号必须预先分配**，否则会撞号。
（**已实际发生过一次撞号**：观感批次与补丁中心批次都占了 A19，已协调解决。）

| 编号 | 归属分支 | 内容 |
|---|---|---|
| A0–A9 | `merge/all-features`（基线） | 数据库 / 可执行文件 / 文件夹大小 / 存档位置 / 错误检查 / 刮削搜索 / 元数据字段 / 上游契约 / 导航页 / 带缓存启动 |
| **A10** | `feat/save-node-ui` | 存档节点扫描 |
| **A11–A19** | `wip/2026-04-16-fixes`（主 / 观感批次） | A11 快速切换安全 / A12 恢复回滚 / A13 安装根存档路径 / A14 恢复校验 / A15 封面下载 / A16 删除游戏 / A17 文件夹丢失 / A18 窗口标题 / **A19 逐页加载冒烟** |
| **A20–A29** | **保留不用** | 给并发工作线留缓冲 |
| **A30+** | `feat/patch-center-ui` | 补丁中心接线 |
| **A40+** | `feat/metadata-sources` | ymgal / cngal 接入 |

**合并要求**：`Program.cs` 里的检查数组**只做追加**，不得重排或改动 A0–A9 的顺序与内容。

#### 3.2 合并进度与顺序

**已完成合并**（`release/1.0.0` @ `88d9798`，零冲突，19/19 PASS 已独立复验）：
`merge/all-features` · `feat/distributable-packaging` · `docs/delivery` · `wip/2026-04-16-fixes`（观感批次）

**待合并**（编号即建议顺序，冲突从少到多）：
1. `feat/patch-center-ui`（补丁中心界面，A30–A32）
2. `feat/metadata-sources`（ymgal/cngal，A40–A42）
3. `fix/rapid-navigation-crash`（导航崩溃，A50+）
4. `feat/moyu-source`（moyu.moe 合规源，A60+）
5. `feat/save-node-ui`（存档节点界面，A10）
6. `feat/health-diagnosis`（健康诊断，A70+）
7. `feat/honest-ui-tests`（测试诚实性，几乎无冲突）

**预期冲突文件**：`Galbox.sln`、`tests\Galbox.Acceptance\Program.cs`、`src\Galbox.App\App.xaml.cs`、`src\Galbox.App\Galbox.App.csproj`、`src\Galbox.App\Program.cs`

#### 3.3 最高危文件：`src\Galbox.App\App.xaml.cs`

**多方都会改这个文件的 DI 注册区**，是收尾时最可能冲突的地方。已知改动方：

| 来源 | 改了什么 | 状态 |
|---|---|---|
| 观感与数据安全批次 | +18 行：`IGameImageService`（Singleton）、`IGameDeletionService`（Singleton）、命名 HttpClient `"ImageDownload"`；`AutoScrapingService` 构造器 +1 参数 | ✅ 已合并 |
| 补丁中心线 | +12 行：`AddGalboxPatches()` + `ILocalPatchService`（Singleton） | 🔄 在途 |
| 元数据源线 | 有修改（新增 `MetadataSourceOptions` 凭据配置） | 🔄 在途 |
| moyu 源线 / 健康诊断线 | 预计会加 | 🔄 在途 |

**合并原则**：
1. 这些改动**互不矛盾**，都是往 `ConfigureServices` 里追加注册 —— **取并集即可**
2. **必须逐一核对生存期**：本仓库出过 `Cannot consume scoped service from singleton` 导致启动崩溃。新注册若依赖 `DbContext`，**必须走 `IDbContextFactory`**，不能直接注入 `GalboxDbContext`
3. **合并后必须实机启动一次**（`MainWindowHandle` 非 0）。DI 错误只在这里暴露 —— `Program.cs` 里已开 `ValidateScopes=true` / `ValidateOnBuild=true`，容器构建时就会报错

#### 3.4 尚未派发：流程图追踪与社区成就（P2 预留接口）

**目标与规格都要求它们**，但规格明确写的是 **"第一版只预留接口，前端隐藏"**（`Galbox-产品知识总纲.md:173-174`）。

**当前真实状态**：`src/` 全目录搜索 `Achievement|Flowchart|成就|流程图` —— **零命中**。也就是说**连接口都没预留**，比规格要求还缺一层。

**待资源腾出后派发**，范围严格限定为"预留"：
- 定义接口与数据模型（流程图节点/边；成就定义/解锁记录）
- **前端隐藏**：加功能开关，默认关闭，界面上不出现入口
- **不得**实现需要服务端的能力，也不得放"点了没反应"的占位按钮
- 补验收检查，断言"接口存在"且"前端默认隐藏"

> 记录理由（不是遗忘）：当前已有 8 条工作线并行，可用内存约 1 GB。此项是 P2 最低优先级，**等前面的线落地再派**，避免把资源摊得更薄。

### 步骤 4：打包 ✅ 技术路径已打通
- **正式方案**（已独立验证）：`dotnet publish` 产物，**自包含 168.5 MB，用户机器无需预装 .NET**
- 打包代理的根因定位与修复见 `E:\tmp\Galbox_verify\_verify\PACKAGING-VERIFIED.txt`
- 一条命令产出，不再需要手工补文件
- 另有框架依赖变体（78 个文件，体积更小但需预装 .NET 8 桌面运行时）

#### 4.4 交付产物的落点（已定，收尾时按此执行）

| 产物 | 位置 | 说明 |
|---|---|---|
| 解压即用的程序目录 | `C:\Users\Jiang\Desktop\Tmp\dsh启动地址\Galbox-1.0.0-win-x64\` | 用户双击 `Galbox.App.exe` 即可运行 |
| 分发包（压缩） | `C:\Users\Jiang\Desktop\Tmp\dsh启动地址\Galbox-1.0.0-win-x64.zip` | 便于分发与备份 |
| 源码 | GitHub `VinceJan/Galbox_v2` | — |

**选这个位置的理由**：它是本次会话的工作目录，用户回头就能看到，不必在 `E:\tmp` 的一堆临时目录里翻找。

写清 README：解压到任意位置（**建议非中文路径**）、双击 `Galbox.App.exe`、首次运行会创建 `%LocalAppData%\Galbox\` 数据目录、无需安装、无需管理员权限、无需预装 .NET。

#### 4.1 已实测的关键对照（2026-09-11 深夜，主对话亲测）

**能正常运行的构建产物长这样**（`src\Galbox.App\bin\Debug\...\win-x64\`，144 个文件）：

| 文件 | 状态 | 含义 |
|---|---|---|
| `resources.pri` | **不存在** | XAML 不是从 PRI 加载的 |
| `*.xbf` | **10 个，散放在 exe 旁** | XAML 从散放 `.xbf` 加载 |
| `Microsoft.WindowsAppRuntime.Bootstrap.dll` | **存在** | 走 Bootstrap 自举 |
| `Microsoft.ui.xaml.dll` | **不存在** | 用的是系统里已装的运行时 |
| `WindowsAppRuntime*` 框架文件 | **不存在** | 同上 |

**→ 结论：能跑的形态是「框架依赖 + 散放 .xbf + Bootstrap.dll」，且根本没有 `resources.pri`。**
**→ 推论：自包含 / 手工造 PRI 属于"难路径"，可能在解决一个不存在的问题。应先验证最朴素的框架依赖发布。**

#### 4.2 本机环境实测（决定交付形态）

| 项 | 实测值 | 含义 |
|---|---|---|
| Windows App Runtime **1.6** | ✅ 已装（`6000.519.329.0`） | 正是 csproj 所需版本 |
| 其它 Runtime 版本 | 1.1–1.8、**以及 2.2/2.3/2.4** | ⚠️ 两代并存，是可疑点 |
| .NET 8 桌面运行时 | ✅ 已装（8.0.13 / 8.0.25 / 8.0.31） | 框架依赖方案在本机可验证 |
| VC++ v14 Redistributable | ✅ x64 + x86 均已装 | WinUI 3 依赖满足 |
| OS | Windows NT 10.0.26200.0 / AMD64 | — |

**→ 交付形态判定：若最终只能框架依赖，"本机可跑"是可以确认的；代价是用户机器需预装 .NET 8 桌面运行时（Windows App Runtime 1.6 通常由应用首次运行自动引导安装）。这必须写进给用户的说明里。**

#### 4.3 打包成果的保管要求（已发现的风险）
`Galbox_verify` 曾长期处于 **detached HEAD + 97 个未提交改动**状态。已要求代理落到具名分支 `feat/distributable-packaging` 并提交。**合并前必须确认该分支存在且包含完整成果。**

### 步骤 5：文档补齐| 文件 | 内容 |
|---|---|
| `README.md` | 产品简介 + 怎么装怎么用 + **怎么跑验收程序** |
| `docs\USER_MANUAL.md` | 面向用户：每个功能怎么用（已有旧版，需按新功能更新） |
| `docs\DEVELOPER_GUIDE.md` | 架构 + 怎么加数据源 + **迁移机制怎么用** + 验收程序怎么扩 |
| `CHANGELOG.md` | 本次修复清单（可用 `defect-postmortems.md` 作素材） |

### 步骤 6：推送到 GitHub
- 远端：`https://github.com/VinceJan/Galbox_v2.git`（**公开仓库**）
- ⚠️ **推送前必须检查**：`_product\.gitignore` 是否仍排除私人内容
  （`claude-code-输入历史.txt`、`HANDOFF.md`、`OPEN-QUESTIONS.md`、`backup\`）
- 推送 `main`（或把 `merge/all-features` 作为新的 main）

### 步骤 7：清理 `E:\tmp`
| 保留 | 删除 |
|---|---|
| `Galbox_v2`（主线仓库）、`Galbox_v3`（用户的重写仓库） | 635 MB 临时产物 + 各 worktree（用 `git worktree remove` 注销后再删） |

预计腾出 **约 1.2 GB**。

---

## 3. 交付时必须说清楚的"诚实结论"

不要只报喜。交付时应当同时说明：

| 项 | 诚实说明 |
|---|---|
| 引擎支持 | 存档解析**只验证过 Ren'Py 7.4.11 / Python 2.7**；Tyrano、KiriKiri 未实现 |
| 分支分组 | **只能标"疑似"**——存档里读不到路线变量（Ren'Py 只序列化被改动过的变量） |
| 章节进度 | 游戏**没有章节概念**，用的是"已解锁场景数 / 总场景数" |
| ymgal / cngal | 仍是空壳（未接真实 API） |
| 补丁下载 | 官方 API **不给下载直链**，只能跳浏览器；程序化下载被契约禁止 |
| 流程图 / 成就 | 只有接口设计，**未实现**（需要自建服务端） |
| 打包 | 若只能用保底方案，需说明"需要 .NET 8 桌面运行时" |
| 待用户拍板的项 | `OPEN-QUESTIONS.md` 里的 E 节（密钥模式、一键下载承诺、游戏目录里的 `.galbox`、`PatchStatus` 命名冲突） |

---

## 4. 不要做的事

- ❌ 不要为了让验收变绿而注释掉检查
- ❌ 不要在文档里写"完整实现"而实际是占位（这正是旧文档的毛病）
- ❌ 不要把"编译通过"当成完成
- ❌ 不要删除用户数据文件（`%LocalAppData%\Galbox\` 里的一切）
- ❌ 不要把私人内容推进公开仓库
