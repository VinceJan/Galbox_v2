# Galbox.Tests — 诚实测试报告

**最后更新**: 2026-09-12
**分支**: `feat/honest-ui-tests`
**运行环境**: Windows 11, .NET 8.0.419, x64

---

## ⚠️ 本文件此前是一份虚假报告

本文件旧版本（2026-04-14）宣称：

| 项目 | 旧报告宣称 |
|------|-----------|
| 总测试数 | 8 |
| 通过数 | 8 |
| 失败数 | 0 |
| 通过率 | **100%** |
| 「当前无需要修复的问题」 | — |

**这个结论没有任何证据支撑，现予撤回。** 旧报告对应的代码存在以下事实：

1. `Galbox.Tests.csproj` **没有任何 `<ProjectReference>`** —— 它不引用任何生产代码，
   因此「8 个测试」从来没有真正测过应用。
2. 该工程**不在 `Galbox.sln` 中** —— 没有任何构建或测试流程会运行它。
3. `GalboxFunctionalTests.cs` 中 6 处 `return true` 无条件把每一步报告为成功。
4. `RunTestStep` 在读取返回值**之前**就打印 `"{name}: PASSED"`，即使该步返回 `false`
   也照样打印 PASSED。
5. 导航辅助方法按类名 `"NavigationView"` 查找控件 —— 在 WinUI 3 的 UIA 树中
   **不存在该名称的节点**，因此它一个都没点到，而测试仍然打印
   `"Step 7 PASSED: All pages navigated successfully"`。
6. `AppPath` 是写死的绝对路径 `E:\tmp\Galbox_v2\...\Galbox.App.exe`，指向**另一个检出目录**。
7. 旧报告的 Step 4、Step 5 自述为 SKIPPED，却仍被计入「8/8 通过」。

---

## 现在真正被验证的东西

运行命令（三选一，推荐第一条）：

```powershell
# 推荐：一条命令同时构建并运行本测试工程（含 src/Galbox.App 依赖）
dotnet test tests\Galbox.Tests\Galbox.Tests.csproj -c Debug

# 或经由解决方案运行
dotnet test Galbox.sln -c Debug

# 仅验证解决方案还能构建（0 error）
dotnet build Galbox.sln -c Debug
```

实测输出：

```
测试总数: 7
     通过数: 2
     跳过数: 5
     失败数: 0
总时间: ~12 秒
```

| 测试 | 状态 | 实际验证内容 |
|------|------|-------------|
| `AppLaunchSmokeTests.Application_starts_and_shows_a_real_top_level_window` | **REAL** | 启动构建产物，要求出现**可见的、无属主的顶层窗口**，窗口标题不是启动失败对话框，有非零面积，3 秒后仍存活，且启动日志中存在与本进程窗口句柄对应的激活记录 |
| `GalboxFunctionalTests.Step9_StartupDiagnosticsAndDatabaseAreReal` | **REAL** | 应用数据目录存在；`galbox.db` 非空且文件头是 `SQLite format 3`；本进程的启动日志报告窗口激活；无模态对话框 |
| `Step2_AddGameDirectory` | **SKIP** | 需要交互式文件夹选择器，见下 |
| `Step3_VerifyGameLibrary` | **SKIP** | 控件级 UIA 不可靠，见下 |
| `Step6_TestSaveManager` | **SKIP** | 同上 |
| `Step7_TestPageNavigation` | **SKIP** | 同上 |
| `Step8_TestSpecialFeatures` | **SKIP** | 需要真实游戏进程与全局热键输入 |

测试类的 `[Fact(Skip = "...")]` 会把跳过原因打印到测试输出里，不会静默通过。

---

## 显式跳过的项与原因

| 步骤 | 跳过原因 |
|------|---------|
| Step2 添加游戏目录 | 该操作经由 Windows 文件夹选择器（shell 模态对话框）完成，自动化无法稳定选择路径；应用也没有无头入口。**因此「添加游戏目录」这一行为未被本项目验证。** |
| Step3 / Step6 / Step7 页面导航与页面内容 | FlaUI 控件级自动化在本机**不确定**，实测证据见下一节。 |
| Step8 老板键 / 截图 | 老板键是全局热键（`WM_HOTKEY`），需要真实前台窗口与真实键盘输入；截图在游戏进程退出时触发，需要真实游戏进程。二者都无法在本测试中构造。**未被验证。** |

### 为什么控件级自动化被降级为 SKIP（实测证据）

`Support/AppSession.cs` 中有完整记录。三次独立运行同一份二进制得到：

1. `SetForegroundWindow` + `BringWindowToTop` + `SetActiveWindow` **全部无法**把应用窗口变到前台
   （`GetForegroundWindow()` 仍是别的窗口）—— 因为本机桌面被其它窗口共享。
   FlaUI 的 `Click()` 是在屏幕坐标上发真实鼠标点击，于是点到了别的窗口上。
2. UIA 树在一次导航扫描进行到一半时**整体失效**：`FindAllDescendants()` 返回 0，
   此后所有元素查找全部失败，而进程**确实存活**（窗口在、3 秒内响应 `WM_NULL`、无对话框、
   事件日志无本工作树的崩溃记录）。抛出的异常是：

   ```
   System.Runtime.InteropServices.COMException : 灾难性故障 (0x8000FFFF (E_UNEXPECTED))
      at Interop.UIAutomationClient.IUIAutomationElement.FindAll(...)
      at FlaUI.Core.AutomationElements.AutomationElement.FindAllDescendants()
   ```

3. **相同输入、相同二进制，结果不同**：一次扫描中
   `主页 → 游戏库 → 存档管理 → 补丁中心` 前 3 项正确渲染、第 4 项之后整棵树失效；
   另一次运行中 `游戏库` 与 `补丁中心` 都没有发生导航。

把这段控制级自动化作为**通过/失败门禁**会产生「因为错误的理由而通过/失败」的测试，
因此它被保留为带原因的 SKIP，而不是伪装成绿灯。

> 未被确定的事项：上述观察中**没有一次**成功点击到「补丁中心」。由于鼠标点击在本机整体
> 不可靠，**无法据此断定补丁中心页面对真实用户不可达**。需要在一个独占桌面的会话中复测。

---

## 失败先于修复 / 修复后通过（fail-before / pass-after）证据

对启动冒烟测试做了两次**故意破坏**，两次都被检出；撤销后通过。

### 破坏 A：把 `comctl32.dll` 改回 `user32.dll`（历史缺陷原样复现）

`dotnet test ... --filter "FullyQualifiedName~AppLaunchSmokeTests"` 输出：

```
[xUnit.net 00:00:05.97]     Galbox.Tests.AppLaunchSmokeTests.Application_starts_and_shows_a_real_top_level_window [FAIL]
失败 Galbox.Tests.AppLaunchSmokeTests.Application_starts_and_shows_a_real_top_level_window [5 s]
测试总数: 1
失败数: 1
```

失败消息：

```
错误消息:
   Assert.NotEqual() Failure: Strings are equal
Expected: Not "Galbox 启动失败"
Actual:       "Galbox 启动失败"
```

关键观测（摘录）：

```
 Candidate window             : hwnd=0x2B0B30 class='#32770' title='Galbox 启动失败' size=468x248  [from Process.MainWindowHandle]
 Alive after settle           : True
 All windows owned by pid     : 4
     hwnd=0x2B0B30 class='#32770' title='Galbox 启动失败' size=468x248
     hwnd=0xA70972 class='WinUIDesktopWin32WindowClass' title='WinUI Desktop' size=800x533   <-- 不可见
 Modal dialogs (#32770)       : 1
 Log confirms THIS pid activated its window: False
```

注意 `All windows owned by pid` 里那个 **不可见**的 `WinUIDesktopWin32WindowClass` 窗口：
一个只检查「进程有没有某类窗口」的测试会在**这个坏掉的构建上通过**。
本测试要求的是**可见、无属主**的顶层窗口，并且标题不得是启动失败对话框。

### 破坏 B：注释掉 `MainWindow.Activate()`（窗口永不显示）

```
[xUnit.net 00:00:33.50]     Galbox.Tests.AppLaunchSmokeTests.Application_starts_and_shows_a_real_top_level_window [FAIL]
失败 Galbox.Tests.AppLaunchSmokeTests.Application_starts_and_shows_a_real_top_level_window [33 s]
测试总数: 1
失败数: 1
```

失败消息与观测：

```
错误消息:
   No window appeared within 30 s. The process is (or was) running, so the startup exception was
   swallowed: this is the classic 'double-click does nothing' defect.
...
 Process id                   : 36116
 Exited before window         : False
 Time to first window         : never
 Candidate window             : (none)  [from EnumWindows]
 Alive after settle           : True
 Visible top-level windows after settle: 0
 All windows owned by pid     : 2
     hwnd=0x2709D4 class='WinUIDesktopWin32WindowClass' title='WinUI Desktop' size=800x533   <-- 存在但不可见
 Modal dialogs (#32770)       : 0
 Observed hwnd                : 0x0
 Log confirms THIS pid activated its window: False
```

这正是历史缺陷的用户可见症状：**进程活着、窗口存在但从不显示、没有任何报错**。
两次破坏都被撤销，`git status` 中 `src/` 无任何改动。

---

## 一个必须知道的陷阱：启动日志是「全机器共享」的

`%LocalAppData%\Galbox\logs\startup-YYYYMMDD.log` 是**机器级全局文件**。
本机同时存在多个 Galbox 工作树，它们**都会往同一个文件追加**。

审计期间直接观察到：本工作树的进程失败（无窗口）时，同一个日志文件里却出现了
别的实例写下的 `Main window created and activated` 与
`OnLaunched: startup sequence completed`。

因此「日志说应用启动成功了」**不能**作为判定依据。本项目的判定完全基于
**按进程 id 关联的窗口状态**；只有那条带**本进程自己的窗口句柄**的日志行才被采信
（窗口句柄在存活窗口间唯一，无法被别的进程伪造）。

`Galbox.Acceptance` 的 A9 检查依赖该共享日志中的 `StartupSequenceCompleted` 标记 ——
在多实例并行运行时会读到别的实例的标记。**这是一个真实存在的脆弱点，建议单独修复。**

---

## 已知限制（未被验证 / 未解决）

1. **控件级 UI 行为未被验证**：页面内容、按钮、设置开关、存档管理操作等，本项目一律没有验证。
2. **添加游戏目录、老板键、截图三项功能未被验证**（原因见上表）。
3. **补丁中心是否可点击到达未确定**（原因见上）。
4. **一次未能复现的瞬时启动失败**：在一次「`dotnet build Galbox.sln` 后紧跟
   `dotnet test <csproj>`」的混合序列中，应用以 `exit code -1` **立即退出**、从未建窗；
   随后 8 次运行（5 次 `--no-build`、3 次带构建）**全部通过**，Step9 用同一个二进制亦正常启动。
   根因未确定；推测与「解决方案构建输出到 `bin\x64\Debug`、csproj 构建输出到 `bin\Debug`，
   两者共用同一个 `obj\` 目录」有关。**结论：这不是应用缺陷（真实缺陷在两次破坏中都稳定失败），
   但属于需留意的环境噪声。**规避方式：优先使用单条命令
   `dotnet test tests\Galbox.Tests\Galbox.Tests.csproj -c Debug`。
5. 本项目**不修改任何生产代码**，不含任何生产行为修复。

---

## 文件清单

| 文件 | 作用 |
|------|------|
| `Galbox.Tests.csproj` | 补上对 `src/Galbox.App` 的 `ProjectReference`；`TargetFramework` 对齐为 `net8.0-windows10.0.19041.0` |
| `AssemblyInfo.cs` | `[assembly: CollectionBehavior(DisableTestParallelization = true)]` —— 所有测试共用同一份应用与 `%LocalAppData%\Galbox` 状态 |
| `AppLaunchSmokeTests.cs` | 启动冒烟测试（本项目唯一的高价值门禁） |
| `GalboxFunctionalTests.cs` | 逐步审计后的重写：真实断言或带原因的 SKIP |
| `Support/AppLaunchObserver.cs` | 启动 + 按进程 id 观测窗口 |
| `Support/NativeWindows.cs` | `EnumWindows` 层：按 pid 过滤的可见顶层窗口 |
| `Support/RepoLayout.cs` | 从测试程序集位置反推仓库根与构建产物（不再写死路径） |
| `Support/AppSession.cs` | 控件级会话；同时是「为什么必须跳过」的实测记录 |
| `TEST_REPORT.md` | 本文件 |
