# 存档节点功能：接线方案（数据层 × 解析器 → 产品）

> 编制时间：2026-09-11
> 状态：**两端都已就绪，缺中间那层"胶水"和界面**
> 这是产品**最核心的差异化功能**（PRD 里评 ⭐⭐⭐⭐⭐「颠覆性体验」，竞品只有基础备份）

---

## 1. 目标（产品要求，来自总纲 §3.3）

用户要看到的是：

```
某个游戏的存档管理页
┌──────────────────────────────────────────────────────────┐
│ 游玩时间线（横向）                                        │
│  ●──────●────────●───────────●──────────●                │
│  孤独感  合鍵   紀伊国屋の疑問  絶妙なトロッコ問題  俺結婚した？？ │
│  4/8    4/9     4/9          4/9        4/9              │
│                                                          │
│  CG 图鉴：6 / 27（22.2%）  [查看还差哪 21 张]              │
│  分支：A 路线组（3 个存档） / B 路线组（2 个存档）          │
│                                                          │
│  [把当前存档标记为快照]  [按剧情位置命名]  [对比两个存档]    │
└──────────────────────────────────────────────────────────┘
```

**关键：这些数字不是演示，是真实可算的**（见第 4 节实测值）。

---

## 2. 两端已经建好了什么

### 2.1 数据层（分支 `feat/save-node-model`，已验证 107/107）

**`SaveNode` 实体（26 列）**——产品侧的完整模型：

| 分组 | 字段 |
|---|---|
| 归属 | `GameInfoId`、`SaveGroupId`（可空） |
| 剧情 | `SlotName`、**`SceneLabel`**、**`RouteName`**、`ChapterName` |
| 进度 | `ChapterProgressPercent`（可空：**不知道 ≠ 0%**） |
| CG | **`CgUnlockedCount` + `CgTotalCount`** + `CgUnlockPercent` + **`CgUnlockedIdsJson`**（→ 能回答"还差哪几张"） |
| 时长 | `PlayTimeSeconds` |
| 文件 | `SaveFilePath`、`SaveSizeBytes`、`SaveFileCount` |
| 时间 | `SaveCreatedTime` / `SaveModifiedTime`（**存档的**时间）与 `CreatedTime` / `UpdatedTime`（**行的**时间）**严格分开** |
| 快照 | `IsSnapshot`、`SnapshotDescription` |
| 解析状态 | `Source`、`ParseStatus`、`ParseError`（失败必须可解释）、`ExtendedMetadataJson` |
| 派生 | `DisplayName`（快照描述 > 场景标签 > 章节名 > 槽位名）、`FormattedPlayTime` |

**接入缝（不需要改 schema）**：
```csharp
SaveNodeMetadata metadata = ...;   // 全部字段可空
saveNode.ApplyMetadata(metadata);  // 只写非空值；百分比夹取 0-100；成功后 Source 自动转 EngineScan
```

**`SaveGroup` 实体**：`Name` / `RouteName` / `Description` / `OrderIndex`。

**`GameStatus`（五档）**：未玩过 / 正在游玩 / 已完成 / 待玩 / 暂停中 + `IsStatusUserSet`（用户手设过就不许被自动建议覆盖）。

### 2.2 解析器（分支 `feat/renpy-parser`，验收全项复现）

**入口**：
```csharp
var analysis = new RenpySaveAnalyzer().Analyze(@"D:\GAME\Dreamin'_Her");
```
**产出**（与 `SaveNode` 字段对得上）：

| 解析器 DTO | 说明 |
|---|---|
| `analysis.Directories.SaveDirectoryName` | `"dreamin_her-1631775296"` |
| `analysis.Saves[i].SlotName` | 槽位名 |
| `analysis.Saves[i].CurrentSceneLabel` | **"孤独感"** ← 产品要的存档节点名 |
| `analysis.Saves[i].SceneSequence` | 该存档经过的场景序列 |
| `analysis.Saves[i].Playtime` / `PlaytimeSeconds` | `00:38:35` / 秒 |
| `analysis.Saves[i].Metadata.LastWriteTimeUtc` | 存档时间（必须用文件 mtime） |
| `analysis.Saves[i].LastDialogueText` | 最后一句对白（**详情页可展示，很有代入感**） |
| `analysis.CgProgress` | `TotalCount / UnlockedCount / Percent / UnlockedSlots[] / LockedSlots[]` |
| `analysis.Persistent.Choices` | 玩家选过的选项（4 条实测） |
| `analysis.UnlockedSceneLabels` | **"章节进度"的诚实替代品** |
| `analysis.ScriptIndex.CallSiteToLabel` | 3995 条映射 |

---

## 3. 要做的：中间那层"胶水"

### 3.1 一个服务（`Galbox.App\Services\SaveNodeScanService.cs` 或 Core 里）

```
ScanForGame(gameId, installPath)
  → 用解析器扫描该游戏的存档目录
  → 把每个存档槽位映射成 SaveNodeMetadata
  → upsert 到 SaveNodes 表（按 SaveFilePath 去重，不重复插入）
  → 返回统计：新增 N 个节点 / 更新 M 个 / 失败 K 个（附原因）
```

**映射表（机械对应，逐字段）**：

| `RenpySaveSlot` | → `SaveNodeMetadata` | 说明 |
|---|---|---|
| `SlotName` | `SlotName` | 12/12 实测为空字符串（该游戏不用槽位名）→ **这时 `DisplayName` 会回退到 `SceneLabel`**，正是产品想要的效果 |
| `CurrentSceneLabel` | **`SceneLabel`** | 核心字段 |
| `PlaytimeSeconds` | `PlayTimeSeconds` | |
| `Metadata.LastWriteTimeUtc` | `SaveModifiedTime` | |
| `Metadata.FileSize` | `SaveSizeBytes` | |
| `FilePath` | `SaveFilePath` | 去重键 |
| （`SceneSequence` 的首个 / 最长路径） | `RouteName` ⚠️ | **只能算"疑似路线"，不能当事实**（见 §5） |
| `analysis.CgProgress.UnlockedCount / TotalCount` | `CgUnlockedCount` / `CgTotalCount` | |
| `analysis.CgProgress.UnlockedSlots` | `CgUnlockedIdsJson` | |
| `ParseStatus` | `Parsed` / `Failed` + `ParseError` | **失败必须写原因，不许静默跳过** |
| `Metadata.GameVersion` 与当前游戏版本不一致 | `IsVersionMismatch`（或放 `ExtendedMetadataJson`） | 实测存档是 1.0.2，游戏已是 1.0.3 |

### 3.2 三件必须做对的事

1. **CG 数据是"游戏级"的，不是"存档级"的**：`persistent` 是跨存档文件。所以 `CgUnlockedCount` 应该写在**每个节点上**（当前时点的快照），但**界面展示时用最新那份**。要在 UI 层明确"这是当前进度"，不是"这个存档当时的进度"。
2. **去重**：同一个槽位在两个目录各有一份（`MirroredPaths`），**只能生成一个节点**，`SaveFilePath` 存"实际生效的那份"（按 mtime 取最新），另一份记进 `ExtendedMetadataJson`。
3. **幂等**：重复扫描 → 更新而不是插重复行（按 `SaveFilePath` 唯一约束或 upsert）。

---

## 4. 实测的真实数据（验收标准就用这些数字）

来自用户自己的 12 个存档（《我梦见了她》）：

| 验收项 | 期望值 |
|---|---|
| 扫描出的存档节点数 | **12**（不是 24——两处镜像要去重） |
| 每个节点的 `SceneLabel` | 非空，且是 `main_scenario.rpy` 里真实的 label |
| label 命中率 | **22/22** |
| `auto-3` 的节点名 | **`孤独感`** |
| 其他样例 | `合鍵`、`七瀬邸へ`、`俺結婚した？？`、`昔もこういう話してたよね` |
| CG 进度 | **6 / 27 = 22.2%**，集合精确等于 `{0101,0301,0401,0501,0801,2401}` |
| "还差哪几张" | 21 张，可列出 ID |
| runtime 区间 | 1194.19 ~ 2415.06 秒 |
| `_chosen` 选项 | 4 条 |
| 界面**绝不能**出现 | 槽位名空白（该游戏 12/12 槽位名为空，必须回退到场景名） |

---

## 5. 三条诚实性红线（**不可违反**）

这几条是这一轮反复确立的原则，实现时必须遵守：

| # | 红线 | 原因 |
|---|---|---|
| 1 | **分支分组只能标"疑似"** | 实测 12/12 存档里**一个路线变量都读不到**（Ren'Py 只序列化被改动过的变量，而玩家的进度还没走到那些赋值点）。可靠信号只剩"场景集合聚类"。**验收标准里不要写"读出路线名"** |
| 2 | **"章节进度"不能用行号百分比** | 这个游戏**没有章节概念**（21073 行 = 262 个扁平 label，全文无 chapter 字段）。**应当改用"已解锁场景数 / 262"**——语义诚实，不会因 label 长短失真 |
| 3 | **解析失败必须可见** | `ParseStatus.Failed` + `ParseError` 要写进数据库并能在界面看到。**不许静默跳过**——这正是这个项目过去最大的毛病 |

---

## 6. 界面（`SaveManagerPage` 增强）

### 6.1 时间线视图
- 横向时间轴，节点按 `SaveModifiedTime` 排序
- 每个节点显示：**场景名（`DisplayName`）** + 时间 + 自动档/手动档（`auto-*` vs `1-*`）
- 鼠标悬停显示最后一句对白（`LastDialogueText`）

### 6.2 快照
- "把当前存档标记为快照" → `IsSnapshot = true` + 让用户填 `SnapshotDescription`
- 快照在时间线上用不同样式标记

### 6.3 CG 图鉴
- 显示 `6 / 27 (22.2%)` + 进度条
- 点击展开：已解锁的 6 个 ID、**还差的 21 个 ID**

### 6.4 空态与错误态（必须诚实）
- 未扫描过 → "尚未扫描存档（点击扫描）"
- 扫描失败 → **显示具体原因**（不是"扫描失败"四个字）
- 不支持的引擎 → "暂不支持 XXX 引擎的存档解析"（不要显示假数据）

---

## 7. 验收标准（可自动验证）

新增验收检查 **A10：存档节点扫描**：

1. 对 `D:\GAME\Dreamin'_Her` 扫一次 → **节点数 = 12**
2. 12 个节点的 `SceneLabel` **全部非空**
3. `auto-3` 对应的节点 `DisplayName` **包含 `孤独感`**
4. CG 进度 = **6/27**，`CgUnlockedIdsJson` 集合精确等于 `{0101,0301,0401,0501,0801,2401}`
5. **幂等**：再扫一次 → 节点数**仍是 12**（不是 24）
6. **失败可见**：对一个不存在的游戏目录扫描 → 返回明确的失败原因，**不抛异常、不留脏数据**

---

## 8. 已知缺口（不在本轮，但要记录）

| 缺口 | 说明 |
|---|---|
| 其它引擎 | 目前只有 Ren'Py。Tyrano（JSON 存档，最简单）、KiriKiri（二进制，最难）都未实现 |
| 通用性 | 解析器只验证过 Ren'Py 7.4.11 / Python 2.7。8.x 的存档安全令牌、6.x 的 zlib 裸流**都未验证** |
| 分支聚类 | 需要静态分析剧本建分支树（唯一能拿到真分支结构的途径），工作量大 |
| `_seen_images` 多元素 key | 217 个多元素元组的确切语义**未从引擎源码确认**（已暴露原始 key 备用） |
