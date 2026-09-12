# `_probe/` —— 快速导航崩溃的排查工具与证据

这个目录是排查「快速连续切换导航导致进程崩溃（`0xC000027B`）」时留下的。

## 保留什么

| 文件 | 为什么留 |
|---|---|
| `Nav-Rapid.ps1` | 复现脚本：驱动 UI Automation 快速连续切换导航，直到进程死掉 |
| `Nav-Rapid-Cdb.ps1` | 带 cdb（WinDbg 命令行）的版本，崩了之后抓转储并分析 |
| `artifacts/dump-analysis*.txt` | **根因证据**：cdb 抓到的托管调用栈，证明是两个缺陷（Flyout 里的 `{x:Bind}` + 池线程上取 DispatcherQueue 得到 null） |
| `uiatest/` | 一个极小的 UI Automation 探针工程 |
| `cdb*.txt` | cdb 命令脚本 |

## 删掉了什么

大量**重复的运行输出**（`navrapid-*.txt`、`cdb-*.txt`、`acceptance-*.txt` 等 27 个文件、约 560 KB）
在一次排查里被反复覆盖，对后来者没有增量信息。它们的结论已经写进：

- 提交 `967ed5e` 的提交信息（两处根因的完整说明与判别实验数据）
- `docs/DEVELOPER-GUIDE.md` §6.1（离屏窗口那条硬性规定与其来龙去脉）
- `_product/design/acceptance-runs/15-merged-43-checks-run.txt`（修复后的验收原始输出）

**留下方法，丢掉噪声。** 需要重新生成那些输出时，跑 `Nav-Rapid.ps1` 即可。
