# Galbox 开发者指南

本指南面向希望参与 Galbox 开发或进行二次开发的贡献者，介绍项目架构、关键技术实现和贡献规范。

## 目录

1. [项目结构概述](#项目结构概述)
2. [架构设计](#架构设计)
3. [核心服务说明](#核心服务说明)
4. [数据库设计](#数据库设计)
5. [扩展刮削源](#扩展刮削源)
6. [贡献指南](#贡献指南)
7. [代码风格规范](#代码风格规范)

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

### 必需工具

- Visual Studio 2022 (17.8+)
- .NET 8.0 SDK
- Windows App SDK 1.5+

### 推荐扩展

- XAML Styler
- C# Dev Kit
- EditorConfig

### 调试配置

项目使用 `app.manifest` 配置应用权限：

```xml
<Identity Name="Galbox.App" Publisher="CN=YourPublisher" Version="1.0.0.0" />
```

---

## 构建和发布

### 本地构建

```bash
# 构建解决方案
dotnet build

# 运行应用
dotnet run --project src/Galbox.App
```

### 发布打包

```bash
# 创建发布包
dotnet publish src/Galbox.App -c Release -r win-x64 --self-contained

# 或使用 MSIX 打包
# 在 Visual Studio 中使用 Packaging Project
```

---

*文档版本: 1.0.0-beta | 最后更新: 2026-04-13*