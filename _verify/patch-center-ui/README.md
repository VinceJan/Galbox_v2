# 补丁中心接线（feat/patch-center-ui）—— 原始证据

全部文件都是工具直接输出，未经编辑。复现命令写在每一节。

## 1-baseline-A30-A32-FAIL.txt —— 实现之前的基线（红）

```
git checkout 072953c        # 只有契约声明 + 新检查，没有实现
dotnet build Galbox.sln -c Release -p:Platform=x64
tests\Galbox.Acceptance\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Galbox.Acceptance.exe --timeout 600
```

结果：`A30 FAIL / A31 FAIL / A32 FAIL`，`PASSED 9 / FAILED 3 / ERRORS 1`，退出码 1。
（A0 的 ERROR 是同机并发运行验收程序导致的 SQLite 文件锁，与本次改动无关，
已由 `ed6636f`（cherry-pick 自 `5f3fac9`，按 pid 隔离验收库）消除。）

## 2-after-A30-A32-PASS.txt —— 实现之后（绿）

```
tests\Galbox.Acceptance\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Galbox.Acceptance.exe --timeout 600
```

结果：`13 PASSED / 0 FAILED / 0 ERRORS`，退出码 0。连续三次运行结果一致。

## 3-real-machine-launch-PASS.txt —— 实机启动

脚本：`pwsh -File launch-verify.ps1`
（启动 Release 版 Galbox.App.exe，等真实窗口，用 UI Automation 点开「补丁中心」，
dump 自动化树并截图；不做任何游戏目录写入）

结果：`MainWindowHandle = 0x55095E`，导航后进程仍存活，页面包含新工作流的全部文案，
截图 1800x1200。

## 4-a-b-test-baseline-page-crash.txt —— 补丁中心页崩溃是既有缺陷的 A/B 证据

同样的脚本，`-Exe` 指向 `0e051ea` 基线的构建产物。基线同样在打开「补丁中心」后
进程消失、页面无内容，Windows 事件日志给出同一条
`Application Error 0xc000027b / Microsoft.UI.Xaml.dll`。

结论：**补丁中心页在本次工作之前就是"一打开就杀进程"的状态**，不是本次改动引入的。

## 5-patch-center-page.png —— 补丁中心页实机截图
