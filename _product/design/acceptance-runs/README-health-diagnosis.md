# 健康诊断（A70–A74）验收记录说明

本目录下与「游戏健康诊断真实修复能力」有关的三个原始输出：

| 文件 | 内容 | 退出码 |
|---|---|---|
| `09-health-diagnosis-before-fix-18of24.txt` | **实现前**的基线。`src/` 回退到 `release/1.0.0` 的原始状态，检查代码是最终版。A70–A74 全部 FAIL，另有 A9 因环境噪声 FAIL。 | 1 |
| `10-health-diagnosis-after-fix-23of24-A9-env.txt` | **实现后**的最终输出。A70–A74 全部 PASS；唯一 FAIL 是 A9（环境原因，见下）。 | 1 |
| `11-health-diagnosis-after-fix-24of24-intermediate.txt` | 实现过程中一次整套全绿的运行（A70–A74 全 PASS，退出码 0）。它产生于 A73「用例 4 批量修复」加入之前，因此不作为最终证据，只作为全绿运行的存在性记录。 | 0 |

## 两次基线为什么编号不同

第一次基线（`19of24`）是在检查代码还在演进的阶段抓的：A72 当时还没有「用例 5：目录改名时兼容性层跟着走」。为了不让「基线」与「最终检查代码」错位，最终基线是**把 `src/` 整体回退到实现前、构建、再用最终检查代码跑一遍**得到的，也就是 `09-health-diagnosis-before-fix-18of24.txt`。两者内容量相同（同一批检查），`18of24` 才是与最终代码一一对应的基线。

## A9 的 FAIL 为什么不算本功能的回归

A9（`The GUI application opens a window while scrape-cache data is present`）的 FAIL 与本次改动无关，证据有三条：

1. **在实现前的 `src/` 上以完全相同的方式失败。** 最终基线那一跑里，A9 的输出是
   `completed=False, failed=False`，与实现后逐字相同 —— 同一套检查代码、回退后的生产代码，同样失败。
2. **A9 读的是一台机器共享的按天日志。** 它把「本次启动写了哪些行」定义为
   `%LocalAppData%\Galbox\logs\startup-YYYYMMDD.log` 中记录偏移量之后的内容，而这台机器上同时有
   4 个其它 worktree（`Galbox_e2e`、`Galbox_crash`、`Galbox_reserved`、`Galbox_final`…）在跑同一套
   `Galbox.Acceptance`，每次都会拉起 `Galbox.App.exe` 并写同一个文件。早期的一次失败里，A9 读到的
   「本次启动」日志行中有 8 处指向 `E:\tmp\Galbox_e2e\...`，报告的失败原因是别的 worktree 的
   e2e 用例故意制造的 `unable to open database file`。
3. **本 build 的启动被独立验证过。** 手工启动 `src\Galbox.App\bin\Debug\...\Galbox.App.exe`，
   共享日志中按顺序出现 `Main window created and activated` → `Process monitor: started=True` →
   `OnLaunched: startup sequence completed`，进程存活、窗口标题 `Galbox 1.0.0`。

根因是 A9 自身的一个竞态（`A9StartupWithCacheCheck.cs` 第 145–164 行的等待循环）：一旦
`MainWindowHandle != IntPtr.Zero` 就 `break`，紧接着去读日志；而「Main window created」与
「startup sequence completed」两行之间还有 `ProcessMonitorStartup` 一步（实测间隔约 18 ms，
机器负载高时更长）。窗口句柄已经出现、完成标记还没落盘，读到的就是空。

- 它不是本次改动引入的：同样的间隔在实现前的构建里也存在，且实现前的 `src/` 上同样失败。
- 修法（留给 A9 的负责人决定）：读到空时不立刻判定失败，而是在剩余超时预算内轮询等待完成标记，
  或把「窗口出现」与「启动完成」两个条件分开判定。本次任务范围不含 A9，未擅自改动别人的检查。
