# Galbox 重大缺陷档案（Postmortem）

> 记录 2026-09-11 这一轮修复中，**被查清根因并有实验证据**的重大缺陷。
> 每一条都包含：现象 → 根因 → 证据 → 修复 → 验证 → 教训。
> 目的：这些坑只有真踩过才知道，留档避免重犯。

---

## 缺陷 #1：应用打不开——7 处 `ConfigureAwait(false)` 用在了 UI 线程上

**严重度：阻断级（对真实用户必然发生）**

### 现象
双击应用后：**进程活着、40+ 线程、158MB 内存，但没有任何顶层窗口**，也不报任何错。用户视角就是"双击没反应"。

### 根因
`App.xaml.cs` 的启动路径中，`MainWindow` 创建之前有 7 次 `await ... .ConfigureAwait(false)`：

```
:181  dbContext.Database.EnsureCreatedAsync()
:195  connection.OpenAsync()
:202  command.ExecuteScalarAsync()
:207  command.ExecuteNonQueryAsync()
:211  connection.CloseAsync()
:223  bangumiAuthService.InitializeAsync()
:229  scrapingCacheService.InitializeAsync()
```

`ConfigureAwait(false)` 在 UI 线程上下文里是错误用法：**只要其中任意一个真正异步让出，后续代码就运行在线程池线程上**，然后：

```csharp
MainWindow = new MainWindow();   // ← 在错误线程创建 WinUI 窗口
```

抛出 `COMException 0x8001010E (RPC_E_WRONG_THREAD)`，被 `catch` 静默吞掉（日志只配了 `AddDebug`，用户看不到）。

### 证据（三重）
1. **真实异常栈**（把 catch 改成写文件后捕获）：
   ```
   System.Runtime.InteropServices.COMException (0x8001010E)
      at Microsoft.UI.Xaml.Window..ctor()
      at Galbox.App.MainWindow..ctor()  ... MainWindow.xaml.cs:51
      at Galbox.App.App.OnLaunched()    ... App.xaml.cs:213
   ```
2. **A/B 对照实验**（同一份构建）：

   | 条件 | 结果 |
   |---|---|
   | `%LocalAppData%\Galbox\ScrapingCache` 为空 | ✅ 窗口出现（handle=2296238） |
   | 该目录有 2 个缓存文件 | ❌ COMException，无窗口 |
   | 有缓存文件 + 改 `ConfigureAwait(true)` | ✅ 窗口出现（handle=592626） |

3. **Win32 层面**：`EnumWindows` 确认该进程"没有任何顶层窗口"——不是窗口在屏幕外，是根本没创建成功。

### 为什么潜伏了这么久
缓存为空、凭据未存时，这些 `await` 大多**同步完成**（不真正让出），窗口侥幸建在 UI 线程上，一切正常。**一旦有任何一次真正异步让出，应用就永久打不开。**

### 触发它的原因（值得注意）
**是这一轮修好刮削后产生的缓存文件触发了它。** 修好一个功能，暴露了另一个潜伏的阻断级 bug——两个缺陷在互相作用。

### 修复
7 处 `.ConfigureAwait(false)` → `.ConfigureAwait(true)`，并**把启动期异常兜底改成可见**（落盘日志 + 尽量弹提示）。

### 教训
- **UI 线程上下文中永远不要用 `ConfigureAwait(false)`**。
- **"静默吞掉异常"是放大器**：一个普通的线程错误，因为被吞了，变成了"应用打不开且无从查起"。
- **需要特定前置状态才复发的 bug，必须专门构造那个状态去测**（→ 已要求新增验收检查 A9）。

---

## 缺陷 #2：应用打不开——三个 P/Invoke 声明在错误的 DLL 上

**严重度：阻断级**

### 现象
与 #1 表象完全相同：进程活着、无窗口、无报错。

### 根因
`MainWindow.xaml.cs` 把 `SetWindowSubclass` / `RemoveWindowSubclass` / `DefSubclassProc` 三个函数 **声明在 `user32.dll`**，而这三个符号**只由 `comctl32.dll` 导出**。构造函数里第一次调用即抛 `EntryPointNotFoundException`，同样被启动 catch 吞掉。

### 证据
1. **二进制导出表扫描**（两个 DLL 都扫）：

   | 函数 | user32.dll | comctl32.dll |
   |---|---|---|
   | `SetWindowSubclass` | **不存在** | 存在 |
   | `DefSubclassProc` | **不存在** | 存在 |
   | `RegisterHotKey`（对照组） | 存在 | 不存在 |

   对照组证明扫描方法本身有效（能正确找到真实存在的符号）。

2. **实机 A/B**：修复前 `MainWindowHandle=0`；修复后 `MainWindowHandle=2362014`、标题 "WinUI Desktop"。

3. **时间线证据**：这段代码是在最后一次提交（`cc540e7` "编译通过，准备进行测试"）**之后**加入的，而唯一一次"应用成功启动"的记录早于它 → **含此代码的版本从未被人运行过**。

### 教训
- **"编译通过"和"能跑起来"之间的距离，可以大到应用根本打不开。**
- 这也解释了为什么当年那份测试报告宣称"8/8 通过 100%"却毫无意义——测试项目**没有任何项目引用**，每步只 `return true`，**零断言**。

---

## 缺陷 #3：刮削从未成功过一次——四重故障叠加

**严重度：核心功能完全不可用**

### 现象
产品的核心功能之一"元数据刮削"**从诞生到当时一次都没成功过**。

### 证据（本机取证）
- 用户数据库里唯一一条游戏记录 `IsScraped = 0`，中文名 / 开发商 / 评分 / 来源**全为空**
- `%LocalAppData%\Galbox\ScrapingCache\` 目录存在（构造函数会创建）但**完全为空**——每次搜索都会写一个 json，空目录证明**搜索从未跑到终点**

### 四重根因（各自独立致命）
1. **零 UI 入口**：没有刮削页面、没有导航项、全工程 XAML 里没有任何刮削按钮；设置里的"添加游戏时自动刮削"**没有任何代码读取**
2. **Bangumi 端点错配**：调的是私有旧接口（返回 `{results, list}`），而数据模型绑定 `{data, total, limit, offset}` → 永远读到 0 条，**且 HTTP 200 不抛异常**（静默失败）
3. **VNDB 请求体形状错误**：`filters` 传成字符串、`fields` 含裸 `titles` → 服务端**硬 400**
4. **90% 阈值会丢掉正确结果**：实测 `SabbatOfTheWitch`（文件夹名无空格）vs 正确标题 `Sabbat of the Witch` 相似度仅 **84.21 < 90** → 被丢弃；且用无空格的名字搜索，Bangumi **返回 0 条**

### 修复与验证
- Bangumi 改用 v0 端点、VNDB 修正请求体、名称归一化、阈值改从设置读取、空结果不再写入缓存、补上 UI 入口
- **验证（诊断报告指定的唯一客观判据）**：`ScrapingCache\` 里出现了 `search_dreamin'_her.json` 与 `search_sabbatofthewitch.json`，且**刮到了正确的中文标题"我梦见了她"和"魔女的夜宴"**

### 教训
- **静默失败比崩溃更可怕**：三个源里有两个是"不报错但永远返回空"，用户只会觉得"这软件搜不到东西"。
- 一个功能"从来没成功过"，往往不是单个 bug，而是**多个独立故障叠加**——只修一个仍然不能用。

---

## 缺陷 #4：启动器选错，且结果不确定

**严重度：中（用户可能跑到错误的程序）**

### 现象
游戏文件夹里有 `dreaminher.exe`（64 位）和 `dreaminher-32.exe`（32 位），服务返回了 **32 位那个**。

### 根因
`GameUtilityService.FindExecutableInFolder` 用黑名单过滤掉 setup/install/patch 之类之后，直接 `FirstOrDefault()` —— **依赖操作系统的文件枚举顺序**。

### 为什么值得记录
**它是验收程序第一次运行时抓到的，而四份人工调研（累计 250KB 分析）都没发现它。** 因为它只在"枚举顺序恰好不利"时暴露，靠读代码很难判断。

### 修复
改为**确定性排序**：
1. 文件名与文件夹名归一化后精确匹配（`Dreamin'_Her` → `dreaminher`）
2. 带架构后缀（`-32`/`_x64`/`64`/`x86`）的降权
3. 文件更大者优先
4. 名称排序兜底（**保证同样输入永远同样输出**）

### 验证
A/B 互补验证：主工作区（有刮削修复无此项）= A1 FAIL；净室（有此项无刮削修复）= A1 PASS；两者合一 = 8/8 全绿。

---

## 缺陷 #5（类问题）：会说谎的功能

**严重度：高（比"功能缺失"更伤害信任）**

这一类不是单个 bug，而是**多处功能在欺骗用户**：

| 位置 | 谎言 |
|---|---|
| `GameDetailViewModel` "创建备份" | 只往数据库写一行，`BackupPath` 是拼出来的假路径，**磁盘上什么都没有** |
| `GameDetailViewModel` "恢复备份" | 直接显示 `"存档备份恢复功能尚未实现"` |
| `PatchCenterViewModel` | 生成 `stub_trans_*` **假补丁并真写进用户数据库**（用"名字长度偶数"当 50% 概率） |
| 补丁中心三个按钮 | `Command` 恒为 null（死按钮，点了没反应也不报错） |
| `tests/Galbox.Tests` | 每步 `return true`、零断言，报告却宣称"8/8 通过 100%" |
| 26 个设置项 | 能改、能存、**零影响**；`Theme`/`Language` 全仓无任何应用代码 |

### 教训
**一个对用户撒谎的软件，比一个功能不完整的软件更糟。** 不完整用户能接受并等待；被骗用户会失去信任。所以"诚实性"被单列为一类任务，优先于新功能。

---

## 附：这些缺陷是怎么被发现/漏掉的

| 缺陷 | 发现者 | 人工调研是否发现 |
|---|---|---|
| #1 ConfigureAwait | 主对话（手动启动应用 + 改造 catch 拿异常栈） | ❌ 都没有 |
| #2 DLL 声明错误 | 审计代理（二进制扫描） | ✅ 审计发现（推测，未实机验证） |
| #3 刮削四重故障 | 白盒诊断代理（联网实测 + 代码对照） | ✅ 诊断发现 |
| #4 启动器选择 | **验收程序**（自动检查） | ❌ 四份调研都没发现 |
| #5 说谎的功能 | 审计代理 + 验收程序 | ✅ 审计发现 |

**结论**：四份人工调研（250KB）＋ 一个自动化验收程序，覆盖的是**不同的盲区**。
**两者缺一不可**——尤其是 #1 和 #4，都是"必须真的跑起来才会暴露"的类型。
