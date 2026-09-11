# moyu.moe 程序化对接调研报告

- 调研日期：**2026-09-12**（北京时间；服务端 `Date` 头为 `Fri, 11 Sep 2026 16:08–16:09 GMT`）
- 调研方式：**只读 + 本人真实联网实测**。未修改任何现有文件（本报告是唯一新增文件）、未构建、未提交。
- 网络纪律：单站点请求受控，相邻请求间隔 ≥ 0.9–3 秒；全程 **SFW 模式**，未请求、未记录、未下载任何 R18/NSFW 内容；下载验证只做了 **256–2048 字节** 的 Range 请求，未下载完整补丁文件。
- 未做：未注册账号、未铸造任何 API Key、未 clone 仓库、未绕过任何验证码或限流、未使用代理。
- 相关文档：`_product/design/patch-source-research.md`（前置调研）、`_product/Galbox-产品知识总纲.md`

---

## 0. 结论先行

> **能。moyu.moe 可以完整程序化对接——搜索、元数据、直链、实际字节下载，我已逐环节实测跑通，全程无需登录、无需 Token、无需 API Key、无 UA 嗅探。**
>
> **但这不等于"应该这样做"：`https://www.moyu.moe/robots.txt` 明确写着 `Disallow: /api`，而整个可用接口就在 `/api` 底下。**
>
> **一句话：技术门是敞开的，合规门上挂着一把锁，钥匙在站方手里——应该去要，而不是翻墙。**

### 0.1 三句话版本

| 问题 | 结论 |
|---|---|
| moyu.moe 能不能程序化对接？ | **技术上完全能，且已实测到字节层**（见 §3）。搜索/详情/直链/下载四段全通，无鉴权。 |
| 那为什么还不能直接做？ | `robots.txt` 的 `Disallow: /api` + 官方 OpenAPI 里"**链接不能被批量抓走**"的设计声明 + 源码里 `link` 端点的 **30 次/分钟** 限流——**三处独立证据指向同一意图**。 |
| 推荐怎么做？ | **发现**走官方授权面 `/v2/moyu`（免费、有 OpenAPI、有 BYOK）；**下载**跳浏览器；**一键下载暂缓**，先向站方要一句书面许可（见 §7）。 |

### 0.2 本次调研的三个新事实（前置文档没有的）

1. **`robots.txt` 有 `Disallow: /api`。** 前置文档测了 2DFan 的 robots，却**从未测 moyu 自己的 robots**。这一条把前置文档 §3.3 留给产品拍板的"唯一灰区"从**灰区变成了明确的红灯**。
2. **下载链路已经换代，且老结论已过期。** 前置文档手上只有 `oss.moyu.moe` 的静态直链，并在 §7.2 #14/#15 把"链接是否会过期""`dl.imoe.uk` 与 `oss.moyu.moe` 的关系"列为**未验证**。本次实测：现在的正主是 `dl.imoe.uk`（Backblaze B2），由 `artifact_uuid` 签发；`oss.moyu.moe` 作为**遗留静态直链仍然并存可用**。
3. **站方的"藏链接"只做了半程。** `/api/v1/search`（可批量的一侧）**主动清空**了 `content` / `s3_key` 字段；但 `/api/v1/patch/{id}/resource`（单页一侧）**照旧明文返回完整直链**。这是实测到的、可复现的不对称（见 §3.3）。

---

## 1. 对前置文档 `patch-source-research.md` 的指认与更正

> 任务书上说这份文档"研究的是**别的补丁源**（不是 moyu.moe）"。**这个前提不准确，必须先更正**，否则后续会重复劳动或误判。

### 1.1 它到底研究了什么（事实）

读了全文 545 行后确认：**它确实研究了 moyu.moe**，而且是**九源横评**：

| 它的章节 | 覆盖的来源 |
|---|---|
| §1.1–1.4、§2.1（moyu 第一方） | **www.moyu.moe 的 `/api/v1/*`**（实测 200、含 `content` 直链、`HEAD oss.moyu.moe` → 200） |
| §1.3、§2.1 | **moyu 官方 NextMoe 面 `/v2/moyu`**（实测 401、拉到 OpenAPI 21,658 bytes） |
| §2.1 其余行 | NextMoe catalog、2DFan、月幕 ymgal、kungal 论坛、Bangumi、VNDB、Steam、DLsite、汉化组渠道、GitHub 社区清单（11 组查询） |

所以它**不是**"研究了别的源"，而是"研究了包括 moyu 在内的九个源"。

### 1.2 它的结论为什么不覆盖本次问题

它的标题结论是"**程序化下载被契约禁止**"。但拆开看，这句话的**适用域比标题窄得多**：

| 它的结论 | 实际适用域 | 对 moyu.moe 第一方接口是否成立 |
|---|---|---|
| "契约里写死了不给下载直链、提取码、解压密码" | **仅指官方 NextMoe `/v2/moyu` 面**（我本次复验：spec 第 24–27 行原文仍在，见 §5.1） | ❌ **不成立**。第一方 `/api/v1/*` 给直链，我实测拿到了。 |
| "方案 E（第一方抓取）技术上可行，产品上不应该做" | 这是**价值判断/政策建议**，不是技术结论 | ⚠️ 是建议，不是能力边界。 |
| "唯一可讨论的灰区：用户显式点击时帮取那一条直链" | 它自己列为"**待产品拍板的灰区**"（§3.3 末） | ⚠️ **它当时没有 robots.txt 证据**，所以只能算灰区。 |

### 1.3 它真正的空白（本次补上的）

1. **没测 moyu 的 robots.txt** → 漏掉了 `Disallow: /api` 这条决定性证据。
2. **没测下载链路的新形态**（`/link` 端点 + `dl.imoe.uk` + `artifact_uuid`），只握有旧静态链，并把关键疑问挂在 §7.2 的"未验证"里。
3. **没读站点源码**。moyu 的后端是**开源 AGPL-3.0**（`KunMoe/kun-galgame-patch`），路由、限流、下载签发逻辑全都能直接读——这是本次最大的取证来源。
4. **没说清 `/api/v1/search` 与 `/api/v1/patch/{id}/resource` 的字段不对称**。

### 1.4 结论：不重复劳动，但它不能代替本次调研

- **可以沿用的**：它的九源横评、Bangumi 无补丁端点、VNDB 只有语言/补丁标记无文件、月幕只有元数据、2DFan 被 Cloudflare 挡死、GitHub 无社区清单——这些我用不着重测。
- **必须推翻/补充的**：它的"下载不可得"结论**只对官方 API 成立**；对第一方接口，**下载是实测可得的**，真正的障碍是 `robots.txt` 表达的**意愿**，不是技术。

---

## 2. 站点与源码基本盘（实测）

| 项 | 证据 | 结果 |
|---|---|---|
| 站点 | `GET https://www.moyu.moe/robots.txt` | `200`，189 bytes，`Server: cloudflare` |
| 前端 | 页面 HTML | Nuxt 3 SSR（`/_nuxt/*.js`、`/_ipx/` 图片代理、`__NUXT_DATA__` 载荷） |
| 后端 | GitHub `KunMoe/kun-galgame-patch` | **Go (Fiber v3) + GORM + Redis + PostgreSQL，AGPL-3.0，323 ★，`master` 最后推送 2026-09-09**，`homepage = https://www.moyu.moe` |
| 关键分支 | GitHub API `/branches` | **`face-moyu-public-api`** ← 官方公开面的实现分支，本次取证主战场 |
| 项目定位 | 仓库 description | *"The most advanced visual novel patch resource website in the world at the moment! Free forever! 开源, 免费, 零门槛, 最先进的 Galgame 补丁资源下载站, 永远免费！"* |
| Swagger | `GET /swagger` → `404`；`/api/_swagger` → `404`；`/openapi.json` → `404`；`/api/v1/swagger` → `500` | **站点自身没有 Swagger / OpenAPI**。这一点与 `router.go` 源码一致（未注册任何 swagger 路由）。官方 OpenAPI **只在** `developer.nextmoe.dev` 上。 |
| Sitemap | `GET /sitemap_index.xml` → `200`，分片 `moyu-0.xml`… | `moyu-0.xml` 实测 193,441 bytes，含 **1000** 个 `<loc>`；但该分片内 `/patch/` 命中 **0**、`/galgame` 命中 1（`/galgame` 列表页本身）。**分片按类型切分，未逐片穷举**（见 §8 未验证项）。 |

---

## 3. 实测 API 端点清单

所有请求均由本机 `curl.exe` 直接发出，UA 为常规浏览器串（§3.5 另做了非浏览器 UA 对照）。全部走 `https://www.moyu.moe`，**无 Cookie、无 Token、无 API Key**。

### 3.1 端点总表

| # | 端点 | 实测 HTTP | 大小 | 鉴权 | 用途 |
|---|---|---|---|---|---|
| 1 | `GET /robots.txt` | `200` | 189 B | — | 合规依据（§5） |
| 2 | `GET /sitemap_index.xml` | `200` | — | — | 站点自述的发现入口 |
| 3 | `GET /api/v1/galgame?page=1&sort_field=resource_update_time&sort_order=desc&selected_type=all&limit=3` | `200` | 5,465 B | 无 | 补丁列表 / 浏览 |
| 4 | `GET /api/v1/search?keywords=CLANNAD&type=resource&page=1&limit=3` | `200` | 4,622 B | 无 | **按名搜索（资源粒度）** |
| 5 | `GET /api/v1/patch/86` | `200` | 1,833 B | 无 | 补丁详情 |
| 6 | `GET /api/v1/patch/86/resource` | `200` | 3,603 B | 无 | **资源列表（含直链）** |
| 7 | `GET /api/v1/patch/resource/223/link` | `200` | 295 B | 无 | **取下载地址** |
| 8 | `GET /v2/moyu/*`（官方面） | `401` | — | **需 `nmk_` Key** | 官方授权面 |
| 9 | `GET developer.nextmoe.dev/specs/moyu-openapi.yaml` | `200` | 21,658 B | 无 | 官方契约 |

### 3.2 搜索接口（#4）——**实测通过**

请求：
```
GET https://www.moyu.moe/api/v1/search?keywords=CLANNAD&type=resource&page=1&limit=3
```
响应 `200 application/json`（片段，已按 SFW 过滤，未触碰任何 R18 条目）：
```json
{"code":0,"message":"OK","data":{"items":[{
  "id":223,"storage":"s3","name":"",
  "model_name":"","localization_group_name":"",
  "size":"17.953 MB","code":"","password":"",
  "note":"KeyFansClub汉化组汉化，该游戏是CLANNAD FULL VOICE比原版加了语音",
  "blake3":"690eac754d7fcd16ee988a76b9684c4fdad1bf8113f6afccbd76875fd2932de5",
  "s3_key":"","artifact_uuid":"ec3ecb28-2e7e-51fa-bb03-c0df69bc9ac7","content":"",
  "type":["manual"],"language":["zh-Hans"],"platform":["windows"],
  "download":282,"status":0,
  "update_time":"2024-12-13T05:25:54.167Z","like_count":0,
  "user_id":7274,"galgame_id":86,
  "created":"2024-12-13T05:25:54.167Z","updated":"2026-05-27T18:41:40.338Z",
  "user":{"id":7274,"name":"爱吃的萝卜子","roles":["moderator"]},
  "note_html":"\u003cp\u003eKeyFansClub汉化组汉化…\u003c/p\u003e\n",
  "patch":{"id":86,"vndb_id":"v4","name":{"en-us":"CLANNAD","ja-jp":"CLANNAD","zh-cn":"CLANNAD | 克兰纳德","zh-tw":""}},
  "is_liked":false,"is_favorite":false}],"total":N}}
```

**查询参数契约**（来自源码 `apps/api/internal/common/site_search_handler.go:18-29`，均已实测触发校验）：

| 参数 | 约束 | 说明 |
|---|---|---|
| `keywords` | 必填，`max=107` | 关键词 |
| `type` | 必填，`oneof=galgame resource user` | **`resource` 是补丁中心要的道**；`galgame` 是作品道；`user` 是用户道 |
| `page` | 必填，`min=1` | |
| `limit` | 必填，`min=1,max=24` | **单页上限 24**，比官方面的 100 小 |
| `scope` | 可选，`oneof=model` | |
| `sort` | 可选，`oneof=relevance released_desc released_asc updated popularity` | |
| `tag_ids` / `company_id` / `released_from` / `released_to` | 可选 | 高级筛选 |

列表接口 `GET /api/v1/galgame` 的参数（来自 `router.go` + `galgame_list.go`）：`page`、`sort_field`、`sort_order`、`selected_type`、`limit`、以及 `language` / `platform`（逗号分隔，白名单 `zh-Hans,zh-Hant,ja,en,other` / `windows,android,macos,ios,linux,other`）、`library`、`indexed`、`released_from/to`、`released_months`。无关键字搜索能力——**搜索必须走 `/api/v1/search`**。

### 3.3 详情与资源（#5 #6）——**实测通过，并发现字段不对称**

`GET /api/v1/patch/86` → `200`，1,833 B，含 `id / name{4 语言} / vndb_id:"v4" / bangumi_id:null / type / language / platform / content_limit:"sfw" / release_date / resource_update_time / count{resource,favorite_by,comment} / user / creator / galgame{...catalog_work_id:86, age_limit:"all"...}`。**注意 `bangumi_id` 为 `null`** —— 与前置文档 §2.2(A) 的"moyu 的 bangumi_id 有错链/不可靠"一致，本次再次确认**不能拿它当主键**。

`GET /api/v1/patch/86/resource` → `200`，3,603 B。**这一条是关键**：

```json
{"code":0,"message":"OK","data":[{
  "id":6262,"storage":"s3",
  "name":"【Key Fans Club×光坂高校中文部×dwing整合校对&V2.0】CLANNAD FULL VOICE - 人工汉化补丁",
  "localization_group_name":"Key Fans Club×光坂高校中文部×dwing整合校对&V2.0",
  "size":"17.953MB",
  "note":"Key - CLANNAD FULL VOICE 中文化补丁\n\n…（Markdown）",
  "blake3":"690eac754d7fcd16ee988a76b9684c4fdad1bf8113f6afccbd76875fd2932de5",
  "s3_key":"patch/243/690eac…/Key20040414CLANNADFULLVOICE…CHS.rar",
  "artifact_uuid":"648ab9e2-c06f-50a9-9d13-ea7742f95dbe",
  "content":"https://oss.moyu.moe/patch/243/690eac…/Key20040414CLANNADFULLVOICE…CHS.rar",
  "type":["manual"],"language":["zh-Hans"],"platform":["windows"],
  "download":122,"status":0,
  "update_time":"2025-11-11T08:31:33.206Z","like_count":0,
  "user_id":2310,"galgame_id":86, …}]}
```

> **实测发现（不对称）**：同一个资源 `id=6262`，
> - 在 `/api/v1/patch/86/resource` 里 `s3_key` 与 `content`（**完整 `oss.moyu.moe` 直链**）**都是明文非空**；
> - 在 `/api/v1/search?type=resource` 里同一 `id=6262` 的 `s3_key` 与 `content` **都被清空成 `""`**。
>
> 三个返回项的字段填充实测对照（对 `s1.json` 逐项解析）：

| 资源 id | storage | `s3_key` 非空 | `artifact_uuid` 非空 | `content` 非空 | size |
|---|---|---|---|---|---|
| 223 | s3 | 否 | **是** | 否 | `17.953 MB` |
| 8357 | **user** | 否 | 否 | 否 | `823.82MB` |
| 6262 | s3 | 否 | **是** | 否 | `17.953MB` |

→ **`/api/v1/search` 明确剥离了 `s3_key` 与 `content`**（6262 在另一端点是有值的，可交叉验证）；但 `/api/v1/patch/{id}/resource` **没有剥离**，直链照给。站方"链接不能被批量抓走"的加固**只做了搜索道**。

### 3.4 取下载地址（#7）——**实测通过，这是决定性的那一条**

```
GET https://www.moyu.moe/api/v1/patch/resource/223/link
→ HTTP 200, application/json, 295 bytes
```
```json
{"code":0,"message":"OK","data":{
  "code":"",
  "content":"https://oss.moyu.moe/patch/243/690eac754d7fcd16ee988a76b9684c4fdad1bf8113f6afccbd76875fd2932de5/KeyFansClubCLANNADFULLVOICE.rar",
  "download_url":"https://dl.imoe.uk/moyu/ec3ecb28-2e7e-51fa-bb03-c0df69bc9ac7.rar",
  "password":"",
  "storage":"s3"}}
```

**两个 URL 同时返回**：
- `content` = **遗留静态直链**（`oss.moyu.moe`，按 `s3_key` 拼出）
- `download_url` = **当前正主**（`dl.imoe.uk`，由 `artifact_uuid` 向 artifact 服务签发）

**无需登录、无需 Token、无需 API Key。**

### 3.5 鉴权需求：**不需要**（已用多种方式交叉验证）

| 验证项 | 做法 | 结果 |
|---|---|---|
| Cookie | 全程未发送任何 Cookie | 全部 `200` |
| Referer | 全程未发送 Referer | 全部 `200` |
| Token / Key | 全程未发送任何 `Authorization` / `X-API-Key` | 全部 `200` |
| UA 嗅探 | 用 `Galbox/2.0 (Windows NT 10.0; Win64; x64; .NET 8) moyu-research` 重放下载请求 | `206`，256 bytes，**与浏览器 UA 结果一致** |
| 源码佐证 | `router.go` 中读端点全部挂 `optionalAuth`（而非 `auth`），`/link` 额外挂 `RateLimit` | 匿名放行是**设计如此**，不是配置疏漏 |

### 3.6 限流：**源码级证据**（本次最强的"意图"证据）

`apps/api/internal/app/router.go` 原文：
```go
patchRoutes.Get(
    "/resource/:resourceId/link",
    optionalAuth,
    middleware.RateLimit(a.RDB, "resource-link", 30, time.Minute),   // ← 30 次/分钟
    a.PatchHandler.GetResourceDownloadInfo,
)
```
`apps/api/internal/middleware/ratelimit.go` 实现：Redis `INCR` 计数，**已登录按 `userID`、未登录按 `IP`**，窗口 60 秒，超限返回 `ErrTooManyRequests`。

> **诚实评估**：30 次/分钟**按 IP** 对**单个桌面用户**来说一点都不紧——用户一分钟内点不出 30 次"显示链接"。**所以限流本身不是技术障碍**，它的意义是**意图声明**：站方把"显示链接"当成一件需要被计量的、不能批量的动作。**批量刮取**才会撞上它。

---

## 4. 下载链路实测证据

### 4.1 结论：**链路完全可行，已实测到字节层**

链路全貌（每一段都实测过）：
```
/api/v1/search 或 /api/v1/patch/{id}/resource
        ↓ 得到 resource.id / artifact_uuid / s3_key
/api/v1/patch/resource/{id}/link
        ↓ 得到 download_url（+ 遗留 content）
https://dl.imoe.uk/moyu/{artifact_uuid}.rar
        ↓ 206 Partial Content
真实 RAR5 字节
```

### 4.2 实测响应头（决定性证据）

**A) 新链路 `dl.imoe.uk` —— Range 请求 0–2047 字节**
```
GET https://dl.imoe.uk/moyu/ec3ecb28-2e7e-51fa-bb03-c0df69bc9ac7.rar
Range: bytes=0-2047

HTTP/1.1 206 Partial Content
Content-Type: application/octet-stream
Content-Length: 2048
content-range: bytes 0-2047/18825520
Content-Disposition: attachment; filename*=UTF-8''KeyFansClubCLANNADFULLVOICE.rar
Cache-Control: max-age=14400
x-bz-file-name: moyu/ec3ecb28-2e7e-51fa-bb03-c0df69bc9ac7.rar
x-bz-file-id: 4_z8aba085affc6e8ad95e60417_f118d7c4c18c4babc_d20260622_m125755_c005_v0501043_t0049_u01782133075847
x-bz-content-sha1: 3cb77ac34c721e9686ec162ea9c7897d67e91674
X-Bz-Upload-Timestamp: 1782133075847
Strict-Transport-Security: max-age=63072000
Server: cloudflare
cf-cache-status: MISS
```

**B) 文件头 16 字节（确认是真文件，不是错误页）**
```
52 61 72 21 1a 07 01 00 57 fb a3 15 0d 01 05 09
ascii: Rar!....
```
`52 61 72 21 1a 07 01 00` 是 **RAR5** 的标准魔数 —— 下载到的确实是补丁压缩包。

**C) 大小自洽校验（三重吻合）**
| 来源 | 值 |
|---|---|
| `content-range` 总长 | `18825520` 字节 |
| API 的 `size` 字段 | `"17.953 MB"` |
| `18825520 / 1048576` | `17.953110` ✅ **完全吻合** |

→ **`size` 字符串用的是二进制单位（MiB/GiB），1 MB = 1048576 字节。** 这是本次实测标定的换算常数，可直接写进解析器（§6.3）。

**D) 遗留链路 `oss.moyu.moe`（对照）**
```
GET https://oss.moyu.moe/patch/243/690eac…/KeyFansClubCLANNADFULLVOICE.rar
Range: bytes=0-255
→ HTTP/1.1 206 Partial Content
  Content-Type: application/octet-stream
  Content-Length: 256
  content-range: bytes 0-255/18825520      ← 同一文件、同一大小
  Cache-Control: max-age=720000
  Server: cloudflare
```
**两条链路并存，服务同一份字节。**

**E) 非浏览器 UA + HEAD**
```
GET (UA = Galbox/2.0 (Windows NT 10.0; Win64; x64; .NET 8) moyu-research)
→ HTTP/1.1 206 Partial Content, 256 bytes, content-range: bytes 0-255/18825520

HEAD https://dl.imoe.uk/moyu/ec3ecb28-….rar
→ HTTP/1.1 200 OK
  Content-Type: application/octet-stream
  Content-Length: 18825520
  Accept-Ranges: bytes
```
→ **无 UA 嗅探；`HEAD` 可用（可先查大小再决定要不要下）；`Accept-Ranges: bytes` 可用（可断点续传）。** 这对动辄上 GB 的补丁（站方公告：管理员单文件上限 **20 GB**）是必需的。

### 4.3 `download_url` 的有效期：**形态上像长期直链，但未做时间验证**

| 观察 | 含义 |
|---|---|
| URL **不带任何 query string** | 没有 `X-Amz-Signature` / `Expires` 之类的预签名参数 |
| 路径是 `/{artifact_uuid}.rar`（**内容寻址**，不是时间戳路径） | 形态上是稳定的公开直链 |
| 响应头只有 `Cache-Control: max-age=14400` | 这是 **CDN 缓存指令（4 小时）**，**不是** URL 失效时间 |

> ⚠️ **诚实标注**：`Cache-Control: max-age=14400` 常被误读成"链接 4 小时后失效"，**不是**。但要证明"永不过期"必须**跨时段复测**，本次调研（几小时内）**做不到**。
> 另外，前置文档 §1.3 读到的基础设施文档曾把 artifact 的下载描述为"**短时效（约 1 小时）预签名**"——**这与本次实测的 URL 形态（无签名、无过期参数）不符**。
> → **判定：`download_url` 在本次实测时点完全可用；其长期稳定性"未验证成功"，落地前必须补 24h 以上的跨时段复测。**

### 4.4 源码佐证：链接是怎么签发的

`apps/api/internal/patch/service/service.go:1001`
```go
func (s *PatchService) ResolveDownloadURL(ctx context.Context, r *model.PatchResource) error {
	if r == nil || r.ArtifactUUID == "" {
		return nil                       // ← storage=user 的无 artifact，直接返回空
	}
	dl, err := s.art.Download(ctx, r.ArtifactUUID)
	if err != nil {
		return fmt.Errorf("获取下载地址失败: %w", err)
	}
	r.DownloadURL = dl.Url
	return nil
}
```
`apps/api/internal/patch/handler/handler.go:635` 的 `GetResourceDownloadInfo` 把它包成响应，并做三道闸：内容分级闸（`gatePatchByContentLimit`）、`status==2` 视为不存在、`status!=0` 返回 `40310 该资源已被禁用`。

**由代码可推定（非本次实测）**：`storage == "user"`（发布者放在别处）的资源 `ArtifactUUID` 为空 → `download_url` 为空，此时只有 `content` 字段（外部链接）+ `code`/`password`（提取码）可用。§3.3 表中 id=8357 正是 `storage=user` 且 `artifact_uuid`/`content` 皆空，与代码路径一致——但我**没有**去对这条具体资源调用 `/link` 验证（它属于 R18 内容，本次明确回避）。

---

## 5. 合规与站点友好性（**必答项**）

### 5.1 证据一：`robots.txt` —— 明确 `Disallow: /api`

`GET https://www.moyu.moe/robots.txt` → `200`，`text/plain; charset=utf-8`，**189 bytes，全文如下（逐字）**：

```
User-agent: *
Allow: /
Disallow: /admin
Disallow: /api
Disallow: /auth
Disallow: /edit
Disallow: /me
Disallow: /message
Disallow: /settings

Sitemap: https://www.moyu.moe/sitemap_index.xml
```

**这是本次调研最重要的一条。**

- 它 `Allow: /`，所以**页面**（`/patch/{id}/introduction`、`/galgame`、`/calendar`）是**明确欢迎抓取的**，并且主动发布了 sitemap。
- 它把 `/api` 与 `/admin` / `/auth` / `/me` / `/settings` 这些**管理性和私有性路径并列禁止**。
- 而**全部可用的数据接口都在 `/api/v1/*` 底下**——也就是说：**站方开放的是"页面"，禁止的是"接口"。**

### 5.2 证据二：官方 OpenAPI 写明了链接的设计意图

`GET https://developer.nextmoe.dev/specs/moyu-openapi.yaml` → `200`，**21,658 bytes**（与前置文档昨日记录的大小一致，说明**未变更**）。第 24–27 行原文：

> It also carries **no download link, share code or password**. Revealing a link on moyu is a separate, rate-limited, per-resource request whose whole purpose is that **links cannot be harvested in bulk**. Every row carries a `web_url` instead — send a reader there.

`Resource` schema 的 description（第 456–458 行）再次重申：
> One downloadable item. **Carries no link, code or password by design** — `web_url` is the way to it.

spec 仅 4 条路径：`/v2/moyu/patches`、`/v2/moyu/patches/{id}`、`/v2/moyu/patches/{id}/resources`、`/v2/moyu/resources/{id}`。`info.x-stability: stable`，要求 `X-API-Key: nmk_live_…` 或 `Authorization: Bearer nmk_live_…`。

### 5.3 证据三：源码里那个 30/分钟 的限流

见 §3.6。`/link` 是**唯一**挂 `RateLimit` 的下载相关读端点，而它恰好就是"显示链接"那一下。

### 5.4 服务条款 / 内容规范页

站点 `/doc` 下的文章目录（实测 `/doc` → `200`，122,081 bytes）中与使用规范直接相关的有：

| 路径 | 标题 | 本次取到的情况 |
|---|---|---|
| `/doc/notice/forward-patch` | 全体补丁作者关于补丁资源转载的**重要公告** | **已取正文**（见下） |
| `/doc/notice/privacy` | 鲲 Galgame 补丁**用户协议以及隐私政策** | 标题已确认存在；**正文未能完整提取**（见 §8） |
| `/doc/notice/rule` | 鲲 Galgame 补丁规定 | 未取 |
| `/doc/notice/open-source` | 开源声明 | 未取 |
| `/doc/galgame/resource` | 补丁资源发布规范 / 资源系统介绍 | 未取 |
| `/doc/dev/documentation` | **开发文档** | 未取（值得补，可能含官方开发者立场） |

**转载公告正文（实测提取，节选）**：
> **先授权，后转载**……在转载或搬运任何**非您原创**的补丁资源前，**必须**首先联系原作者，并明确获得其转载许可。
> **转载补丁资源需要授权凭证**……**任何无授权证明的转载，都将被初步视为"违规搬运"。**
> **违规处罚措施**：首次发现——删除相关补丁帖子并向发布者发送警告通知；累计三次——**永久封禁其账户**。

> **准确解读（不要过度外推）**：这条公告规范的是**站内用户之间"搬运/转载补丁资源"**（把别人的补丁重新上传到 moyu），**不是**针对 API 程序化访问的条款。
> **但它确立了站方的核心立场：对"未经授权的二次流转"零容忍。** 如果 Galbox 去镜像、缓存、再分发补丁文件，就正撞在这条上。

### 5.5 站点性质与负担评估

| 判断项 | 证据 | 结论 |
|---|---|---|
| 运营性质 | 仓库 description："开源, 免费, 零门槛……**永远免费**"；`AGPL-3.0`；站点源码公开 | **社区/公益性质的开源站**，不是商业公司 |
| 规模 | 前置文档实测列表页"共 4,225 部"；公告："已上传完毕世界上绝大部分 Galgame 汉化补丁下载资源**[共计 1527 个]**" | 中等规模，个人/小团队可维护 |
| 基础设施 | `Server: cloudflare`（前置）+ `x-bz-*` 头（本次）→ **Cloudflare + Backblaze B2** | 有 CDN 前置与对象存储，边际带宽成本**不是零** |
| 单文件上限 | 公告：管理员 20 GB / 创作者 5 GB / 普通用户 1 GB | 大文件站，**下载流量是真实成本** |
| 负担敏感点 | 补丁本体走 `dl.imoe.uk`（B2 + Cloudflare），**下载流量直接产生 B2 出网费用** | **大批量下载会真花钱**；少量用户触发式下载则几乎无感 |
| 反爬 | 前置文档：无 JS 挑战、无验证码；本次：全部 `200` 无挑战 | **站方没有设技术壁垒，而是用 robots + 文档 + 限流表达意愿** |

### 5.6 明确建议：**怎么对接才叫对站点友好且合规**

**红线（不做）**

1. ❌ **不以 `/api/v1/*` 作为产品数据源。** `robots.txt` 写了 `Disallow: /api`。
2. ❌ **不批量抓取全站补丁清单、不建立本地镜像、不缓存全站数据。**
3. ❌ **不把补丁文件再托管、再分发、做 P2P 或镜像。** 撞 §5.4 转载公告的立场。
4. ❌ **不预取、不预热、不后台全量轮询。**
5. ❌ **不请求 18+ / NSFW 内容**（默认 SFW；不传任何放开分级参数）。

**绿灯（做）**

6. ✅ **只在用户主动搜索/打开某游戏时发请求**，一次交互 = 一次请求，绝不预取。
7. ✅ **带可识别、可联系的 User-Agent**（现有客户端是 `Galbox/1.0`，**信息太少**）。建议：
   `Galbox/2.0 (Windows; .NET 8; +https://<项目主页>; contact:<邮箱>)`
8. ✅ **严格限流**：客户端侧自建令牌桶，建议 **≤ 1 请求/秒**，并设更低的分钟上限；`/link` 类调用**每次都由用户显式点击触发**。
9. ✅ **不做站点数据落库**（除用户自己关注的那几条），**不提供"导出全站清单"功能**。
10. ✅ **署名 + 链接**：在补丁中心页脚与"关于"页标注 *"补丁数据来自 鲲 Galgame 补丁 (www.moyu.moe)"* 并给出可点击链接。
11. ✅ **尊重 429**：读 `Retry-After` 退避；不重试到把配额打满。
12. ✅ **本地安装能力必须独立于网络可用性**——站点挂了，Galbox 仍能把用户已下载的补丁装好。

### 5.7 关于 `robots.txt` 的一个诚实注记（**不要把话说满**）

严格来说，`robots.txt`（RFC 9309）规范的对象是**自动化爬虫（robots/crawlers）**，其语义是"请勿自动遍历抓取"。**一个人类用户在桌面客户端里点一下"搜索"，在形式上并不构成机器人抓取**——这是真实存在的解释空间，我不打算假装它不存在。

但我仍然建议**尊重站方意图**，理由有三条，且我愿意为这个判断负责：

1. **三处独立证据指向同一意图**：robots 禁 `/api`、spec 写"链接不能被批量抓走"、源码给 `/link` 挂 30/分钟限流。指出这一点不需要推测——它们是三个各自独立的、站方主动写下的声明。
2. **风险不对等**：Galbox 是**要分发的产品**。一次越界的代价（被拉黑、被封 IP、被公开点名，乃至前置文档 §6.1 讨论的著作权与传播刑事风险）远高于"多一步跳浏览器"的体验损失。
3. **合规版本的成本其实很低**：官方**专门建了** `face-moyu-public-api` 分支和 `developer.nextmoe.dev` 开发者平台，正是为下游应用准备的。**放着授权的路不走，去走被 Disallow 的路，是技术上的懒惰，不是产品上的必要。**

---

## 6. C# 集成设计

### 6.0 先看清现状（避免照着空壳学）

| 现有类 | 实际状态 |
|---|---|
| `BangumiApi.cs`（14,399 B） | ✅ **真实实现**：`POST /v0/search/subjects`、`GET /v0/subjects/{id}`、角色查询；类注释里还留了 D2 缺陷的修复说明 |
| `VndbApi.cs`（9,012 B） | ✅ **真实实现**：Kana API `POST /kana/vn`，含字段语法踩坑注释（`filters` 必须是数组、`titles` 必须用花括号） |
| `YmgalApi.cs` / `CngalApi.cs`（同文件，7,827 B） | ❌ **是 stub**。`SearchAsync` 里只有 `await Task.CompletedTask;` 然后返回 `Success=false, Message="...pending - needs API research"`；`GetGameAsync` 直接 `return null`。**不要以它们为范本。** |
| `PatchRecord.cs` | ✅ 实体完整（13 个业务字段 + `PatchStatus` / `PatchType` 枚举） |
| `PatchCenterViewModel.cs` | ⚠️ **当前无任何数据源**。类注释写明："Patch sources are not wired up yet (the moYu/NextMoe integration is a separate work line)"；并说明**旧的伪造生成器（`stub_trans_*`、`{game} 汉化补丁`、`https://moyu.moe/patches/example`）已被彻底移除**。（注：那个假 URL 连域名和路径都是错的——真站是 `www.moyu.moe`，真路径是 `/patch/{id}/introduction`。） |
| DI 注册 | `App.xaml.cs:104-142`，模式为 `services.AddHttpClient<XxxHttpClient>().ConfigureHttpClient(c => { BaseAddress; User-Agent "Galbox/1.0"; Timeout 30s; })` |

**结论：补丁中心的"在线半边"目前是空的。** 本报告要填的就是这块。

### 6.1 新增类清单

```
src\Galbox.Core\Api\
  ├─ MoyuApi.cs                  ← 新：ApiClient 子类（对齐 BangumiApi / VndbApi 风格）
  ├─ MoyuHttpClientWrapper.cs    ← 新：或者直接加进现有的 HttpClientWrappers.cs（推荐，保持一致）
  ├─ MoyuOptions.cs              ← 新：密钥 + 限流 + 端点开关
  └─ MoyuModels.cs               ← 新：DTO（也可像 VndbApi 那样内联在 MoyuApi.cs 的 #region 里）
src\Galbox.Core\Patch\
  ├─ MoyuPatchMapper.cs          ← 新：DTO → PatchRecord 映射 + size 解析 + 类型枚举映射
  ├─ PatchSizeParser.cs          ← 新：解析 "17.953 MB" / "823.82MB" → long
  └─ IMoyuRateLimiter.cs         ← 新：令牌桶（客户端侧自律限流）
tests\Galbox.Core.Tests\
  └─ Moyu\
     ├─ MoyuApiTests.cs
     ├─ MoyuPatchMapperTests.cs
     └─ Fixtures\*.json          ← 合成夹具（不联网）
```

`HttpClientWrappers.cs` 追加（与现有四个 wrapper **完全同构**）：
```csharp
/// <summary>
/// Typed HttpClient wrapper for the moyu patch API.
/// </summary>
public class MoyuHttpClient
{
    public HttpClient HttpClient { get; }
    public MoyuHttpClient(HttpClient httpClient)
        => HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
}
```

`App.xaml.cs` 追加（对齐既有模式）：
```csharp
services.AddHttpClient<MoyuHttpClient>()
    .ConfigureHttpClient(client =>
    {
        client.BaseAddress = new Uri("https://api.nextmoe.dev/v2/moyu/");
        // 可识别 + 可联系（现有四个客户端只写 "Galbox/1.0"，信息量不足）
        client.DefaultRequestHeaders.Add("User-Agent",
            "Galbox/2.0 (Windows; .NET 8; +https://<项目主页>; contact:<邮箱>)");
        client.Timeout = TimeSpan.FromSeconds(30);
    });
```

### 6.2 建议的接口签名

按**当前唯一合规可用**的路径（官方 `/v2/moyu`）设计：

```csharp
public class MoyuApi : ApiClient
{
    private const string BaseUrl = "https://api.nextmoe.dev/v2/moyu";

    public MoyuApi(MoyuHttpClient wrapper, MoyuOptions options, IMoyuRateLimiter limiter)
        : base(wrapper.HttpClient) { /* 存 options / limiter */ }

    /// <summary>
    /// 按作品锚点批量反查补丁页。一次最多 100 个锚（官方 spec 约束）。
    /// 锚只接受 "vndb:vXXXX" 与 "catalog:&lt;id&gt;" 两种 —— 不支持 Bangumi id。
    /// </summary>
    Task<MoyuPatchListResponse?> FindPatchesAsync(
        IReadOnlyList<string> refs,
        bool includeResources = true,
        CancellationToken ct = default);

    /// <summary>取单个补丁页。</summary>
    Task<MoyuPatch?> GetPatchAsync(
        string patchId, bool includeResources = true, CancellationToken ct = default);

    /// <summary>取补丁页下的资源，游标翻页。</summary>
    Task<MoyuResourceListResponse?> ListResourcesAsync(
        string patchId, string? cursor = null, int limit = 50, CancellationToken ct = default);

    /// <summary>取单个资源。</summary>
    Task<MoyuResource?> GetResourceAsync(
        string resourceId, CancellationToken ct = default);

    /// <summary>本地记录：这条资源对应的网页地址（给用户点"去下载"）。</summary>
    static string BuildWebUrl(MoyuResource r) => r.WebUrl;
}
```

> ⚠️ **两个必须处理的现实约束：**
> 1. **`ApiClient` 基类不够用。** `GetJsonAsync<T>` 走 `HttpClient.GetStringAsync(url)`，**无法挂 `X-API-Key` 请求头**，也**不支持 `If-None-Match` / 304**。`MoyuApi` 必须自带一条 `SendAsync(HttpRequestMessage)` 路径，**不能**直接复用基类方法。
> 2. **`ApiClient` 的 429 处理不看 `Retry-After`。** `HandleResponseAsync` 抛 `HttpRequestException(statusCode)`，`ShouldRetry` 会重试 429 并用 2× 乘数退避，但**完全忽略响应头的 `Retry-After` / `X-RateLimit-*` / `X-Quota-*`**。MoyuApi 需要在基类之上补一层读取这些头的逻辑（或给 `ApiClient` 加一个 `protected virtual` 钩子）。

**BYOK（Bring Your Own Key）**：`MoyuOptions` 持有 `nmk_live_…` 密钥，来源是用户在 `https://developer.nextmoe.dev` **自助铸造**（免费、即时、无需审批、无 scope）。存储用 **Windows DPAPI**（`ProtectedData.Protect`，`DataProtectionScope.CurrentUser`）；**绝不明文落配置、绝不进日志、绝不进诊断包**。Galbox **不打包任何密钥**——泄漏的永远是用户自己的，配额记在用户自己头上。

### 6.3 数据结构：补丁条目需要哪些字段才能填满 `PatchRecord`

`PatchRecord` 有 13 个业务字段。**逐字段映射如下**（左列取自 `PatchRecord.cs` 实测行号）：

| # | `PatchRecord` 字段 | 类型/约束 | 数据来源 | 备注 |
|---|---|---|---|---|
| 1 | `Name` | `string`, MaxLength **500** | `resource.Name`；若为空（实测 id=223 的 `name` 就是 `""`）→ 回退 `patch.Name["zh-cn"] + 类型标签` | **必须处理空 name** |
| 2 | `PatchType` | `string`, MaxLength **50** | 官方 `type[]` 12 值枚举 → 中文标签 | 见 §6.4 映射表 |
| 3 | `Version` | `string?`, MaxLength **100** | **无结构化来源 → 留 null** | 站方不给版本号；从 `name` 里抠 `V2.0` 只能用于**展示**，不要写进版本字段（前置文档 §5.2 已论证） |
| 4 | `SizeBytes` | `long` | 官方 `size` 字符串解析 | **实测标定：单位是二进制（1 MB = 1048576）**，见下 |
| 5 | `Source` | `string?`, MaxLength **200** | 常量 `"moyu.moe"` | |
| 6 | `DownloadUrl` | `string?`, MaxLength **2000** | 官方面**不提供** → 存 `web_url` | 若将来拿到站方授权再存真实直链；**且直链有时效，不应长期入库** |
| 7 | `LocalPath` | `string?`, MaxLength **2000** | 用户下载后拖入 / 选择 | 与在线半边解耦 |
| 8 | `Status` | `PatchStatus` | 本地状态机 | 见 §6.5 |
| 9 | `Description` | `string?`, MaxLength **5000** | `resource.Note`（Markdown） | 超长需截断；`note_html` 不要入库（渲染成 HTML 有 XSS 面） |
| 10 | `AddedTime` | `DateTime` | `resource.CreatedAt` | |
| 11 | `DownloadedTime` | `DateTime?` | **本地事件时间** | 上游没有 |
| 12 | `InstalledTime` | `DateTime?` | **本地事件时间** | 上游没有 |
| 13 | `DownloadProgress` | `int` (0-100) | **本地下载器状态** | 上游没有 |
| 14 | `ExternalId` | `string?`, MaxLength **100** | 复合键，见下 | 长度要卡住 |

**`ExternalId` 建议格式**（适配三个互不相同的 id 空间）：
```
moyu:p{patchId}:r{resourceId}         例：moyu:p86:r6262
```
官方 spec 专门警告过：`patch.id` / `vndb_id` / `catalog_work_id` 是**三个互不相等、互不可替换的 id 空间**。

**`size` 解析规则（实测标定，可直接落地）**
```csharp
// 实测样本："17.953 MB"（search 道，有空格） / "17.953MB"（resource 道，无空格） / "823.82MB"
// 实测标定：18825520 字节 ←→ "17.953 MB"  ⇒ 1 MB = 1048576 字节（二进制单位，非十进制）
// 正则：^([\d.]+)\s*(B|KB|MB|GB|TB)$   —— 必须允许空格可有可无
private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };
```
> 顺带：`PatchRecord.FormattedSize` 现有的实现**也是**按 1024 进制（`const long MB = 1024*1024`），**与上游口径一致，可以直接复用做展示**。

**作品锚（这是产品必须提前处理的一个坑）**：官方面只认 `vndb:vXXXX` 和 `catalog:<id>`，**不支持 Bangumi id**。而 Galbox 的刮削主链是 Bangumi。所以我**实测确认**了 moyu 会返回这两个锚：`/api/v1/patch/86` 返回 `"vndb_id":"v4"`、`galgame.catalog_work_id:86`，`/api/v1/galgame` 列表也逐条带 `vndb_id` / `catalog_work_id`。
→ **建议：在刮削阶段就把 `vndb_id` 一并落库**，为补丁中心预埋锚；页面上的 `catalog_work_id` 只在走官方 catalog 面时有意义。**若某作品没有 `vndb_id`，UI 必须如实显示"无法查询"，不要猜。**

### 6.4 `type[]` 12 值枚举 → 中文标签（取自官方 spec 第 508 行）

| 上游值 | 中文 | | 上游值 | 中文 |
|---|---|---|---|---|
| `manual` | 人工翻译补丁 | | `crack` | 破解补丁 |
| `ai` | AI 翻译补丁 | | `fix` | 修正补丁 |
| `machine_polishing` | 机翻润色 | | `mod` | 魔改补丁 |
| `machine` | 机翻补丁 | | `r18` | 18+ |
| `save` | 全 CG 存档 | | `decensor` | 去马赛克补丁 |
| `image` | 修图补丁 | | `other` | 其它 |

> **注意**：现有 `PatchRecord.PatchTypeDisplay` 只认 `Translation/Fix/Adult/Other` 四个值，**与上游 12 值枚举对不上**，需要扩展（但要注意这是 shared 代码，改它会影响别处——建议在 `MoyuPatchMapper` 里做映射，**不要**动 `PatchRecord` 的既有 switch）。
>
> **`save` 类要走存档链路**，不要走"覆盖游戏目录"流程（前置文档 §5.1 结论，我认同）。

### 6.5 错误处理与失败降级

**核心原则：在线半边挂了，本地半边必须照常可用。** 补丁中心必须能在完全断网时把"用户已经下好的补丁包"装好、回滚。

| 情形 | 判定 | 处理 |
|---|---|---|
| 未配置密钥 | `MoyuOptions.ApiKey` 为空 | **不发请求**。UI 静默降级为"仅本机安装"，附"如何接入补丁发现"的引导 |
| 网络不可达 / DNS 失败 | `HttpRequestException`（无 StatusCode） | `LastError` 置位 → UI 显示"补丁源暂时无法连接，你仍可使用本机安装功能"；**不弹模态框** |
| 超时 | `TaskCanceledException` 且非用户取消 | 基类已重试 3 次；仍失败则同上网处理 |
| `401` | 密钥失效 | 明确提示"密钥无效或已吊销" → 一键打开 `developer.nextmoe.dev` 重新铸造 |
| `429` | 配额耗尽 | **读 `Retry-After` / `X-RateLimit-*` / `X-Quota-*` 退避**；UI 显示"配额用尽，X 秒后重试" |
| `5xx` | 上游故障 | 基类已重试；失败则降级提示 |
| JSON 结构变了 | `JsonException` | 基类**已**保留 `LastError` + `LastErrorBody`（截断 500 字符）→ **必须把这个字段透出到 UI/日志**，否则会退化成"搜到 0 条"，用户无法分辨"真的没有"还是"接口坏了" |
| `missing[]` 非空 | 官方面语义 | **正常展示"该作品暂无补丁"，不是错误** |
| 无 `vndb_id` | 锚缺失 | 显示"无法查询"（诚实优先） |

**关键的降级契约**：`MoyuApi` 的所有方法在失败时**返回 `null` 或空集合，绝不抛异常到 ViewModel**（由 `LastError` 承载原因）。这与 `ApiClient` 既有的"用 `LastError` 区分'服务器拒绝'和'真的没结果'"设计一致。

### 6.6 限流与缓存策略

**限流（客户端自律）**
- 令牌桶：**稳态 ≤ 1 请求/秒**，并设一个**分钟上限**（建议 ≤ 30）。
- **所有请求串行化**（`SemaphoreSlim(1,1)`），不做并发扇出。
- `/v2/moyu/patches?refs=…` 支持一次 100 个锚 → **优先用批量，把请求数压到最低**。
- **只有用户主动操作才发请求**：打开某游戏详情、点"查找补丁"。**无后台轮询、无定时全量刷新。**
- 尊重 `Retry-After`；不在 429 后立刻重试。

**缓存**
- **用 `ETag` 做条件请求**。官方面**明确鼓励**并支持共享缓存（`Cache-Control: public, max-age=300, s-maxage=1800, stale-while-revalidate=3600`）。
- 缓存**只放内存或用户本机**，且**只缓存用户实际看过的那几条**（key = 请求 URL，value = ETag + 最后响应）。**不做全站落库。**
- 收到 `304` 时复用本地副本，**不计入"重新解析"**。
- `DownloadUrl` 若将来入库：**短 TTL（如 10 分钟）或不入库**，因为其时效性未验证（§4.3）。

### 6.7 用合成数据写验收测试（**不依赖真实站点**）

`ApiClient` 的 `HttpClient` 是**构造注入**的，所以替换 `HttpMessageHandler` 即可完全离线。

```csharp
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public List<HttpRequestMessage> Requests { get; } = new();   // 断言用

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(_responder(request));
    }
}
```

**建议的测试用例矩阵**（全部用**内联合成 JSON**，夹具可由本次实测响应脱敏改写而来——注意脱敏掉 `artifact_uuid`、`s3_key`、真实用户名）：

| # | 场景 | 合成输入 | 期望 |
|---|---|---|---|
| 1 | 正常命中 | `{"object":"list","items":[…1 条…],"next_cursor":null,"total":1}` | 解析出 1 条，字段逐一断言 |
| 2 | 无补丁 | `items: []`，`missing: ["vndb:v99999"]` | **返回空集合且 `LastError == null`**（不是错误） |
| 3 | `size` 解析 | `"17.953 MB"` / `"17.953MB"` / `"823.82MB"` / `"1.5 GB"` / `""` / `"abc"` | 分别得到 `18825520` / `18825520` / … / `0` / `0`（**不抛异常**） |
| 4 | 空 `name` 回退 | `name:""`，patch 名 `"CLANNAD"`，`type:["manual"]` | 回退出可读名称，非空 |
| 5 | `401` | 状态码 401 + 问题 JSON | 返回 `null`，`LastError` 含 "401"，**不抛异常** |
| 6 | `429` + `Retry-After` | 429，`Retry-After: 30` | 退避策略读到 30s；断言**没有**立即第三次重试 |
| 7 | `304` | 先 200 带 ETag，再 304 | 第二次请求带 `If-None-Match`；返回缓存副本 |
| 8 | JSON 结构变更 | `{"unexpected":true}` | 返回 `null`，`LastErrorBody` 非空（**验证不会静默变成"0 条"**） |
| 9 | 密钥注入 | 任意请求 | 断言请求头带 `X-API-Key`，**且日志/异常文本中不含密钥** |
| 10 | 限流自律 | 连发 10 次 | 断言相邻请求间隔 ≥ 阈值 |
| 11 | `storage:"user"` | 资源无 `web_url` 之外的链接 | 映射后 `DownloadUrl` 为 `web_url`，标记"外部链接" |
| 12 | 无 Bangumi 锚 | 只有 `bangumi_id` 没有 `vndb_id` | `FindPatchesAsync` **不发请求**，返回"无法查询" |

**硬性规则**：测试项目里**不得**出现任何真实域名；加一条守卫测试，断言 `StubHttpMessageHandler` 是唯一被使用的 handler（防止有人手滑接真站）。

---

## 7. 推荐方案与下一步动作

### 7.1 三个方案的取舍

| | **方案 A · 授权面发现 + 浏览器下载**（✅ 推荐） | **方案 B · 站方授权后的一键下载**（待批准） | **方案 C · 直接用 `/api/v1` 抓**（❌ 否） |
|---|---|---|---|
| 做法 | 用官方 `/v2/moyu` 发现补丁，`Process.Start(web_url)` 跳浏览器，用户下完回 Galbox 装 | 同 A，但 `/link` 由 Galbox 在用户点击时代取，直接下载 | 把 `/api/v1/*` 当产品数据源，全自动 |
| 合规 | ✅ 官方授权、有 OpenAPI、有稳定性承诺 | ⚠️ 需站方书面许可 | ❌ `robots.txt` `Disallow: /api` |
| 用户体验 | 多一步（浏览器点"显示链接"再下载） | 一步到位 | 一步到位 |
| 可行性 | ✅ 现在就能做 | ✅ 技术上已实测跑通，只等一句许可 | ✅ 技术上跑通 |
| 风险 | 低 | 低（有许可则归零） | **高**：未授权抓取，与站方明确意图冲突 |

### 7.2 建议的落地顺序

1. **先做"本机补丁安装器 + 状态台账 + 安全回滚"**（纯本地，零合规风险，且是真正没人做好的差异化——见前置文档 §4.1 P0-4/P0-5）。**这一块不依赖任何在线接口，现在就能推进。**
2. **并行去要许可**（见 §7.3）。这是**成本最低、收益最高**的一个动作。
3. **接入官方 `/v2/moyu` 做"发现"**（BYOK）。注意它**不支持 Bangumi id**，所以要先在刮削阶段补 `vndb_id`。
4. **拿到许可后**，再开"用户点击 → 代取直链 → 下载"这条路径（方案 B）。
5. 若许可被拒 → 方案 A 收尾，产品价值不打折，只是多一步浏览器操作。

### 7.3 下一步动作：**去问站方**（具体、可执行）

不要猜。站方已经建了完整的开发者生态，问的成本极低：

| 渠道 | 位置 | 建议问什么 |
|---|---|---|
| GitHub Issue | `KunMoe/kun-galgame-patch`（AGPL-3.0，323★，活跃至 2026-09-09） | ① 桌面客户端能否在**用户显式触发**下调用 `/api/v1/patch/resource/{id}/link` 做**单资源**下载？② 允许的请求频率上限是多少？③ 是否更希望我们只用 `/v2/moyu`？ |
| 论坛 | `https://www.kungal.com`（鲲 Galgame 论坛） | 同上，或在开发者板块发帖 |
| 开发者平台 | `https://developer.nextmoe.dev` | 是否计划给 `/v2/moyu` 增加"下载直链"或"用户令牌"路径 |

**同时值得指出一个产品事实**：`/v2/moyu` **2026-09-08 才立项**（前置文档 §1.3），是站方为下游应用新开的门。**在门刚开的时候去提需求，成功率远高于门关着的时候去撬锁。** 尤其是"桌面客户端在用户主动操作下取单条链接"这个需求——它和站方"链接不能被**批量**抓走"的顾虑**并不冲突**，完全有谈成的空间。

---

## 8. 实测 / 源码推定 / 未验证 —— 逐项标注

> 本节是这份报告的自检表。**凡未实测的，一律不写"可以"。**

### 8.1 ✅ 本人实测确认（可复现）

| # | 结论 | 证据 |
|---|---|---|
| 1 | `robots.txt` 含 `Disallow: /api` | `GET /robots.txt` → 200，189 B，全文见 §5.1 |
| 2 | sitemap 存在且分片 | `/sitemap_index.xml` → 200；`moyu-0.xml` → 200，193,441 B，1000 个 `<loc>` |
| 3 | 搜索接口可用、免鉴权 | `GET /api/v1/search?keywords=CLANNAD&type=resource&page=1&limit=3` → 200，4,622 B |
| 4 | 列表接口可用、免鉴权 | `GET /api/v1/galgame?...&limit=3` → 200，5,465 B |
| 5 | 补丁详情可用 | `GET /api/v1/patch/86` → 200，1,833 B，`vndb_id:"v4"`、`bangumi_id:null` |
| 6 | 资源列表可用且**明文含直链** | `GET /api/v1/patch/86/resource` → 200，3,603 B，`content` 为完整 `oss.moyu.moe` URL |
| 7 | **搜索道剥离了链接字段** | 同一资源 id=6262：search 道 `content=""`/`s3_key=""`，resource 道两者皆有值 |
| 8 | **取链接口可用、免鉴权** | `GET /api/v1/patch/resource/223/link` → 200，295 B，返回 `content` + `download_url` |
| 9 | **下载真实可用** | `dl.imoe.uk` Range 0–2047 → **206**，`content-range: bytes 0-2047/18825520`，魔数 `52 61 72 21 1a 07 01 00`（RAR5） |
| 10 | 遗留链并存 | `oss.moyu.moe` Range 0–255 → **206**，同一总长 18825520 |
| 11 | 无 UA 嗅探 | 用 `Galbox/2.0 …` UA → 同样 206 |
| 12 | `HEAD` 与 Range 均支持 | `HEAD` → 200 + `Content-Length: 18825520` + `Accept-Ranges: bytes` |
| 13 | `size` 是二进制单位 | `18825520 / 1048576 = 17.9531` ↔ API `"17.953 MB"` |
| 14 | 站点自身无 Swagger | `/swagger`→404、`/api/_swagger`→404、`/openapi.json`→404、`/api/v1/swagger`→500 |
| 15 | 官方面需密钥 | `api.nextmoe.dev/v2/moyu*` → 401（前置文档实测，本次通过 spec 复核） |
| 16 | 官方 spec 仍未给直链 | `specs/moyu-openapi.yaml` → 200，21,658 B，第 24–27 行原文见 §5.2 |
| 17 | 源码路由与限流 | `router.go` 原文：`/link` 挂 `RateLimit(…, "resource-link", 30, time.Minute)`、`optionalAuth` |
| 18 | 限流实现按 用户ID/IP | `middleware/ratelimit.go` Redis `INCR` + 60s 窗口 |
| 19 | 下载 URL 由 artifact 服务签发 | `service.go:1001 ResolveDownloadURL` → `s.art.Download(ctx, r.ArtifactUUID)` |
| 20 | 转载公告正文 | `/doc/notice/forward-patch` → 200，正文见 §5.4 |
| 21 | `YmgalApi`/`CngalApi` 是 stub | 读源码：`await Task.CompletedTask; return …Success=false…` |
| 22 | 补丁中心当前无数据源 | 读 `PatchCenterViewModel.cs` 类注释 |

### 8.2 ⚠️ 源码推定（读了代码，但**没有**发请求验证该分支）

| # | 推定内容 | 依据 | 为何未实测 |
|---|---|---|---|
| 1 | `storage:"user"` 的资源 `download_url` 为空，只有 `content`（外部链接）+ `code`/`password` | `ResolveDownloadURL` 在 `ArtifactUUID == ""` 时直接 `return nil` | 该分支样本属于 R18 内容，本次主动回避 |
| 2 | `patch_id` / `vndb_id` / `catalog_work_id` 三个 id 空间互不相等 | 官方 spec 明文警告 | 属契约陈述，非行为 |
| 3 | 超过 30 次/分钟会得到 429 | `ratelimit.go` 逻辑 + `ErrTooManyRequests` | **未故意打满限流**（那本身就是不友好行为） |

### 8.3 ❌ 未验证成功 / 本次没做（**落地前必须补**）

| # | 未验证项 | 为什么重要 | 建议怎么补 |
|---|---|---|---|
| 1 | **`download_url` 的长期有效性** | 决定"能不能提前取链、能不能缓存" | 同一 URL 隔 1h / 24h / 7d 各复测一次，记录是否 403/404 |
| 2 | **`/v2/moyu` 带真实密钥的完整响应** | BYOK 方案的实际可用性 | 去开发者门户铸一把测试密钥，跑通 4 个端点 |
| 3 | **`/doc/notice/privacy`（用户协议与隐私政策）正文** | 是判断"程序化访问是否被条款禁止"的**直接依据** | 该页 SSR 正文未通过 `__NUXT_DATA__` 正则成功提取。改用无头浏览器或改用 `/api/v1/doc/post` 取正文 |
| 4 | `/doc/dev/documentation`（开发文档）、`/doc/notice/rule` | 可能含官方开发者立场与硬性规定 | 同上方式取正文 |
| 5 | **sitemap 分片是否含 `/patch/` URL** | 影响"只抓页面不碰 API"是否可行 | `moyu-0.xml` 中 `/patch/` 命中 0，需遍历全部 `moyu-*.xml` |
| 6 | 真实 `storage` 分布（s3 vs user 占比） | `user` 多则"跳浏览器"体验打折（要输提取码、过网盘） | 抽样统计（可走**官方面**，合规） |
| 7 | `hash`（BLAKE3）字段的实际填充率 | 空则无法做文件同一性校验 | 官方面抽样（实测已见 id=223 与 6262 的 blake3 **相同**，说明同一文件的两行；填充率未统计） |
| 8 | `resource_updated_at` 的语义漂移 | "可能有更新"完全建立在此字段上 | 选 5–10 页连续观察数日 |
| 9 | 上游 `type[]` 12 值在 SFW 模式下 `r18` 是消失还是被过滤 | 影响分级 UI | 需真实密钥抽样 |
| 10 | Byond B2 头部暴露 | `x-bz-file-id` / `x-bz-content-sha1` 会暴露存储内部标识 | 安全评审项，非阻塞 |

### 8.4 本次明确**没有**做（因硬约束）

- 未注册任何账号、未铸造任何 API Key。
- 未下载任何完整补丁文件（最大只取了 **2048 字节**）。
- 未请求、未记录、未下载任何 R18 / NSFW 内容；全程 SFW。
- 未对任何站点做并发、未绕过验证码、未使用代理、未故意触发限流。
- 未 clone 仓库；GitHub 侧只做了只读的 tree / raw / API 查询（共 6 次）。
- **未修改 `E:\tmp\Galbox_v2` 下的任何现有文件**；本报告是唯一新增文件。

---

## 9. 一句话交接

> **moyu.moe 的技术门是开着的——我把搜索、元数据、直链、字节下载四段全部实测跑通了，全程不用登录、不用密钥、没有 UA 嗅探。**
> **但 `robots.txt` 上写着 `Disallow: /api`，而全部接口就在 `/api` 底下；官方 spec 又白纸黑字写着链接"不能被批量抓走"。**
> **所以别去撬锁：站方 2026-09-08 刚为下游应用新开了 `/v2/moyu` 这扇门，去敲门要一句许可，成本是一个 GitHub issue，收益是完整的一键下载体验。**
> **在那之前，把工程投入放到"本机安装器 + 状态台账 + 安全回滚"上——那是纯本地、零合规风险、且至今没人做好的那块，也是"补丁地狱"这个痛点真正的核心。**
