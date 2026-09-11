# 本机补丁安装器（Galbox.Core.Patches）

纯本地引擎：**不联网、不调 API、不碰数据库、不依赖 UI**。
输入是「用户已经下载好的补丁包 + 一个游戏目录」，输出是可序列化的对象。

这一层存在的理由见 `_product/design/patch-source-research.md` §5：
官方 API 契约层不给下载直链，所以产品价值全部落在「装得对、装得可追溯、装错了能原样回去」。

---

## 30 秒上手

```csharp
var options = new PatchInstallerOptions();          // 默认值已经是最保守的
var engine  = new PatchInstaller(options);

// 1) 预览：解压到沙箱 + 算出每个条目的最终落点，什么都不写进游戏目录
var preview = await engine.PreviewAsync(new PatchPreviewRequest
{
    ArchivePath = @"D:\Downloads\汉化补丁.rar",
    GameRoot    = @"E:\Games\SomeGalgame",
    GameId      = "3",                               // 备份目录用它分文件夹
    PatchName   = "某种汉化补丁 v1.1",
    DeclaredTypes = new[] { "manual" },              // moyu 的 type[]；含 "save" 会被直接拒绝
    KnownOriginalHashes = knownOriginalsFromDb       // 可选：来自 DB 的「原版文件哈希表」
});

// 2) 让用户确认冲突（preview.Entries 里 Action == Conflict 的那些）
var result = await engine.InstallAsync(new PatchInstallRequest
{
    Preview   = preview,
    Decisions = new PatchInstallDecisions { ConfirmedConflicts = userApprovedPaths }
});

// 3) 状态：只有 Galbox 自己的台账 + 哈希复核才能说「已安装」
var status = await engine.GetStatusAsync(new PatchStatusRequest { GameRoot = gameRoot, GameId = "3" });

// 4) 回滚：恢复被覆盖的文件 + 删除本次新增的文件，最后用哈希证明一致
var undo = await engine.RollbackAsync(new PatchRollbackRequest { GameRoot = gameRoot, GameId = "3" });
```

也可用 `services.AddGalboxPatches()` 注册 `IPatchEngine` 单例。

---

## 调用地图

| 想做什么 | 调什么 | 返回 |
|---|---|---|
| 只识别包，不解压 | `InspectAsync(path)` | `PatchArchiveInfo`（格式、条目数、编码、是否 SFX/加密） |
| 只看会覆盖什么 | `PreviewAsync(request)` | `OverwritePreview`（逐条最终落点 + 新增/覆盖/冲突分级） |
| 不想处理异常 | `TryPreviewAsync(request)` | `PatchPreviewAttempt`（被拒时带 `RejectionCode` + `Remedy`） |
| 正式安装 | `InstallAsync(request)` | `PatchInstallResult`（含 `PatchManifest`、逐文件结果、保留策略建议） |
| 判定装没装 | `GetStatusAsync(request)` | `PatchStatusReport`（`PatchStatus` + `PatchEvidenceClass`） |
| 回滚 | `RollbackAsync(request)` | `PatchRollbackResult`（`ByteIdenticalToPreInstall`） |
| 断电后恢复 | `FindInterruptedAsync(gameRoot)` → `RecoverAsync(plan)` | `PatchRecoveryPlan` / `PatchRollbackResult` |
| 清理旧备份 | `Backups.PlanRetention(...)` → `ApplyRetention(...)` | `PatchRetentionPlan`（**只建议，不自动删**） |
| 有没有更新 | `PatchUpdateAdvisor.Assess(providerUpdatedAt, manifest)` | `PatchUpdateAssessment`（弱判断，措辞是「可能有更新」） |

---

## 状态判定：事实 vs 推测

`PatchStatusReport` 同时带 `Evidence`（`Fact` / `Inference` / `None`）和 `EvidenceScope`。

| PatchStatus | 证据 | 判定规则 |
|---|---|---|
| `Installed` | **Fact** | 有 committed manifest，且 manifest 里每个文件的 `hashAfter` 与磁盘现况**全部相等** |
| `ModifiedSinceInstall` | **Fact** | 有 committed manifest，但部分文件哈希不符或缺失（游戏更新 / 别的补丁 / 杀软） |
| `Interrupted` | **Fact** | 存在 status=pending（或 failed）的 journal |
| `RolledBack` | **Fact** | 最新安装的 manifest 已标记回滚 |
| `NotInstalled` | **Fact**（范围仅限 Galbox 台账） | 无 manifest 且启发式扫描无命中 |
| `Suspected` | **Inference** | 无 manifest，但扫到 `patch.xp3` / `汉化说明.txt` / `BepInEx` 等第三方痕迹 |
| `Unknown` | None | 目录不可读、台账损坏、installId 不存在 |
| `NotApplicable` | Fact（来源=包元数据） | 调用方声明了 `type=save` |

`PatchStatusRules.IsCompatible` 是硬约束，`PatchStatusEvaluator` 在返回前会用 `Assert()` 校验：
**推测永远不可能被包装成 `Installed`**。UI 请把 `Explanation` 原样展示，它已经写好了免责说明。

> ⚠️ 注意命名冲突：`Galbox.Data.Entities.PatchStatus` 已存在（那是「可下载/下载中/已安装」的生命周期枚举）。
> 本引擎的 `Galbox.Core.Patches.PatchStatus` 是**安装事实的判定结果**，两者语义不同。
> 同时 using 两个命名空间的文件需要写全限定名。

---

## 落盘位置

```
%LOCALAPPDATA%\Galbox\patchbak\<gameId>\<installId>\
    ├─ manifest.json          ← 台账（权威记录，先写后做）
    └─ files\...              ← 仅「将被覆盖的文件」的备份，镜像相对路径

<gameRoot>\.galbox\patch-manifest.json   ← 游戏目录内的副本（该目录全部安装的清单集合）
```

* 备份根可用 `PatchInstallerOptions.BackupRoot` 换到别的盘；**放进游戏目录内会被直接拒绝**。
* manifest 的对象是 `PatchManifest`，`System.Text.Json` 可序列化 —— DB 工作线直接存它即可，本引擎不去动实体。
* 沙箱默认 `<TEMP>\Galbox\patchsink\<previewId>\`，安装成功后自动清理（`KeepSandboxAfterInstall` 可关）。
  预览与安装可以跨进程传递：`PatchInstaller.LoadPreview(sandboxRoot)` 能读回 `preview.json`。

---

## 安全边界（都在代码里，不靠调用方自觉）

1. 归档先完整解压到沙箱，**绝不边解压边覆盖**。
2. `..\` 逃逸 / 绝对路径 / 盘符 / UNC 条目 → 记录在 `OverwritePreview.Rejected`，永不落盘。
3. 符号链接条目、目标链路里的 reparse point → 拒绝。
4. 保留名（CON/PRN/AUX/NUL/COM1-9/LPT1-9）、非法字符、结尾点空格 → 确定性改名并在预览里明示。
5. 长路径统一走 `\\?\` 前缀。
6. 自解压 exe 只识别、提示「在隔离目录手动运行」，**永不执行、永不自动解压**。
7. `type` 含 `save` → 硬拒绝；内容嗅探像存档包 → 拒绝（可用 `AllowSaveLikeContent` 显式放行）。
8. 冲突三级：新增 / 覆盖（原版或 Galbox 写过）/ 冲突（来历不明，必须用户确认）。
9. 预览到安装之间会二次校验沙箱文件哈希与目标现况，防止「预览后被改动」。
10. 写盘后回读校验，抓杀软静默拦截。

---

## 验证

```powershell
dotnet run --project tools\Galbox.PatchVerifier -c Release -- --scratch E:\tmp\_galbox_patch_scratch
```

`tools/Galbox.PatchVerifier` 会造一个假游戏目录 + 一堆恶劣压缩包，逐条打印原始证据
（预览清单、备份树、前后哈希、被拒条目、journal、外部真实 7z/RAR 样本），最后写
`<scratch>\verify-report.txt`。退出码 0 表示全部断言通过。
