# Galbox 开发者指南

本指南面向希望参与 Galbox 开发或进行二次开发的贡献者，介绍项目架构、关键技术实现和贡献规范。

## 目录

1. [项目架构概述](#项目结构概述)
2. [技术栈详情](#技术栈详情)
3. [开发环境配置](#开发环境配置)
4. [编译与构建](#编译与构建)
5. [调试方法](#调试方法)
6. [架构设计](#架构设计)
7. [核心服务说明](#核心服务说明)
8. [数据库设计](#数据库设计)
9. [扩展刮削源](#扩展刮削源)
10. [贡献指南](#贡献指南)
11. [代码风格规范](#代码风格规范)

---

## 技术栈详情

### 核心技术栈

| 技术 | 版本 | 用途 |
|------|------|------|
| .NET | 8.0 | 运行时框架 |
| WinUI 3 | Windows App SDK 1.5+ | UI 框架 |
| Entity Framework Core | 8.0 | ORM 数据访问 |
| SQLite | 3.x | 本地数据库 |
| CommunityToolkit.Mvvm | 8.x | MVVM 框架 |
| Microsoft.Extensions.DependencyInjection | 8.0 | 依赖注入容器 |
| Microsoft.Extensions.Logging | 8.0 | 日志框架 |

### UI 框架

- **WinUI 3**: Windows App SDK 提供的现代原生 UI 框架
- **MVVM 模式**: 使用 CommunityToolkit.Mvvm (Source Generators)
- **Fluent Design**: 遵循 Windows 11 设计语言

### 数据存储

- **SQLite**: 轻量级嵌入式数据库
- **EF Core**: Code First 模式，自动创建数据库结构
- **JSON 配置**: 用户设置和应用配置

### 网络通信

- **HttpClient**: HTTP 请求处理
- **System.Text.Json**: JSON 序列化/反序列化
- **API 集成**: Bangumi、VNDB、ymgal、cngal

### 架构模式

```
┌─────────────────────────────────────────────────────────────┐
│                      Galbox.App (表现层)                      │
│   ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐ │
│   │    Views    │  │ ViewModels  │  │   Services (App)    │ │
│   │   (XAML)    │  │  (MVVM)     │  │ Navigation, UI等    │ │
│   └─────────────┘  └─────────────┘  └─────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                     Galbox.Core (业务逻辑层)                  │
│   ┌─────────────────────────────────────────────────────┐   │
│   │ API Clients, Helpers, Models, Interfaces             │   │
│   │ BangumiApi, VndbApi, StringMatcher 等               │   │
│   └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                     Galbox.Data (数据访问层)                  │
│   ┌─────────────────────────────────────────────────────┐   │
│   │ Entities, DbContext, Migrations                     │   │
│   │ GameInfo, GalboxDbContext, 等                       │   │
│   └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

### 主要 NuGet 依赖

```xml
<!-- Galbox.App.csproj -->
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.2" />
<PackageReference Include="Microsoft.WindowsAppSDK" Version="1.5.*" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.*" />

<!-- Galbox.Data.csproj -->
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="8.0.*" />

<!-- Galbox.Core.csproj -->
<PackageReference Include="System.Text.Json" Version="8.0.*" />
```

---

## 项目结构概述

> **注意**: 实际项目结构可能略有不同，实体定义位于 `Galbox.Data/Entities/GameInfo.cs` 中。

### 解决方案结构

```
Galbox_v2/
├── src/                          # 源代码目录
│   ├── Galbox.App/               # WinUI3 主应用程序
│   │   ├── App.xaml.cs           # 应用入口、DI 配置
│   │   ├── MainWindow.xaml       # 主窗口、导航框架
│   │   ├── Views/                # XAML 视图页面
│   │   │   ├── MainPage.xaml
│   │   │   ├── LibraryPage.xaml
│   │   │   ├── GameDetailPage.xaml
│   │   │   ├── SaveManagerPage.xaml
│   │   │   ├── PatchCenterPage.xaml
│   │   │   └── SettingsPage.xaml
│   │   ├── ViewModels/           # MVVM ViewModel
│   │   │   ├── MainViewModel.cs
│   │   │   ├── LibraryViewModel.cs
│   │   │   ├── GameDetailViewModel.cs
│   │   │   ├── SaveManagerViewModel.cs
│   │   │   ├── PatchCenterViewModel.cs
│   │   │   ├── SettingsViewModel.cs
│   │   │   ├── ScrapingProgressViewModel.cs
│   │   │   └── ErrorReportViewModel.cs
│   │   ├── Services/             # 应用层服务
│   │   │   ├── NavigationService.cs
│   │   │   ├── GameScrapingService.cs
│   │   │   ├── SaveManagementService.cs
│   │   │   ├── ProcessMonitorService.cs
│   │   │   ├── AutoScrapingService.cs
│   │   │   ├── BangumiAuthService.cs
│   │   │   ├── ScrapingCacheService.cs
│   │   │   ├── ErrorCheckingService.cs
│   │   │   └── EngineSaveDetector.cs
│   │   ├── Converters/           # XAML 值转换器
│   │   │   └── CommonConverters.cs
│   │   └── Models/               # 数据模型
│   │       └── GameErrorInfo.cs
│   │
│   ├── Galbox.Core/              # 核心业务逻辑层
│   │   ├── Api/                  # API 客户端
│   │   │   ├── ApiClient.cs      # API 基类
│   │   │   ├── BangumiApi.cs     # Bangumi API
│   │   │   ├── VndbApi.cs        # VNDB API
│   │   │   ├── YmgalCngalApi.cs  # ymgal/cngal API
│   │   │   └── HttpClientWrappers.cs
│   │
│   └── Galbox.Data/              # 数据访问层
│       ├── Entities/             # 数据库实体
│       │   ├── GameInfo.cs       # 游戏信息实体
│       │   ├── GameCharacter.cs  # 角色实体
│       │   ├── GameDocument.cs   # 文档实体
│       │   ├── GameMediaFile.cs  # 媒体文件实体
│       │   ├── GameScreenshot.cs # 截图实体
│       │   ├── GameSaveBackup.cs # 存档备份实体
│       │   ├── PatchRecord.cs    # 补丁记录实体
│       │   ├── GameErrorRecord.cs# 错误记录实体
│       │   ├── UserSettings.cs   # 用户设置实体
│       │   └── GalboxDbContext.cs# EF Core DbContext
│      
├── docs/                         # 文档目录
│   ├── README.md
│   ├── API_DOCUMENTATION.md
│   ├── USER_MANUAL.md
│   ├── DEVELOPER_GUIDE.md
│   └── CHANGELOG.md
│
└── README.md                     # 项目 README
```

### 项目依赖关系

```
┌─────────────────┐
│   Galbox.App    │  (WinUI3 应用)
└─────────────────┘
        │
        ├──依赖──→ Galbox.Core (核心逻辑)
        │
        └──依赖──→ Galbox.Data (数据层)
```

---

## 架构设计

### MVVM 架构模式

Galbox 采用 MVVM (Model-View-ViewModel) 架构模式：

```
┌──────────┐     ┌──────────────┐     ┌─────────────┐
│   View   │ ←─→ │  ViewModel   │ ←─→ │   Model     │
│  (XAML)  │     │(Observable)  │     │  (Entity)   │
└──────────┘     └──────────────┘     └─────────────┘
      │                  │                   │
      │                  │                   │
      └──────────────────┴───────────────────┘
                         │
                  ┌──────┴──────┐
                  │   Services  │
                  └─────────────┘
```

**组件职责:**

| 层级 | 职责 | 示例 |
|------|------|------|
| View | UI 呈现、用户交互 | MainPage.xaml |
| ViewModel | 状态管理、业务逻辑绑定 | MainViewModel.cs |
| Model | 数据实体定义 | GameInfo.cs |
| Services | 业务逻辑实现 | GameScrapingService.cs |

### 依赖注入 (DI)

所有服务通过 DI 容器管理，在 `App.xaml.cs` 中配置：

```csharp
private static void ConfigureServices(IServiceCollection services)
{
    // Logging
    services.AddLogging(builder =>
    {
        builder.AddDebug();
        builder.SetMinimumLevel(LogLevel.Information);
    });

    // Database Context
    var dbPath = Path.Combine(GetAppDataPath(), "galbox.db");
    services.AddDbContext<GalboxDbContext>(options =>
    {
        options.UseSqlite($"Data Source={dbPath}");
    });

    // HTTP Clients for APIs
    services.AddHttpClient<BangumiHttpClient>()
        .ConfigureHttpClient(client =>
        {
            client.BaseAddress = new Uri("https://api.bgm.tv/");
            client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
        });

    // API Clients
    services.AddTransient<BangumiApi>();
    services.AddTransient<VndbApi>();

    // Services
    services.AddSingleton<INavigationService, NavigationService>();
    services.AddTransient<IGameScrapingService, GameScrapingService>();
    services.AddSingleton<ISaveManagementService, SaveManagementService>();
    services.AddSingleton<IProcessMonitorService, ProcessMonitorService>();
    services.AddSingleton<IAutoScrapingService, AutoScrapingService>();
    services.AddSingleton<IBangumiAuthService, BangumiAuthService>();
    services.AddSingleton<IScrapingCacheService, ScrapingCacheService>();
    services.AddSingleton<IErrorCheckingService, ErrorCheckingService>();

    // ViewModels
    services.AddTransient<MainViewModel>();
    services.AddTransient<LibraryViewModel>();
    // ... 其他 ViewModel
}
```

### 服务生命周期

| 生命周期 | 适用场景 | 示例 |
|----------|----------|------|
| Singleton | 全局共享、状态保持 | NavigationService, SaveManagementService |
| Transient | 每次请求新建 | GameScrapingService, ViewModels |

### 导航架构

导航服务接口：

```csharp
public interface INavigationService
{
    void NavigateTo(string pageKey, object? parameter = null);
    bool CanGoBack { get; }
    void GoBack();
}
```

实现方式：
- 使用 WinUI 3 `NavigationView` 控件
- `Frame` 作为内容承载容器
- 页面通过 `Tag` 属性标识

---

## 核心服务说明

### GameScrapingService

游戏元数据刮削服务，核心功能：

```csharp
public interface IGameScrapingService
{
    // 搜索游戏
    Task<ScrapingResult> SearchGameAsync(string gameName, CancellationToken cancellationToken = default);
    
    // 从指定源搜索
    Task<SourceScrapingResult> SearchFromSourceAsync(string gameName, ScraperSource source, CancellationToken cancellationToken = default);
    
    // 获取详情
    Task<GameMetadata?> GetGameDetailsAsync(string sourceId, ScraperSource source, CancellationToken cancellationToken = default);
    
    // 自动刮削
    Task<AutoScrapeResult> AutoScrapeAsync(GameInfo gameInfo, CancellationToken cancellationToken = default);
    
    // 清除缓存
    void ClearCache();
}
```

**刮削优先级:**

Bangumi > VNDB > ymgal > cngal

**匹配算法:**

使用 Levenshtein 距离计算相似度：
- 100% = 完全匹配
- 90%+ = 包含匹配
- 90%+ 自动接受阈值

### SaveManagementService

存档管理服务：

```csharp
public interface ISaveManagementService
{
    // 检测存档位置
    Task<SaveLocationResult> DetectSaveLocationAsync(GameInfo game, CancellationToken cancellationToken = default);
    
    // 创建备份
    Task<GameSaveBackup?> CreateBackupAsync(GameInfo game, string description, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default);
    
    // 自动备份
    Task<GameSaveBackup?> CreateAutoBackupAsync(GameInfo game, CancellationToken cancellationToken = default);
    
    // 恢复备份
    Task<bool> RestoreBackupAsync(int saveId, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default);
    
    // 获取备份列表
    Task<List<GameSaveBackup>> GetSaveBackupsAsync(int gameId, CancellationToken cancellationToken = default);
    
    // 删除备份
    Task<bool> DeleteBackupAsync(int saveId, CancellationToken cancellationToken = default);
    
    // 快速切换存档
    Task<QuickSwitchResult> QuickSwitchSaveAsync(int gameId, int targetSaveId, CancellationToken cancellationToken = default);
    
    // 获取存档元数据
    Task<SaveMetadata?> GetSaveMetadataAsync(string savePath, GameEngineType engineType, CancellationToken cancellationToken = default);
}
```

**支持的引擎:**

| 引擎 | 检测方式 | 存档位置 |
|------|----------|----------|
| Ren'Py | `renpy` 目录, `.rpy` 文件 | `%APPDATA%/{Game}/saves` |
| Kirikiri | `.xp3` 包, `savedata` 目录 | `savedata/` |
| TyranoBuilder | `tyrano` 目录 | `data/save/` |
| VNM | `project.json` | 配置文件指定 |
| Unity | `UnityPlayer.dll`, `_Data` 目录 | `AppData/LocalLow/{Company}/{Game}` |
| RPG Maker | `rpg_core.js`, `.rvdata2` | `www/save/` |

### ErrorCheckingService

错误检测服务：

```csharp
public interface IErrorCheckingService
{
    // 全面检查
    Task<List<GameErrorInfo>> CheckGameAsync(GameInfo gameInfo, CancellationToken cancellationToken = default);
    
    // 分类检查
    Task<GameErrorInfo?> CheckCategoryAsync(GameInfo gameInfo, ErrorCategory category, CancellationToken cancellationToken = default);
    
    // 获取错误历史
    Task<List<GameErrorRecord>> GetErrorHistoryAsync(int gameId, CancellationToken cancellationToken = default);
    
    // 标记已解决
    Task<bool> MarkErrorResolvedAsync(int errorRecordId, CancellationToken cancellationToken = default);
    
    // 自动修复
    Task<AutoFixResult> AttemptAutoFixAsync(GameErrorInfo error, CancellationToken cancellationToken = default);
    
    // 批量检查
    Task<Dictionary<int, List<GameErrorInfo>>> BatchCheckGamesAsync(IEnumerable<GameInfo> games, CancellationToken cancellationToken = default);
    
    // 获取未解决错误
    Task<List<GameErrorRecord>> GetAllUnresolvedErrorsAsync(CancellationToken cancellationToken = default);
}
```

**检测类别:**

| 类别 | 描述 |
|------|------|
| ChineseDirectory | 路径包含中文字符 |
| LocaleRequirement | 需要日语区域设置 |
| DirectXMissing | DirectX 组件缺失 |
| KLiteCodecMissing | 视频解码器缺失 |
| WindowsCompatibility | Windows 兼容性问题 |
| RuntimeMissing | 运行库缺失 |
| PermissionIssue | 权限问题 |
| AntivirusBlocking | 杀毒软件拦截 |

### ProcessMonitorService

进程监控服务：

- 监控游戏进程状态
- Boss Key 快速隐藏
- 自动截图功能
- CPU/内存监控（可选）

---

## 数据库设计

### SQLite Schema

Galbox 使用 Entity Framework Core 管理 SQLite 数据库。

### 主要实体关系

```
┌───────────────┐
│   GameInfo    │────────────────────────────────────┐
│   (游戏主表)   │                                    │
└───────────────┘                                    │
       │                                             │
       │ 1:N                                         │ 1:N
       │                                             │
       ├──→ GameCharacter (角色)                     │
       ├──→ GameDocument (文档)                      │
       ├──→ GameMediaFile (媒体文件)                 │
       ├──→ GameScreenshot (截图)                    │
       ├──→ GameSaveBackup (存档备份)                │
       ├──→ PatchRecord (补丁记录)                   │
       └────→ GameErrorRecord (错误记录)─────────────┘
```

### GameInfo 实体

```csharp
public class GameInfo
{
    public int Id { get; set; }
    public string? NameCn { get; set; }          // 中文名
    public string NameOriginal { get; set; }     // 原名
    public string InstallPath { get; set; }      // 安装路径
    public string MainExecutable { get; set; }   // 主可执行文件
    public string? Description { get; set; }     // 简介
    public string? CoverImagePath { get; set; }  // 封面路径
    public string? CoverImageUrl { get; set; }   // 封面 URL
    public string? Developer { get; set; }       // 开发商
    public DateTime? ReleaseDate { get; set; }   // 发布日期
    public double? Rating { get; set; }          // 评分
    public string? SourceId { get; set; }        // 数据源 ID
    public string? SourceType { get; set; }      // 数据源类型
    public long TotalPlayTimeSeconds { get; set; } // 总游玩时间
    public int LaunchCount { get; set; }         // 启动次数
    public DateTime? LastSessionTime { get; set; } // 最后游玩
    public DateTime AddedTime { get; set; }      // 添加时间
    public bool IsFavorite { get; set; }         // 是否收藏
    public bool IsScraped { get; set; }          // 是否刮削
    public long SizeBytes { get; set; }          // 游戏大小
    
    // 关联实体
    public ICollection<GameCharacter> Characters { get; set; }
    public ICollection<GameDocument> Documents { get; set; }
    public ICollection<GameMediaFile> MediaFiles { get; set; }
    public ICollection<GameScreenshot> Screenshots { get; set; }
    public ICollection<GameSaveBackup> SaveBackups { get; set; }
}
```

### 数据库索引

```csharp
modelBuilder.Entity<GameInfo>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.HasIndex(e => e.NameCn);
    entity.HasIndex(e => e.NameOriginal);
    entity.HasIndex(e => e.InstallPath);
    entity.HasIndex(e => e.IsFavorite);
    entity.HasIndex(e => e.AddedTime);
});
```

### 数据库迁移

当前使用 `EnsureCreatedAsync()` 初始化数据库：

```csharp
await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);
```

后续版本可使用 EF Core Migrations 进行版本管理。

---

## 扩展刮削源

### 添加新数据源步骤

#### 1. 创建 API 客户端

在 `Galbox.Core/Api/` 创建新类：

```csharp
public class NewSourceApi : ApiClient
{
    private const string BaseUrl = "https://api.newsource.com";
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    
    public NewSourceApi(NewSourceHttpClient httpClientWrapper) : base(httpClientWrapper.HttpClient)
    {
    }
    
    public async Task<NewSourceSearchResponse?> SearchAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/search?q={Uri.EscapeDataString(title)}";
        return await GetJsonAsync<NewSourceSearchResponse>(url, cancellationToken);
    }
    
    public async Task<NewSourceGameDetail?> GetGameAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/game/{gameId}";
        return await GetJsonAsync<NewSourceGameDetail>(url, cancellationToken);
    }
}
```

#### 2. 定义响应模型

```csharp
public class NewSourceSearchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("items")]
    public List<NewSourceGameItem> Items { get; set; } = new();
}

public class NewSourceGameItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
    
    [JsonPropertyName("title_cn")]
    public string? TitleCn { get; set; }
    
    [JsonPropertyName("cover")]
    public string? CoverUrl { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
```

#### 3. 创建 HttpClient 包装

```csharp
public class NewSourceHttpClient
{
    public HttpClient HttpClient { get; }
    
    public NewSourceHttpClient(HttpClient httpClient)
    {
        HttpClient = httpClient;
    }
}
```

#### 4. 注册服务

在 `App.xaml.cs` 的 `ConfigureServices` 中添加：

```csharp
services.AddHttpClient<NewSourceHttpClient>()
    .ConfigureHttpClient(client =>
    {
        client.BaseAddress = new Uri("https://api.newsource.com/");
        client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
    });

services.AddTransient<NewSourceApi>();
```

#### 5. 扩展 ScraperSource 枚举

```csharp
public enum ScraperSource
{
    Bangumi,
    Vndb,
    Ymgal,
    Cngal,
    NewSource  // 新增
}
```

#### 6. 更新 GameScrapingService

在 `GameScrapingService.cs` 中添加新源处理：

```csharp
private async Task<SourceScrapingResult> SearchNewSourceAsync(
    string gameName,
    CancellationToken cancellationToken)
{
    var result = new SourceScrapingResult
    {
        Source = ScraperSource.NewSource,
        SearchQuery = gameName
    };
    
    var searchResponse = await _newSourceApi.SearchAsync(gameName, cancellationToken);
    
    if (searchResponse?.Items != null)
    {
        foreach (var item in searchResponse.Items)
        {
            var metadata = new GameMetadata
            {
                SourceId = item.Id,
                Source = ScraperSource.NewSource,
                TitleCn = item.TitleCn,
                TitleOriginal = item.Title,
                Description = item.Description,
                CoverImageUrl = item.CoverUrl,
                Titles = new List<string> { item.Title, item.TitleCn ?? "" }
            };
            result.Items.Add(metadata);
        }
    }
    
    return result;
}
```

---

## 贡献指南

### 参与贡献方式

1. **报告问题**
   - 在 GitHub Issues 提交 Bug 报告
   - 提供详细的问题描述和复现步骤

2. **功能建议**
   - 在 GitHub Discussions 发起讨论
   - 描述功能需求和预期效果

3. **代码贡献**
   - Fork 仓库
   - 创建功能分支
   - 提交 Pull Request

### Pull Request 流程

1. Fork 主仓库
2. 创建分支：`git checkout -b feature/your-feature`
3. 进行开发
4. 确保代码通过测试
5. 提交 PR：填写完整的 PR 描述

### PR 检查清单

- [ ] 代码符合项目风格规范
- [ ] 新功能有相应测试
- [ ] 文档已更新（如有必要）
- [ ] 无编译错误和警告
- [ ] 功能正常工作

### 分支命名规范

- `feature/xxx` - 新功能开发
- `fix/xxx` - Bug 修复
- `docs/xxx` - 文档更新
- `refactor/xxx` - 代码重构

---

## 代码风格规范

### C# 代码规范

#### 命名约定

| 类型 | 命名风格 | 示例 |
|------|----------|------|
| 类、接口 | PascalCase | `GameScrapingService` |
| 方法 | PascalCase | `SearchGameAsync` |
| 属性 | PascalCase | `TotalGamesCount` |
| 私有字段 | _camelCase | `_isLoading` |
| 参数 | camelCase | `gameName` |
| 常量 | PascalCase | `BaseUrl` |

#### 文档注释

所有公共 API 必须包含 XML 文档注释：

```csharp
/// <summary>
/// Search for games across all available sources.
/// </summary>
/// <param name="gameName">The game name to search for.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>A scraping result containing matches from all sources.</returns>
public async Task<ScrapingResult> SearchGameAsync(string gameName, CancellationToken cancellationToken = default)
```

#### 异步方法命名

异步方法以 `Async` 结尾：

```csharp
public async Task LoadDataAsync()
public async Task<GameMetadata?> GetGameDetailsAsync(...)
```

#### 使用 nullable 引用类型

项目启用 nullable 引用类型，正确标注：

```csharp
public string? NameCn { get; set; }  // 可为 null
public string NameOriginal { get; set; } = string.Empty;  // 必不为 null
```

#### 错误处理

使用 `try-catch` 处理预期错误，记录日志：

```csharp
try
{
    // 业务逻辑
}
catch (OperationCanceledException)
{
    _logger.LogInformation("Operation cancelled for game {GameId}", gameId);
    throw;
}
catch (Exception ex)
{
    _logger.LogError(ex, "Error during operation for game {GameId}", gameId);
    return new Result { Success = false, ErrorMessage = ex.Message };
}
```

### XAML 代码规范

#### 命名约定

| 类型 | 命名风格 | 示例 |
|------|----------|------|
| 控件 | x:Name 使用 PascalCase | `RootGrid`, `AppTitleBar` |
| 样式 | 描述性 PascalCase | `QuickLaunchCardStyle` |

#### 绑定语法

使用 `x:Bind` 进行强类型绑定：

```xml
<TextBlock Text="{x:Bind ViewModel.DisplayName, Mode=OneWay}"/>
```

使用 `Binding` 进行弱类型绑定：

```xml
<TextBlock Text="{Binding DisplayName, Converter={StaticResource PathToImageConverter}}"/>
```

#### 资源定义

在 `Page.Resources` 中集中定义：

```xml
<Page.Resources>
    <converters:NullToVisibilityConverter x:Key="NullToVisibilityConverter"/>
    <Style x:Key="QuickLaunchCardStyle" TargetType="Border">
        <Setter Property="Background" Value="{ThemeResource CardBackgroundFillColorDefaultBrush}"/>
    </Style>
</Page.Resources>
```

### 提交信息规范

使用约定式提交格式：

```
<type>(<scope>): <subject>

<body>

<footer>
```

**类型:**

- `feat`: 新功能
- `fix`: Bug 修复
- `docs`: 文档更新
- `style`: 代码格式
- `refactor`: 重构
- `test`: 测试
- `chore`: 构建/工具

**示例:**

```
feat(scraping): add VNDB search functionality

Implement VNDB kana API integration for game metadata scraping.
Supports title search and detailed game information retrieval.

Closes #123
```

---

## 开发环境配置

### 系统要求

- **操作系统**: Windows 10 (1809+) 或 Windows 11
- **架构**: x64
- **磁盘空间**: 至少 10GB 可用空间

### 必需工具

#### 1. Visual Studio 2022 (17.8+)

下载地址: https://visualstudio.microsoft.com/

**需要安装的工作负载:**

- .NET 桌面开发 (使用 .NET 8)
- Windows 应用程序开发 (Windows App SDK)

**可选但推荐的组件:**
- C++ 桌面开发 (某些原生库可能需要)
- Git 集成

#### 2. .NET 8.0 SDK

下载地址: https://dotnet.microsoft.com/download/dotnet/8.0

验证安装:
```bash
dotnet --version
# 应显示 8.0.x
```

#### 3. Windows App SDK 1.5+

VS 2022 安装时会自动包含，也可手动下载：
https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads

### 推荐扩展

在 Visual Studio 中通过 **扩展 > 管理扩展** 安装：

| 扩展名 | 用途 |
|--------|------|
| XAML Styler | XAML 代码格式化 |
| C# Dev Kit | 增强的 C# 开发体验 |
| EditorConfig | 代码风格统一 |
| ReSharper (可选) | 代码分析和重构 |
| GitLab/GitHub Extension | Git 集成 |

### 克隆和配置项目

```bash
# 克隆仓库
git clone https://github.com/your-org/Galbox_v2.git
cd Galbox_v2

# 恢复依赖
dotnet restore

# 打开解决方案
# 方式1: 使用 VS 打开 Galbox_v2.sln
# 方式2: 使用 VS Code
code .
```

### 项目配置

#### 数据库位置

数据库文件默认存储在:
```
%LOCALAPPDATA%\Packages\Galbox_*\LocalState\galbox.db
```

开发时可修改 `App.xaml.cs` 中的数据库路径：
```csharp
var dbPath = Path.Combine(GetAppDataPath(), "galbox.db");
```

#### 日志配置

日志级别在 `App.xaml.cs` 中配置：
```csharp
services.AddLogging(builder =>
{
    builder.AddDebug();
    builder.SetMinimumLevel(LogLevel.Information);
});
```

### 常见问题

#### 问题: Windows App SDK 未找到

解决方案:
1. 确保安装了 Windows App SDK 1.5+
2. 检查 NuGet 包是否正确还原
3. 清理并重新构建解决方案

```bash
dotnet clean
dotnet restore
dotnet build
```

#### 问题: SQLite 数据库锁定

解决方案:
1. 确保没有其他进程占用数据库文件
2. 检查是否有未释放的 DbContext

#### 问题: WinUI 3 设计器无法加载

解决方案:
1. 重启 Visual Studio
2. 清理解决方案后重新构建
3. 检查 XAML 语法错误

---

## 编译与构建

### 前置条件

- .NET 8.0 SDK 已安装
- Windows App SDK 已安装
- 项目依赖已还原

### 开发环境构建

```bash
# 进入项目目录
cd Galbox_v2

# 还原 NuGet 包
dotnet restore

# 调试构建
dotnet build

# 或指定配置
dotnet build -c Debug

# 运行应用
dotnet run --project src/Galbox.App
```

### 发布构建

```bash
# 发布为自包含应用
dotnet publish src/Galbox.App -c Release -r win-x64 --self-contained

# 发布为框架依赖应用 (体积更小)
dotnet publish src/Galbox.App -c Release -r win-x64 --self-contained false

# 输出位置
# src/Galbox.App/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/
```

### 发布参数说明

| 参数 | 说明 |
|------|------|
| `-c Release` | 使用 Release 配置 |
| `-r win-x64` | 目标平台 Windows x64 |
| `--self-contained` | 包含 .NET 运行时 |
| `-p:PublishSingleFile=true` | 打包为单个可执行文件 |
| `-p:PublishTrimmed=true` | 裁剪未使用的程序集 |

### MSIX 打包 (可选)

在 Visual Studio 中创建打包项目：

1. 添加新项目 > Windows 应用程序打包项目
2. 添加对 Galbox.App 的引用
3. 配置应用标识和签名证书
4. 构建 > 创建应用包

### 构建输出目录结构

```
publish/
├── Galbox.exe              # 主可执行文件
├── Galbox.dll              # 应用程序集
├── Galbox.Data.dll         # 数据层
├── Galbox.Core.dll         # 核心层
├── *.dll                   # 依赖程序集
├── galbox.db               # 数据库 (首次运行创建)
├── Assets/                 # 资源文件
└── runtimes/               # 原生运行时
```

---

## 调试方法

### Visual Studio 调试

#### 启动调试

1. 在 Visual Studio 中打开解决方案
2. 设置 `Galbox.App` 为启动项目
3. 按 `F5` 启动调试，或 `Ctrl+F5` 运行不调试

#### 断点调试

- 在代码行左侧点击设置断点 (红点)
- 按 `F9` 切换当前行断点
- 运行时会在断点处暂停

**调试快捷键:**

| 快捷键 | 功能 |
|--------|------|
| F5 | 继续执行 |
| F10 | 单步跳过 |
| F11 | 单步进入 |
| Shift+F11 | 跳出 |
| Shift+F5 | 停止调试 |

#### 条件断点

右键断点 > 条件，设置条件表达式：
```csharp
gameId == 123  // 当 gameId 等于 123 时触发
string.IsNullOrEmpty(name)  // 当 name 为空时触发
```

#### 日志点 (Tracepoint)

右键代码行 > 添加日志点，不暂停执行但输出日志：
```
Game {gameId} loaded at {DateTime.Now}
```

### 输出窗口调试

使用 `Debug.WriteLine` 或 `ILogger` 输出调试信息：

```csharp
using Microsoft.Extensions.Logging;

public class GameScrapingService
{
    private readonly ILogger<GameScrapingService> _logger;
    
    public async Task<ScrapingResult> SearchGameAsync(string gameName)
    {
        _logger.LogInformation("开始搜索游戏: {GameName}", gameName);
        // ...
        _logger.LogDebug("搜索完成，找到 {Count} 个结果", results.Count);
    }
}
```

### 数据库调试

#### 查看 SQLite 数据库

推荐工具:
- **DB Browser for SQLite**: https://sqlitebrowser.org/
- **SQLite Studio**: https://sqlitestudio.pl/

#### 查看 EF Core SQL

在 `App.xaml.cs` 中启用敏感数据日志：

```csharp
services.AddDbContext<GalboxDbContext>(options =>
{
    options.UseSqlite($"Data Source={dbPath}")
           .EnableSensitiveDataLogging()      // 显示参数值
           .EnableDetailedErrors();           // 详细错误信息
});
```

### XAML 热重载

WinUI 3 支持运行时 XAML 热重载：

1. 确保选项 > 调试 > XAML 热重载 已启用
2. 运行应用后修改 XAML 文件
3. 保存后界面自动更新

**注意:** C# 代码修改需要重新启动应用。

### 实时可视化树

在运行时检查 UI 结构：

1. 调试运行时按 `Ctrl+Shift+F9`
2. 或在调试工具栏点击 "实时可视化树"
3. 可以查看控件属性和层级结构

### 异常处理

#### 捕获全局异常

在 `App.xaml.cs` 中：

```csharp
public App()
{
    this.UnhandledException += OnUnhandledException;
    TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
}

private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
{
    _logger?.LogCritical(e.Exception, "未处理的异常: {Message}", e.Message);
    // 可选：显示错误对话框
}

private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
{
    _logger?.LogError(e.Exception, "未观察的任务异常");
    e.SetObserved(); // 防止应用崩溃
}
```

### 性能分析

#### 使用 Visual Studio 诊断工具

1. 调试 > 性能探查器
2. 选择分析类型：
   - CPU 使用率
   - 内存使用率
   - .NET 对象分配

#### 应用程序性能提示

```csharp
// 使用 ValueTask 避免异步方法的小额开销
public ValueTask<GameInfo?> GetGameAsync(int id)

// 使用 StringBuilder 进行字符串拼接
var sb = new StringBuilder();
foreach (var item in items) sb.Append(item);

// 避免在循环中分配
private static readonly JsonSerializerOptions JsonOptions = new() { ... };
```

### 常见调试场景

#### 场景1: 刮削服务无响应

检查步骤:
1. 查看输出窗口的 HTTP 请求日志
2. 检查网络连接和 API 可用性
3. 确认 User-Agent 配置正确
4. 检查 CancellationToken 是否正确传递

#### 场景2: 数据库操作失败

检查步骤:
1. 查看 EF Core 生成的 SQL 语句
2. 检查数据库文件权限
3. 验证实体验证规则
4. 确认 DbContext 生命周期

#### 场景3: UI 绑定不更新

检查步骤:
1. 确认 ViewModel 实现 INotifyPropertyChanged
2. 使用 CommunityToolkit.Mvvm 的 [ObservableProperty]
3. 检查绑定模式 (OneWay/TwoWay)
4. 验证 DataContext 是否正确设置

### 调试配置文件

#### app.manifest

项目使用 `app.manifest` 配置应用权限：

```xml
<Identity Name="Galbox.App" Publisher="CN=YourPublisher" Version="1.0.0.0" />
```

#### launchSettings.json

调试启动配置位于 `Properties/launchSettings.json`：

```json
{
  "profiles": {
    "Galbox.App": {
      "commandName": "MsixPackage",
      "commandLineArgs": ""
    },
    "Galbox.App (Unpackaged)": {
      "commandName": "Project"
    }
  }
}
```

**两种调试模式:**
- **Packaged (MsixPackage)**: 以 MSIX 包形式运行，数据存储在应用数据目录
- **Unpackaged (Project)**: 以普通应用运行，数据存储在本地目录

---

*文档版本: 1.1.0 | 最后更新: 2026-04-14*