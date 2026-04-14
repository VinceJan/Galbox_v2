# Galbox API 接口规范

本文档详细描述 Galbox 应用程序使用的各种外部 API 接口，包括第三方数据源 API 和自定义服务 API。

## 目录

1. [Bangumi API](#bangumi-api)
2. [VNDB API](#vndb-api)
3. [ymgal API](#ymgal-api) (待实现)
4. [cngal API](#cngal-api) (待实现)
5. [自定义 API](#自定义-api)
6. [认证方式](#认证方式)
7. [错误处理](#错误处理)
8. [速率限制](#速率限制)

---

## Bangumi API

Bangumi (bgm.tv) 是中文动画、游戏、书籍数据库，是 Galbox 的主要元数据来源。

### 基础信息

| 属性 | 值 |
|------|-----|
| 基础 URL | `https://api.bgm.tv` |
| 认证方式 | OAuth 2.0 / API Key (可选) |
| 响应格式 | JSON |
| 编码 | UTF-8 |

### 端点

#### 搜索游戏

```
GET /search/subject/{title}?type={type}&responseGroup={group}
```

**参数:**

| 参数 | 类型 | 必填 | 描述 |
|------|------|------|------|
| title | string | 是 | 搜索标题（URL 编码） |
| type | int | 否 | 类型：4 = 游戏（默认） |
| responseGroup | string | 否 | 响应组：small, medium, large |

**响应示例:**

```json
{
  "total": 10,
  "limit": 25,
  "offset": 0,
  "data": [
    {
      "id": 12345,
      "name": "游戏名称",
      "name_cn": "中文名称",
      "type": 4,
      "summary": "游戏简介...",
      "images": {
        "small": "https://...",
        "grid": "https://...",
        "large": "https://...",
        "medium": "https://...",
        "common": "https://..."
      },
      "rating": {
        "score": 7.5,
        "total": 100
      },
      "info": [
        {
          "key": "developer",
          "value": "制作公司"
        }
      ]
    }
  ]
}
```

#### 获取详情

```
GET /v0/subjects/{subjectId}
```

**参数:**

| 参数 | 类型 | 必填 | 描述 |
|------|------|------|------|
| subjectId | int | 是 | 条目 ID |

**响应示例:**

```json
{
  "id": 12345,
  "name": "游戏名称",
  "name_cn": "中文名称",
  "summary": "详细简介...",
  "images": {
    "large": "https://...",
    "common": "https://..."
  },
  "rating": {
    "score": 7.5,
    "total": 100
  },
  "infobox": [
    {
      "key": "developer",
      "value": "制作公司"
    },
    {
      "key": "制作公司",
      "value": "制作公司名称"
    }
  ],
  "date": "2023-01-01"
}
```

#### 获取角色

```
GET /v0/subjects/{subjectId}/characters
```

**响应示例:**

```json
[
  {
    "id": 100,
    "name": "角色名",
    "name_cn": "中文名",
    "images": {
      "large": "https://..."
    },
    "relation": "主角"
  }
]
```

#### 获取标签

```
GET /v0/subjects/{subjectId}/tags
```

**响应示例:**

```json
[
  {
    "id": 1,
    "name": "恋爱",
    "count": 50
  }
]
```

---

## VNDB API

VNDB (Visual Novel Database) 是国际视觉小说数据库，提供英文和多语言游戏信息。

### 基础信息

| 属性 | 值 |
|------|-----|
| 基础 URL | `https://api.vndb.org/kana` |
| 认证方式 | 无需认证（公开 API） |
| 响应格式 | JSON |
| 编码 | UTF-8 |

### 端点

#### 搜索视觉小说

```
POST /vn
```

**请求体:**

```json
{
  "filters": "search ~ \"游戏名称\"",
  "fields": "id, title, titles, titles.lang, titles.title, titles.official, titles.main, image.url, rating, length_minutes",
  "results": 10
}
```

**字段说明:**

| 字段 | 描述 |
|------|------|
| id | VN ID (如 v17) |
| title | 主标题 |
| titles | 多语言标题列表 |
| image.url | 封面图片 URL |
| rating | 评分 (0-10) |
| length_minutes | 预估时长 |

**响应示例:**

```json
{
  "more": false,
  "count": 5,
  "results": [
    {
      "id": "v17",
      "title": "Game Title",
      "titles": [
        {
          "lang": "ja",
          "title": "日本語タイトル",
          "official": true,
          "main": true
        },
        {
          "lang": "zh-Hans",
          "title": "中文标题",
          "official": false,
          "main": false
        }
      ],
      "image": {
        "url": "https://..."
      },
      "rating": 8.5,
      "length_minutes": 1200
    }
  ]
}
```

#### 获取详情

```
POST /vn
```

**请求体:**

```json
{
  "filters": "id = \"v17\"",
  "fields": "id, title, titles, description, image.url, rating, length_minutes, developers.name, tags.id, tags.name, tags.rating, characters.id, characters.name, characters.original, characters.image.url"
}
```

**响应字段:**

| 字段 | 描述 |
|------|------|
| description | 游戏描述 |
| developers.name | 开发商名称 |
| tags | 标签列表（含评分） |
| characters | 角色列表 |

---

## ymgal API

*状态: 待研究实现*

ymgal (Yumei Galgame) 是中文 Galgame 数据库，API 结构待研究。

### 基础信息

| 属性 | 值 |
|------|-----|
| 网站 | `https://www.ymgal.games/` |
| 状态 | 待研究 |

**预期端点:**

```
GET /api/search?q={title}
GET /api/game/{id}
```

---

## cngal API

*状态: 待研究实现*

cngal (Chinese Galgame) 是中文 Galgame 数据库，API 结构待研究。

### 基础信息

| 属性 | 值 |
|------|-----|
| 网站 | `https://www.cngal.org/` |
| 状态 | 待研究 |

**预期端点:**

```
GET /api/search?keyword={title}
GET /api/game/{id}
```

---

## 自定义 API

以下为计划中的自定义服务 API（待实现）。

### moyu.moe 补丁 API

*计划功能：汉化补丁、修复补丁查询*

**预期端点:**

```
GET /api/patches?game={gameId}
GET /api/patch/{patchId}/download
```

**响应模型:**

```json
{
  "patches": [
    {
      "id": "patch-001",
      "name": "汉化补丁 v1.0",
      "type": "Translation",
      "version": "1.0",
      "size": 104857600,
      "downloadUrl": "https://...",
      "description": "补丁说明"
    }
  ]
}
```

### 流程图 API

*计划功能：游戏流程图查询*

**预期端点:**

```
GET /api/flowchart?game={gameId}
```

**响应模型:**

```json
{
  "gameId": "game-123",
  "nodes": [
    {
      "id": "node-1",
      "label": "起始点",
      "type": "start",
      "choices": [
        {
          "text": "选项A",
          "nextNodeId": "node-2"
        }
      ]
    }
  ],
  "edges": [
    {
      "from": "node-1",
      "to": "node-2",
      "label": "选择A"
    }
  ]
}
```

### 成就 API

*计划功能：社区成就系统*

**预期端点:**

```
GET /api/achievements?game={gameId}
POST /api/achievements/report
```

**响应模型:**

```json
{
  "achievements": [
    {
      "id": "ach-001",
      "name": "全 CG 收集",
      "description": "解锁所有 CG",
      "iconUrl": "https://...",
      "progress": 80,
      "unlocked": false
    }
  ]
}
```

---

## 认证方式

### Bangumi OAuth 2.0

Bangumi 支持 OAuth 2.0 认证，用于获取用户私有数据或提高 API 限额。

**授权流程:**

1. 注册应用获取 `client_id` 和 `client_secret`
2. 用户授权：`GET /oauth/authorize?client_id={id}&redirect_uri={uri}&response_type=code`
3. 获取 Token：`POST /oauth/access_token`

**Token 使用:**

```http
Authorization: Bearer {access_token}
```

**配置示例 (Galbox):**

```csharp
services.AddHttpClient<BangumiHttpClient>()
    .ConfigureHttpClient(client =>
    {
        client.BaseAddress = new Uri("https://api.bgm.tv/");
        client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
        // Bearer token added dynamically when authenticated
    });
```

### API Key 认证

部分自定义 API 可能使用 API Key 认证：

```http
X-API-Key: {api_key}
```

---

## 错误处理

### HTTP 状态码

| 状态码 | 含义 | 处理方式 |
|--------|------|----------|
| 200 | 成功 | 正常处理响应 |
| 400 | 请求错误 | 检查参数格式 |
| 401 | 未认证 | 提示用户登录 |
| 403 | 禁止访问 | 检查权限 |
| 404 | 未找到 | 无匹配结果 |
| 429 | 请求过多 | 应用速率限制 |
| 500 | 服务器错误 | 稍后重试 |

### Bangumi 错误响应

```json
{
  "error": {
    "code": "RATE_LIMIT",
    "message": "请求过于频繁"
  }
}
```

### VNDB 错误响应

```json
{
  "error": "Invalid filter"
}
```

### Galbox 内部错误处理

```csharp
public class SourceScrapingResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ExtendedErrorInfo { get; set; }
}
```

---

## 速率限制

### Bangumi

| 限制 | 值 |
|------|-----|
| 默认间隔 | 500ms |
| 建议 | 使用缓存减少请求 |

**Galbox 实现:**

```csharp
private const int BangumiRateLimitMs = 500;
private readonly SemaphoreSlim _bangumiRateLimitLock = new(1, 1);

private async Task ApplyRateLimitAsync(ScraperSource source, CancellationToken cancellationToken)
{
    await _bangumiRateLimitLock.WaitAsync(cancellationToken);
    try
    {
        var elapsed = DateTime.UtcNow - _lastBangumiRequest;
        if (elapsed.TotalMilliseconds < BangumiRateLimitMs)
        {
            await Task.Delay(BangumiRateLimitMs - (int)elapsed.TotalMilliseconds, cancellationToken);
        }
        _lastBangumiRequest = DateTime.UtcNow;
    }
    finally
    {
        _bangumiRateLimitLock.Release();
    }
}
```

### VNDB

| 限制 | 值 |
|------|-----|
| 默认间隔 | 200ms |
| 建议 | 批量查询减少请求 |

---

## 请求头规范

所有 API 请求应包含以下标准头部：

```http
User-Agent: Galbox/1.0
Accept: application/json
Content-Type: application/json
Accept-Language: zh-CN,zh;q=0.9,en;q=0.8
```

---

## 数据缓存策略

Galbox 采用双层缓存策略：

1. **内存缓存** - 短期缓存（30分钟）
2. **持久缓存** - SQLite 数据库存储

**缓存键格式:**

```
{source}_{cleaned_game_name}
```

**缓存失效:**

- 手动清除
- 数据更新
- 超时失效（30分钟内存，永久数据库）

---

## 扩展指南

### 添加新数据源

1. 在 `Galbox.Core/Api/` 创建新的 API 客户端类
2. 继承 `ApiClient` 基类
3. 定义请求/响应模型
4. 在 `App.xaml.cs` 注册 HttpClient
5. 在 `GameScrapingService` 添加调用逻辑

### 实现示例

```csharp
public class NewSourceApi : ApiClient
{
    private const string BaseUrl = "https://api.newsource.com";
    
    public NewSourceApi(NewSourceHttpClient httpClientWrapper) : base(httpClientWrapper.HttpClient)
    {
    }
    
    public async Task<NewSourceResponse?> SearchAsync(string title, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/search?q={Uri.EscapeDataString(title)}";
        return await GetJsonAsync<NewSourceResponse>(url, cancellationToken);
    }
}
```

---

*文档版本: 1.0.0-beta | 最后更新: 2024*