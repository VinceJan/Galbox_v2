# Galbox 补丁中心 · 数据源调研与设计方案

- 调研日期：2026-09-11
- 调研方式：**只读 + 真实联网实测**（未修改任何现有文件，未构建，未提交，未做任何写操作）
- 网络纪律：单站点请求次数受控，相邻请求间隔 ≥ 1 秒；仅使用普通 GET/HEAD/POST 读取，未绕过任何验证码、未使用代理池、未并发轰炸
- 相关前置文档：`_product/galbox-prd-v1.0.md` §3.4、`_product/Galbox-产品知识总纲.md` §3.4、`_product/design/scraping-diagnosis.md`（刮削链路诊断）

---

## 0. 结论速览

> **moyu.moe 站是可用的，而且它有一个官方的、免费的、有文档、有 OpenAPI 的公开 API。**
> **但它被官方刻意设计成"不给下载直链"——所以"一键下载 + 一键解压覆盖"里，前半段在原方案下走不通，后半段才是 Galbox 真正的价值。**
> **推荐方案：官方 NextMoe `/v2/moyu` 补丁面负责"发现补丁"，Galbox 本机负责"识别补丁 / 备份 / 覆盖 / 状态 / 回滚"，下载交给浏览器。**

### 0.1 三句话版本

| 问题 | 结论 |
|---|---|
| moyu.moe 有没有可用的接口？ | **有，而且是两条。** ① 第一方 `https://www.moyu.moe/api/v1/*`——无鉴权、可直接拿直链，但属于站点自用接口（未文档化、未授权）。② 官方 `https://api.nextmoe.dev/v2/moyu/*`——**正式对外开放、有 OpenAPI 规范、免 scope、自助领密钥**，但**契约里写死了不给下载直链、提取码、解压密码**。 |
| 推荐走哪个方案？ | **方案 B+（官方 moyu 补丁面 + 本机安装器）**，配合**方案 D（可插拔 Provider，用户可自建源）**做长期扩展。 |
| 一句话理由 | 唯一一条"合法、免费、有文档、有稳定承诺、还在被官方持续维护"的补丁数据通路就是它；而它不给直链这件事**不是缺陷，是合规护栏**——正好把 Galbox 的产品重心逼到"本机补丁安装与回滚"这个真正的差异化上。 |

### 0.2 必须同时接受的三条现实

1. **原 PRD 的"调用 moyu.moe 搜索接口 + 一键下载"已经不可能按原样实现。** 官方文档原文：*"没有下载直链、提取码与解压密码。在 moyu 上「显示链接」是一次单独的、按资源限速的请求，它存在的全部意义就是链接不能被批量抓走。……把读者送到 www.moyu.moe 的那个页面上去下载，这是唯一的路径。"*
2. **原 PRD 的"查询主键 = Bangumi 条目 ID（`bgm-12345`）"是错的。** 官方补丁面只认两种锚：`vndb:vXXXX` 和 `catalog:<NextMoe catalog work id>`。**不支持 Bangumi id。** 实测还发现 moyu 自己的 `bangumi_id` 字段存在错链（见 §2.1 脚注）。
3. **原 PRD 的"仅从 moyu.moe 官方源下载"这条安全要求，与"一键下载"是互相矛盾的**——官方源根本不允许程序化取直链。二者只能保其一，建议保前者。

---

## 1. moyu.moe 到底有没有可用的接口？

### 1.1 站点性质（实测）

`https://www.moyu.moe/` 是 **鲲 Galgame 补丁**——一个**开源的 Galgame 补丁资源下载站**，不是论坛，也不是搜索引擎。

| 实测项 | 请求 | 结果 |
|---|---|---|
| 首页 | `HEAD https://www.moyu.moe/` | `200`，`Server: cloudflare`，`cf-ray: a39777274fc39a37-NRT`，`Content-Type: text/html; charset=utf-8` |
| 首页正文 | `GET https://www.moyu.moe/` | `200`，**272,948 bytes**，Nuxt 3 SSR 渲染（`<title>首页</title>`，`/_nuxt/*.js`） |
| 补丁列表页 | `GET https://www.moyu.moe/galgame` | `200`，**251,317 bytes**，页内可见 **"共 4,225 部"** |
| 补丁详情页 | `GET https://www.moyu.moe/patch/19310/introduction` | `200`，**107,231 bytes**，`og:title = "Girl Doll Toy 2 ～使者～ - 鲲 Galgame 补丁 - 开源 Galgame 补丁资源下载站"` |
| 反爬 | 上述全部请求 | **无 JS 挑战、无验证码、无需登录、无需 Cookie**，仅需带一个常规 UA。与 2DFan 形成鲜明对比（见 §2.1） |
| 站方自述 | 页面 meta | *"开源的 Galgame 补丁资源下载站，提供 Galgame 汉化补丁、AI 翻译补丁、全 CG 存档等资源的免费下载"* |
| 源码 | GitHub | `KunMoe/kun-galgame-patch`，**Go 语言，AGPL-3.0，323 ★**，`homepage = https://www.moyu.moe`，最近推送 2026-09-09 |

### 1.2 接口一：第一方站内 JSON API（未文档化，但完全公开）

从首页 HTML 内嵌的 Nuxt 运行时配置里直接读到 API 基址：

```
window.__NUXT__.config={public:{apiBase:"https://www.moyu.moe/api/v1",
  oauthServerUrl:"https://oauth.kungal.com/api/v1",
  oauthWebUrl:"https://oauth.kungal.com",
  oauthClientId:"df3ff6008d740bfacbe46aa8cf483cf2",
  oauthRedirectUri:"https://www.moyu.moe/auth/callback",
  imageBed:"https://image.kungal.iloveren.link", ...}}
```

再从 Nuxt 路由 chunk（`/_nuxt/DZKX_O_H.js`）里读出前端真实的查询参数构造代码：

```js
const e = { page: String($.value), sort_field: U.value, sort_order: O.value };
// ...
const e2 = new URLSearchParams({ ...I(), selected_type: g.value ? "all" : v.value, limit: String(Z) });
const a = await te.get(`/galgame?${e2.toString()}`);
return a.code !== 0 ? { galgames: [], total: 0 } : a.data;
```

据此得到真实的端点契约，并逐条实测：

| # | 请求 | HTTP | 关键响应（原文片段） |
|---|---|---|---|
| 1 | `GET /api/v1/galgame` （无参） | **400** | `{"code":40000,"message":"SelectedType is required; SortField is required; SortOrder is required; Page is required; Limit is required","data":null}` |
| 2 | `GET /api/v1/patch` | **500** | `{"code":50000,"message":"Internal server error","data":null}`（**路由存在**，缺参导致服务端异常） |
| 3 | `GET /api/v1/galgame?page=1&sort_field=resource_update_time&sort_order=desc&selected_type=all&limit=3` | **200** | `{"code":0,"message":"OK","data":{"galgames":[{...}]}}`，5,566 bytes，含 `id / name{en-us,ja-jp,zh-cn,zh-tw} / vndb_id / bangumi_id / type[] / language[] / platform[] / content_limit / release_date / count.resource / resource_update_time` |
| 4 | `GET /api/v1/patch/300` | **200** | 1,865 bytes，补丁页详情：`{"code":0,...,"data":{"id":300,"name":{...},"vndb_id":"v415","bangumi_id":263262,"type":["other","manual"],"language":["zh-Hans"],"platform":["windows"],"content_limit":"sfw",...}}` |
| 5 | `GET /api/v1/patch/300/resource` | **200** | 3,675 bytes，**资源列表含直链字段**（见下） |
| 6 | `HEAD https://oss.moyu.moe/patch/4864/<blake3>/<文件名>.rar` | **200** | `Content-Length: 296003465`（≈282.29 MB，与接口返回的 `"size":"282.291MB"` 吻合），`Content-Type: application/octet-stream`，`Accept-Ranges: bytes`，`Server: cloudflare` |
| 7 | 用非浏览器 UA `Galbox/2.0 (Windows; .NET; +https://galbox.app)` 重放 #3 | **200** | 与 #3 一致 —— **无 UA 嗅探、无 Referer 校验、无 Cookie 依赖** |

请求 5 的响应片段（决定性证据）：

```json
{"code":0,"message":"OK","data":[{
  "id":6165,
  "storage":"s3",
  "name":"【famille汉化组&V1.1】世界でいちばんNG（だめ）な恋 - 人工汉化补丁",
  "localization_group_name":"famille汉化组&V1.1",
  "size":"282.291MB",
  "code":"", "password":"",
  "blake3":"9652c6a4d5ed85b81768f28a847a7ab5828e1babba06789aeac6cf2d9c527ecb",
  "s3_key":"patch/4864/9652c6a4…/HERMIT20071026…CHS.rar",
  "content":"https://oss.moyu.moe/patch/4864/9652c6a4…/HERMIT20071026…CHS.rar",
  "type":["manual"], "language":["zh-Hans"], "platform":["windows"],
  "download":56, "update_time":"2025-11-11T06:27:34.878Z",
  "note":"…由 famille汉化组&V1.1 开坑于 07-10-26, 完成于 14-04-01…",
  "note_html":"<p>…</p>"}]}
```

**结论：第一方 API 技术上完全可用——5000 条条目、无鉴权、可拿直链、可下载。但它没有文档、没有稳定性承诺、没有 ToS 授权，且它的存在意义正是"链接不能被批量抓走"。把它当产品数据源 = 未授权抓取。**

### 1.3 接口二：官方 NextMoe 开放 API（有文档、有 OpenAPI、有授权）

这是本次调研的**最重要发现**。"鲲 Galgame 生态"已经建成了一套正式的开发者平台。

**证据链：**

| # | 请求 | HTTP | 结果 |
|---|---|---|---|
| 1 | `GET https://api.nextmoe.dev/v2/catalog/works/300`（无密钥） | **401** | `Content-Type: application/problem+json`，291 bytes（RFC 9457 错误契约） |
| 2 | `GET https://api.nextmoe.dev/v2/moyu`（无密钥） | **401** | `{"code":10001,"message":"未授权，请先登录"}` —— **注意：这是 moyu 后端自己的信封，证明 `/v2/moyu` 路由确实被 Traefik 转发到了补丁站** |
| 3 | `GET https://api.nextmoe.dev/v2/moyu/galgame?page=1&limit=1` | **401** | 同上 |
| 4 | `GET https://api.nextmoe.dev/v2/moyu/patch/300` | **401** | 同上 |
| 5 | `GET https://api.nextmoe.dev/v2/sticker` | **401** | 同上（表情包面，同一机制） |
| 6 | `GET https://developer.nextmoe.dev/` | **200** | 27,239 bytes，Nuxt 应用，标题「开发者平台 · NextMoe 开发者平台」，含 `/dashboard` `/docs` `/llms.txt` |
| 7 | `GET https://developer.nextmoe.dev/llms.txt` | **200** | 全站文档索引（Markdown 孪生） |
| 8 | `GET https://developer.nextmoe.dev/llms-full.txt` | **200** | **305,359 bytes** 全量端点文档 |
| 9 | `GET https://developer.nextmoe.dev/docs/moyu-patches.md` | **200** | **《moyu 补丁面接入》专页** —— 就是为 Galbox 这类需求写的 |
| 10 | `GET https://developer.nextmoe.dev/specs/moyu-openapi.yaml` | **200** | **21,658 bytes，OpenAPI 3.1.0**，`info.title: NextMoe moyu Face`，`version: 1.0.0`，`x-stability: stable`，`license: AGPL-3.0`，`contact: www.moyu.moe` |

**`/v2/moyu` 面的完整契约（4 个端点）：**

| 方法与路径 | 做什么 |
|---|---|
| `GET /v2/moyu/patches` | 列出补丁页；或按 `ids=`（moyu 补丁 id）/ `refs=`（`vndb:vXXXX` 或 `catalog:<id>`）**批量反查，一次最多 100 个锚** |
| `GET /v2/moyu/patches/{id}` | 单个补丁页 |
| `GET /v2/moyu/patches/{id}/resources` | 该页上的资源，游标翻页 |
| `GET /v2/moyu/resources/{id}` | 单个资源 |

**关键属性：**

- **鉴权**：`Authorization: Bearer nmk_live_…` 或 `X-API-Key: nmk_live_…`。密钥在 `developer.nextmoe.dev` **自助铸造、无需审批、无需 scope**。官方原文：*"不需要任何 scope。这个面是免费只读面，网关只做身份、计量与限流，不做授权——任意有效密钥都放行，永远不会 403。"*
- **限流**：free 档 **60 次/分、50,000 次/日**，与 `/v2` 共池；429 带 `Retry-After` / `X-RateLimit-*` / `X-Quota-*`。
- **缓存**：每个 200 都带 `ETag`；`Cache-Control: public, max-age=300, s-maxage=1800, stale-while-revalidate=3600`。**可共享缓存**（与 `/v2/catalog` 大多数面不同），客户端做条件请求是官方鼓励的。
- **字段**：`patch.id`（字符串）、`vndb_id`、`catalog_work_id`、`content_limit`（`sfw`/`nsfw`/`null`）、`release_date`、`type[]`、`language[]`、`platform[]`、`resource_count`、`download_count`、`view_count`、`favorite_count`、`comment_count`、`web_url`、`created_at`、`updated_at`、`resource_updated_at`。
- **补丁类型枚举（官方 spec 原文，12 个值）**：
  `[manual, ai, machine_polishing, machine, save, crack, fix, mod, r18, decensor, image, other]`
  → 与站内 UI 标签一一对应：人工翻译补丁 / AI 翻译补丁 / 机翻润色 / 机翻补丁 / 全 CG 存档 / 破解补丁 / 修正补丁 / 魔改补丁 / 18+ / 去马赛克补丁 / 修图补丁 / 其它。
- **资源字段**：`id`、`patch_id`、`name`、`storage`（`s3` = 站内对象存储 / `user` = 发布者放在别处的链接）、`size`（**给人看的字符串**，如 `"0.571 MB"`，不是字节数）、`hash`（**文件的 BLAKE3**）、`model_name`（仅 AI 翻译补丁、自由文本、不是词表）、`localization_group_name`（汉化组）、`note`（**Markdown 源码**，图片 token 已解析为绝对 URL）、`type[]`、`language[]`、`platform[]`、`download_count`、`like_count`、`web_url`、`created_at`、`updated_at`。

**官方明确列出「它不给什么」（原文照引）：**

> **没有下载直链、提取码与解压密码。** 在 moyu 上「显示链接」是一次单独的、按资源限速的请求，它存在的全部意义就是链接不能被批量抓走。这个面每一行都带 `web_url`——把读者送到 www.moyu.moe 的那个页面上去下载，这是唯一的路径。
>
> **没有游戏名、封面、标签、角色与制作人员。** 那些归 catalog，moyu 一份副本都不存。

**两条使用约束：**

1. **不能被浏览器直连。** 网关对包括 `OPTIONS` 在内的每个方法都验密钥，CORS 预检不带认证头 → 必然 401。官方要求"从你自己的服务端调用"。**桌面客户端不是浏览器**（普通 GET 不触发预检），实测证明标准 HTTP 客户端可直接调用——但"密钥不该出现在分发的二进制里"这条 RFC 8252 原则仍然成立，见 §4.2 的 BYOK 设计。
2. **`patch.id` / `vndb_id` / `catalog_work_id` 是三个互不相等、互不可替换的 id 空间。** 官方专门警告：*"把 `catalog_work_id` 当补丁 id 去打 `/v2/moyu/patches/{id}` 有时也能返回 200——那是另一个页，不是你要的那个。"*

**生态全貌（`KunMoe/kun-galgame-infra` 仓库 `docs/` 目录，Tier-A 契约源）：**

- `catalog` 服务 —— 「身份与关系」注册层：work / release / credit_name / character / label / external_ref。**不做面向匿名用户的公开端点。**
- `artifact` 服务 —— 「大文件字节」：私有桶 + 预签名直传直下 + 可选 manifest。**moyu 已于 2026-06-22 切换过去（含 8,200+ 存量回填）**，公开下载走专用域 `dl.imoe.uk`。下载 URL **短时效（约 1 小时）**、按需签发。
- `community` 服务 —— 论坛（kungal.com）的帖子/话题/评论/信任度。
- `image_service`、`developer-platform`（开发者门户 `developer.nextmoe.dev`）、`oauth`（`oauth.kungal.com`）。

**下游面联邦（`docs/developer-platform/08-downstream-faces-and-sdk.md` §16.5，2026-09-08 立项）：** 首批下游面就是 **moyu（www.moyu.moe 补丁资源）** 和 sticker；路径前缀 `/v2/<site>/*`；免费只读面**不设 scope**。这解释了为什么 `/v2/moyu` 现在能实测到 401 而不是 404。

### 1.4 反爬评估

| 站点 | 是否有反爬 | 实测证据 |
|---|---|---|
| `www.moyu.moe` | **无实质反爬**（Cloudflare 仅作 CDN/缓存前置，不挂挑战） | 首页/列表/详情/API 全部直接 `200`，无 Cookie、无挑战页 |
| `api.nextmoe.dev` | **有鉴权与限流**（这就是它的反爬） | 无密钥 `401`；free 档 60/分、50,000/日；网关对每个方法（含 OPTIONS）验密钥 |

---

## 2. 各数据源实测结果表

### 2.1 总表

| 来源 | 是否可达 | 有无接口 | 实测证据（请求 / 响应） | 结论 |
|---|---|---|---|---|
| **moyu.moe（第一方）** | ✅ `200` | ✅ 未文档化 JSON API | `GET /api/v1/galgame?page=1&sort_field=resource_update_time&sort_order=desc&selected_type=all&limit=3` → `200`，`{"code":0,...,"galgames":[…]}`；`GET /api/v1/patch/300/resource` → `200`，含 `content` 直链；`HEAD oss.moyu.moe/…rar` → `200`，`Content-Length: 296003465` | **技术可用，但不建议作为产品数据源**：未文档化、无承诺、无授权，且官方明说链接不该被批量取 |
| **moyu 补丁面（官方 NextMoe）** | ✅ `401`（需密钥） | ✅ **正式公开 API，OpenAPI 3.1** | `GET api.nextmoe.dev/v2/moyu` → `401 {"code":10001,...}`；`/specs/moyu-openapi.yaml` → `200`，21,658 bytes；`/docs/moyu-patches.md` → `200` | ✅ **推荐主源**。免费、免 scope、自助领密钥、4 端点、ETag + 可共享缓存。**契约不给直链** |
| **NextMoe catalog 面** | ✅ `401`（需密钥） | ✅ 公开 API，116 端点 | `GET /v2/catalog/works/300` → `401 application/problem+json` | ✅ **推荐配套**：补丁页只给 `catalog_work_id`，作品名/封面/标签全部要回 catalog 取 |
| **2DFan**（2dfan.com） | ⚠️ **`403` 被 Cloudflare 挑战拦截** | ❌ 未发现任何 API | `GET https://www.2dfan.com/` → **`403`**，`Server: cloudflare`，`<title>Just a moment...</title>`（5,467 bytes）；`https://2dfan.com/` 同样 `403`。`GET /robots.txt` → `200`（1,836 bytes） | ❌ **不可编程访问**。带 Cloudflare Managed Challenge；robots 声明 `Content-Signal: search=yes,ai-train=no,use=reference`。站方另有"直连工具"页，说明其域名在境内有可达性问题 |
| **月幕 Galgame**（ymgal.games） | ✅ `200` | ✅ **有正式开放 API**（OAuth2） | `GET /oauth/token?grant_type=client_credentials&client_id=ymgal&client_secret=luna0327&scope=public` → `200 {"access_token":"ac371206-…","expires_in":497,"scope":"public"}`；`GET /open/archive/search-game?mode=accurate&keyword=近月少女的礼仪&similarity=50` → `200`，`gid=31147`、`haveChinese=true`、`restricted=true`。官方文档页 `https://www.ymgal.games/developer` → `200` | ⚠️ **可用但只给元数据**：有 `haveChinese`（有无中文版）、`restricted`（18+）、`releases[].releaseLanguage`（Chinese/English/Japanese）、`releases[].restrictionLevel`。**没有任何补丁文件、下载链接或补丁索引**。**文档明确"仅适用于非商业用途"** |
| **鲲 Galgame 论坛**（kungal.com） | ✅ `200` | 论坛面（`docs/community/openapi.yaml`，21 个端点：threads/posts/comments/feedback/trust） | `GET https://www.kungal.com/` → `200`，800,899 bytes，`<title>主页 - 鲲 Galgame 论坛 🐳 开源 Galgame 网站</title>`，`Server: cloudflare` | ⚠️ **是论坛，不是补丁源**。补丁本体全在 moyu.moe。论坛 API 是对站内治理用途的，不是给第三方查补丁的 |
| **Bangumi**（bgm.tv） | ✅ `200` | ✅ API v0 可用 | `GET https://api.bgm.tv/v0/subjects/263262` → `200`；`POST /v0/search/subjects` → `200`。拉取官方 spec `https://raw.githubusercontent.com/bangumi/api/master/open-api/v0.yaml`（113,279 bytes，`200`）并穷举全部 **49 条路径** | ❌ **完全没有补丁/资源/下载相关端点**。全表只有 subjects / characters / persons / episodes / revisions / indices / search / users-collections。**从条目无法关联到任何补丁** |
| **VNDB**（api.vndb.org/kana） | ✅ `200` | ✅ Kana API | `POST /kana/vn {"filters":["id","=","v66716"],"fields":"id,title,alttitle,olang,platforms"}` → `200 {"more":false,"results":[{"id":"v66716",...}]}`；`POST /kana/release {"filters":["id","=","r1"],"fields":"id,title,official,patch,languages.lang,languages.mtl"}` → `200`，`r1 official=True patch=False langs=ja` | ⚠️ **有语言/补丁标记字段，但不含下载**。release 对象确实带 `official`(bool) / `patch`(bool) / `languages[].lang` / `languages[].mtl`（机翻标记）——**可用于判断"这部作品有没有官方中文版 / 有没有第三方翻译发布"**，但 VNDB 本身**不托管、不索引任何补丁文件** |
| **Steam** | ✅ `200` | 有公开 storefront API | `GET https://store.steampowered.com/api/appdetails?appids=1144400` → `200`（18,370 bytes） | ❌ **无补丁相关接口**。Steam Web API 只给商店/DLC/成就/创意工坊；官方补丁（如 18+ 内容）走的是**独立 DLC 包或第三方 patch 站点**，没有可编程的补丁索引 |
| **DLsite** | ✅ 部分 | 未发现公开目录 API | `GET https://www.dlsite.com/maniax/api/` → `404`；`GET https://play.dlsite.com/api/` → `200`（播放器侧，非目录） | ❌ **无公开补丁 API**。DLsite 数据已被 NextMoe catalog 作为「六源」之一整合（用于店铺条目/社团归属），间接可用 |
| **汉化组自发渠道**（论坛 / Discord / Telegram / 贴吧） | — | ❌ 无可编程公开入口 | 官方 README 给出 Telegram 群 `https://t.me/kungalgame`；未发现任何公开 Bot API / RSS / Webhook 供第三方查询补丁清单 | ❌ **不可编程**。Discord 需 Bot token 且需服务器授权；贴吧无官方 API；Telegram 群内容不可通过公开 API 结构化读取 |
| **社区维护的公开 JSON/YAML 补丁清单（GitHub）** | ✅ 可达 | ❌ **不存在** | 通过 GitHub Search API 跑了 **11 组查询**：`galgame patch index`(0)、`galgame+汉化`(16，全是单个作品的汉化工程)、`visual novel translation patch list`(0)、`moyu.moe`(2)、`galgame 资源 索引`(0)、`kungal`(17，生态组件)、`galgame patch archive`(0)、`galgame 补丁合集`(0)、`galgame patch list`(0)、`visual novel chinese patch`(1，单个游戏)、`galgame patch index 补丁 索引 json`(0) | ❌ **重点结论：GitHub 上不存在社区维护的、覆盖多作品的 galgame 补丁索引清单**。能找到的只有：① 单个作品的汉化工程仓库；② 站点自己的源码（`KunMoe/kun-galgame-patch`，Go/AGPL-3.0/323★）；③ 生态周边工具（`kungal/kungal-link-live-checker` 网盘链接存活校验、`next-moe/nextmoe-link-shortener` 短链） |

### 2.2 三个必须点名的坑

**(A) moyu 自己的 `bangumi_id` 字段有错链。**
`GET /api/v1/patch/300` 返回 `"vndb_id":"v415","bangumi_id":263262`（作品：世界上最NG的恋爱，2007）。但 `GET https://api.bgm.tv/v0/subjects/263262` → `name: 世界でいちばん`，`type: 1`（**书籍/漫画**），`platform: 漫画`，`date: 2001-09-01`。**这是另一个条目。**
→ 结论：**不要把 moyu 的 `bangumi_id` 当作可靠的交叉引用**。正确的锚是官方面给的 `vndb_id` 与 `catalog_work_id`（catalog 层有 `external_ref` + `link_kind`（exact/probable/related）分级与唯一约束，才是权威映射）。

**(B) 官方 API 的锚里没有 Bangumi。**
`refs=` 只接受 `vndb:vXXXX` 与 `catalog:<id>` 两种 source。原 PRD 设想的"以 Bangumi 条目 ID 为主键"必须改成 **Bangumi → VNDB 或 Bangumi → catalog work id 的本地映射**（Galbox 已有 Bangumi 刮削，建议在刮削阶段就把 `vndb_id` 一并落库，为补丁中心预埋锚）。

**(C) "显示链接"是一次按资源限速的单独请求。**
这既是官方不给直链的原因，也说明**即使走第一方接口，逐资源取直链也会被限速**——批量场景下不可行。

---

## 3. 备选方案对比

### 3.1 方案总表

| | 方案 A · 纯本地 | **方案 B · 官方 moyu 补丁面 + 本机安装器**（推荐） | 方案 C · 社区清单驱动 | 方案 D · 用户自建源 | 方案 E · 第一方站内接口抓取 |
|---|---|---|---|---|---|
| **一句话** | 软件完全不联网，用户自己下载，Galbox 只管装 | 官方 API 告诉用户"这部作品有什么补丁、去哪下"，用户点开浏览器手动下，Galbox 负责装 | 自建/社区维护一份 GitHub 上的 JSON 清单，Galbox 拉清单 | 设置里让用户填自己的源地址与接口格式 | 直接调 `www.moyu.moe/api/v1/*` 拿直链并下载 |
| **能实现什么** | 识别补丁类型、预览将覆盖的文件、备份、解压覆盖、记录状态、一键回滚 | 以上全部 **+ 自动发现补丁（按作品批量反查，100 部/次）+ 汉化组/体积/类型/更新时间/说明展示 + "有更新"提示 + 跳转下载页** | 以上 A 的全部 + 清单里有编码的下载链接时可直接下 | 以上全部，且可接任意第三方源 | 理论上最完整：自动发现 + 自动下载 + 自动安装，**全自动** |
| **不能实现什么** | 不能发现补丁，用户得自己知道去哪找；没法做"更新提醒" | **不能自动下载**（官方契约层面禁止）；不能显示 18+ 内容（除非显式 `nsfw=true`）；不能从 Bangumi id 直接查 | 没有现成清单可用（实测 11 组查询全部为零）；清单谁维护、谁负责内容合法性都没有答案 | 每个源都要单独适配；用户门槛高；无统一质量保证 | — |
| **用户手工成本** | **高**：知道去哪找、手动下、手动指定解压目标 | **中低**：点"去下载"→ 浏览器打开对应页 → 下载 → 回到 Galbox 拖入文件即可（一次注册领密钥的一次性成本） | 视清单质量而定；"下载"仍需手动（清单多半也是跳转） | 高（要懂接口格式） | 低 |
| **实现难度** | 低–中（解压/备份/回滚是硬骨头，但是本机活） | **中**：API 集成不难（4 个端点、游标翻页、ETag），难点在安装器与状态判定 | 中（清单要自己定义 schema、自己找托管、自己写校验） | 中高（要做插件/Provider 抽象 + 沙箱 + 安全边界） | 低（技术） |
| **法律 / 合规风险** | **最低**：软件只是本地文件工具，不接触任何版权内容分发 | **低–中**：使用的是官方授权开放的 API，且官方已把"不给直链"作为护栏；仍需处理 18+ 的展示与年龄门槛 | **中–高**：清单本身构成"补丁索引"→ 若含直链则接近再分发；自建清单还要承担内容合法性责任 | 视用户配置而定，风险外置；但软件提供"源"的抽象可能被认定为帮助侵权 | **高**：未授权抓取 + 绕过官方明确设计的限速护栏 + 批量获取受保护链接；违反 robots/ToS 的可能性大；**且直接违背 PRD 自己写的"仅从 moyu.moe 官方源下载"精神** |
| **可持续性** | 永远可用 | ✅ 官方 `x-stability: stable`、有版本化与演进条款、有 CI 破坏性变更门 | ❌ 无人维护就是死 | 取决于用户 | ❌ 站点一改前端就崩，且随时可能被封 |

### 3.2 为什么推荐 B + D 而不是别的

1. **B 是唯一一条"合法 + 免费 + 有文档 + 有 SLA + 官方还在持续投入"的通路。** 实测 `/v2/moyu` 是 2026-09-08 才立项交付的首批下游面，官方把它作为"面联邦"的样板；`x-stability: stable` 写在 spec 里。
2. **B 恰好把 Galbox 的产品重心逼到正确的位置。** "发现补丁"这件事，任何人都能做（浏览器打开 moyu.moe 就行）；而"**识别这个补丁是什么类型 → 备份将被覆盖的文件 → 正确解压（含 Shift-JIS 文件名）→ 覆盖 → 记录状态 → 一键回滚**"才是 Galbox 真正能做、别人没做好的事。这和原本的产品立意（*"装错了覆盖不回来"*）完全一致。
3. **A 作为 B 的降级路径必须始终可用。** 用户不领密钥、不联网，Galbox 依然要能把"已经下好的补丁包"装好。这也让 B 的引入没有任何"锁死"风险。
4. **D 是长期扩展位**，不是 MVP。等 B 跑通、Provider 接口被真实用例打磨过之后再开放，否则会变成一个没人配得明白的空壳设置项。
5. **C 应当明确否决（至少目前）。** 不是因为它不好，而是因为**清单不存在**——实测 11 组 GitHub 查询全部为零。从零自建一份清单等于 Galbox 自己成为"补丁索引服务"，法律与运营负担陡增，且没有任何收益是 B 给不了的。

### 3.3 关于方案 E 的明确态度

**技术上可行，产品上不应该做。** 理由：

- 官方文档把"不给直链"写进了 spec 的 description，并解释了设计意图：*"它存在的全部意义就是链接不能被批量抓走"*。绕过它 = 明确对抗站方意图。
- 第一方接口无文档、无承诺、无授权，属于典型的"能跑但不该依赖"。
- 一旦被用于商业产品，风险从"技术违规"升级为"不正当竞争 / 侵权帮助"。
- **唯一可讨论的灰区**：Galbox 是否可以在用户**显式点击"去下载"**时，用第一方接口帮用户把那一个资源的直链取出来并交给系统浏览器/下载器，从而省掉用户在网页上再点一次"显示链接"？
  → 建议 **MVP 阶段不做**。若后续要做，必须：单资源、用户触发、不做批量、不缓存链接、频率严格对齐站方限速、并在 UI 中明示"链接来自 moyu.moe，Galbox 不存储、不镜像"。这一条列为**待产品拍板的灰区**（见 §7）。

---

## 4. 推荐的 MVP 范围

### 4.1 第一个版本做什么

**主题：从"补丁发现"到"装好且能回滚"的完整闭环，下载交给浏览器。**

#### P0-1 数据接入（NextMoe 官方 moyu 补丁面）

| 项 | 内容 |
|---|---|
| 密钥 | 设置页引导用户去 `https://developer.nextmoe.dev` **注册 → 创建应用 → 自助铸造 `nmk_live_` 密钥**（免费、即时、无需审批）。粘贴进 Galbox |
| 存储 | 用 **Windows DPAPI**（`ProtectedData.Protect`，`DataProtectionScope.CurrentUser`）加密后落库；绝不明文写配置文件，绝不进日志 |
| 图形 | 设置页展示密钥状态（已配置 / 未配置 / 上次校验结果）、一键"测试连接"、一键"打开开发者门户" |
| 请求 | `Authorization: Bearer <key>`；所有请求带 `If-None-Match` 条件请求；遇到 `429` 读 `Retry-After` 退避；遇到 `401 {"code":10001}` 提示密钥失效并引导重新铸造 |
| 归因 | 按 `llms.txt` 要求，在"关于/致谢"页与补丁中心页脚标注数据来源：**"鲲 Galgame 论坛 / 鲲 Galgame 补丁 (moyu.moe)"**，并链接 `https://www.moyu.moe` |

#### P0-2 补丁发现（按作品批量反查）

```
GET https://api.nextmoe.dev/v2/moyu/patches
      ?refs=vndb:v415,catalog:61311,…   ← 一次最多 100 个锚
      &include=resources
```

- 锚来源优先级：**① 库里已有的 `catalog_work_id` → ② `vndb_id`（刮削时一并落库）→ ③ 都没有则标记"无法查询"**。
- **不支持 Bangumi id**——不可用 Bangumi id 直接查。若只有 Bangumi id，走"Bangumi 条目 → 其 VNDB 外链/关联"的本地映射，查不到就如实显示"无法查询"。
- 响应里的 `missing[]` 就是"这些作品没有补丁"，**正常展示，不是错误**。
- 列表项展示：`name`、`type[]`（翻译成中文标签）、`language[]`、`platform[]`、`size`、`localization_group_name`（汉化组）、`note`（Markdown 渲染）、`resource_count`、`updated_at`、`download_count`。
- `refs=catalog:<id>` **可能返回多于一项**（moyu 按 VNDB 字符串去重，同一游戏两种写法会有两个页），官方约定"该落地的那个排在最前"——**取 `items[0]` 作为主条目，其余折叠展示**。

#### P0-3 跳转下载（不做程序化下载）

- 每个资源给一个「**去 moyu.moe 下载**」按钮 → `Process.Start` 打开 `web_url`（系统默认浏览器）。
- 附一段引导：*"请在打开的页面点击「显示链接」下载补丁文件，下载完成后回到 Galbox，把文件拖到这里。"*
- **`storage` 字段的差异要体现在 UI 上**：`s3` = 站内文件；`user` = 发布者放在别处（网盘/外部链接），下载页可能还要输提取码。后者在 UI 上标注"外部链接"。

#### P0-4 本机补丁安装器（**这是 MVP 的真正核心**）

流程（复用原共识的 5 步，但把 Step1 换成"用户指定本地文件"）：

```
用户拖入 / 选择已下载的补丁包（zip / rar / 7z / exe）
 ▼ Step 1 识别：判类型（压缩包 / 自解压 exe）、列出条目、检测文件名编码
 ▼ Step 2 预览：解压到临时沙箱目录 → 展示"将被覆盖的文件清单"（逐条：新增 / 覆盖）
 ▼ Step 3 状态检测：与本地 manifest、已知原版文件哈希比对 → "检测到旧版本，是否更新？"
 ▼ Step 4 备份：只备份将被覆盖的文件 → %LOCALAPPDATA%\Galbox\patchbak\<gameId>\<installId>\
 ▼ Step 5 覆盖：按清单写入 → 写 patch-manifest.json → 显示"安装完成 [立即启动游戏][查看变更][回滚]"
```

#### P0-5 状态与回滚

| 状态 | 判定依据 | UI |
|---|---|---|
| **已安装（Galbox 管理）** | 本地 manifest 存在，且记录的文件哈希与磁盘现状一致 | ✅ 已安装 v…（来源 / 时间 / 文件数） |
| **已安装但已被改动** | manifest 存在，但有文件哈希不符（被游戏更新或其他补丁覆盖） | ⚠️ 已安装（文件已变化） |
| **未安装** | 无 manifest | ○ 未安装 |
| **可能有更新** | 官方 API 的 `resource_updated_at` / `updated_at` **晚于**本地 manifest 记录的 `installed_at` 或记录时的 `resource_updated_at` | 🔔 可能有更新（`resource_updated_at` > 本地记录） |
| **无法判断** | 无 API 数据 或 只有启发式痕迹 | ❓ 无法判断（检测到疑似补丁文件：`…`） |
| **回滚** | 按 manifest 逆操作：恢复备份文件、删除本次新增的文件、清理 manifest | 按钮「回滚此次安装」 |

#### P0-6 安全与健壮性（不可省）

- **Zip Slip / 路径穿越防护**：每个条目解析后的绝对路径必须落在目标目录内，否则拒绝并报错。
- **Shift-JIS 文件名**：日文 galgame 压缩包大量使用 CP932。需注册 `CodePagesEncodingProvider.Instance`，zip 条目在 UTF-8 标志位缺失时按 CP932 解码，并提供"文件名编码"手动切换（UTF-8 / CP932 / GBK）。
- **长路径**：`\\?\` 前缀或启用长路径感知，避免 >260 字符失败。
- **备份完整性**：备份后计算哈希；回滚前校验备份文件完好。
- **中断恢复**：写 journal（先记后做，做完提交），支持"恢复上次中断的安装"。
- **覆盖冲突保护**：即将覆盖的文件若既不在"游戏原始文件哈希表"里、也不在任何已知 manifest 里 → **必须弹窗让用户确认**。
- **杀软干扰**：18+/破解类补丁常被杀软隔离导致解压失败，需要给出可读的错误提示。

### 4.2 MVP 明确不做什么（写进文档，避免范围蔓延）

| 不做 | 理由 |
|---|---|
| ❌ **不做补丁文件的程序化下载** | 官方契约层面不提供直链；第一方接口属于未授权抓取 |
| ❌ **不做"一键全自动"（发现→下载→安装）** | 同上，物理上不可能按原 PRD 实现 |
| ❌ **不抓取 `www.moyu.moe/api/v1/*`** | 未文档化、未授权；与官方意图冲突 |
| ❌ **不做 18+ / 破解类补丁的自动发现** | 合规风险，见 §6。MVP 阶段请求固定不带 `nsfw=true`（官方默认 `sfw`） |
| ❌ **不做补丁文件的上传 / 分享 / 镜像 / P2P** | 一旦成为内容分发方，责任模型彻底改变 |
| ❌ **不做语义化版本比较** | moyu 不提供结构化版本号；只能做"更新时间比对"的弱判断 |
| ❌ **不做后台定时全量轮询** | 配额 60/分、50,000/日；改为用户手动刷新 + `ETag` 条件请求 + 单次批量 100 条 |
| ❌ **不做方案 C（社区清单）** | 清单不存在（11 组实测查询全零），自建无收益 |
| ❌ **不做方案 D（用户自建源）** | 长期位；MVP 只把 Provider 接口的**形状**留在代码里（不开放 UI） |
| ❌ **不做 moyu 全站 4,225 部作品的本地镜像** | 对单机管理器无意义，且是明显的批量抓取行为 |
| ❌ **不自带 API Key** | 见下 |

### 4.3 关于密钥的一个关键设计决策：BYOK

**纠结点**：官方要求"从你自己的服务端调用"，而 Galbox 是**分发的桌面客户端**。官方另一份文档（`10-native-app-integration.md`）对此有明确立场：

> *"应用密钥是机密，而分发出去的桌面客户端没有机密可言。密钥躺在用户机器上的可执行文件里，`strings` 一遍就出来……这不是本平台的特殊规定，是 RFC 8252 整篇文档的前提。"*

但那条结论针对的是 **`/v2/catalog` 面**，且给出的解法是"用**用户访问令牌**（OAuth 授权码 + PKCE + 环回回调）"。**问题在于：`/v2/moyu` 面只收 `nmk_` 应用密钥，不接受用户令牌**（spec 的 `securitySchemes` 只有 `apiKey` 与 `bearerKey` 两种 `nmk_` 形状）。官方在 spec 里也强调这个面不能被浏览器直连。

**因此 MVP 采用 BYOK（Bring Your Own Key）**：

- Galbox **不打包任何密钥**；每个用户在开发者门户**铸造自己的密钥**并粘贴进设置。
- 这样"泄漏"的永远是**用户自己的**密钥，配额记在用户自己头上，被吊销也只影响一个人 —— 从根本上消解了 RFC 8252 那段话指向的风险（"泄漏的是**你的**密钥"）。
- 桌面客户端不是浏览器，普通 GET 不触发 CORS 预检，实测标准 HTTP 客户端可正常调用。
- 密钥用 DPAPI（CurrentUser）加密存储，不进日志、不上传、不随诊断包导出。
- UI 要诚实说明：这是每用户自己的密钥；Galbox 不代管、不中转。

> ⚠️ **这条设计是本次调研中唯一"产品必须自己拍板"的地方**，因为它踩在官方两份文档的交叉缝隙上。备选做法是 Galbox 自建一个中转服务端（用户 → Galbox 服务端 → NextMoe），代价是要运维后端、要承担配额与滥用责任。**建议 MVP 先 BYOK，观察真实使用后再决定是否加中转。**

---

## 5. 技术要点

### 5.1 解压与覆盖安装

**格式支持矩阵：**

| 格式 | 读 | 写 | .NET 方案 | 注意事项 |
|---|---|---|---|---|
| `.zip` | ✅ | ✅ | `System.IO.Compression.ZipArchive`（BCL 内置，零依赖） | **文件名编码是大坑**：条目无 UTF-8 标志位时，日文包需按 **CP932（Shift-JIS）** 解码。需注册 `CodePagesEncodingProvider.Instance` 并 `Encoding.GetEncoding(932)`。也常见 GBK 包，需手动切换 |
| `.7z` | ✅ | — | `SharpCompress`（MIT，纯托管） | SharpCompress 支持 7z 读取；LZMA2 较慢，需要进度回调与取消 |
| `.rar` | ✅ | ❌ | `SharpCompress`（**只读**）或外挂 `UnRAR.dll`/`7z.exe` | **.NET 没有任何内置 RAR 支持**。RAR5 在 SharpCompress 中为读取支持；如需更强兼容性可考虑随包分发 `7za.exe`（注意其许可含 unRAR 限制条款）。**RAR 压缩（创建）有专利/许可问题，不要做** |
| `.exe`（自解压 SFX） | ❌ | ❌ | — | **不要尝试自动解压**。SFX 需要执行，无法安全沙箱化。策略：识别出来 → 提示用户在**隔离目录**中手动运行并指定游戏目录，或手动用 7-Zip 解出内容再走标准流程 |
| `.iso` / `.mdf` | 部分 | — | 挂载或用 7z | 少数补丁盘是镜像；MVP 可不支持，仅识别并提示 |
| `.tar.gz` / `.lzh` | 部分 | — | SharpCompress | 日文老物偶见 `.lzh`，如需要可后补 |

**安装器的六条硬规则：**

1. **先解压到沙箱临时目录，再决定覆盖集。** 绝不"边解压边覆盖"——否则一旦解压中途失败，游戏已被改坏一半。
2. **不自动剥掉顶层目录。** galgame 补丁的压缩包内层结构五花八门：有的直接是 `data.xp3`/`*.exe`（要铺到游戏根），有的套了一层 `补丁/`、有的套了游戏名目录。**自动剥离猜测一定会在某些包上猜错。** 正确做法：默认按"铺到游戏根"预览，并把每个条目的**最终落点路径**显示给用户，允许用户手动指定"这些文件应该落在哪个子目录"。
3. **路径穿越防护（Zip Slip）。** 对每个条目求规范化绝对路径，必须 `StartsWith(targetRoot)`，否则拒绝并记为可疑。
4. **覆盖冲突分级**：
   - 目标是**本次新增**（不存在）→ 直接写入，记 `created`。
   - 目标存在且在**原版文件哈希表**或**已知 manifest** 中 → 安全覆盖，备份后写入。
   - 目标存在但**来历不明** → 弹窗要求用户确认（这往往意味着装过别的手工补丁）。
5. **长路径与保留名**：启用长路径（`\\?\`）；处理 `CON`/`PRN`/`.` 结尾等 Windows 非法名。
6. **杀软**：覆盖后校验每个目标文件的大小/哈希；若文件消失或哈希不符，提示"可能被杀毒软件拦截"，而不是静默成功。

**`save` 类型不走覆盖流程。** `type` 含 `save`（全 CG 存档）的资源是**存档文件**，不是补丁。它应该走 Galbox 已有的存档管理链路（复制到存档目录并备份），而不是覆盖游戏目录。MVP 可以只做识别 + 引导，不实现自动投放。

### 5.2 「已安装 / 装的是旧版本」怎么判定

**先说清楚一个关键事实：官方 API 的 `hash` 字段是「下载到的那个压缩包的 BLAKE3」，不是「安装后游戏目录的状态」。** 它只能回答"我手上这个文件和 moyu 上的是不是同一个"，**不能**回答"这个补丁装没装"。

因此状态判定必须分层，可靠性从高到低：

**第 1 层 · Galbox 自己的安装台账（唯一可靠来源）**

```
%LOCALAPPDATA%\Galbox\patchbak\<gameId>\<installId>\
  ├─ manifest.json        ← 权威记录
  └─ files/…              ← 被覆盖文件的备份（保留相对路径）
```

`manifest.json` 建议字段：

```jsonc
{
  "installId": "…",
  "gameId": 3,
  "source": { "kind": "nextmoe-moyu", "patchId": "300", "resourceId": "6165",
              "webUrl": "https://www.moyu.moe/…", "resourceUpdatedAt": "2025-11-11T06:27:34Z" },
  "patchName": "【famille汉化组&V1.1】…人工汉化补丁",
  "type": ["manual"], "language": ["zh-Hans"], "platform": ["windows"],
  "archive": { "fileName": "….rar", "sizeBytes": 296003465,
               "blake3": "9652c6a4…", "format": "rar", "nameEncoding": "cp932" },
  "installedAt": "2026-09-11T15:00:00Z",
  "status": "committed",                       // journal: pending | committed | rolledback
  "files": [
    { "rel": "data.xp3",        "action": "overwritten", "backup": "files/data.xp3",
      "sizeBefore": 123, "hashBefore": "…", "sizeAfter": 456, "hashAfter": "…" },
    { "rel": "patch/game.pfs",  "action": "created",     "sizeAfter": 789, "hashAfter": "…" }
  ]
}
```

**同时往游戏目录写一份轻量标记** `gameRoot\.galbox\patch-manifest.json`（同一份内容），好处是：换机器/换装数据库时状态不丢，且用户肉眼可见 Galbox 做过什么。

**第 2 层 · 文件级复核（检测"已安装但被改动"）**

安装后每次打开补丁中心，抽查 manifest 中记录的 `hashAfter`：全部一致 → 已安装；有差异 → "已安装（文件已变化）"，并指出是哪几个文件。这一层能抓住 **① 游戏本体更新覆盖了补丁 ② 另一个补丁又覆盖了同一文件**。

**第 3 层 · 第三方痕迹启发式（只能给"无法判断/疑似"，绝不能给"已安装"）**

| 线索 | 说明 | 可靠度 |
|---|---|---|
| 汉化组标记文件 | `汉化说明.txt` / `*汉化*.txt` / `README_中文*` / 组名目录 | 低 |
| 引擎特定补丁位 | KiriKiri：`patch.xp3`、`patch2.xp3`、`data.xp3` 的修改时间；Ren'Py：`game/*.rpyc` 与 `tl/`（翻译目录）；Unity：`*_Data/Managed/Assembly-CSharp.dll` | 中 |
| 常见汉化/机翻工具痕迹 | `BepInEx/`、`AutoTranslator/`、`Translation/*.txt`、`XUnity.AutoTranslator*`、`*_Data/Managed/*Localization*` | 中 |
| 转区 / 代理 DLL | `d3d9.dll`、`version.dll`、`Locale Emulator` 相关 | 低（这些也可能是兼容工具） |
| exe 元数据变化 | exe 的修改时间 / 大小 / 版本资源与已知原版不符 | 中 |
| 字符串探测 | 在 `data.xp3`/`*.dll` 中搜 `汉化`、`简体`、组名 | 低（慢，且可能误报） |

**明确写进 UI 的原则**：启发式命中的只有 `❓ 无法判断（检测到疑似补丁痕迹：patch.xp3）`，**永远不显示"已安装"**。只有台账 + 哈希复核才能给出"已安装"。

**第 4 层 · "版本过旧"（弱判断，必须诚实）**

moyu **不提供结构化版本号**。可用的线索只有：

- `resource_updated_at` / `updated_at`（官方 API）—— 语义是"资源最近一次新增或改动的时间"，官方特意强调 *"这与补丁页本身被编辑的时间不是一回事"*。
- 资源的 `name` 里常含 `v1.1` / `V1.1` / `ver2.0` 等手写版本串（**自由文本，不可靠**）。
- `localization_group_name` 里的版本后缀（**同样不可靠**）。

→ 判定规则只能是：**「官方 API 的 `resource_updated_at` 晚于本地 manifest 里记录的那个 `resourceUpdatedAt` 或 `installedAt`」⇒ 显示"可能有更新"**，并附上两个时间点让用户自己判断。**不做语义化版本比较**，也**不显示"版本过旧"这种像是精确结论的措辞**——改成"可能有更新"。

### 5.3 安全回滚：备份策略

**结论：只备份将被覆盖的文件，全目录备份不可接受。**

| | 全目录备份 | 只备份将被覆盖的文件（推荐） |
|---|---|---|
| 空间 | galgame 常见 5–50 GB，18+ 作品更大；备份 3 次就爆盘 | 补丁通常只触及几个到几十个文件，通常 MB 级 |
| 耗时 | 每次安装都要复制数十 GB | 秒级 |
| 实现 | 简单 | 需要"解压到沙箱 → 算出覆盖集"的两段式流程 |
| 回滚完整性 | 最彻底 | **覆盖集算对了就完整**；新增文件靠 manifest 的 `created` 记录删除 |

**备份与回滚的具体规则：**

1. **备份位**：`%LOCALAPPDATA%\Galbox\patchbak\<gameId>\<installId>\`。
   同时提供"备份目录自定义到其他磁盘"（C 盘空间常见不足），并在设置页显示 patchbak 当前占用与清理入口。
2. **备份命名**：镜像原相对路径（`files/data.xp3`），而不是平铺加后缀。平铺在文件多时会撞名，且回滚容易乱。
3. **备份前校验**：记录 `hashBefore`；**备份完成后立刻回读校验一次**（防止"备份了一个半截文件还当成功"）。
4. **回滚流程**：
   - 校验备份文件哈希 → 不一致则中止并报告（不允许"部分回滚"）。
   - 恢复所有 `overwritten` 文件。
   - 删除所有 `created` 文件（若已被用户改过，提示后再删）。
   - manifest `status = rolledback`，但**不删除备份**（保留为"上次装过的证据/后悔药"）。
5. **补丁链（补丁套补丁）**：A 覆盖 X，B 又覆盖 X。B 的备份存的是 **A 之后的 X**——这是**正确**行为，回滚 B 会回到 A 的状态。manifest 用 `installId` 的先后顺序表达链条，UI 上以时间线呈现，并允许"回滚到某个时间点"（依次逆序回滚）。
6. **journal 与中断恢复**：覆盖开始前先写 `status: pending` 的 manifest，全部完成后改 `committed`。启动时若发现 `pending` 的安装，提示"上次安装被中断"并提供"完成回滚"。
7. **保留策略**：默认保留最近 **5 次** installId 的备份，超出提示用户清理（**不静默删除**——删掉可能就是删掉了唯一的原版文件）。
8. **绝不备份到游戏目录内**（否则会被下一次游戏完整性校验/云同步当作用户文件处理）。

---

## 6. 合规与风险提示（**重要判断项**）

### 6.1 18+ 补丁在国内分发：风险等级「高」

> **本节是整份文档中产品风险最高的一节，建议由产品负责人单独拍板。**

**风险构成：**

1. **内容定性。** 18+ 解除补丁（`r18` / `decensor` / 去马赛克）的功能就是**生成/解锁淫秽内容**。在国内法律框架下，**制作、复制、出版、贩卖、传播淫秽物品**均被《刑法》第 363–367 条规制；电子物品同样适用。
2. **"软件自动下载并分发"= 传播行为。** 如果 Galbox 自身（或 Galbox 的中转服务端）参与取链、下载或缓存补丁文件，行为性质从"用户自己的本地工具"变为"提供传播渠道"，风险性质发生质变。
3. **破解补丁（`crack`）另有著作权风险。** 规避技术措施（《著作权法》第 53 条）在国内属于明确禁止的侵权行为，商业产品提供破解类补丁的一键安装，风险叠加。
4. **MVP 不做下载这一条，本身就是最有效的风险隔离。** 只要 Galbox 不碰字节、不做镜像、不做 P2P，它的角色就是"本地文件管理器"，与"下载器/资源站"有本质区别。

**产品上的处理建议（按优先级）：**

| # | 措施 | 说明 |
|---|---|---|
| 1 | **默认 `sfw`，不主动请求 18+** | 官方 API 的 `nsfw` 缺省就是 `false`；请求里**不要写 `nsfw=true`**。默认状态下 `r18` / `decensor` 类资源根本不会出现在响应里 |
| 2 | **把 18+ 开关放进"高级设置"，默认关闭，且加年龄确认页** | 若一定要提供：开关默认关 + 首次开启弹年龄确认与免责声明 + 开启后补丁中心显式标注"含成人内容" |
| 3 | **破解类单独分类、单独提示** | `crack` 类不混进汉化补丁列表；给出"此类补丁可能涉及著作权问题，请确认你拥有正版"的提示 |
| 4 | **软件不做内容分发方** | 不托管、不镜像、不缓存、不做 P2P、不做 UGC 上传/分享。所有字节都由用户在本机从原始站点获取 |
| 5 | **不做直链** | 只用官方 `web_url` 跳转。这既是官方指定的唯一路径，也是最好的合规护栏 |
| 6 | **用户协议 + 免责声明** | 明确写：Galbox 是本地游戏库与文件管理工具；不提供、不存储、不分发任何游戏补丁内容；补丁的获取与合法性由用户自行负责 |
| 7 | **商业化注意** | 月幕 Galgame 开放 API 明确写 **"仅适用于非商业用途，请不要将其用于商业项目"** → **Galbox 若商业化，就不能接 ymgal**。NextMoe 开放 API 目前免费无付费档位，但**未有书面商用授权**，商业化前需确认。moyu 站点源码为 **AGPL-3.0** → 若只调 API 不受传染；若复制/修改其代码则需遵守 AGPL |
| 8 | **署名要求** | NextMoe `llms.txt` 明确：使用其 API 需按规指标注来源（"鲲 Galgame 论坛" / "LetMoe·一启萌"）。月幕要求"在项目的说明区域（README、关于、页脚等）提到月幕Galgame" |

### 6.2 其他合规点

- **NextMoe 的"来源投影"姿态**：官方明确 **"公开投影 = 聚合记录，不做任何逐源原始字段的批量再分发"**。Galbox 不应把 API 返回的数据整体转储、批量再发布给第三方，也不应把 NextMoe 当"上游数据交易商"使用。
- **2DFan 的 robots 信号**：`Content-Signal: search=yes,ai-train=no,use=reference`。即便能访问，也只是"可被搜索索引"，不等于"可被产品化再分发"。**本项目不做 2DFan 抓取。**
- **VNDB 的引用**：VNDB 数据为社区贡献，通常要求署名与遵守其 API 条款。**本项目只把它当"语言/补丁标记"的判据，不批量转储。**
- **不要把"能调通"当作"被授权"**：`www.moyu.moe/api/v1/*` 能调通，是因为它是给自家前端用的。**能调 ≠ 可依赖 ≠ 被授权。**

---

## 7. 未验证部分清单

以下内容**本次未做实测**或**测了但结论不完整**，落地前需要补验证。

### 7.1 完全未验证（需优先补齐）

| # | 未验证项 | 为什么需要 | 建议验证方式 |
|---|---|---|---|
| 1 | **`/v2/moyu` 带真实密钥的完整响应形状** | 只验证到 401（无密钥）。真实 JSON、字段实际取值、`missing[]` 行为、分页游标都还没见过 | 有人去 `developer.nextmoe.dev` 自助铸一把测试密钥，跑通 4 个端点各一次 |
| 2 | **`nmk_` 密钥从桌面客户端直连是否长期可行** | 官方文档说"只能从你的服务端调用"（理由是浏览器 CORS 预检），我实测的是 **未带密钥** 的 401。带上密钥后的完整请求会不会有额外限制（UA、header、IP）**未验证** | 同上，用真实密钥从 .NET `HttpClient` 发一次完整请求 |
| 3 | **BYOK 方案是否被官方接受（政策层面）** | §4.3 的整个设计依赖于此；这是官方两份文档的交叉缝隙 | 去 `kungal.com` 论坛或 `kungal/.github` 提 issue 咨询：桌面客户端每用户自持密钥是否被允许 |
| 4 | **官方是否计划给 `/v2/moyu` 加"用户令牌"路径** | 若有，BYOK 可升级为标准 OAuth + PKCE 流程，风险清零 | 同上，或在 `docs/developer-platform/01-design.md` 的分期计划里找 |
| 5 | **moyu 站上真实存在多少 `storage: user`（网盘）资源** | 若占比高，"跳转到网页下载"的实际体验会明显更差（还要输提取码、还要过网盘广告与限速） | 抽样统计：随机取 100 个资源看 `storage` 分布 |
| 6 | **`hash`（BLAKE3）字段的实际填充率** | 官方说"在开始记录它之前上传的行为空字符串"。若老资源普遍为空，就不能靠它做文件同一性校验 | 同上抽样统计 |
| 7 | **`resource_updated_at` 语义的实测校准** | §5.2 的"可能有更新"完全建立在这个字段上。需要观察同一补丁页在**仅改说明文字**时该字段是否变化 | 选 5–10 个页连续观察几天，记录 `updated_at` vs `resource_updated_at` 的漂移 |
| 8 | **`refs=` 单请求 100 个锚时，返回项数量与配额的真实成本** | 影响批量刷新的策略设计 | 真实密钥下压测一次 |
| 9 | **官方 API 的 `type` 枚举与实际数据的吻合度** | spec 列了 12 个值，但站内 UI 标签是 11 个中文；`r18` 类型在 `nsfw=false` 时是完全消失还是仅类型被过滤 | 真实密钥下抽样比对 |
| 10 | **2DFan 是否有隐藏 API 或移动端接口** | 只测到首页 403（Cloudflare JS 挑战）。**未尝试**绕过（本调研明确不做绕过验证码的行为） | —（建议直接放弃该源） |

### 7.2 测了但结论有保留

| # | 项 | 保留点 |
|---|---|---|
| 11 | VNDB `/kana/release` 的 `vn` 过滤器 | `["vn","=",[...]]` 与 `["vn","=","v…"]` 两种写法实测均返回 `400 (Invalid predicate / Invalid query)`，而 `["id","=","r1"]` 正常 `200`。**字段本身（`official` / `patch` / `languages[].lang` / `languages[].mtl`）已用 r1 验证存在**，但"按 VN 批量取 release"这条查询路径**未跑通**，需要按 VNDB 最新文档重新确认真实语法 |
| 12 | moyu 的 `bangumi_id` 错链 | 只抽查了 1 条（galgame 300 → bgm 263262，实际是漫画）。是普遍数据质量问题还是个例，**未抽样验证** |
| 13 | NextMoe catalog 的 `external_ref` 是否真能完成 Bangumi ↔ VNDB ↔ moyu 的三方对齐 | 只在文档里读到设计（`link_kind` exact/probable/related + 唯一约束），**未用真实密钥验证** |
| 14 | `oss.moyu.moe` 直链的有效期 | 实测该 URL 现在可用（`200`，296 MB）。但 `artifact` 服务的公开下载走 `dl.imoe.uk` 且预签名 URL **短时效（约 1 小时）**——**当前拿到的 `oss.moyu.moe` 链接是不是稳定持久链接，未验证**（若它会过期，第一方路径的可行性进一步下降，反而印证"不该用它"） |
| 15 | `dl.imoe.uk` 与 `oss.moyu.moe` 的关系 | `docs/artifact/09-download-domain-and-worker.md` 提到专用下载域 `dl.imoe.uk`，但资源接口返回的是 `oss.moyu.moe`。**未实测两条域名的差异与稳定性** |

### 7.3 明确未做（因硬约束）

- 未创建任何 `developer.nextmoe.dev` 账号、未铸造任何密钥（涉及注册行为）。
- 未下载任何补丁文件（只做了 `HEAD`，未下载 296 MB 的正文）。
- 未对任何站点做超过必要次数的请求，未并发、未绕过验证码、未使用代理。
- 未 clone、未修改、未向 GitHub 提交任何内容；仅做只读的公开 API 查询与 raw 文件读取。

---

## 附：一句话给下一个接手的人

> 补丁数据源这件事**已经有解了**，而且比原 PRD 设想的更好——官方 API 免费、有文档、有 OpenAPI、自助领密钥。
> 但它**故意不给下载直链**，所以"一键下载 + 一键解压覆盖"里的前半段必须砍掉，**把全部工程投入放到"本机补丁安装器 + 状态台账 + 安全回滚"**上。
> 那不是妥协——那才是当初"补丁地狱"这个痛点里真正没人解决的问题。
