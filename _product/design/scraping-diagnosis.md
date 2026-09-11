# Galbox 元数据刮削链路 白盒诊断报告

- 诊断日期：2026-09-11
- 目标代码：`E:\tmp\Galbox_v2\src`（`Galbox.App` / `Galbox.Core`）
- 诊断方式：**纯只读代码分析 + 真实联网 API 实测**（未修改任何 `.cs`，未构建，未提交）
- 被测环境：Windows，本机用户库 `%LocalAppData%\Galbox\galbox.db`

---

## 0. 一句话结论

**刮削链路从未被触发过（零 UI 入口），而且即便被触发也必然失败——Bangumi 用了已废弃的旧搜索端点导致响应反序列化后恒为空，VNDB 的请求体形状错误被服务端直接 400 拒绝。**

用户库里唯一那条记录 `SabbatOfTheWitch` 的 `IsScraped = 0` 是"两重必然"叠加的结果：
1. 界面上根本没有可点的刮削入口；
2. 即使有人手工调用服务层，Bangumi/VNDB 两个真实数据源都不会返回任何条目。

---

## 1. 证据基础

### 1.1 联网实测记录（原始响应）

所有请求 UA 均为代码中实际配置的 `Galbox/1.0`；请求间隔 ≥ 1s，单端点 ≤ 3 次，未触发限流。

| # | 端点 | 方法 | 请求体 | HTTP | 关键原始响应 |
|---|------|------|--------|------|--------------|
| 1 | `https://api.bgm.tv/search/subject/CLANNAD?type=4&responseGroup=large` | GET | — | **200** | `{"results":15,"list":[…]}`，顶层键仅 `results, list` |
| 2 | 同上 | GET | — | 200 | `list[0]` 键：`id,url,type,name,name_cn,summary,air_date,air_weekday,rating,rank,images,collection`；9 条全部 `type=4` |
| 3 | `https://api.bgm.tv/v0/search/subjects` | POST | `{"keyword":"CLANNAD","filter":{"type":[4]}}` | **200** | 顶层键 `data,total,limit,offset`，`total=5` |
| 4 | `https://api.bgm.tv/v0/subjects/13` | GET | — | **200** | `infobox` 实际键：`别名\|平台\|游戏类型\|游戏引擎\|游玩人数\|发行日期\|售价\|开发\|发行\|剧本\|链接\|音乐\|…`；`rating={"rank":14,"total":6460,"count":{…},"score":8.9}` |
| 5 | `https://api.vndb.org/kana/vn` | POST | **代码原样生成的 body** | **400** | 响应体解码后：`Invalid 'fields' member: The 'titles' object requires specifying sub-field(s).` |
| 6 | 同上 | POST | `{"filters":["search","=","CLANNAD"],"fields":"id,title,alttitle,titles{lang,title,official,main},image.url,rating,length_minutes","results":3}` | **200** | `{"more":true,"results":[{"alttitle":null,"id":"v4","image":{"url":"…"},"length_minutes":4665,"rating":87.2,"title":"CLANNAD","titles":[…]}]}`，**响应中没有 `count` 键** |
| 7 | 同上 | POST | `filters` 为字符串 + 点号 `titles.lang`（无裸 `titles`） | **400** | 响应体解码后：`Invalid 'filters' member: Trailing garbage` |
| 8 | 同上 | GET | — | **404** | 该端点只接受 POST |
| 9 | `/search/subject/千恋万花?type=4&responseGroup=large` | GET | — | 200 | `results=26`，`list[0] = 172612 \| 千恋＊万花 \| 千恋＊万花`（旧端点可以搜中文） |
| 10 | `/v0/subjects/13` | GET | — | 200 | 全量 infobox 键，确认**没有** `developer` / `制作公司` 键 |
| 11 | `/v0/search/subjects` | POST | `{"keyword":"SabbatOfTheWitch","filter":{"type":[4]}}` | 200 | **`total = 0`**（用户库里那条游戏的真实名字，无空格文件夹名，Bangumi 搜不到） |

### 1.2 本机取证（决定性证据）

```
%LocalAppData%\Galbox\
  ├─ ScrapingCache\        ← 目录存在（由 ScrapingCacheService 构造函数创建），但【完全为空】
  ├─ SaveBackups\
  ├─ Screenshots\
  └─ galbox.db             126,976 bytes
```

`ScrapingCacheService.CacheResult()` 对**每一次**搜索都会写一个 `search_*.json`（`SaveCacheToFileFireAndForget`）。目录存在却为空 ⇒ **`GameScrapingService.SearchGameAsync()` 从未成功跑完过一次**。这是"当时压根没人触发过"的铁证。

用户库内容：

```
Id  NameCn  NameOriginal       Developer  Rating  SourceId  SourceType  IsScraped  HasCover  HasDesc
--  ------  -----------------  ---------  ------  --------  ----------  ---------  --------  -------
3           SabbatOfTheWitch                                     0          0         0
```

`NameCn` 为 NULL，`NameOriginal` = 文件夹名（无空格）。这决定了实际搜索词就是 `SabbatOfTheWitch`。

### 1.3 静态取证：UI 入口穷举

| 检查项 | 结果 |
|--------|------|
| `Views/*.xaml` 页面总数 | 6 个：`MainPage` `LibraryPage` `GameDetailPage` `SaveManagerPage` `PatchCenterPage` `SettingsPage` —— **没有 ScrapingProgressPage** |
| `NavigationService._pageMapping`（NavigationService.cs:20-27） | 只有 `Home/Library/SaveManager/PatchCenter/Settings/GameDetail`，**没有刮削页** |
| 全仓库 `.xaml` 中 `Scrap/刮削` 命中 | 仅 `SettingsPage.xaml`（设置项文案），**没有任何按钮/命令绑定** |
| 所有 `Views/*.xaml.cs` 中 `Scrap/Metadata/刮削/元数据` | **零命中** |
| `ScrapingProgressViewModel` 引用点（全仓库） | 仅 `App.xaml.cs:140`（DI 注册）和它自己的文件 —— **没有任何 View 解析它** |
| `IGameScrapingService` 注入点 | 仅 `ScrapingProgressViewModel`（ScrapingProgressViewModel.cs:18） |
| `AutoScrapeOnAdd` 消费点 | 仅 `UserSettings.cs:88`、`GalboxDbContext.cs:253`、`SettingsViewModel.cs:304/543`、`SettingsPage.xaml:281` —— **没有任何服务读取它** |
| `tests/` 中的刮削测试 | **无**（只有 A0 数据库 / A1 可执行文件 / A2 文件夹大小 / A3 存档位置 / A4 错误检查） |

---

## 2. 代码路径速览

```
[用户点击?]  ← 断点在这里：不存在
      │
      ▼
ScrapingProgressViewModel.StartBatchScrapingAsync  (ScrapingProgressViewModel.cs:240)
      │  _autoScrapingService.EnqueueGames(gameIds)      (:253)
      ▼
AutoScrapingService.StartScrapingAsync             (AutoScrapingService.cs:81)
      │  批量 5 个并行 ProcessGameAsync                (:28, :129)
      ▼
AutoScrapingService.ProcessGameAsync               (:358)
      │  IsMetadataComplete(game)? → Skip             (:388)
      │  scrapingService.AutoScrapeAsync(game) × 最多 3 次  (:403)
      ▼
GameScrapingService.AutoScrapeAsync                (GameScrapingService.cs:267)
      │  searchName = NameCn ?? NameOriginal          (:272-276)
      ▼
GameScrapingService.SearchGameAsync                (:74)
      │  ① 磁盘缓存命中？→ 直接 return（跳过网络）    (:82-89)
      │  ② 内存缓存命中？→ 直接 return（30 分钟）     (:92-95, :36)
      │  ③ 4 源并行                                    (:98-103)
      ▼
SearchFromSourceAsync (:138) → SearchBangumiAsync (:329) / SearchVndbAsync (:365) / ymgal·cngal (Stub)
      │  CalculateMatchScore(gameName, item.Titles)   (:204)
      ▼
FindBestMatch (:718)   →  AutoScrapeResult.AutoAccepted = (score >= 90.0)  (:306)
      ▼
AutoScrapingService.ProcessGameAsync: AutoAccepted ? 写库 + IsScraped=true : NeedsReview（不写库）
```

---

## 3. 真实 API 契约核对

### 3.1 Bangumi（api.bgm.tv）

| 核对项 | 代码现状 | 实测真相 | 判定 |
|--------|----------|----------|------|
| 搜索端点 | `GET /search/subject/{kw}?type=4&responseGroup=large`（BangumiApi.cs:34，**私有 v0 旧接口**） | 旧接口仍**可用**（200），但返回 `{"results":N,"list":[…]}`；**官方现行接口是 `POST /v0/search/subjects`**，返回 `{"data":[…],"total":…,"limit":…,"offset":…}` | ❌ 端点用错 |
| User-Agent | DI 注入 `User-Agent: Galbox/1.0`（App.xaml.cs:75） | **实测 200**，三个端点全部通过。UA 不是问题（仅值缺少联系方式，属官方建议而非硬性） | ✅ 正常 |
| `type=4` 过滤 | 硬编码 4 | 实测 9/9 条均 `type=4`（= game）。v0 用 `filter.type:[4]` 同理 | ✅ 正确 |
| 响应模型 | `BangumiSearchResponse` 期待 `total/limit/offset/data`（BangumiApi.cs:74-89） | 旧接口返回 `results/list` → `Data` 恒为 `null` → `Items` 恒为空 | ❌ **致命**（模型本身与 v0 完全吻合，只是配错了端点） |
| 详情端点 | `GET /v0/subjects/{id}`（:45） | 实测 200，模型字段 `name/name_cn/summary/images/rating/infobox/date` **全部匹配** | ✅ 正确 |
| 开发商字段 | `GetDeveloper()` 找 `developer` / `制作公司`（:164-170） | 实测真实 infobox 键是 **`开发`**（另有 `发行`）。没有任何 subject 有 `developer`/`制作公司` 键 | ❌ 恒为 null |
| infobox 取值 | `developerItem?.Value?.ToString()`（:169） | `Value` 是 `object` → 反序列化为 `JsonElement`；`开发` 的值是**数组** `[{"v":"Key"}]`，`JsonElement.ToString()` 对数组返回**原始 JSON 文本** | ❌ 即使键名对了也会得到 `[{"v":"Key"}]` |
| 认证 | `BangumiApi` 从不发 `Authorization` | 实测两个 v0 端点匿名 200，**查询不需要登录**；`BangumiAuthService` 的 OAuth 是 Stub（:320-324），但不影响匿名查询 | ✅ 无关 |

### 3.2 VNDB（api.vndb.org/kana）

**代码实际生成的请求体（原样打印）：**

```json
{"filters":"search ~ \"CLANNAD\"","fields":"id, title, titles, titles.lang, titles.title, titles.official, titles.main, image.url, rating, length_minutes","results":10}
```

实测结果：**HTTP 400**，响应体（ASCII 解码）：

```
Invalid 'fields' member: The 'titles' object requires specifying sub-field(s).
```

去掉裸 `titles`、仅保留 `filters` 为字符串后：

```
Invalid 'filters' member: Trailing garbage
```

| 核对项 | 代码现状 | 实测真相 | 判定 |
|--------|----------|----------|------|
| 端点 | `POST https://api.vndb.org/kana/vn`（VndbApi.cs:42） | ✅ 正确（`GET` 实测 404） | ✅ |
| HTTP 方法 | POST（ApiClient.PostJsonAsync） | ✅ 正确 | ✅ |
| `filters` 形状 | **字符串** `"search ~ \"title\""`（VndbApi.cs:37, :56; 模型 :70-72 `string Filters`） | 必须是 **JSON 数组**：`["search","=","CLANNAD"]`；字符串直接 400 | ❌ **致命** |
| `search` 运算符 | 用了 `~` | `search` 过滤器要求 **`=`**（`~` 用于别的过滤器的模糊匹配） | ❌ 致命 |
| `fields` 形状 | `"id, title, titles, titles.lang, …"`（:38） | 嵌套对象必须用花括号：`titles{lang,title,official,main}`；**裸 `titles` 直接 400** | ❌ **致命** |
| `results` | `int Results = 10`（:77-78） | ✅ 正确（1–100） | ✅ |
| `count` | 模型有 `Count`（:86-87） | 响应**不含** `count`，除非显式请求 `"count":true` → 恒为 0 | ⚠️ 一般 |
| 响应字段 | `id/title/titles/rating/length_minutes/image.url`（:105-135） | 实测 200 响应键完全吻合（`image` 为 `{url}` 对象，`titles` 为对象数组） | ✅ 一致 |
| 详情请求 | `"id = \"{vnId}\""` 同为字符串（:56） | 同样 400 | ❌ 致命 |
| 发行日期 | `GetReleaseDate()` 硬编码 `return null`（:160-165），且 `fields` 未请求 `released` | VNDB 有 `released` 字段但从未请求 | ⚠️ 一般 |

### 3.3 ymgal / cngal

| 核对项 | 代码现状 | 判定 |
|--------|----------|------|
| 是否 Stub | **是**。`YmgalApi.SearchAsync`（YmgalCngalApi.cs:38-52）与 `CngalApi.SearchAsync`（:101-114）只有 `await Task.CompletedTask`，**不发任何 HTTP 请求**，直接返回硬编码的 `Success=false` + 空 `Items` | Stub 确认 |
| BaseAddress | `App.xaml.cs:87-101` **注释掉了**，从未设置 `client.BaseAddress` | ⚠️ 见下 |
| BaseAddress 为空时调用会怎样 | `HttpClient` 发相对 URI 且无 `BaseAddress` 时抛 `InvalidOperationException: An invalid request URI was provided. Either the request URI must be an absolute URI or BaseAddress must be set.` —— **但当前代码根本不发请求，所以这个坑只是定时炸弹**（一旦有人照着 Stub 补实现，第一行就会炸） | ⚠️ 一般（潜在） |
| 附带缺陷 | `SearchYmgalAsync`（GameScrapingService.cs:425-433）与 `SearchCngalAsync`（:459-467）构造 `GameMetadata` 时**没有填 `Titles`**。虽然 `SearchFromSourceAsync:204` 会统一重算 `MatchScore`，但重算时 `Titles` 为空列表 → `CalculateMatchScore` 返回 0 | ⚠️ 一般（潜在） |

---

## 4. 缺陷清单

严重度定义：**致命** = 单独一条就足以造成"永远刮不到"；**严重** = 即使 API 修好也会造成结果丢失/不可诊断；**一般** = 局部缺陷、影响面有限或当前是潜在风险。

| ID | 文件:行号 | 问题描述 | 严重度 | 为什么会导致"永远刮不到" | 一句话修法 |
|----|-----------|----------|--------|---------------------------|------------|
| **D1** | `App.xaml.cs:140`（注册）、`NavigationService.cs:20-27`（无路由）、`Views/`（无页面） | **刮削功能没有任何 UI 入口**：存在 `ScrapingProgressViewModel` 但没有 `ScrapingProgressPage`，`_pageMapping` 无刮削路由，全仓库 XAML 无刮削按钮 | **致命** | 用户点不到 → `StartScrapingAsync` 永不执行 → 库里永远是 `IsScraped=0`。这是"从来没有成功过一次"的第一因 | 新建 `ScrapingProgressPage` + 在「游戏库」加"刮削元数据"菜单项，或去掉 ViewModel 直调服务 |
| **D2** | `BangumiApi.cs:34` + `:74-89` | 搜索调用**旧接口** `GET /search/subject/{kw}?type=…&responseGroup=large`，而响应模型期待 v0 的 `data/total/limit/offset` | **致命** | 旧接口返回 `{results, list}`，`Data` 反序列化恒为 `null` → `Items` 恒空 → Bangumi 源 0 条。且 HTTP **200**，不抛异常，上层只看到"没结果" | 改为 `POST /v0/search/subjects`，body `{"keyword":…,"filter":{"type":[4]}}`；**现有响应模型一行都不用改**（实测完全吻合） |
| **D3** | `VndbApi.cs:37`、`:56`、`:70-72` | VNDB `filters` 被序列化成**字符串** `"search ~ \"…\""`，且用了错误的 `~` 运算符 | **致命** | 服务端实测 **400 `Invalid 'filters' member: Trailing garbage`** → `SearchVndbAsync` 抛 `HttpRequestException` 被捕获 → VNDB 源 0 条。详情查询同样 400 | `filters` 改成 JSON 数组 `["search","=",kw]`（模型字段从 `string` 改为 `object`/数组） |
| **D4** | `VndbApi.cs:38`、`:57` | VNDB `fields` 含裸 `titles`（应为 `titles{…}`），点号语法 `titles.lang` 也非官方嵌套写法 | **致命** | 服务端实测 **400 `Invalid 'fields' member: The 'titles' object requires specifying sub-field(s).`** → 即使 D3 修好，VNDB 仍然全灭 | 改为 `titles{lang,title,official,main}` |
| **D5** | `BangumiApi.cs:164-170` | `GetDeveloper()` 只匹配 infobox 键 `developer` / `制作公司` | **严重** | 实测 Bangumi 真实键是 **`开发`** → 即使详情接口 200，开发商字段也**永远是 null**。这正是用户库里 `Developer` 为空的原因之一 | 键名改为 `开发`（兼容 `开发商`/`developer`），并支持 `发行` 兜底 |
| **D6** | `BangumiApi.cs:169` | `developerItem?.Value?.ToString()`：`Value` 是 `object`→`JsonElement`，其值为**数组** `[{"v":"…"}]`，`ToString()` 返回原始 JSON 文本 | **严重** | 取到的会是 `[{"v":"Key"}]` 而不是 `Key`，写进 `Developer` 字段是垃圾串 | 判断 `JsonElement` 种类，数组则取元素里的 `v` |
| **D7** | `GameScrapingService.cs:82-89` + `:126-130`；`ScrapingCacheService.cs:342-367`、`:232` | **空结果/全失败结果也被缓存**：`CacheResult` 无"结果为空"守卫；`SearchGameAsync` 命中缓存后**直接 return，完全跳过网络**；内存 30 分钟、磁盘 **默认 7 天** | **严重** | 一次网络抖动/一次 400 → 该游戏名**7 天内每次刮削都必然失败**，且用户无法感知原因（缓存命中无日志）。是"一次失败=永久失败"的放大器 | 仅当 `result.HasResults == true` 且无源级错误时才写缓存 |
| **D8** | `GameScrapingService.cs:47`、`:306`、`:614-672` | 匹配算法：先 Trim 后 Levenshtein，相似度 = `(1 - dist/maxLen) * 100`，阈值**硬编码 90.0** 且 `>= 90` 才自动接受 | **严重** | 用代码算法在真实数据上复算：用户库唯一游戏 `SabbatOfTheWitch` vs 正确标题 `Sabbat of the Witch`（只差空格）= **84.21 < 90 → 被丢弃**；跨语言（`9-nine-九次九日九重色` vs `9-nine-ここのつここのかここのいろ`）= 35；标点差异（`千恋万花` vs `千恋＊万花`）= 80 → 全部进 NeedsReview（见 D11 后**等于永久丢弃**） | 比较前归一化（去空格/全半角/标点），"仅空格差异"直接判 90+；阈值改读用户设置 |
| **D9** | `UserSettings.cs:83`、`SettingsViewModel.cs:79/303/542`、`SettingsPage.xaml:244` ↔ `AutoScrapingService.cs:30`、`GameScrapingService.cs:47` | 设置页有"匹配阈值"滑块并持久化到 `MatchThresholdPercent`，但两个服务**都硬编码 `const 90.0`**，从不读取该设置 | **严重** | 用户把阈值调到 50% 也毫无作用；只能靠改代码才能放宽 → 加剧 D8 的丢弃 | 把阈值作为可注入配置，从 `UserSettings.MatchThresholdPercent` 读取 |
| **D10** | `UserSettings.cs:22/27/32/37/77`、`SettingsViewModel.cs:297-307/536-546` ↔ `GameScrapingService.cs:97-103`、`:723` | `EnableBangumi` / `EnableVndb` / `EnableYmgal` / `EnableCngal` 与 `SourcePriorityJson` **只被设置页读写，刮削服务从不消费** | **严重** | 4 个源被无条件并行查询，优先级在 `FindBestMatch` 里硬编码。用户"关闭 ymgal"完全无效，浪费请求并可能触发限流 | 注入设置，按开关与优先级过滤数据源 |
| **D11** | `AutoScrapingService.cs:434-460`（尤其 `:453-460`）+ `ScrapingProgressViewModel.cs:386-430` | 只有 `AutoAccepted == true` 才写库（`:441-447`）；否则标 `SuccessNeedsReview` **不写任何字段**。而承接"人工复核"的 `ApplyMetadataAsync` 所在的 ViewModel **没有任何 View 会解析**（见 D1） | **严重** | 得分 < 90 的**正确结果被静默丢弃**且无处复核。与 D8 组合 = 绝大多数真实游戏永远写不进去 | ① D1 补 UI 让复核可达；② 或让低分结果也写入"待确认"状态而非丢弃 |
| **D12** | `GameScrapingService.cs:188`、`:195`（写入）↔ 全仓库无读取点 | `SourceScrapingResult.ExtendedErrorInfo`（完整异常栈）被采集但**没有任何消费者**；`ManualSearchAsync`（`ScrapingProgressViewModel.cs:488-506`）只显示"未找到匹配项"，丢掉 `result.Errors` | **严重** | HTTP 400 / 反序列化失败 / 空结果**在 UI 上完全同形**——都表现为"没找到"。这就是当年测试报告只能写"整条链路未验证"的技术原因 | 把 `Errors` + `ExtendedErrorInfo` 显示到 UI/日志，区分"网络错误"与"无匹配" |
| **D13** | `ScrapingProgressViewModel.cs:27`、`:56-67` | `SubscribeEvents()` 首行 `if (_eventsSubscribed) return;`，而字段初值为 `true` → **永远不订阅** `ProgressChanged` / `GameCompleted` / `BatchCompleted` | 一般 | 即使 D1 补了 UI，进度条、逐游戏结果、汇总也**永远不更新**，界面像卡死 | 初值改 `false`，或在构造函数直接订阅 |
| **D14** | `App.xaml.cs:87-101`；`GameScrapingService.cs:425-433`、`:459-467` | ymgal/cngal 的 `BaseAddress` 被注释掉从未设置；且两个 `SearchXxxAsync` 构造 `GameMetadata` 时未填 `Titles` | 一般 | 现状无害（Stub 不发请求、空 Items 不合并）。但一旦补实现：`HttpClient` 无 `BaseAddress` 发相对 URI 会抛 `InvalidOperationException`；且 `Titles` 为空 → `MatchScore` 恒 0（`:204`）→ 结果被 D8/D11 丢弃 | 补 `BaseAddress`；补 `Titles` 赋值 |
| **D15** | `VndbApi.cs:56-57`、`:160-165` | 详情请求：同样的字符串 `filters` + 裸 `titles`；`GetReleaseDate()` 硬编码 `return null`，`released` 未在 `fields` 中 | 一般 | 与 D3/D4 同因，VNDB 详情永远失败；发行日期字段永远拿不到（VNDB 是唯一提供发行日期的源之一） | 复用 D3/D4 修法；`fields` 加 `released` 并解析 |
| **D16** | `GameScrapingService.cs:35-44`；`App.xaml.cs:111`；`AutoScrapingService.cs:28`、`:129`、`:374` | 限流状态（`_bangumiRateLimitLock` / `_lastBangumiRequest`）与 `_memoryCache` 都是**实例级**，而 `IGameScrapingService` 注册为 `AddTransient`，批量时每个游戏新建一个实例、5 个并行 | 一般 | 注释声称的"Bangumi 5 req/s"限流在批量路径下**完全不生效**（每实例首次请求都从 `DateTime.MinValue` 起算，不延迟）→ 有被 429 的风险；同时内存缓存形同虚设（每次都新实例） | 限流状态与内存缓存改到单例服务（或 `IScrapingCacheService`）里 |
| **D17** | `ApiClient.cs:71-76`、`:104-108` | `JsonException` 被捕获后只 `Debug.WriteLine` 并 `return default` | 一般 | Release 构建下 `Debug.WriteLine` 无输出 → 反序列化失败被彻底静默，与"无结果"无法区分，直接妨碍排障 | 改成注入 `ILogger` 记录 Error 并把异常信息回流到 `ExtendedErrorInfo` |
| **D18** | `BangumiApi.cs:114-115` | `BangumiSearchItem.Info` 映射键 `"info"`，但 v0 搜索返回的是 `infobox`，旧接口两者都不返回 | 一般 | 死字段。搜索阶段永远拿不到 infobox（开发商只能靠详情接口，而详情接口又因 D5/D6 拿不到） | 改映射 `infobox`，或统一走详情接口取开发商 |
| **D19** | `AutoScrapingService.cs:399-418` | 重试循环捕获 `HttpRequestException`，但 `AutoScrapeAsync` 内部每源都已 `catch` 所有异常（`GameScrapingService.cs:178-196`），**永不上抛**；`break` 条件是 `BestMatch != null`，无匹配时无延迟地重跑 3 次 | 一般 | `catch` 是死代码；无匹配时 3 次迭代（第 2、3 次被 D7 的缓存秒答）→ 幽灵重试，掩盖真实失败 | 让源级错误上抛或以 `BestMatch == null && Errors.Count > 0` 判定重试 |

---

## 5. 第四步：明确判断

### 5.1 已确认的致命缺陷（代码 + 实测双重证据）

| 缺陷 | 代码证据 | 实测证据 |
|------|----------|----------|
| **D1 零 UI 入口** | `App.xaml.cs:140` 注册了 ViewModel；`Views/` 无对应页面；`NavigationService.cs:20-27` 无路由；全 XAML 无按钮 | `%LocalAppData%\Galbox\ScrapingCache\` **目录存在但空** ⇒ `SearchGameAsync` 从未跑完过一次 |
| **D2 Bangumi 端点/模型错配** | `BangumiApi.cs:34` 用 `GET /search/subject/…`；`:74-89` 模型期待 `data` | 旧端点实测 200 且返回 `{"results":15,"list":[…]}`，顶层键为 `results,list`，**无 `data`** ⇒ `Items` 恒空。对照实测 `POST /v0/search/subjects` 返回 `data,total,limit,offset`，与现有模型**逐字吻合** |
| **D3 VNDB filters 形状错** | `VndbApi.cs:37`/`:56` 字符串赋值；`:70-72` `string Filters` | 实测 HTTP **400**：`Invalid 'filters' member: Trailing garbage` |
| **D4 VNDB fields 形状错** | `VndbApi.cs:38` 含裸 `titles` | 实测 HTTP **400**：`Invalid 'fields' member: The 'titles' object requires specifying sub-field(s).` |

**换言之：即使今天把 D1 的 UI 补上，两个真实数据源仍会 100% 返回 0 条。**

### 5.2 疑似缺陷（有代码证据，但未做端到端实测）

- **D5 / D6 开发商字段**：已实测确认 Bangumi 真实 infobox 键是 `开发`（不存在 `developer`/`制作公司`），因此 `GetDeveloper()` 恒 null 是**已证实**的；但"`JsonElement.ToString()` 对数组返回原始 JSON"这一步是基于 .NET 语义推断（未起进程验证），故整体归入疑似。
- **D7 空结果缓存**：代码路径 100% 确定（`CacheResult` 无守卫 + 命中即 return）。未实测是因为无法在不触发 D1 的前提下运行应用。
- **D8 匹配分被丢**：算法已在 PowerShell 中**逐行复刻并实算**（`SabbatOfTheWitch` → 84.21，`千恋万花` → 80，跨语言 → 35），且实测 `POST /v0/search/subjects` 搜 `SabbatOfTheWitch` 返回 **total=0**。结论可靠，但"真实运行时会丢掉多少比例"未做全库统计。
- **D9 / D10 设置不生效**：全仓库 grep 无消费者，静态确定。
- **D11 NeedsReview 永久丢弃**：由 D1 的"复核 UI 不可达"推出，静态确定，未跑运行时。
- **D13 ~ D19**：均为静态代码证据，未实测。

### 5.3 不成立的怀疑（看起来像 bug，其实代码是对的）

| 怀疑 | 结论 | 依据 |
|------|------|------|
| **"没带 User-Agent 被 Bangumi 403"** | ❌ **不成立** | `App.xaml.cs:75` 确实设置了 `User-Agent: Galbox/1.0`。实测该值在旧搜索、`POST /v0/search/subjects`、`GET /v0/subjects/13` 三个端点**全部 200**。UA 不是失败原因（唯一残留风险：该值不含联系方式，属官方建议项，未来 CDN 策略可能收紧） |
| **"需要登录 Bangumi / API Key 才能刮削"** | ❌ **不成立** | 两个 v0 端点匿名实测 200；`BangumiApi` 从不发送 `Authorization`；OAuth 虽是 Stub（`BangumiAuthService.cs:320-324`），但**匿名搜索/详情完全不需要认证**。认证与"永远刮不到"无关 |
| **"VNDB 用错 HTTP 方法（该 POST 用了 GET）"** | ❌ **不成立** | `VndbApi.cs:41-44` 用的是 `PostJsonAsync` → POST，**正确**。反证：`GET /kana/vn` 实测 **404**，证明必须 POST。VNDB 失败是 D3/D4 请求体形状问题，不是动词 |
| **"JSON 大小写/命名风格不匹配导致字段全 null"** | ❌ **不成立** | `ApiClient.cs:222-226` 及各客户端均设 `PropertyNameCaseInsensitive = true`，且**每个模型属性都带显式 `[JsonPropertyName]`**（`name_cn`、`length_minutes`、`image`+`url` 等 snake_case）。实测 VNDB 200 响应的键与模型**完全一致**。Bangumi 的 null 是"键名 `list` vs `data`"这种**结构性**错配，不是大小写问题 |
| **"BaseAddress 没设置导致 Bangumi/VNDB 立刻抛异常"** | ❌ **对 Bangumi/VNDB 不成立** | `App.xaml.cs:74`、`:82` 都设置了 `BaseAddress`；且两个 API 类用的是**绝对 URL**，绝对 URI 优先于 `BaseAddress`，双保险。只有 ymgal/cngal 没设（D14），而它们不发请求 |
| **"90% 阈值本身就是主因"** | ⚠️ **部分不成立** | 阈值不是**主因**（主因是 D1+D2+D3+D4）。包含匹配恰好给 90 且判定是 `>= 90`，所以 `千恋万花`→`千恋＊万花` 之外的大多数中/英同名匹配都能过。但它**确实是"API 修好后仍会失败"的第二道闸门**——用户库里唯一的游戏 `SabbatOfTheWitch` 精确命中了这个陷阱（84.21） |
| **"ymgal/cngal 的 Stub 会返回假数据污染结果"** | ❌ **不成立** | Stub 返回 `Success=false` + **空 `Items`**（`YmgalCngalApi.cs:46-51`、`:108-113`），空列表不参与合并，不会产生脏数据。它只是"没用"，不是"有害" |

---

## 6. "如果现在有人点一下刮削，会发生什么？"

### 事实：**没有可点的地方。**

穷举所有界面：
- 侧边栏只有 主页 / 游戏库 / 存档管理 / 补丁中心（`MainWindow.xaml:44-71`，`NavigationService.cs:22-27`）；
- `ScrapingProgressViewModel` 在 DI 里注册了（`App.xaml.cs:140`），但**没有任何 View 构造它**，它的 `StartBatchScrapingAsync` 命令**没有任何 XAML 绑定**；
- 游戏详情页按钮只有：启动、收藏、角色、打开文档、截图、媒体、备份存档（`GameDetailPage.xaml:175/206/265/322/368/400/432`）—— **没有"刮削"**；
- 设置里的"添加新游戏时自动刮削"开关（`SettingsPage.xaml:281` → `AutoScrapeOnAdd`）**没有任何代码读取**，是纯装饰。

所以最可能的现实是：**用户点了半天找不到入口，然后认为"功能没做"。** 这与测试报告"刮削整条链路未验证"完全吻合。

### 假设有人绕过 UI，直接调用 `SearchGameAsync("SabbatOfTheWitch")`，逐帧推演：

1. `CleanGameName("SabbatOfTheWitch")` → 版本号/括号正则都不匹配 → 原文返回（`GameScrapingService.cs:593-608`）。
2. 查磁盘缓存 `IScrapingCacheService.GetCachedResult` → 目录为空 → miss（`:82-89`）。
3. 查内存缓存 → 首次 → miss（`:92-95`）。
4. 4 个源并行启动（`:98-103`）。
5. **Bangumi 支线**：`GET /search/subject/SabbatOfTheWitch?type=4&responseGroup=large`
   - 服务端返回 **200** + `{"results":0,"list":[]}`（实测：改用 v0 搜同名，`total=0`）；
   - 反序列化成 `BangumiSearchResponse`，`Data` = null（键名是 `list`，模型要 `data`）；
   - `Items` 返回空列表 → **0 条结果**；
   - **不抛任何异常**，`Success=false`、`ErrorMessage=null`（`result.Success = result.Items.Count > 0`，`:176`）。
6. **VNDB 支线**：`POST /kana/vn`，body 为 `{"filters":"search ~ \"SabbatOfTheWitch\", …}`
   - 服务端返回 **400 `Invalid 'fields' member: The 'titles' object requires specifying sub-field(s).`**；
   - `ApiClient.HandleResponseAsync`（`:169-192`）抛 `HttpRequestException`；
   - 被 `SearchFromSourceAsync` 的 catch 兜住（`:183-189`）→ `Success=false`，`ErrorMessage="Network error: API error: BadRequest …"`，`ExtendedErrorInfo` 记下完整堆栈；
   - **0 条结果**。
7. **ymgal / cngal 支线**：Stub 直接返回 `Success=false` + 空 Items，**0 条结果**（`:419`、`:453`）。
8. `Task.WhenAll` 完成，`result.Errors` 里最多只有一条 VNDB 的 `Network error: …`（Bangumi 因为没抛异常，连错误都没有）。
9. `FindBestMatch`（`:718-754`）遍历 4 个源，**全部 `Success == false`** → `bestMatch == null` → 返回 null。
10. `AddToMemoryCache` + `_cacheService.CacheResult(cleanName, result)` **无条件执行**（`:126-130`）→ **这次"全空失败"被写进磁盘，有效期 7 天**。`ScrapingCache\search_sabbatofthewitch.json` 被创建。
11. `AutoScrapeAsync` 返回 `AutoAccepted=false`、`BestMatch=null`。
12. `ProcessGameAsync`：`scrapeResult.BestMatch == null` → `Status = NoMatch`，`ErrorMessage = Errors.FirstOrDefault()`（`:421-426`）。
13. **数据库零写入**：不走 `ApplyMetadataAsync`（`:290`），也不走自动接受分支（`:442`），`IsScraped` 保持 `0`，所有字段保持 NULL。
14. 重试循环第 2、3 次：`AutoScrapeAsync` → `SearchGameAsync` → **命中第 10 步刚写的磁盘缓存 → 直接 return，连网络都不发**（`:82-89`）→ 又是 NoMatch。
15. 界面（如果有）显示"未找到匹配项"；`Errors` 与 `ExtendedErrorInfo` **没有任何 UI 读取**。
16. **7 天内再点，全部瞬时返回同一个空结果。**

### 假设 D1~D4 全部修好，再对同一条记录点一次：

1. `AutoScrapeAsync` 取 `searchName`：`NameCn` 为 NULL → 回退到 `NameOriginal = "SabbatOfTheWitch"`（`:272-276`）。
2. Bangumi v0 搜索 `SabbatOfTheWitch` → 实测 **total = 0** → 仍然 0 条。
3. VNDB 正确请求 `["search","=","SabbatOfTheWitch"]` → 由于无空格，模糊搜索**大概率也匹配不到** `Sabbat of the Witch`（VNDB 的 `search` 是 token 级匹配）。
4. 假设运气好拿到了 `Sabbat of the Witch`：`CalculateMatchScore("SabbatOfTheWitch", ["Sabbat of the Witch"])` → 既非相等也非包含 → Levenshtein → **84.21 < 90**（已实算）→ `AutoAccepted = false`。
5. `Status = SuccessNeedsReview` → **依然不写库**，且复核 UI（D1 刚修的那个）需要用户手动点进去选。
6. **结论：这条记录在 API 修好之后，仍然刮不到。必须同时修 D8（归一化空格）+ D9。**

---

## 7. 交付结论：刮削能不能修好？

### 结论：**能修，而且不需要重写核心架构。**
链路设计（多源聚合 + 评分 + 缓存 + 批量 + 复核）是合理的，`GameMetadata` / `ScrapingResult` 这套模型也基本够用。真正坏掉的是 4 个具体接缝。

### 修复分级

| 级别 | 内容 | 规模 |
|------|------|------|
| **必修（4 项，否则功能恒为 0）** | ① D1 补 UI 入口（一个页面 + 一个菜单项/按钮）<br>② D2 Bangumi 换成 `POST /v0/search/subjects`（**响应模型已吻合，无需改模型**）<br>③ D3 VNDB `filters` 改 JSON 数组 + `search` 用 `=`<br>④ D4 VNDB `fields` 改 `titles{...}` 嵌套语法 | 极小：②③④ 合计约 3~5 行代码 + 1 个模型字段类型改动；① 是新增页面（中等工作量，但可先用"设置页一个按钮"过渡） |
| **强烈建议（不修则"刮到了也存不下来/查不出原因"）** | ⑤ D11 让 NeedsReview 可复核（依赖 D1）<br>⑥ D8+D9 名称归一化 + 阈值读设置<br>⑦ D7 不缓存空结果<br>⑧ D12 把 Errors/ExtendedErrorInfo 暴露到 UI 或日志 | 小~中 |
| **需重写/补实现的部分** | ⑨ **ymgal 与 cngal 是空 Stub**（`YmgalCngalApi.cs:38-52`、`:101-114`），要真正启用必须**从零做 API 集成**：确认端点、设置 `BaseAddress`、建模、填 `Titles`。这两个源目前对功能零贡献 | 中等（各半天~一天，取决于对方是否有公开 API） |
| **质量项** | ⑩ D5/D6 开发商取值<br>⑪ D10 源开关/优先级生效<br>⑫ D13 事件订阅修复<br>⑬ D16 限流与内存缓存提到单例<br>⑭ D17/D19 异常与重试语义 | 小 |

### 外部依赖风险

| 风险 | 说明 | 缓解 |
|------|------|------|
| **Bangumi 旧接口随时下线** | 代码现在用的 `/search/subject/…` 是**未文档化的私有旧接口**。它今天还能用（实测 200），但官方推荐路径是 `/v0/*`。若下线，D2 会从"静默空结果"升级为"硬 404" | 尽快迁到 `v0`（D2） |
| **Bangumi UA 策略** | 实测 `Galbox/1.0` 目前 200 通过。但官方文档要求 UA 携带**可联系信息**，纯产品名有被 Cloudflare 拦截的先例 | 把 UA 改成 `Galbox/1.0 (https://github.com/…)` 形式 |
| **VNDB 字段是版本化契约** | `fields` 必须显式列子字段，任何拼写/语法错误都是**硬 400**（实测已验证两次）。新增字段时必须同步改 `fields` | 给 VNDB 调用加单测（用 Mock 校验请求体形状） |
| **ymgal / cngal 无稳定公开 API** | 代码注释自己写着 "needs research"，`BaseAddress` 被注释掉。ymgal 有社区 API 但需自行调研其稳定性与授权 | 单独评估，不阻塞主链路 |
| **限流/风控** | Bangumi 官方限流较严，D16 使批量场景实际无限流；VNDB 建议 ≤ 4 req/s | 修 D16，把限流状态提到单例 |
| **图片链接是 http** | Bangumi `images.*` 返回 `http://lain.bgm.tv/...`（实测），WinUI 加载 https 页面下的 http 资源可能被拦 | 入库前把 `http://` 升级为 `https://`（实测域名支持 https） |

### 建议的验证顺序（修完以后）

1. 单元级：断言 Bangumi 请求体为 `{"keyword":…,"filter":{"type":[4]}}`、VNDB 请求体 `filters` 是数组且 `fields` 不含裸 `titles`。
2. 契约级：对 `api.bgm.tv` / `api.vndb.org` 各打一次真实请求，断言反序列化后 `Items.Count > 0`。
3. 集成级：用 `SabbatOfTheWitch` 跑一次，先修 D8/D9 让它能过阈值，再断言 `Games.IsScraped = 1`、`SourceId` / `CoverImageUrl` / `Description` 非空。
4. 回归：确认 `%LocalAppData%\Galbox\ScrapingCache\` 里**出现了 `search_*.json`** —— 这是"链路真的跑通了"的最简判据。

---

## 附录：本次诊断的只读保证

- 未修改任何 `.cs` / `.xaml` / 配置文件；
- 未执行 `dotnet build` / `dotnet test`；
- 未执行任何 `git` 写操作，未访问 GitHub；
- 数据库以**副本**方式在 `%TEMP%` 打开查询，原库未被写入；
- 网络实测：Bangumi 6 次、VNDB 4 次，间隔均 ≥ 1s，全部返回后即停止，无高频轮询。
