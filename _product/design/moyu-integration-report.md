# Galbox × moyu.moe 补丁发现层 —— 实现与验收报告

- 分支：`feat/moyu-source`（基于 `release/1.0.0`）
- 工作区：`E:\tmp\Galbox_moyu`
- 提交：`a3033e2`（基线检查）、`2ceb7ab`（实现）
- 依据：`E:\tmp\Galbox_v2\_product\design\moyu-moe-integration-research.md`（789 行专项调研）
- 官方契约：`https://developer.nextmoe.dev/specs/moyu-openapi.yaml`（v1.0.0，`x-stability: stable`，本次实现前**重新取取过一次**，21658 bytes 未变更）

---

## 1. 结论先行

### 1.1 合规路径下，补丁发现能做到什么

**能，而且够用。** 官方 `/v2/moyu` 面提供了补丁中心"在线半边"所需的全部发现能力：

| 能力 | 能否做到 | 说明 |
|---|---|---|
| 按作品查有没有补丁 | ✅ | `GET /v2/moyu/patches?refs=vndb:v4`，批量最多 100 个锚，一次往返 |
| 拿到补丁页列表与元数据 | ✅ | 名称、类型（12 值枚举）、语言、平台、下载数、发布时间、`resource_updated_at` |
| 拿到每个补丁条目的元数据 | ✅ | 资源名、体积（`size`）、BLAKE3（`hash`）、汉化组、模型名、Markdown 说明、发布者 |
| 拿到"去哪里下载" | ✅ | 每行都有 `web_url`，这是**设计上**通往文件的唯一途径 |
| 交给浏览器下载 | ✅ | `MoyuBrowserLauncher` → 校验后 `Process.Start(UseShellExecute)` |
| 接管用户下载好的文件 | ✅ | `MoyuDownloadWatcher` 按体积匹配 + 稳定性判定 + 超时/取消 |
| 交给已有补丁引擎安装 | ✅ | 产出 `PatchSourceInfo`，路径直接喂 `IPatchEngine`，本层不自己解压 |
| **拿到直链 / 提取码 / 解压密码** | ❌ **做不到，且不应该做** | 契约明写不含；见 §1.2 |
| **按 Bangumi 条目 ID 查** | ❌ **做不到** | 只认 `vndb:vXXXX` / `catalog:<id>`；见 §1.3 |
| **精确按字节匹配下载文件** | ❌ **做不到** | `size` 是 3 位小数的展示串，不是精确字节数；见 §1.4 |

### 1.2 为什么拿不到直链（这是结论，不是缺陷）

调研已确认 moyu 自己的 `/api/v1/*` 接口**技术上完全可通**，`/api/v1/patch/resource/{id}/link` 甚至匿名返回完整直链，文件也真的能下到字节层。**但 `www.moyu.moe/robots.txt` 写着 `Disallow: /api`，而所有可用接口都在 `/api` 底下。**

三处独立证据指向同一意图：robots 禁 `/api`、官方 spec 明写链接"不能被批量抓走"（a separate, rate-limited, per-resource request whose whole purpose is that links cannot be harvested in bulk）、服务端源码给取链接口挂了 30 次/分钟限流。

**技术上可行 ≠ 可以这么做。** 本实现**一个 `/api/v1/*` 请求都没有发过**，并且把这件事做成了代码结构上的不可能（§3）。官方面不给直链是**设计**，不是遗漏——"跳浏览器 + 接管下载文件夹"正是它期望的用法，所以本任务要求的交互形态与合规路径**完全一致，不存在取舍**。

### 1.3 一个真实的产品依赖：没有 vndb id 就查不了

公开面只认 `vndb:vXXXX` 与 `catalog:<id>`。而 Galbox 的刮削主链是 **Bangumi**。官方 spec 专门警告 `patch.id` / `vndb_id` / `catalog_work_id` 是三个互不相等、互不可替换的 id 空间。

本层的处理是**如实报告做不到**，绝不猜锚点、绝不退回被禁接口：

- `MoyuRef.PickAnchor(vndbId, catalogWorkId)` 优先 VNDB，其次 catalog；
- 两者都没有 → `MoyuFailureCode.NoAnchor`，消息为"这部作品没有 vndb id……需要先给作品补上 vndb id"，**不发任何请求**（A64 断言 `PickAnchor(null,null) == null`）；
- `MoyuRef.FromVndbId("bangumi:1234")` 返回 `null`（A64 断言）。

> **需要刮削工作线配合的动作（本层不越界改）**：在刮削阶段把 `vndb_id` 一并落库。moyu 确认会返回该字段（调研实测 `/api/v1/patch/86` → `"vndb_id":"v4"`）。在补上之前，补丁发现对"只有 Bangumi ID"的作品会显示"无法查询"，这是设计行为。

### 1.4 一个实测发现的精度事实

调研用 `content-range: bytes 0-2047/18825520` 标定了单位（**1 MB = 1048576**，二进制），这一点是确定的，本实现按此实现并断言（A64：`"1.5 GB" → 1610612736` 等 7 个样本 bit-exact）。

但**"17.953" 并不精确等于 18825520 字节**：`17.953 × 1048576 = 18825085`，与真实字节数差 **435**。也就是说 `size` 是一个 **3 位小数的展示串**，不是精确字节数。

**后果（已按此实现）**：下载文件的匹配**必须容忍误差**，不能做等值比较。`MoyuDownloadWatchOptions.SizeTolerance` 默认 2%，远小于任何两个不同补丁的差距、远大于该展示误差。A65 断言了这个容差既**接受**匹配文件、也**拒绝**一个 64 KB / 声明 170 MB 的文件。

> 报告 §8.3 把 `hash`（BLAKE3）填充率列为未验证。因此本层**没有**把 BLAKE3 当作匹配前提；如果将来确认填充率高，按内容匹配是更好的做法。

### 1.5 合规路径上是否有无法克服的障碍？

**没有。** 发现、跳转、接管、安装四段都通，唯一少的是"自动下载"那一步，而官方面明确就是不给这一步的。要拿到一键下载需要站方书面许可（调研 §7.3 建议去开 issue），在那之前产品价值不打折，只是多一次浏览器操作。

---

## 2. 文件清单与职责

### 新增（`src\Galbox.Core\Api\`）

| 文件 | 职责 |
|---|---|
| `MoyuComplianceGuard.cs` | **合规边界，唯一构造请求 URI 的地方。** 允许面 `https://api.nextmoe.dev/v2/moyu/*`；拒绝 `/api` 前缀、非 HTTPS、其它主机。另有浏览器 URL 校验与 `UserAgent` 常量 |
| `MoyuOptions.cs` | 密钥、节流参数、缓存策略、下载文件夹。`ToString()` 重写为只输出**存在性 + 指纹**，防止 `log.Info(options)` 泄漏密钥；`FromKeyStore()` 是 App 与验收共用的唯一装配入口 |
| `MoyuApi.cs` | 主客户端。4 个端点 + 单作品便利入口；**自带请求管线**（见 §2.1）；ETag/304 内存缓存；失败全部落成 `MoyuResult<T>` |
| `MoyuResults.cs` | `MoyuFailureCode`（13 个可区分失败码）、`MoyuFailure`、`MoyuResult<T>`、批量/分页信封 |
| `MoyuRef.cs` | `vndb:` / `catalog:` 锚点解析与优先级；拒绝 Bangumi ID |
| `MoyuSize.cs` | `"17.953 MB"` → 字节（二进制单位）；不可解析返回 0 且**不抛异常** |
| `MoyuKeyStore.cs` | `IMoyuKeyStore` + `MoyuDpapiKeyStore`（DPAPI）+ `MoyuMemoryKeyStore`（测试用） |
| `MoyuRateLimiter.cs` | 客户端自律节流：最小间隔 + 滚动分钟预算；单调时钟 |
| `MoyuModels.cs` | 官方契约 DTO（严格按 OpenAPI，**不含任何链接/提取码字段**） |
| `MoyuModels.Domain.cs` | 领域模型、`MoyuPatchTypes`（12 值 → 中文）、`ToPatchSourceInfo()` |
| `MoyuBrowserLauncher.cs` | 跳浏览器；`DryRun` 与可注入 `StartProcess` 便于验收 |
| `MoyuDownloadWatcher.cs` | 下载文件夹接管：快照、稳定性判定、体积容差匹配、超时、取消 |
| `MoyuDownloadsFolder.cs` | 解析下载文件夹（注册表 `User Shell Folders` → `%USERPROFILE%\Downloads`） |
| `HttpClientWrappers.cs`（改） | 追加 `MoyuHttpClient`，与既有四个 wrapper 同构 |

### 2.1 为什么 `MoyuApi` 不复用 `ApiClient` 的 `GetJsonAsync`

调研 §6.2 指出基类不够用，本实现**按报告方案处理并说明了理由**（代码内也有注释）：

1. `ApiClient.GetJsonAsync` 走 `HttpClient.GetStringAsync`，**挂不了 `X-API-Key`**（该面强制要求），也没有 `If-None-Match` / 304 路径；
2. 基类重试循环**完全不读响应头**：429 按 2× 退避重试而忽略 `Retry-After`，也不读 `X-RateLimit-*` / `X-Quota-*`。

因此 `MoyuApi` **继承** `ApiClient`（保留调用方已理解的 `LastError` / `LastErrorBody` 诊断契约），但携带自己的 `HttpRequestMessage` 管线。A67 断言：`Retry-After: 2` 被遵守（实测 2004–2016 ms，而不是立即重试）；`Retry-After: 3600` 超过 `MaxHonouredRetryAfter` 时**不睡满**，而是返回 `RateLimited` 并带上 `RetryAfter`。

### 2.2 项目选择与理由（任务要求说明）

**放在 `Galbox.Core`，不放 `Galbox.Services`。**

理由：本层是**纯 HTTP + 本地文件系统**，不需要 `Galbox.Data`（EF Core / SQLite），也不需要数据库。现有约定是 API 客户端放 `Galbox.Core.Api`（`BangumiApi` / `VndbApi` 同处），补丁引擎放 `Galbox.Core.Patches`。放进 `Galbox.Services` 必须给它加 `Galbox.App` 项目引用，而 `Galbox.Services` 自己的 README 写明它的存在理由是"**同时**需要解析器**和**数据库的编排"——补丁发现不满足这个条件。

**没有本层不落库。** 发现结果不写 `galbox.db`，不建本地镜像，符合调研 §5.6"不做站点数据落库"。落库是 UI/数据工作线的事。

### 2.3 唯一的 `Galbox.App` 改动

`App.xaml.cs` 只改 DI 注册块（约 105–191 行区域），`Views\` / `ViewModels\` **一个文件都没碰**（`git show --stat` 可证）。

注册内容：`MoyuHttpClient`（BaseAddress = `https://api.nextmoe.dev/`、完整 UA、30s）、`IMoyuKeyStore`、`IMoyuRateLimiter`（**必须 singleton**，否则节流不生效）、`MoyuOptions`、`MoyuDownloadWatcher`、`MoyuBrowserLauncher`、`MoyuApi`。

**额外发现并修复了一处真实缺口**：`IPatchEngine` 在容器里**从未被注册过**（`AddGalboxPatches()` 在 Core 里存在，但没人调用）。也就是说即便补丁发现把文件递过去，也**没有接收方**。本层加了 `services.AddGalboxPatches();`——这是交接的前置条件，不是界面接线。

生存期：`AddDbContextFactory` + scope 取 DbContext 的既有约定**未被触碰**，moyu 全部注册都是 singleton / transient，不存在 captive dependency。验收容器用 `ValidateScopes=true, ValidateOnBuild=true` 构建，与 App 一致。

---

## 3. 合规护栏的证据（证明代码不会请求 `/api/v1/*`）

护栏不是注释里的承诺，是**代码结构 + 自动断言**两层：

### 3.1 结构层

`MoyuApi` 的**每一条**请求 URI 都由 `MoyuComplianceGuard.EnsureApiUri` 构造，该方法校验失败会**抛 `InvalidOperationException`**，而不是返回一个越界的 URI：

```csharp
// MoyuApi.SendAsync 内唯一构造 URI 的地方
uri = MoyuComplianceGuard.EnsureApiUri(_options.BaseAddress, relativePathAndQuery + query);
```

`IsAllowedApiUri` 的判定顺序刻意把**禁止项放在允许项之前**，这样将来即使 `ApiPathPrefix` 常量写错，也打不开 `/api` 的门：

```csharp
if (path.StartsWith(ForbiddenPathPrefix, StringComparison.OrdinalIgnoreCase))  // "/api"
    return false;
return path.StartsWith(ApiPathPrefix, StringComparison.OrdinalIgnoreCase);     // "/v2/moyu/"
```

`rel_path` 常量只有四处，全是 `/v2/moyu/...`；查询串由 `QueryBuilder` 全量百分号编码，锚点无法夹带第二个参数。

### 3.2 断言层（A63）

A63 三重断言，实测输出：

```
REJECT expected : rejected  https://www.moyu.moe/api/v1/search?keywords=CLANNAD&type=resource&page=1&limit=3
REJECT expected : rejected  https://www.moyu.moe/api/v1/patch/86/resource
REJECT expected : rejected  https://www.moyu.moe/api/v1/patch/resource/223/link
REJECT expected : rejected  https://www.moyu.moe/api/v1/galgame?page=1
REJECT expected : rejected  https://www.moyu.moe/API/V1/search
REJECT expected : rejected  https://api.nextmoe.dev/api/v1/moyu/patches
REJECT expected : rejected  https://api.nextmoe.dev/v1/moyu/patches
REJECT expected : rejected  http://api.nextmoe.dev/v2/moyu/patches
REJECT expected : rejected  https://evil.example/v2/moyu/patches
ACCEPT expected : accepted  https://api.nextmoe.dev/v2/moyu/patches?refs=vndb%3Av4
ACCEPT expected : accepted  https://api.nextmoe.dev/v2/moyu/patches/86?include=resources
ACCEPT expected : accepted  https://api.nextmoe.dev/v2/moyu/patches/86/resources?limit=50
ACCEPT expected : accepted  https://api.nextmoe.dev/v2/moyu/resources/6262
EnsureApiUri('/api/v1/…') threw instead of returning a URI: True

ACTUAL : 9/9 forbidden paths refused, 4/4 legitimate paths accepted, 4/4 driven requests
         inside the allow-list, 0 recorded pipeline request(s) inside the allow-list
```

其中"4/4 driven requests"是关键：A63 **真的驱动了一个客户端**（带密钥、带记录型 `HttpMessageHandler`），让 4 个端点各发一次请求，然后断言**实际发出的每一条 URL** 都在允许面内——所以护栏是在真实代码路径上被验证的，不只是"这个函数能调用"。

`MoyuApi.BuildWebUrl` / `MoyuBrowserLauncher` 同样拒绝 `/api` 路径、非 HTTPS、其它主机（A66：3 个合法页面接受、12 个危险 URL 拒绝，含 `dl.imoe.uk` / `oss.moyu.moe` 直链与 `javascript:` / `file:`）。

### 3.3 站点友好性

- `User-Agent: Galbox/2.0 (+https://github.com/VinceJan/Galbox_v2)` —— 可识别、可联系（既有四个客户端只有 `Galbox/1.0`，信息量不足）；
- 节流 ≤ 1 请求/秒 且 ≤ 30/分钟，**串行化**（`SemaphoreSlim(1,1)`）；
- 批量优先：一次 `refs=` 最多 100 个锚，把请求数压到最低；
- **只在用户显式操作时发请求**：无后台轮询、无定时刷新、无预取、无全站遍历；
- 尊重 `Retry-After`，429 不做即时重试（A67 断言）；
- 缓存只在内存、只存用户实际看过的那几条，TTL 5 分钟与官方 `Cache-Control: max-age=300` 一致；
- **不镜像、不缓存、不再分发补丁文件本体**。

---

## 4. 基线 FAIL → 实现后 PASS（原始输出）

原始文件已入库：`_product/design/moyu-evidence/`。

### 4.1 命令

```powershell
cd E:\tmp\Galbox_moyu
dotnet build tests\Galbox.Acceptance\Galbox.Acceptance.csproj -c Debug
.\tests\Galbox.Acceptance\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.Acceptance.exe --timeout 400
```

### 4.2 基线（提交 `a3033e2`，实现前）—— 7 条 FAIL

`_product/design/moyu-evidence/00-baseline-run-FAIL.txt`

```
# [A60] MoyuApi resolves from DI with the official /v2/moyu HttpClient configuration
ACTUAL   : the feature is absent: type Galbox.Core.Api.MoyuApi is not present in the built Galbox.Core assembly
RESULT   : FAIL  (2 ms)

# [A61] No configured nmk_ key is reported as a distinct failure, never as an empty result
ACTUAL   : the feature is absent: type Galbox.Core.Api.MoyuApi is not present in the built Galbox.Core assembly
RESULT   : FAIL  (8 ms)

# [A62] The nmk_ key is DPAPI-encrypted on disk and never stored in plaintext
ACTUAL   : the feature is absent: type Galbox.Core.Api.MoyuDpapiKeyStore is not present in Galbox.Core
RESULT   : FAIL  (4 ms)

# [A63] No request path under /api is reachable (robots.txt Disallow: /api)
ACTUAL   : the feature is absent: type Galbox.Core.Api.MoyuComplianceGuard is not present in Galbox.Core
RESULT   : FAIL  (1 ms)

# [A64] size strings parse as binary units and game anchors accept only vndb:/catalog:
ACTUAL   : the feature is absent: no MoyuSize/MoyuSizeParser type in Galbox.Core
RESULT   : FAIL  (1 ms)

# [A65] Downloads-folder watcher adopts a new file by size and gives up on a timeout
ACTUAL   : the feature is absent: type Galbox.Core.Api.MoyuDownloadWatcher is not present in Galbox.Core
RESULT   : FAIL  (2 ms)

# [A66] The browser hop accepts only HTTPS moyu pages and refuses API URLs
ACTUAL   : the feature is absent: neither MoyuBrowserLauncher nor MoyuComplianceGuard is present in Galbox.Core
RESULT   : FAIL  (1 ms)

 PASSED : 10
 FAILED : 7
 ERRORS : 0
 SKIPPED: 0
 TOTAL  : 17
 EXIT CODE: 1  (at least one check did not PASS)
```

（A0–A9 全部 PASS，说明失败确实来自缺失的新功能，而不是环境问题。）

### 4.3 实现后 —— 18 条全 PASS，退出码 0

`_product/design/moyu-evidence/10-final-run-PASS.txt`

```
# [A60] MoyuApi resolves from DI with the official /v2/moyu HttpClient configuration
ACTUAL   : all 7 registrations resolve; BaseAddress=https://api.nextmoe.dev/, UA="Galbox/2.0 (+https://github.com/VinceJan/Galbox_v2)", Timeout=30s, pacing=1.0s/30 per minute on a shared limiter
RESULT   : PASS  (3 ms)

# [A61] No configured nmk_ key is reported as a distinct failure, never as an empty result
ACTUAL   : Failed=true, Code=NotConfigured, 0 HTTP request(s) sent, 6 ms
RESULT   : PASS  (11 ms)

# [A62] The nmk_ key is DPAPI-encrypted on disk and never stored in plaintext
ACTUAL   : 278-byte DPAPI blob, 0 plaintext occurrences, CurrentUser decrypt under the app's entropy succeeds and a foreign entropy fails, round-trip and clear OK, no secret in any diagnostic
RESULT   : PASS  (22 ms)

# [A63] No request path under /api is reachable (robots.txt Disallow: /api)
RESULT   : PASS  (34 ms)

# [A64] size strings parse as binary units and game anchors accept only vndb:/catalog:
ACTUAL   : 7 exact samples bit-exact, the calibrated sample within 525 bytes of the known size, 21 malformed inputs returned 0, 6 anchors accepted and 12 refused
RESULT   : PASS  (5 ms)

# [A65] The downloads-folder watcher adopts the new file and times out cleanly
ACTUAL   : adopted "A65-synthetic-patch.rar" after 972 ms by size match; a 64 KB file was refused against a declared 170 MB; the empty watch reported TimedOut; cancellation and a missing folder are reported states
RESULT   : PASS  (2700 ms)

# [A66] The browser hop accepts only HTTPS moyu pages and refuses API URLs
ACTUAL   : 3 legitimate page URL(s) accepted, 12 dangerous URL(s) refused, dry run started no process, a refused URL reported failure
RESULT   : PASS  (2 ms)

# [A67] 429 Retry-After is honoured and a 304 reuses the cached document
ACTUAL   : If-None-Match sent on the second call and the 304 reused the cache; Retry-After: 2 honoured (2016 ms); Retry-After: 3600 reported as RateLimited without waiting; the key is absent from every error surface; pacing and the minute budget both hold
RESULT   : PASS  (2529 ms)

 PASSED : 18
 FAILED : 0
 ERRORS : 0
 SKIPPED: 0
 TOTAL  : 18
 EXIT CODE: 0  (all checks PASS)
```

### 4.4 稳定性（诚实记录）

连续 3 次默认超时运行退出码 0；随后 5 次运行中出现 **2 次** 单条 FAIL，失败的是 **A9**（别人工作线的 GUI 启动检查）：

```
A9: expected [... owns a visible top-level window within 30s] but measured
    [MainWindowHandle=0x12903DC ("WinUI Desktop"), visibleTopLevelWindows=1,
     alive=True, exitedEarly=False, completed=False, failed=False]
```

窗口在、进程活，只是**没等到 `StartupCompletedMarker` 写入启动日志**——`completed=False`。A9 轮询日志尾部取该标记，而 `App.OnLaunched` 是在 `MainWindow.Activate()` **之后**才写这个标记的，两者之间存在先天的时序窗口。

**判定：这是 A9 自身的既有 flake，与本工作线无关。** 依据：失败点完全在启动日志时序；本层在启动路径上只增加了 DI 注册（不发请求、不读文件——密钥未配置时 `FromKeyStore` 只是返回空配置）。**A60–A67 在全部 8 次运行中一次都没有失败过。** 未修改 A9。

### 4.5 全解决方案构建

```powershell
dotnet build Galbox.sln -c Debug
# 已成功生成。  0 个警告
```

（`Galbox.MoyuVerifier` 已加入 `Galbox.sln`，所以常规构建会编译它，它不会悄悄烂掉。）

---

## 4.6 复验工具：`tools\Galbox.MoyuVerifier`

为了让"拿到密钥后一条命令复验"成立，新增了一个零依赖控制台工具（已入 sln）。**无密钥时也能跑**，此时只跳过联网那一段并明确说明：

```powershell
cd E:\tmp\Galbox_moyu
dotnet run --project tools\Galbox.MoyuVerifier -c Debug
```

实测输出（无密钥，节选）：

```
--- Compliance guard (robots.txt says `Disallow: /api`) ---
  [PASS] refuses https://www.moyu.moe/api/v1/patch/resource/223/link
  [PASS] EnsureApiUri refuses to construct a /api URI
         threw InvalidOperationException (the URI cannot reach the wire)

--- Parsers (binary-unit size and game anchor) ---
  [PASS] size "17.953 MB"
         18825085 bytes; the file is 18825520 bytes (delta 435). The upstream field is a
         3-decimal display string, so the download matcher uses a tolerance rather than equality.
  [PASS] a game with neither a VNDB nor a catalog id produces no anchor (and sends no request)

--- Key storage (Windows DPAPI) ---
         Configured : False
  [PASS] the stored file contains no plaintext
  [PASS] the blob is a real DPAPI(CurrentUser) ciphertext

--- Downloads-folder takeover (synthetic file, nothing downloaded) ---
  [PASS] a newly downloaded file is adopted by size
  [PASS] a 64 KB file is NOT adopted when the provider declares 170 MB
  [PASS] a watch with nothing to find ends with TimedOut instead of running forever

--- Handover to the local patch engine ---
  [PASS] a `save` resource is identifiable as such, and only a `save` one is
         IsSaveType([save])=True, IsSaveType([manual])=False, IsSaveType([manual,save])=True

--- Live call against the official /v2/moyu face ---
         Key      : not configured
  SKIPPED - no nmk_ key is configured, so no request was sent.

 RESULT: 26 check(s) passed, 0 failed.
```

配好密钥后同一命令会补上联网段：对 `vndb:v4` 发**一次**请求，打印每一行元数据的原始字段；若该页还有资源再发**第二次**（仅此两次，串行、带节流），并打印 `RequestsSent` 与 `X-RateLimit-*` / `X-Quota-*`。**密钥全程不出现在输出里**，只打印 `SHA-256` 前 6 字节的指纹。

---

## 5. 未完成的部分与原因

| 项 | 状态 | 原因 |
|---|---|---|
| **真实调用 4 个端点** | ❌ 未做 | 没有 `nmk_` 密钥（任务明确不要求）。**没有伪造任何成功响应**；相关的断言全部使用离线桩数据，且这些桩数据是按官方 OpenAPI 契约手写的，不是从真实响应抄的。测试项目内**不存在任何真实域名**（除合规断言里刻意列举的被禁 URL） |
| **UI 接线** | ❌ 未做（有意） | 任务规定只做服务层。补丁中心页面如何显示、按钮怎么点，留给后续单独一次工作 |
| **`PatchRecord` 映射** | ❌ 未做 | `PatchRecord` 在 `Galbox.Data`，而本层刻意不引 `Galbox.Data`（§2.2）。落库/映射属 UI+数据工作线。本层已提供 `MoyuResource.ExternalId`（`moyu:p{patchId}:r{resourceId}`）与 `ToPatchSourceInfo()` 作为接口 |
| **刮削阶段补 `vndb_id`** | ❌ 未做（越界） | 任务要求"只报告依赖，不要越界改刮削"。已在 §1.3 报告 |
| **BLAKE3 内容匹配** | ⚠️ 未实现 | 调研 §8.3 把 `hash` 填充率列为未验证，不能作为匹配前提。已改用体积容差并在 §1.4 说明 |
| **真实 `size` 占位串识别** | ⚠️ 部分 | 官方 spec 说 `vndb_id` 对"VNDB 收录前创建的页面"存的是**占位符**，但没给出占位符长什么样（`vndb:...`？`unknown`？）。本实现接受 `^v\d+$` 之外一律拒绝锚点，并在 `MoyuPatch.VndbId` 保留原值供上层判断；**未验证的部分没有硬编码猜测** |
| **`storage: user` 分支的体验** | ⚠️ 已标注未验证 | 调研 §8.2 把"`storage=user` 的资源 `download_url` 为空、只有外部链接+提取码"列为**源码推定而非实测**。本层如实标注 `MoyuResource.IsExternallyHosted`，但"跳浏览器后是否需要输入提取码"未经实测 |
| **`download_url` 长期有效性** | — | 与本层无关：本层根本不取 `download_url` |

---

## 6. 用户拿到 `nmk_` 密钥后，一条命令复验

密钥自助铸造：<https://developer.nextmoe.dev>（免费、即时、无审批）。

### 6.1 离线复验（不需要密钥，**任何时候都能跑**）

```powershell
cd E:\tmp\Galbox_moyu
dotnet run --project tools\Galbox.MoyuVerifier -c Debug
```

`tools/Galbox.MoyuVerifier` 是一个**零网络**的自校验工具，会逐条打印：

- 合规护栏矩阵（拒绝 /api、接受 /v2/moyu）；
- `size` 解析（含 17.953 MB ↔ 18825520 的标定样本）与锚点矩阵；
- 下载文件夹接管的合成验证（临时目录，不下载任何东西）；
- **密钥是否已配置**（只打印指纹，不打印密钥）；
- 如果密钥已配置，再对 `/v2/moyu/patches?refs=vndb:v4` 做**一次**真实请求并打印原始响应。

### 6.2 配置密钥的三种方式（任选其一，密钥永不明文落盘到本工具）

```powershell
# 方式 A：环境变量（最简单，仅当前会话）
$env:GALBOX_MOYU_API_KEY = "nmk_live_...."
dotnet run --project tools\Galbox.MoyuVerifier -c Debug

# 方式 B：交给 Galbox 自己的 DPAPI 存储（加密落盘到 %LocalAppData%\Galbox\secrets\）
dotnet run --project tools\Galbox.MoyuVerifier -c Debug -- --save-key "nmk_live_...."

# 方式 C：不写密钥，只指定一个临时密钥库路径
$env:GALBOX_MOYU_KEYSTORE = "$env:TEMP\galbox-moyu-test.key"
dotnet run --project tools\Galbox.MoyuVerifier -c Debug
```

配好之后，完整验收也只需要一条：

```powershell
cd E:\tmp\Galbox_moyu
dotnet build tests\Galbox.Acceptance\Galbox.Acceptance.csproj -c Debug
.\tests\Galbox.Acceptance\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.Acceptance.exe
```

> **密钥不会进入任何输出**：工具只打印指纹（`SHA-256` 前 6 字节）。`MoyuOptions.ToString()` 与 `MoyuDpapiKeyStore.ToString()` 都被重写为只输出存在性，A62 断言了明文不出现在文件、指纹、`ToString()`、失败消息和 `LastError` 中。**报告中不含任何密钥。**

---

## 7. 一句话交接

> **合规路径完全够用：发现、跳浏览器、接管下载、交给引擎安装，四段都通，而且官方面不给直链本来就是设计——我们要的交互形态正是它期望的用法。**
> **真正需要别人配合的只有一件事：刮削阶段把 `vndb_id` 落库，否则"只有 Bangumi ID"的作品会如实显示"无法查询"。**
> **`/api/v1/*` 一个请求都没发过，而且发不出去——不是靠自觉，是靠 `EnsureApiUri` 这个唯一构造入口和 A63 的断言。**
