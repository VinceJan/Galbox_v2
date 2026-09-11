# Galbox 可分发打包 — 结论与证据

分支：`feat/distributable-packaging`（worktree `E:\tmp\Galbox_verify`）

---

## 1. 结论（一句话）

**能做出来。自包含（免安装、不依赖已装 .NET）绿色版可用，已实机验证窗口正常出现。**
唯一缺失的一环是 `resources.pri` —— 它从未被生成，而不是 .xbf 没被 publish、也不是 Bootstrap 调错。

---

## 2. 根因（已用 cdb + 首异常日志钉死）

`Microsoft.UI.Xaml.dll` 在 `Application.Start` 阶段抛 `E_FAIL`：

```
COMException: Cannot locate resource from
  'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'
```

（证据：`%LocalAppData%\Galbox\logs\firstchance.log`，由临时首异常探针写出）

链式原因：

1. `EnableCoreMrtTooling=false`（当初为绕开缺失的 `Microsoft.Build.Packaging.Pri.Tasks.dll`）
   → 官方 MRT 管线不跑 → **`resources.pri` 从来没有被生成过**。
2. 未打包（unpackaged）的 WinUI 3 应用，**框架自身的资源也走应用的主资源映射**解析。
   `themeresources.xbf` 只存在于框架 PRI 里（`Microsoft.UI.Xaml.Controls.pri`，
   映射名 `Microsoft.UI.Xaml`）。它必须被**合并**进应用的 `resources.pri`。
3. 没有合并 → MRT 先报 `0x80073D54`（进程无包标识），再退到文件查找报 `0x80070002`
   （`MRM.dll MRM.cpp(537)` 找不到 `resources.pri`）→ XAML `E_FAIL`
   → 跨 native 边界成 stowed exception → `0xC000027B`，进程在 `OnLaunched` 第一行之前就死。

**重要更正：这不是 publish-vs-bin、也不是 Release-vs-Debug 的问题。**
任何产出输出目录的配置都受影响。Debug bin 之所以「看起来能跑」，是因为它当时**早于**
`EnableCoreMrtTooling=false`（我的对照实验：同一份陈旧 Debug 产物无 PRI 时同样 `0xC000027B`）。

### 两种部署模式的真实差别（已 A/B 实测）

| | 需要应用自带合并 PRI？ | 实测 |
|---|---|---|
| **自包含** `WindowsAppSDKSelfContained=true` | **需要** | 无 PRI → `0xC000027B`；加 PRI → 正常 |
| **框架依赖**（默认） | **不需要** | 有 PRI、删掉 PRI，两次都正常启动 |

---

## 3. 确切复现命令

### 自包含绿色版（推荐交付物，免装 .NET）

```powershell
cd E:\tmp\Galbox_verify
dotnet publish src\Galbox.App\Galbox.App.csproj -c Release -r win-x64 `
  --self-contained true -p:WindowsAppSDKSelfContained=true
```

产物：`src\Galbox.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\Galbox.App.exe`
（325 文件 / **168.9 MB**；含 `coreclr.dll`、`Microsoft.ui.xaml.dll`、`resources.pri`）

### 框架依赖便携版（体积小 3.5 倍）

```powershell
dotnet publish src\Galbox.App\Galbox.App.csproj -c Release -r win-x64 --self-contained false
```

产物：78 文件 / **47.0 MB**。最终用户机器需要预装：**.NET 8 桌面运行时** +
**Windows App Runtime 1.6**（本机已装 `6000.519.329.0`）。

---

## 4. 实测验收（`_verify\Test-Launch.ps1`）

自包含 Release publish 产物：

```
verdict      : OK - window visible
windowHandle : 4721990        (非 0)
windowTitle  : "WinUI Desktop"   (不是 "Galbox 启动失败")
启动日志      : 完整走完，含 "OnLaunched: startup sequence completed"
```

稳定性：无操作静置 45s 存活；UI Automation 只读枚举导航项（不点击）静置 25s 存活。
导航项文案正确中文化：`主页 | 游戏库 | 存档管理 | 补丁中心 | 刮削进度 | 错误报告 | 设置`。

---

## 5. 改动清单

| 文件 | 改动 | 为什么 |
|---|---|---|
| `src/Galbox.App/Galbox.App.csproj` | 新增 `GenerateAppResourcePriFile` target | 用 `MakePri.exe`（来自 `Microsoft.Windows.SDK.BuildTools` 包，本机已有）生成 `resources.pri`，并通过 `type=PRI` 索引器把框架 PRI 合并进来。替代缺失的官方 Pri 任务。 |
| 同上 | 自写 priconfig（**不含 `<packaging>`**） | `makepri createconfig` 的默认配置会按语言把索引拆成 79 个卫星文件，只有主文件被 publish → 框架文案退化成英文（实测：导航项显示 "Settings" 而非 "设置"）。去掉后是单一索引，多语言仍在。 |
| 同上 | 新增 `_IncludeLooseAppResourcesInPublish` | publish 不搬运散装 `.xbf` 与生成的 `resources.pri`，补进 `$(PublishDir)`。 |
| 同上 | 新增 `_TrimUnusedFrameworkLocales` | 裁掉 86 个框架 `.mui` 语言文件夹。 |
| 同上 | `_AppPriFrameworkPri` 只在自包含时收集 | 避免框架依赖构建误吃上一次自包含构建残留在同一 `OutDir` 的框架 PRI。 |
| `src/Galbox.App/Program.cs` | `#if !WINDOWSAPPSDK_SELFCONTAINED` 包住 `Bootstrap.Initialize` | 自包含时不该再拉框架包（两种模式混用）。 |
| 同上 | 可选首异常日志（`GALBOX_FIRSTCHANCE_TRACE=1`，默认关闭） | 定位本次根因的关键工具，保留以便下次排查。 |

---

## 6. 体积

| 形态 | 文件数 | 体积 |
|---|---|---|
| 自包含，裁剪前 | 489 | 170.10 MB |
| **自包含，裁剪后（当前）** | **325** | **168.92 MB** |
| 框架依赖 | 78 | 47.02 MB |

裁剪净省 **164 个文件 / 1.64 MB**（`.mui` 从 172 个 1.71 MB → 8 个 0.07 MB，仅保留 `en-us / en-GB / zh-CN / zh-TW`）。
可用 `-p:GalboxTrimUnusedFrameworkLocales=false` 关掉。

---

## 7. 未解决 / 仍需注意

1. **UI Automation 快速连续切换导航项会让进程崩（`0xC000027B`）。**
   但**在自包含与框架依赖两种部署下都复现**，所以**不是打包问题**，是应用层问题。
   对照：单页导航每页静置 25s → 4/4 页面 + 设置页全部存活；无操作静置 45s 存活。
   已生成 WER APPCRASH 报告（23:57:52 / 23:58:12）。**建议单独派单排查。**
2. 启动时两条 first-chance 异常（`InvalidOperationException` + `TargetInvocationException`），
   被内部捕获、不影响启动（日志完整走完）。未定位，**不属于打包范畴**。
3. 窗口标题是 WinUI 默认的 `WinUI Desktop`（应用没设 `Title`），非缺陷，仅记录。
4. `resources.pri` 现为单一索引（1.33 MB）；未验证超大项目下的构建耗时影响（本项目 ~1s）。

---

## 8. 用户数据

测试期间应用按自身逻辑写入了 `%LocalAppData%\Galbox`（含其自建的
`galbox.db.pre-migration-*.bak`）。**未删除、未回滚**——回滚时该库正被运行中的实例占用，
强行覆盖有损坏风险，故按「只读」原则保持原状。
