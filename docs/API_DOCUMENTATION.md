> ⚠️ **本文档已过时，仅作历史存档保留。** 其中的"自定义 API"（流程图 / 路线节点 / 成就）
> 描述的是一套**并不存在**的服务端接口，源码中没有任何对应实现。
> 实际使用的上游只有 Bangumi 与 VNDB，其真实契约以
> `src/Galbox.Core/Api` 下的代码和验收检查 A5/A7 的输出为准。

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

*状态: 待研究实现 - 当前使用 Stub 服务*

ymgal (Yumei Galgame) 是中文 Galgame 数据库，API 结构待研究。在正式 API 可用前，使用 Stub 服务进行开发测试。

### 基础信息

| 属性 | 值 |
|------|-----|
| 网站 | `https://www.ymgal.games/` |
| 状态 | 待研究 |
| Stub 服务 | `Galbox.Core/Api/YmgalApi.cs` |

### Stub 服务接口规范

Stub 服务用于开发测试阶段，模拟 API 响应。

**预期端点:**

```
GET /api/search?q={title}
GET /api/game/{id}
```

**搜索响应模型 (Stub):**

```json
{
  "code": 200,
  "message": "success",
  "data": [
    {
      "id": "ymgal-001",
      "name": "游戏名称",
      "nameCn": "中文名称",
      "cover": "https://...",
      "developer": "制作公司",
      "releaseDate": "2023-01-01",
      "description": "游戏简介..."
    }
  ]
}
```

**详情响应模型 (Stub):**

```json
{
  "code": 200,
  "message": "success",
  "data": {
    "id": "ymgal-001",
    "name": "游戏名称",
    "nameCn": "中文名称",
    "cover": "https://...",
    "developer": "制作公司",
    "releaseDate": "2023-01-01",
    "description": "详细简介...",
    "tags": ["恋爱", "冒险"],
    "characters": [
      {
        "id": "char-001",
        "name": "角色名",
        "avatar": "https://..."
      }
    ],
    "screenshots": [
      "https://..."
    ]
  }
}
```

### Stub 服务实现示例

```csharp
public class YmgalApi : ApiClient, IYmgalApi
{
    private const bool UseStub = true; // 开发阶段使用 Stub
    
    public async Task<YmgalSearchResponse?> SearchAsync(
        string title, 
        CancellationToken cancellationToken = default)
    {
        if (UseStub)
        {
            return GetStubSearchResponse(title);
        }
        
        var url = $"https://www.ymgal.games/api/search?q={Uri.EscapeDataString(title)}";
        return await GetJsonAsync<YmgalSearchResponse>(url, cancellationToken);
    }
    
    private YmgalSearchResponse GetStubSearchResponse(string title)
    {
        // 返回模拟数据用于开发测试
        return new YmgalSearchResponse
        {
            Code = 200,
            Message = "success",
            Data = new List<YmgalGameItem>
            {
                new YmgalGameItem
                {
                    Id = "stub-001",
                    Name = title,
                    NameCn = $"{title} (中文)",
                    // ... 其他字段
                }
            }
        };
    }
}
```

---

## cngal API

*状态: 待研究实现 - 当前使用 Stub 服务*

cngal (Chinese Galgame) 是中文 Galgame 数据库，API 结构待研究。在正式 API 可用前，使用 Stub 服务进行开发测试。

### 基础信息

| 属性 | 值 |
|------|-----|
| 网站 | `https://www.cngal.org/` |
| 状态 | 待研究 |
| Stub 服务 | `Galbox.Core/Api/CngalApi.cs` |

### Stub 服务接口规范

**预期端点:**

```
GET /api/search?keyword={title}
GET /api/game/{id}
```

**搜索响应模型 (Stub):**

```json
{
  "success": true,
  "result": [
    {
      "id": 12345,
      "name": "游戏名称",
      "nameCn": "中文名称",
      "cover": "https://...",
      "developer": "制作公司",
      "pubulishDate": "2023-01-01",
      "introduction": "游戏简介..."
    }
  ]
}
```

**详情响应模型 (Stub):**

```json
{
  "success": true,
  "result": {
    "id": 12345,
    "name": "游戏名称",
    "nameCn": "中文名称",
    "cover": "https://...",
    "developer": {
      "name": "制作公司",
      "link": "https://..."
    },
    "pubulishDate": "2023-01-01",
    "introduction": "详细简介...",
    "tags": [
      {
        "name": "恋爱",
        "link": "https://..."
      }
    ],
    "characters": [
      {
        "name": "角色名",
        "image": "https://..."
      }
    ],
    "pictures": [
      "https://..."
    ]
  }
}
```

### Stub 服务模式说明

Stub 服务模式用于以下场景：
- **开发阶段**：API 尚未正式发布或结构未确定
- **离线测试**：无网络环境下的功能测试
- **速率限制规避**：避免频繁请求真实 API
- **模拟异常场景**：测试错误处理逻辑

**切换机制：**

```csharp
// 在配置文件中控制
public class ApiOptions
{
    public bool UseStubMode { get; set; } = true;
    public bool UseYmgalStub { get; set; } = true;
    public bool UseCngalStub { get; set; } = true;
}

// 在 DI 注册时配置
services.Configure<ApiOptions>(configuration.GetSection("Api"));
services.AddScoped<IYmgalApi, YmgalApi>();
services.AddScoped<ICngalApi, CngalApi>();
```

---

## 自定义 API

以下为计划中的自定义服务 API（待实现）。所有自定义 API 均需认证。

### 基础信息

| 属性 | 值 |
|------|-----|
| 基础 URL | `https://api.moyu.moe` (待定) |
| 认证方式 | API Key / JWT Token |
| 响应格式 | JSON |
| 编码 | UTF-8 |

### 认证说明

自定义 API 支持两种认证方式：

**方式一：API Key**
```http
X-API-Key: {your_api_key}
```

**方式二：JWT Bearer Token**
```http
Authorization: Bearer {your_jwt_token}
```

**获取 API Key：**
1. 在 Galbox 设置中注册开发者账号
2. 在开发者控制台创建应用
3. 获取 API Key 和 Secret

---

### moyu.moe 补丁 API

*计划功能：汉化补丁、修复补丁查询*

#### 获取游戏补丁列表

```
GET /api/v1/patches
```

**请求参数：**

| 参数 | 类型 | 必填 | 描述 |
|------|------|------|------|
| gameId | string | 是 | 游戏 ID（支持 Bangumi ID） |
| type | string | 否 | 补丁类型：Translation, Fix, Mod |
| version | string | 否 | 游戏版本筛选 |
| page | int | 否 | 页码，默认 1 |
| limit | int | 否 | 每页数量，默认 20，最大 100 |

**请求示例：**
```http
GET /api/v1/patches?gameId=bgm-12345&type=Translation&page=1&limit=10
X-API-Key: your_api_key
```

**响应模型：**

```json
{
  "success": true,
  "data": {
    "total": 5,
    "page": 1,
    "limit": 10,
    "patches": [
      {
        "id": "patch-001",
        "name": "汉化补丁 v1.0",
        "type": "Translation",
        "version": "1.0",
        "gameVersion": "1.0.0",
        "size": 104857600,
        "sizeFormatted": "100 MB",
        "downloadUrl": "https://...",
        "mirrorUrls": [
          "https://mirror1...",
          "https://mirror2..."
        ],
        "description": "补丁说明",
        "author": "汉化组名称",
        "releaseDate": "2023-01-01",
        "updateDate": "2023-06-01",
        "downloads": 1234,
        "rating": 4.5,
        "tags": ["简体中文", "完整汉化"]
      }
    ]
  }
}
```

#### 获取补丁详情

```
GET /api/v1/patches/{patchId}
```

**响应模型：**

```json
{
  "success": true,
  "data": {
    "id": "patch-001",
    "name": "汉化补丁 v1.0",
    "type": "Translation",
    "version": "1.0",
    "gameVersion": "1.0.0",
    "size": 104857600,
    "description": "详细补丁说明...",
    "installGuide": "安装说明...",
    "changelog": [
      {
        "version": "1.0",
        "date": "2023-01-01",
        "changes": ["初始版本"]
      }
    ],
    "screenshots": ["https://..."],
    "requirements": ["游戏本体 v1.0.0"],
    "author": {
      "name": "汉化组名称",
      "url": "https://..."
    }
  }
}
```

#### 下载补丁

```
GET /api/v1/patches/{patchId}/download
```

**请求参数：**

| 参数 | 类型 | 必填 | 描述 |
|------|------|------|------|
| mirror | int | 否 | 镜像服务器编号 |

**响应：**
- 成功：重定向到下载链接 (HTTP 302)
- 或返回签名下载 URL：

```json
{
  "success": true,
  "data": {
    "downloadUrl": "https://...",
    "expiresAt": "2024-01-01T00:00:00Z",
    "signature": "..."
  }
}
```

#### 错误码

| 错误码 | HTTP状态 | 描述 |
|--------|----------|------|
| PATCH_NOT_FOUND | 404 | 补丁不存在 |
| GAME_NOT_FOUND | 404 | 游戏不存在 |
| DOWNLOAD_LIMIT_EXCEEDED | 429 | 下载次数超限 |
| INVALID_VERSION | 400 | 游戏版本不匹配 |

---

### 流程图 API

*计划功能：游戏流程图查询*

#### 获取游戏流程图

```
GET /api/v1/flowcharts/{gameId}
```

**请求参数：**

| 参数 | 类型 | 必填 | 描述 |
|------|------|------|------|
| includeHidden | bool | 否 | 是否包含隐藏路线，默认 false |
| language | string | 否 | 语言，默认 zh-CN |

**请求示例：**
```http
GET /api/v1/flowcharts/bgm-12345?includeHidden=true&language=zh-CN
X-API-Key: your_api_key
```

**响应模型：**

```json
{
  "success": true,
  "data": {
    "gameId": "bgm-12345",
    "version": "1.0",
    "lastUpdated": "2023-01-01",
    "metadata": {
      "totalRoutes": 5,
      "totalEndings": 12,
      "estimatedCompletionTime": "20h"
    },
    "nodes": [
      {
        "id": "node-1",
        "label": "起始点",
        "type": "start",
        "position": { "x": 100, "y": 50 },
        "metadata": {
          "chapter": "序章",
          "description": "游戏开始"
        },
        "choices": [
          {
            "id": "choice-1",
            "text": "选项A",
            "nextNodeId": "node-2",
            "condition": null
          },
          {
            "id": "choice-2",
            "text": "选项B",
            "nextNodeId": "node-3",
            "condition": {
              "flag": "route_b_unlocked",
              "value": true
            }
          }
        ]
      },
      {
        "id": "node-2",
        "label": "路线A",
        "type": "route",
        "position": { "x": 200, "y": 100 },
        "metadata": {
          "character": "角色A",
          "routeType": "romance"
        }
      },
      {
        "id": "end-1",
        "label": "Good End",
        "type": "ending",
        "position": { "x": 300, "y": 150 },
        "metadata": {
          "endingType": "good",
          "cg": ["cg-001", "cg-002"]
        }
      }
    ],
    "edges": [
      {
        "id": "edge-1",
        "from": "node-1",
        "to": "node-2",
        "label": "选择A",
        "style": {
          "color": "#4CAF50",
          "dashed": false
        }
      }
    ],
    "routes": [
      {
        "id": "route-a",
        "name": "角色A路线",
        "color": "#FF5722",
        "nodeIds": ["node-2", "node-4", "end-1"],
        "achievement": "ach-route-a"
      }
    ]
  }
}
```

#### 节点类型说明

| 类型 | 描述 |
|------|------|
| start | 游戏起始点 |
| route | 路线节点 |
| branch | 分支节点 |
| ending | 结局节点 |
| event | 特殊事件节点 |

#### 错误码

| 错误码 | HTTP状态 | 描述 |
|--------|----------|------|
| FLOWCHART_NOT_FOUND | 404 | 流程图不存在 |
| GAME_NOT_SUPPORTED | 400 | 游戏不支持流程图 |

---

### 成就 API

*计划功能：社区成就系统*

#### 获取游戏成就列表

```
GET /api/v1/achievements
```

**请求参数：**

| 参数 | 类型 | 必填 | 描述 |
|------|------|------|------|
| gameId | string | 是 | 游戏 ID |
| includeHidden | bool | 否 | 是否包含隐藏成就，默认 false |
| includeProgress | bool | 否 | 是否包含用户进度，需登录 |

**请求示例：**
```http
GET /api/v1/achievements?gameId=bgm-12345&includeProgress=true
Authorization: Bearer {jwt_token}
```

**响应模型：**

```json
{
  "success": true,
  "data": {
    "gameId": "bgm-12345",
    "totalAchievements": 50,
    "totalPoints": 1000,
    "userProgress": {
      "unlocked": 25,
      "totalPoints": 500,
      "completionPercentage": 50
    },
    "achievements": [
      {
        "id": "ach-001",
        "name": "全 CG 收集",
        "description": "解锁所有 CG",
        "iconUrl": "https://...",
        "iconLockedUrl": "https://...",
        "type": "normal",
        "points": 50,
        "progress": {
          "current": 80,
          "total": 100,
          "percentage": 80
        },
        "unlocked": false,
        "unlockedAt": null,
        "rarity": 0.15,
        "isHidden": false
      },
      {
        "id": "ach-002",
        "name": "???",
        "description": "???",
        "iconUrl": "https://...",
        "iconLockedUrl": "https://...",
        "type": "hidden",
        "points": 100,
        "progress": null,
        "unlocked": false,
        "unlockedAt": null,
        "rarity": 0.01,
        "isHidden": true
      },
      {
        "id": "ach-003",
        "name": "真结局",
        "description": "达成真结局",
        "iconUrl": "https://...",
        "iconLockedUrl": "https://...",
        "type": "story",
        "points": 30,
        "progress": null,
        "unlocked": true,
        "unlockedAt": "2023-06-15T10:30:00Z",
        "rarity": 0.45,
        "isHidden": false
      }
    ]
  }
}
```

#### 上报成就进度

```
POST /api/v1/achievements/report
```

**请求头：**
```http
Authorization: Bearer {jwt_token}
Content-Type: application/json
```

**请求体：**
```json
{
  "gameId": "bgm-12345",
  "achievementId": "ach-001",
  "progress": {
    "current": 80,
    "total": 100
  },
  "metadata": {
    "unlockedCgs": ["cg-001", "cg-002", "..."],
    "lastUnlockedAt": "2023-06-15T10:30:00Z"
  }
}
```

**响应模型：**

```json
{
  "success": true,
  "data": {
    "achievementId": "ach-001",
    "unlocked": false,
    "progress": {
      "current": 80,
      "total": 100,
      "percentage": 80
    },
    "notification": null
  }
}
```

**成就解锁响应：**
```json
{
  "success": true,
  "data": {
    "achievementId": "ach-001",
    "unlocked": true,
    "progress": {
      "current": 100,
      "total": 100,
      "percentage": 100
    },
    "notification": {
      "title": "成就解锁！",
      "message": "全 CG 收集",
      "iconUrl": "https://...",
      "points": 50
    }
  }
}
```

#### 成就类型说明

| 类型 | 描述 |
|------|------|
| story | 剧情相关成就 |
| normal | 普通成就 |
| hidden | 隐藏成就 |
| rare | 稀有成就 |
| community | 社区成就 |

#### 错误码

| 错误码 | HTTP状态 | 描述 |
|--------|----------|------|
| ACHIEVEMENT_NOT_FOUND | 404 | 成就不存在 |
| UNAUTHORIZED | 401 | 未登录 |
| INVALID_PROGRESS | 400 | 进度数据无效 |
| ACHIEVEMENT_ALREADY_UNLOCKED | 409 | 成就已解锁 |
| RATE_LIMITED | 429 | 上报过于频繁 |

---

### 通用错误响应格式

所有自定义 API 使用统一的错误响应格式：

```json
{
  "success": false,
  "error": {
    "code": "ERROR_CODE",
    "message": "错误描述信息",
    "details": {
      "field": "具体字段错误"
    }
  },
  "requestId": "req-12345"
}
```

### 通用请求头规范

```http
User-Agent: Galbox/1.0
Accept: application/json
Content-Type: application/json
Accept-Language: zh-CN,zh;q=0.9,en;q=0.8
X-Request-Id: {uuid}  // 可选，用于追踪
X-Client-Version: 1.0.0  // 可选，客户端版本
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

*文档版本: 1.1.0 | 最后更新: 2026-04-14*