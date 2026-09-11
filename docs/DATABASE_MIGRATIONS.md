# Galbox 数据库迁移机制（EF Core Migrations）

> 本文件说明「为什么要有迁移、存量库怎么升级、怎么验证、以后怎么加迁移」。
> 相关代码：`src/Galbox.Data/Migrations/`、`src/Galbox.Data/GalboxDbContextFactory.cs`、`src/Galbox.App/App.xaml.cs`。

## 1. 背景：为什么必须补这一层

早期版本用 `Database.EnsureCreated()` 建库，**没有 `Migrations/` 目录，也没有 `__EFMigrationsHistory` 表**。
`EnsureCreated()` 的行为是「库不存在就按当前模型建，库存在就什么都不做」，由此带来两个真实问题：

1. **改表结构 = 用户数据丢失风险**：一旦模型加字段，老库不会自动升级；如果换成 `Database.Migrate()`，
   EF 会认为所有表都要新建，直接报错或留下半成品结构。
2. **老库已经悄悄落后于模型**（实测，见 §5）：真实用户库里 `UserSettings` 少了 5 列，
   `SELECT BangumiAuthMethod FROM UserSettings` 在真实文件上会报 `no such column`。
   `EnsureCreated()` 永远不会修好它。

## 2. 启动路径（`GalboxDatabaseInitializer.InitializeAsync`）

应用启动时 `App.xaml.cs` 只调用一次初始化器，它按实际情况选三条路径之一：

| 情况 | 判定 | 处理 | 结果模式 |
|---|---|---|---|
| **全新安装** | 库里一张表都没有 | 正常跑 `Database.Migrate()`，建全部表 + 迁移历史 | `FreshInstall` |
| **存量库（EnsureCreated）** | 有表，但 `__EFMigrationsHistory` 里没有基线迁移 | 先「基线修补」（§4），再把**基线迁移标记为已应用**（不执行它的 DDL），最后照常应用后续迁移 | `LegacyBaselineStamped` |
| **已纳入迁移的库** | 迁移历史里已有基线 | 普通 `Database.Migrate()`；已最新则什么都不做 | `Upgraded` / `AlreadyUpToDate` |

放行前还有两道保险：

* **非 Galbox 文件拒改**：有表但没有 `Games` / `UserSettings` 时抛异常，绝不碰无关的 SQLite 文件。
* **迁移前自动备份**：要动已经有数据的库时，先在库文件旁边复制一份
  `galbox.db.pre-migration-<时间戳>.bak`（只保留最近 5 份），失败不影响升级。

## 3. 基线标记（baseline stamping）为什么是安全的

`20260911145649_InitialBaseline` 是**按改动前的模型生成的**，描述的就是 `EnsureCreated()` 当年建出来的结构
（9 张表 + 索引）。存量库的结构与它同源，所以「直接标记为已应用」而不是「重跑一遍建表」是成立的。

这一点不是靠推测，而是**被验证证明的**：验证场景 4 会
「只把基线迁移应用到一个空库」→ 与真实老库做结构比对（表、列集合、索引，忽略列顺序），
然后「对老库副本只跑修补」→ 再比对，结论是修补后的老库**包含基线声明的全部对象**。
见 `_bmad-output/progress/db-migration-verification-pristine-*.txt` 里的 `SCENARIO 4`。

> 基线迁移**永远不要修改**。它一旦被改动，所有已标记该基线的库都会与历史不符。

## 4. 基线修补（`LegacySchemaRepair`）：让「已标记为基线」这句话是真的

老库可能由**更早的模型**建出来（`EnsureCreated()` 从不更新已存在的库）。因此在标记基线之前，
初始化器会做一次**只针对基线声明内容**的修补：

* **期望来自基线迁移本身**，而不是当前模型：把基线迁移单独应用到一个临时库，读回它建了什么
  （`BaselineSchema.CaptureAsync`）。
* 缺表 → 用 EF 为基线生成的 `CREATE TABLE` 原样建出来，再补它的索引。
* 缺列 → `ALTER TABLE ... ADD COLUMN`，类型 / NOT NULL / 默认值都照基线；`NOT NULL` 且无默认值时按存储类
  给 `0` / `''` / `X''`（SQLite 不允许在非空表上加无默认值的 `NOT NULL` 列）。
* **绝不做后续迁移的工作**：例如 `Games.Status`、`SaveBackups.SaveNodeId` 属于第二个迁移，
  修补阶段绝不添加——否则第二个迁移会以 `duplicate column name` 失败。
  （这正是第一版修补的 bug，已在验证中被专门断言拦下。）
* **绝不删除 / 改写任何已有列**；模型不认识的手工列（`Games.VndbId`、`UserSettings.LastModified`）原样保留。
* 全程幂等：先比对再动手，第二次运行不执行任何语句。

## 5. 在真实用户库上发现的落差（修补要解决的问题）

对真实库（`%LocalAppData%\Galbox\galbox.db` 的副本）实测：

| 缺失对象 | 归属 | 后果 |
|---|---|---|
| `UserSettings.BangumiAuthMethod` | 基线 | **老库根本读不了设置**：EF 会 `select` 这些列 → `no such column` |
| `UserSettings.BangumiRefreshToken` | 基线 | 同上 |
| `UserSettings.BangumiTokenExpiresAt` | 基线 | 同上 |
| `UserSettings.BangumiUsername` | 基线 | 同上 |
| `UserSettings.GameDirectoriesJson` | 基线 | 「游戏目录」配置读不出来 |
| `IX_UserSettings_Id` | 基线 | 缺索引（功能无影响） |
| `Games.Status` / `IsStatusUserSet` / `StatusChangedTime` | 第二个迁移 | 「待玩 / 暂停中」等持久化状态 |
| `SaveBackups.SaveNodeId` | 第二个迁移 | 备份与存档节点关联 |

也就是说：**本次迁移顺带修好了一个既有 bug**（设置表缺列），并补上了存档节点的数据结构。

## 6. 容错：库里已经有「部分新列」怎么办（`TolerantMigrationRunner`）

仓库里有两次「绕开迁移、手工加列」的先例（`Games.EngineType`、`Games.VndbId`）。
如果某个待应用迁移要加的列已经存在，EF 的迁移器会直接 `duplicate column name` 崩掉，应用起不来。

处理方式：

1. 正常情况（没有任何冲突）→ **仍然调用 EF 自己的 `Database.Migrate()`**，不重复造轮子。
2. 检测到冲突 → 用 `IMigrationsModelDiffer` 算出每个待应用迁移的操作，逐条判断「数据库里是否已经有这个对象」：
   * 已存在的 `AddColumn` / `CreateTable` / `CreateIndex`，以及已经消失的 `Drop*` → **跳过**；
   * 剩余操作交给 EF 自己的 `IMigrationsSqlGenerator` 生成 SQL，在**每个迁移一个事务**内执行，
     然后再写入该迁移的历史行（历史保持真实）；
   * 只有「独立对象」类操作允许被跳过。遇到 `AlterColumn`、`AddForeignKey`、数据搬运（`Insert/Update/DeleteData`）
     等操作时**不猜、不修**，直接抛出带清单的异常（宁可让用户看到明确错误，也不留半应用的库）。
   * 列已存在但**存储类型（SQLite affinity）不一致**时同样拒绝继续，避免把值写进类型不对的列。
3. 安全网：跳过冲突不会覆盖手工写入的数据。验证场景 5 会手工把 `Games.Status` 置为 `3`，
   升级后该值仍然是 `3`。

## 7. 幂等性

* 第二次启动：迁移历史已完整 → 直接 `AlreadyUpToDate`，`applied now` 为空，不产生新的备份文件，结构/行数不变。
* `Games.Status` 回填 SQL 只会作用于 `IsStatusUserSet = 0` 的行，**不会覆盖用户手动设置的状态**。
* 验证场景 3（同一库连续跑两次）与场景 7（历史行被删掉、对象都还在）都断言「无副作用」。

## 8. 以后新增迁移怎么做

```powershell
# 1) 只改实体 / DbContext
# 2) 生成迁移（Design 包已在 Galbox.Data 中引用）
dotnet tool install --tool-path E:\tmp\galbox-tools dotnet-ef --version 8.0.0   # 首次
& E:\tmp\galbox-tools\dotnet-ef.exe migrations add <Name> `
    --project src\Galbox.Data\Galbox.Data.csproj `
    --startup-project src\Galbox.Data\Galbox.Data.csproj `
    --output-dir Migrations --context GalboxDbContext
# 3) 检查生成的 Up()：只允许「新增」；需要改数据时显式写 migrationBuilder.Sql
# 4) 跑 tools\verify-db-migration.ps1 验证
```

设计期上下文由 `GalboxDbContextFactory` 提供，默认指向临时文件，
**`dotnet ef` 不会连接真实用户库**（需要时用环境变量 `GALBOX_DESIGNTIME_DB` 指定）。

## 9. 怎么验证（一条命令）

```powershell
# 推荐：用「改动前的真实老库快照」当基线输入（更能证明从老库升级可行）
pwsh -File tools\verify-db-migration.ps1 `
  -LiveDatabasePath 'E:\tmp\Galbox_v2\_product\backup\galbox-real-db-before-migration-20260911-230250.db' `
  -WorkDirectory 'E:\tmp\galbox-verify-pristine'

# 也可以直接验证当前线上库（只读；文件被占用时哈希校验会自动跳过并提示）
pwsh -File tools\verify-db-migration.ps1 -WorkDirectory 'E:\tmp\galbox-verify-livedb'
```

脚本**只读**源库（复制 + 哈希），所有操作发生在临时目录的副本上；跑完会打印源库升级前/后的 SHA256。
7 个场景共 107 项断言，全通过才算成功；完整输出同时写到工作目录的 `migration-verification-output.txt`。

## 10. 上线后 `App.xaml.cs` 里的手工补列循环该删掉

`App.xaml.cs` 里那段「检查必需列 → 缺了就 `ALTER TABLE`」的循环（以及历史上给 `EngineType` 打的那段）
**应当删除**，理由：

1. 它把 schema 变更写在了迁移历史之外，正是「库里已经有部分新列」这一冲突的来源；
2. 它只加列、不改索引 / 外键 / 表，永远无法让库与模型真正一致；
3. 它掩盖了「忘记写迁移」这类真实缺陷（缺列应当由迁移修复，而不是启动时偷偷补）；
4. 新机制已经覆盖它的全部职责：存量库 → 基线修补；手工加过的列 → 容错迁移。

如果确实想保留一层防御，请**只保留只读检查**（发现模型需要的列缺失时写日志 / 提示），不要执行 DDL。

## 11. 已知限制（未覆盖的部分）

* 容错只在「独立对象」类操作上生效；涉及 `AlterColumn` / `AddForeignKey` / 数据搬运的迁移若发生冲突，
  会明确报错而不是自动修补。
* 列是否「已存在」按名字判断（类型额外用 SQLite affinity 校验），不做完整结构等价比较。
* 修补只补**基线**声明的对象；老库若缺的是一张**后续迁移**要建的表，会由该迁移正常创建。
* 未做：SQLite `VACUUM`、迁移失败的自动回滚（EF 的每个迁移本身在一个事务里，失败即回滚该迁移）。
