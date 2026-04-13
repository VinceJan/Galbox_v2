# Galbox 产品需求文档 (PRD)

**产品名称：** Galbox - Galgame 聚合平台客户端
**版本：** v1.0 PRD
**文档日期：** 2026-04-13
**文档状态：** 初稿待审核
**目标读者：** 开发执行者（人类开发者 / AI 辅助开发）

---

## 一、产品概述

### 1.1 产品定义

Galbox 是一款面向 Galgame（视觉小说游戏）玩家的 Windows 桌面客户端软件，提供游戏库管理、存档智能管理、汉化补丁一键整合、游戏健康诊断等核心功能。

### 1.2 产品定位

> **"Galbox 是玩家的游玩伴侣，不只是游戏启动器"**

核心差异化：
- 存档智能节点标记（不只是备份，而是标记剧情进度）
- 汉化补丁一键整合（不用到处找补丁）
- 流程图追踪（首创功能，预留接口）

### 1.3 目标用户

| 用户类型 | 特征 | 核心需求 |
|---------|------|---------|
| **核心玩家** | 拥有 50+ Galgame 游戏 | 快速启动、存档管理、补丁整合 |
| **收藏玩家** | 收集大量游戏但未全部游玩 | 游戏库分类、待玩列表管理 |
| **新手玩家** | 刚接触 Galgame | 健康诊断、补丁安装指导 |

### 1.4 竞品参考

| 竞品 | 功能覆盖 | Galbox 差异点 |
|------|---------|--------------|
| PotatoVN | 游戏库 + 基础存档备份 | 存档节点标记、补丁整合、流程图（首创） |
| Steam | 游戏库管理 | 专为 Galgame 优化，中文刮削优先 |

---

## 二、技术架构

### 2.1 技术栈

| 层级 | 技术选型 | 说明 |
|------|---------|------|
| **UI框架** | WinUI 3 | 微软最新 Windows UI 规范 |
| **编程语言** | C# | 原生 Windows API 调用 |
| **数据存储** | SQLite | 本地数据库，游戏信息/存档记录 |
| **进程监控** | Windows Process API | 监控游戏运行状态 |

### 2.2 项目结构

```
Galbox/
├── Galbox.App/              # 主应用程序
│   ├── Views/               # 页面视图
│   │   ├── MainPage.xaml    # 主页
│   │   ├── LibraryPage.xaml # 游戏库页
│   │   ├── GameDetailPage.xaml # 游戏详情页
│   │   ├── SaveManagerPage.xaml # 存档管理页
│   │   └── SettingsPage.xaml # 设置页
│   ├── ViewModel/           # 视图逻辑
│   ├── Models/              # 数据模型
│   └── Services/            # 服务层
│       ├── GameScanner.cs   # 游戏扫描服务
│       ├── ScraperService.cs # 刮削服务
│       ├── SaveParser.cs    # 存档解析服务
│       ├── PatchService.cs  # 补丁服务
│       └── ProcessMonitor.cs # 进程监控服务
├── Galbox.Core/             # 核心库
│   ├── Engines/             # 存档引擎适配
│   │   ├── RenpyParser.cs
│   │   ├── KrkrParser.cs
│   │   └── TyranoParser.cs
│   └── Api/                 # API 客户端
│       ├── VndbApi.cs
│       ├── BangumiApi.cs
│       └── MoyuApi.cs
├── Galbox.Data/             # 数据层
│   ├── Database/            # SQLite 数据库
│   └── Entities/            # 数据实体
└── Galbox.Tests/            # 测试项目
```

---

## 三、功能需求详细定义

### 3.1 功能优先级总览

| 优先级 | 功能模块 | 第一版状态 |
|-------|---------|-----------|
| P0 | 存档管理 | ✅ 核心交付 |
| P0 | 补丁下载整合 | ✅ 核心交付 |
| P1 | 游戏库管理 | ✅ 核心交付 |
| P1 | 快速启动 | ✅ 核心交付 |
| P2 | 流程图追踪 | 🔧 预留接口 |
| P2 | 成就系统 | 🔧 预留接口 |

---

### 3.2 游戏库管理（P1）

#### 3.2.1 功能清单

| 功能ID | 功能名称 | 描述 | 验收标准 |
|--------|---------|------|---------|
| F-LIB-001 | 游戏扫描 | 自动扫描指定目录下的游戏 | 支持添加多个扫描目录；识别 .exe 文件 |
| F-LIB-002 | 游戏添加 | 手动添加单个游戏 | 支持拖拽文件夹添加；支持指定可执行文件 |
| F-LIB-003 | 游戏刮削 | 自动获取游戏元信息 | 四源融合（Bangumi优先）；90%阈值自动匹配 |
| F-LIB-004 | 游戏分类 | 智能分类游戏库 | 分类：正在游玩/已完成/待玩/暂停中 |
| F-LIB-005 | 游戏搜索 | 搜索游戏库 | 支持按名称/标签/开发商搜索 |
| F-LIB-006 | 游戏详情页 | 展示游戏详细信息 | 显示封面、画板、简介、角色、标签 |

#### 3.2.2 游戏详情页界面结构

```
┌─────────────────────────────────────────────────────────┐
│  [竖版封面]  游戏中文名称                              │
│             游戏原文名称（小字淡化）                    │
│                                                         │
│  [蓝色启动按钮 ▼] ← 下拉显示多个可执行文件              │
│                                                         │
│  ┌─────────────┐  ┌─────────────────────────────────┐  │
│  │ 游戏简介    │  │ 最近游玩时间: 2026-04-10        │  │
│  │             │  │ 游玩时长: 15小时                │  │
│  │ 游戏标签    │  │ 游玩次数: 8次                   │  │
│  │ [点击筛选]  │  │                                 │  │
│  │             │  │ 说明文档: [README.txt]          │  │
│  │ 游戏角色    │  │ 存档备份: [查看] [快速备份]     │  │
│  │ [最多显示3] │  │                                 │  │
│  └─────────────┘  └─────────────────────────────────┘  │
│                                                         │
│  🎵 音乐播放器  │  📹 视频卡片  │  📷 截图展示        │
└─────────────────────────────────────────────────────────┘

背景：游戏画板（模糊 + 半透明遮罩）
```

#### 3.2.3 游戏数据结构

```csharp
public class GameInfo
{
    public int GameId { get; set; }
    public string NameCn { get; set; }       // 中文名称
    public string NameOriginal { get; set; } // 原名
    public string Description { get; set; }  // 简介
    public string CoverPath { get; set; }    // 竖版封面路径
    public string BannerPath { get; set; }   // 横版画板路径
    public string InstallPath { get; set; }  // 安装目录
    public string ExecutablePath { get; set; } // 主可执行文件
    public List<string> AltExecutables { get; set; } // 其他可执行文件
    public DateTime AddedDate { get; set; }  // 添加时间
    public DateTime LastPlayedDate { get; set; } // 最近游玩
    public int PlayTimeMinutes { get; set; } // 游玩时长（分钟）
    public int PlayCount { get; set; }       // 游玩次数
    public GameStatus Status { get; set; }   // 状态枚举
    public List<string> Tags { get; set; }   // 标签
    public List<Character> Characters { get; set; } // 角色
}

public enum GameStatus
{
    Playing,    // 正在游玩
    Completed,  // 已完成
    ToPlay,     // 待玩
    Paused,     // 暂停中
    NeverPlayed // 未玩过
}
```

---

### 3.3 存档管理（P0）

#### 3.3.1 功能清单

| 功能ID | 功能名称 | 描述 | 验收标准 |
|--------|---------|------|---------|
| F-SAV-001 | 存档位置识别 | 自动识别游戏存档位置 | 支持 Renpy/krkr/Tyrano 三引擎 |
| F-SAV-002 | 存档节点命名 | 自动识别存档对应剧情节点 | Renpy: 解析 scene label；krkr: 解析场景名 |
| F-SAV-003 | 存档时间线视图 | 按时间展示存档形成游玩轨迹 | 横向时间轴 + 存档节点标记 |
| F-SAV-004 | 存档快照 | 玩家主动创建关键节点存档备份 | 快照命名 + 标记节点描述 |
| F-SAV-005 | 分支存档管理 | 同一游戏不同路线存档分开管理 | 存档组概念：A路线组/B路线组 |
| F-SAV-006 | 存档安全替换 | 三重保障替换流程 | 进程检测→自动备份→用户确认 |
| F-SAV-007 | 存档自动备份 | 游戏启动/退出时自动备份 | 可配置备份频率 |
| F-SAV-008 | 章节进度显示 | 显示当前章节进度百分比 | 解析存档中的进度数据 |
| F-SAV-009 | CG解锁率 | 显示游戏CG解锁完成度 | 解析存档中的CG解锁数据 |

#### 3.3.2 存档引擎适配

| 引擎 | 存档位置特征 | 存档格式 | 解析策略 |
|------|-------------|---------|---------|
| **Renpy** | `{游戏目录}/game/saves/` 或 `%APPDATA%/{游戏名}/saves/` | `.save` 文件 + `persistent` | Python pickle 格式；解析 scene_label |
| **krkr** | `{游戏目录}/savedata/` | `.ksd` 文件 | 二进制格式；需研究解析方法 |
| **Tyrano** | `{游戏目录}/data/save/` | JSON 文件 | 直接解析 JSON |

#### 3.3.3 存档安全替换流程

```
用户点击"替换存档"
    │
    ▼
┌─────────────────┐
│ Step 1: 进程检测 │
│ 检测游戏是否运行 │
└─────────────────┘
    │
    ├── 游戏正在运行 → 弹窗警告："请先关闭游戏"
    │                    [强制关闭游戏] [取消操作]
    │
    ▼ 游戏已关闭
┌─────────────────┐
│ Step 2: 自动备份 │
│ 备份当前存档到   │
│ /galbox/backups/ │
└─────────────────┘
    │
    ▼
┌─────────────────┐
│ Step 3: 用户确认 │
│ 弹窗："确定替换存档？"│
│ 显示：原存档 → 新存档│
│ [确认] [取消]    │
└─────────────────┘
    │
    ├── 用户确认 → 执行替换 → 更新存档列表
    │
    └── 用户取消 → 操作终止 → 恢复备份
```

#### 3.3.4 存档数据结构

```csharp
public class SaveRecord
{
    public int SaveId { get; set; }
    public int GameId { get; set; }
    public string SaveName { get; set; }      // 存档节点名称
    public string SavePath { get; set; }      // 存档文件路径
    public string BranchName { get; set; }    // 分支名称（A路线/B路线）
    public DateTime CreatedDate { get; set; } // 创建时间
    public DateTime ModifiedDate { get; set; } // 修改时间
    public int ChapterProgress { get; set; }  // 章节进度（0-100）
    public int CgUnlockRate { get; set; }     // CG解锁率（0-100）
    public string SceneLabel { get; set; }    // 场景标签（Renpy）
    public bool IsSnapshot { get; set; }      // 是否为快照
    public string SnapshotDescription { get; set; } // 快照描述
}

public class SaveGroup
{
    public int GroupId { get; set; }
    public int GameId { get; set; }
    public string GroupName { get; set; }     // "A路线" / "B路线"
    public List<SaveRecord> Saves { get; set; }
}
```

---

### 3.4 补丁下载整合（P0）

#### 3.4.1 功能清单

| 功能ID | 功能名称 | 描述 | 验收标准 |
|--------|---------|------|---------|
| F-PCH-001 | 补丁搜索 | 根据游戏自动搜索匹配补丁 | 调用 moyu.moe 搜索接口 |
| F-PCH-002 | 补丁智能匹配 | 根据游戏版本自动匹配正确补丁 | 显示补丁版本、适用范围 |
| F-PCH-003 | 补丁下载 | 一键下载补丁到本地 | 显示下载进度、文件大小 |
| F-PCH-004 | 补丁安装 | 一键解压覆盖安装补丁 | 自动解压到游戏目录 |
| F-PCH-005 | 补丁状态检测 | 检测游戏是否已安装补丁 | 显示：已安装/未安装/版本过旧 |
| F-PCH-006 | 补丁更新提醒 | 补丁有新版本时提醒用户 | 显示更新按钮 |

#### 3.4.2 补丁安装流程

```
用户点击"安装补丁"
    │
    ▼
┌─────────────────────┐
│ Step 1: 下载补丁     │
│ 从 moyu.moe 下载     │
│ 显示下载进度         │
└─────────────────────┘
    │
    ▼
┌─────────────────────┐
│ Step 2: 检测已安装   │
│ 检测游戏目录是否有   │
│ 已安装补丁痕迹       │
└─────────────────────┘
    │
    ├── 已有旧版本 → 弹窗："检测到旧版本补丁，是否更新？"
    │                 [更新] [取消]
    │
    ▼ 无已安装/确认更新
┌─────────────────────┐
│ Step 3: 备份原文件   │
│ 备份将被覆盖的文件   │
│ 到 /galbox/patchbak/│
└─────────────────────┘
    │
    ▼
┌─────────────────────┐
│ Step 4: 解压覆盖     │
│ 解压补丁到游戏目录   │
│ 显示安装进度         │
└─────────────────────┘
    │
    ▼
┌─────────────────────┐
│ Step 5: 完成确认     │
│ 弹窗："补丁安装完成" │
│ [立即启动游戏] [关闭]│
└─────────────────────┘
```

#### 3.4.3 补丁数据结构

```csharp
public class PatchInfo
{
    public string PatchId { get; set; }
    public string Name { get; set; }         // "同居恋人汉化补丁 v1.2"
    public string Type { get; set; }         // 汉化补丁/修复补丁/18+解除
    public string Version { get; set; }      // 1.2
    public string DownloadUrl { get; set; }  // moyu.moe 下载链接
    public long FileSize { get; set; }       // 文件大小（字节）
    public string InstallPath { get; set; }  // 安装目标路径
    public string Description { get; set; }  // 补丁描述
    public DateTime UpdateDate { get; set; } // 更新日期
}

public class PatchStatus
{
    public int GameId { get; set; }
    public bool IsInstalled { get; set; }    // 是否已安装
    public string InstalledVersion { get; set; } // 已安装版本
    public string LatestVersion { get; set; }    // 最新版本
    public bool NeedUpdate { get; set; }     // 是否需要更新
}
```

---

### 3.5 快速启动（P1）

#### 3.5.1 主页界面结构

```
┌─────────────────────────────────────────────────────────┐
│  Galbox 主页                                            │
├─────────────────────────────────────────────────────────┤
│                                                         │
│  ┌─────────────────────────────────────────────┐       │
│  │ 快速启动卡片（宽度 = 3倍普通卡片）           │       │
│  │ [竖版封面]  游戏名称                        │       │
│  │             最近游玩: 昨天 15:30            │       │
│  │             [立即启动] ← 右下角按钮         │       │
│  └─────────────────────────────────────────────┘       │
│                                                         │
│  最近游戏（横向排列，鼠标悬浮显示启动按钮）              │
│  [卡片1] [卡片2] [卡片3] [卡片4] [卡片5]                │
│                                                         │
│  ────────────────────────────────────────────────────  │
│                                                         │
│  正在游玩 [点击进入完整列表]                            │
│  [卡片] [卡片] [卡片] [卡片]                            │
│  [卡片] [卡片] [卡片] [卡片]                            │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

#### 3.5.2 功能清单

| 功能ID | 功能名称 | 描述 | 验收标准 |
|--------|---------|------|---------|
| F-QS-001 | 快速启动卡片 | 主页显示最近游玩游戏 | 显示封面、名称、最近时间 |
| F-QS-002 | 一键启动 | 点击立即启动游戏 | 启动进程监控、记录开始时间 |
| F-QS-003 | 最近游戏列表 | 显示最近5-10个游玩游戏 | 悬浮显示快速启动按钮 |
| F-QS-004 | 正在游玩入口 | 点击进入完整正在游玩列表 | 跳转到筛选后的游戏库 |

---

### 3.6 游戏健康诊断（P1）

#### 3.6.1 诊断项清单

| 诊断ID | 诊断项 | 检测方法 | 解决方案 |
|--------|-------|---------|---------|
| D-001 | 中文目录检测 | 检查游戏路径是否包含中文/特殊字符 | 建议：一键重命名目录 |
| D-002 | 转区需求检测 | 检查游戏是否需要日本区域设置 | 建议：配置 Locale Emulator |
| D-003 | Windows兼容性 | 检查游戏是否仅支持旧版Windows | 建议：使用虚拟机/兼容模式 |
| D-004 | DirectX检测 | 检查是否缺少 DirectX 组件 | 建议：提供修复工具下载 |
| D-005 | K-Lite检测 | 检查是否缺少视频解码器 | 建议：提供 K-Lite 下载链接 |

#### 3.6.2 诊断结果展示

```
┌─────────────────────────────────────────────────────────┐
│  游戏健康诊断报告                                       │
│  游戏：同居恋人                                         │
├─────────────────────────────────────────────────────────┤
│  ✅ 目录检测：正常                                       │
│  ⚠️ 转区检测：游戏需要日本区域设置                       │
│     [一键配置 Locale Emulator]                          │
│  ✅ DirectX：已安装                                     │
│  ⚠️ K-Lite：未安装，可能影响视频播放                     │
│     [下载 K-Lite Codec Pack]                            │
├─────────────────────────────────────────────────────────┤
│  [一键修复所有问题] [关闭]                              │
└─────────────────────────────────────────────────────────┘
```

---

### 3.7 进程监控（P1）

#### 3.7.1 功能清单

| 功能ID | 功能名称 | 描述 | 验收标准 |
|--------|---------|------|---------|
| F-MON-001 | 游戏启动监控 | 监控游戏进程启动/退出 | 记录开始/结束时间 |
| F-MON-002 | 游玩时长统计 | 累计游玩时长 | 累加每次游玩时长 |
| F-MON-003 | 截图功能 | 对游戏窗口截图 | 保存到 /galbox/screenshots/ |
| F-MON-004 | 老板键 | 组合键隐藏游戏窗口 | 默认：Alt+Shift+H |
| F-MON-005 | 自动存档备份 | 游戏退出时自动备份存档 | 可配置开关 |

#### 3.7.2 进程监控逻辑

```csharp
// 进程监控服务
public class ProcessMonitorService
{
    // 监控配置
    public MonitorConfig Config { get; set; }
    
    // 开始监控
    public void StartMonitoring(int gameId, Process gameProcess)
    {
        // 记录启动时间
        _playSession.StartTime = DateTime.Now;
        
        // 定时检测进程状态（每5秒）
        while (!gameProcess.HasExited)
        {
            // 可选：截图（按配置频率）
            if (Config.AutoScreenshot)
                CaptureScreenshot(gameId);
        }
        
        // 进程退出时
        OnGameExited(gameId);
    }
    
    // 游戏退出处理
    private void OnGameExited(int gameId)
    {
        // 计算本次游玩时长
        var duration = DateTime.Now - _playSession.StartTime;
        
        // 更新统计数据
        UpdatePlayStats(gameId, duration);
        
        // 自动备份存档（如开启）
        if (Config.AutoBackupOnExit)
            BackupSaves(gameId);
    }
}
```

---

### 3.8 刮削系统（P1）

#### 3.8.1 刮削源配置

| 优先级 | 来源 | API地址 | 数据类型 |
|-------|------|---------|---------|
| 1 | Bangumi | https://bangumi.github.io/api/ | 中文信息、简介、标签 |
| 2 | VNDB | https://vndb.org/d11 | 封面、角色图、英文补充 |
| 3 | ymgal | 自建爬虫（无公开API） | 中文补充 |
| 4 | cngal | 自建爬虫（无公开API） | 国内资讯 |

#### 3.8.2 刮削匹配流程

```
游戏添加 → 提取游戏名称特征
    │
    ▼
┌─────────────────────┐
│ 调用 Bangumi API    │
│ 搜索游戏名称         │
└─────────────────────┘
    │
    ▼
┌─────────────────────┐
│ 计算匹配率           │
│ 名称相似度算法       │
└─────────────────────┘
    │
    ├── > 90% → 自动采用
    │
    ├── 70-90% → 弹窗让用户确认
    │             显示候选列表：[Bangumi结果] [VNDB结果] [手动选择]
    │
    ├── < 70% → 用户手动输入/选择
    │
    ▼
┌─────────────────────┐
│ 获取详细信息         │
│ 封面、简介、角色、标签│
└─────────────────────┘
    │
    ▼
保存到本地数据库
```

---

### 3.9 设置页面

#### 3.9.1 设置项清单

| 设置ID | 设置项 | 类型 | 默认值 |
|--------|-------|------|-------|
| S-001 | 刮削源选择 | 多选 | 四源全开 |
| S-002 | Bangumi账号登录 | 登录 | 未登录 |
| S-003 | 游戏启动行为 | 单选 | 无行为/最小化/最小化到托盘 |
| S-004 | 游戏退出行为 | 单选 | 最大化至桌面 |
| S-005 | 进程监控开关 | 开关 | 开启 |
| S-006 | 高级内存监控 | 开关 | 关闭 |
| S-007 | 自动存档备份 | 开关 | 开启 |
| S-008 | 备份频率 | 单选 | 每次启动/每次退出/手动 |
| S-009 | 截图保存路径 | 文件路径 | /galbox/screenshots/ |
| S-010 | 老板键组合 | 快捷键 | Alt+Shift+H |

---

## 四、预留功能（P2）

### 4.1 流程图追踪（预留）

**状态：** 第一版预留 API 接口，前端隐藏功能模块

**API 接口设计：**

| 接口 | 方法 | 功能 |
|------|------|------|
| `GET /api/v1/flowcharts/{game_id}` | GET | 获取游戏流程图结构 |
| `POST /api/v1/flowcharts/{game_id}/sync` | POST | 同步存档位置到流程图节点 |

**数据结构（预留）：**

```json
{
  "nodes": [
    {
      "node_id": "node_001",
      "chapter": "第一章",
      "title": "初遇",
      "type": "normal",
      "position": {"x": 100, "y": 50}
    }
  ],
  "edges": [
    {"from": "node_001", "to": "node_002", "type": "linear"}
  ]
}
```

---

### 4.2 成就系统（预留）

**状态：** 第一版预留 API 接口，前端隐藏功能模块

**API 接口设计：**

| 接口 | 方法 | 功能 |
|------|------|------|
| `GET /api/v1/achievements/{game_id}` | GET | 获取游戏成就列表 |
| `POST /api/v1/achievements/{game_id}/unlock` | POST | 上报成就解锁 |
| `GET /api/v1/achievements/share/{id}` | GET | 生成分享卡片 |

---

## 五、开发里程碑

### 5.1 阶段规划

| 阶段 | 目标 | 功能模块 | 预估周期 |
|------|------|---------|---------|
| **Phase 0** | 项目骨架 | WinUI3项目创建、基础UI结构 | 1-2周 |
| **Phase 1** | 游戏库核心 | 游戏扫描、刮削、详情页 | 2-3周 |
| **Phase 2** | 存档管理 | Renpy/krkr/Tyrano存档解析 | 3-4周 |
| **Phase 3** | 补丁中心 | moyu.moe对接、补丁下载安装 | 1-2周 |
| **Phase 4** | 进程监控 | 启动监控、截图、老板键 | 1-2周 |

**第一版预估总周期：** 8-12周

### 5.2 Phase 0 验收清单

| 验收项 | 标准 |
|-------|------|
| WinUI3 项目创建 | Visual Studio 创建 WinUI 3 Desktop App |
| 基础页面结构 | MainPage、LibraryPage、GameDetailPage、SettingsPage 路由 |
| 导航框架 | 左侧导航栏 + 页面切换 |
| 数据库初始化 | SQLite 数据库创建 + 基础表结构 |
| 项目可运行 | 编译无错误，可启动显示空白页面 |

---

## 六、非功能需求

### 6.1 性能要求

| 指标 | 要求 |
|------|------|
| 游戏库加载时间 | < 3秒（1000游戏以内） |
| 存档解析时间 | < 1秒/存档 |
| 补丁搜索响应 | < 5秒 |
| 进程监控延迟 | < 100ms |

### 6.2 兼容性要求

| 维度 | 要求 |
|------|------|
| 操作系统 | Windows 10 1903+ / Windows 11 |
| 游戏引擎 | Renpy / krkr / Tyrano（第一版） |
| 屏幕分辨率 | 最小 1280x720 |

### 6.3 安全要求

| 要求 | 实现 |
|------|------|
| 存档操作安全 | 三重保障流程（进程检测+备份+确认） |
| 补丁来源可信 | 仅从 moyu.moe 官方源下载 |
| 用户数据保护 | 本地存储，不上传用户数据 |

---

## 七、附录

### 7.1 参考资源

| 资源 | 链接 |
|------|------|
| WinUI 3 官方文档 | https://docs.microsoft.com/windows/apps/winui/winui3/ |
| WinUI 3 Gallery | 示例应用 |
| PotatoVN 开源 | 可参考实现方案 |
| Renpy 存档格式 | https://renpy.org/doc/html/save.html |
| moyu.moe 补丁库 | https://www.moyu.moe/ |
| VNDB API | https://vndb.org/d11 |
| Bangumi API | https://bangumi.github.io/api/ |

### 7.2 术语定义

| 术语 | 定义 |
|------|------|
| **刮削** | 从外部数据源获取游戏元信息（名称、封面、简介等） |
| **存档节点** | 存档对应的剧情位置标记（如"第三章-选择A路线"） |
| **存档快照** | 玩家主动创建的关键节点存档备份 |
| **分支存档** | 同一游戏不同剧情路线的存档分组管理 |
| **老板键** | 快捷键一键隐藏游戏窗口 |

---

**PRD 文档完成**

**下一步：按 Phase 0 验收清单开始开发**