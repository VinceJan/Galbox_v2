# Galbox V2 项目交接文档 (Handoff)

> **生成时间**: 2026-09-12  
> **会话目标**: 把 Galbox 从"能编译能启动但功能残缺"推进到高完成度、可实际使用的完整 Windows 桌面产品，跑通真实游戏全链路，补齐核心产品层并打包交付。  
> **交接原因**: 用户下达指令 `现在这样不要继续工作了，把现在的这个状况写成一份 handoff 文件`，主动暂停当前任务并冻结现场。

---

## 1. 当前总体状态一览 (Executive Summary)

- **当前可运行交付物**:
  - 路径: `C:\Users\Jiang\Desktop\Tmp\dsh启动地址\Galbox-1.0.0-win-x64.zip` (约 67.7 MB, 329 个压缩条目)
  - 解压目录: `C:\Users\Jiang\Desktop\Tmp\dsh启动地址\Galbox-1.0.0-win-x64\` (328 个文件，含完整运行时与 PRI 资源)
  - 对应提交: `release/1.0.0` 分支 `@ bad5586`
  - 验收测试通过率: **45 / 45 项全部 PASS** (包含真实游戏全链路 `A80`、备份回退回归 `A100`、快速导航防崩 `A50`)
  - 窗口启动测试: 离屏验证通过（句柄非零、标题正确、耗时约 2.4 秒，完全不打扰用户屏幕）
- **当前暂停进行中的工作**:
  - 分支: `feat/moyu-patch-source-ui` (工作区: `E:\tmp\Galbox_moyuui`)
  - 目标: 补齐最后一项产品层需求 —— **补丁中心接入 moyu.moe 在线源的前端接线与 7 项验收检查 (A110–A116)**
  - 状态: 
    - 子 Agent (91bc982c) 在对 `MoyuUiChecks.cs` 与 `MoyuUiHarness.cs` 进行 fail-first 探针重构后结束；
    - 当前代码包含未提交的 2 个测试文件变更；
    - 经实测，`Galbox.Acceptance.csproj` **生成成功，0 错误，9 警告** (仅为可空性提示)；
    - 52 项验收检查已在 `Program.cs` 注册完毕。
- **目标会话状态**:
  - 目标 ID: `goal-64298f45-d9f0-4ca8-bfe2-faf8bdf1489b`
  - 当前 Phase: `paused` (已暂停自动轮转，防止无人值守时误跑)

---

## 2. 代码仓库与分支状态 (Worktrees & Branches)

远程仓库地址: `https://github.com/VinceJan/Galbox_v2.git` (公开仓库)

| 目录路径 | 所在分支 | 关键提交 / 状态 | 状态说明 |
|---|---|---|---|
| `E:\tmp\Galbox_final` | `release/1.0.0` | `bad5586` (Clean) | **发布集成分支**。45 项全绿，已打出 1.0.0 交付物。等待合入 moyuui。 |
| `E:\tmp\Galbox_moyuui` | `feat/moyu-patch-source-ui` | `cdc34c5` (+ 2 个未提交文件) | **moyu UI 与 A110–A116 开发分支**。已与 release/1.0.0 合并，编译通过。 |
| `E:\tmp\Galbox_v2` | `wip/2026-04-16-fixes` | `582196a` (Clean) | 早期观感与安全批次工作区，历史基线。 |

### `E:\tmp\Galbox_moyuui` 未提交改动说明
- `tests/Galbox.Acceptance/Support/MoyuUiHarness.cs`:
  - 引入 `MoyuUiProbe` 反射探针，使检查可在旧版本上执行以获取真实失败基线（fail-before 测量）。
- `tests/Galbox.Acceptance/Checks/MoyuUiChecks.cs`:
  - 注册并实现了 A110–A116 共 7 项检查。
  - **关键安全保障**: 经主进程严格审计，全部 7 项检查均包含 `vm.Missing.Count > 0` 的硬断言门禁（位于 275/430/570/767/938/1202/1447 行），任何属性或方法缺失必报失败，绝不会因反射返回 null 而静默假绿。

### 远程分支特别提醒
- `origin/main` 目前停留在最初导入的 1900 个文件（包含历史 `.agents/skills/bmad-*` 框架代码）。
- 不要强推（force push）覆盖 `origin/main`，以免丢失初始导入历史。所有发布代码均以 `release/1.0.0` 交付，交由用户后续裁决是否将 release 合入 main。

---

## 3. 核心产品目标完成度盘点

按照用户最初明确的 5 大产品层目标与全链路要求核对：

1. **存档节点标记 / 时间线 / 快照 / 分支标记**:  
   - ✅ **已完成**。Ren'Py 存档解析器实装，多存档路径自动回退，全链路检查 `A80`、回退回归 `A100` 全部通过。
2. **补丁中心 (moyu.moe 查询 / 接管下载 / 本地安装)**:  
   - 🔄 **代码与编译已就绪，待合并闭环**。
   - 底层 API 与合规层 `MoyuComplianceGuard` 已就绪；
   - XAML 与 `PatchCenterViewModel.Moyu.cs` 状态机已就绪；
   - 7 项验收检查 (A110–A116) 代码已写完且编译通过；
   - 待执行最终验收与合入 `release/1.0.0`。
3. **刮削可靠性与四源接入 (Bangumi / VNDB / Ymgal / CnGal)**:  
   - ✅ **已完成**。四源全部接入，修正了 VNDB 过滤器与 Bangumi 接口数据结构。
4. **游戏健康诊断的真实修复能力**:  
   - ✅ **已完成**。缺失文件夹标记、可执行文件重定向、数据完整性修复闭环。
5. **流程图追踪与社区成就预留接口**:  
   - ✅ **已完成**。领域模型已预留，状态与接口已打通。
6. **真实游戏跑通全链路 (A80)**:  
   - ✅ **已完成**。以实际 Ren'Py 游戏目录为测试对象，全链路跑通。

---

## 4. 关键技术约束与防坑规范 (Critical Rules)

接手 Agent **必须严格遵守**以下既有规则，切勿踩坑：

1. **GUI 检查防干扰规范 (严禁桌面闪烁与抢焦点)**:
   - 遵守 `docs/DEVELOPER-GUIDE.md §6.1`。
   - 所有调用 `Galbox.App.exe` 的启动探针与测试必须使用环境变量 `GALBOX_TEST_OFFSCREEN_WINDOW=1`。
   - 窗口必须移至离屏坐标（-32000, -32000），严禁在用户桌面上频繁弹窗、切页抢焦点（用户曾明确批评）。
2. **测试必须单实例执行 (避免并发打假)**:
   - A50 等 GUI 检查如果遇到后台同名进程或并发执行，可能因外部信号以退出码 0 意外关闭。
   - **绝不同时运行多个 acceptance 进程**，每次只跑一个。
3. **moyu.moe 站方合规底线**:
   - 遵守站方 `robots.txt` 中的 `Disallow: /api`。
   - 严禁调用 `/api/v1/*` 接口，严禁抓取批量直链。
   - 仅使用 `https://api.nextmoe.dev/v2/moyu/*` 官方公开面；下载走“跳浏览器页面 + 本机文件夹接管”。
4. **Windows PowerShell 下 Git 提交转义问题**:
   - 在 PowerShell 中执行 `git commit -m "..."` 极易因双引号与中文字符被误识别为 pathspec 导致提交失败。
   - **务必使用临时文件方式提交**: `git commit -F <commit-msg-file>`。

---

## 5. 下一任 Agent 的接续操作步骤 (Action Plan)

新会话启动后，接手 Agent 仅需执行以下标淮流水线即可顺利完成 100% 交付：

### 步骤 1：验证并提交 moyu 分支
```powershell
cd E:\tmp\Galbox_moyuui
# 运行全量 52 项验收测试，确认 A110-A116 全绿
dotnet run --project tests\Galbox.Acceptance -c Debug -- --verbose

# 确认无误后提交修改
git add tests/Galbox.Acceptance/Checks/MoyuUiChecks.cs tests/Galbox.Acceptance/Support/MoyuUiHarness.cs
# 使用临时文件提交
Set-Content -Path $env:TEMP\moyu-commit.txt -Encoding utf8 -Value "验收：完善 A110-A116 moyu 在线源检查与探针门禁"
git commit -F $env:TEMP\moyu-commit.txt
```

### 步骤 2：合并至主集成分支 `release/1.0.0`
```powershell
cd E:\tmp\Galbox_final
git checkout release/1.0.0
# 合并 moyuui 特性分支
git merge feat/moyu-patch-source-ui --no-ff -m "Merge branch 'feat/moyu-patch-source-ui' into release/1.0.0"
```

### 步骤 3：独立复验 52 项测试
```powershell
cd E:\tmp\Galbox_final
# 确保机器无其他 Galbox 进程，单实例跑完 52 项
dotnet run --project tests\Galbox.Acceptance -c Debug -- --verbose
```

### 步骤 4：重新打包发布包
```powershell
cd E:\tmp\Galbox_final
# 使用固化的发布脚本一键打包（包含离屏启动探针核验、PRI检查与 ZIP 压缩）
pwsh -File tools\release.ps1
```

### 步骤 5：推送到 GitHub 并更新文档
```powershell
cd E:\tmp\Galbox_final
git push origin release/1.0.0
git push origin feat/moyu-patch-source-ui

# 更新 C:\Users\Jiang\Desktop\Tmp\dsh启动地址\交付说明-2026-09-12.md 中的验收项数量 (45 -> 52) 与最终哈希
```

### 步骤 6：标记完成会话目标
- 调用 `update_goal` 将目标 `goal-64298f45-d9f0-4ca8-bfe2-faf8bdf1489b` 状态置为 `complete`。

---

## 6. 建议技能 (Suggested Skills)

新接手的 Agent 应该优先调用或知晓以下技能：

- `skill(name: "i-have-adhd")`: 用户明确要求 ADHD 认知友好交互风格 —— 结论先行、首句给出直接回答或动作、使用项目符号与粗体、减少大段技术推演铺陈、决策优先于询问。
