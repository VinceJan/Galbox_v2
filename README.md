# Galbox

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows-blue.svg)]()
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)]()
[![Version](https://img.shields.io/badge/version-1.0.0--beta-orange.svg)]()

**Galbox** 是一款专为 Galgame（视觉小说/美少女游戏）爱好者设计的 Windows 游戏管理平台客户端。它提供了游戏库管理、元数据刮削、存档备份、补丁管理等一系列功能，帮助玩家更好地组织和享受他们的游戏收藏。

## 功能特性

### 核心功能

- **游戏库管理** - 自动扫描、手动添加游戏，支持多种引擎识别（Ren'Py、Krkr、Unity、RPG Maker 等）
- **元数据刮削** - 支持多个数据源（Bangumi、VNDB、ymgal、cngal），智能匹配游戏信息
- **存档管理** - 自动检测存档位置，一键备份/恢复，支持存档槽位切换
- **补丁中心** - 汉化补丁、修复补丁、18+补丁的下载和管理
- **错误检测** - 自动检测游戏兼容性问题（中文路径、日语区域、DirectX、解码器等）
- **进程监控** - 实时监控游戏进程，Boss Key 快速隐藏，自动截图

### 特色功能

- **智能刮削匹配** - 基于 Levenshtein 距离算法，90%+ 匹配自动接受
- **多引擎存档识别** - 支持 Ren'Py、Kirikiri、TyranoBuilder、Unity、RPG Maker 等常见引擎
- **一键存档切换** - 快速切换不同存档槽位，方便体验不同剧情分支
- **系统兼容性检查** - 检测并提示常见运行问题，提供解决方案

## 技术栈

| 技术 | 用途 |
|------|------|
| WinUI 3 | UI 框架 |
| .NET 8.0 | 运行时 |
| CommunityToolkit.Mvvm | MVVM 框架 |
| Entity Framework Core | 数据库 ORM |
| SQLite | 本地数据库 |
| Microsoft.Extensions.DependencyInjection | 依赖注入 |
| Microsoft.Extensions.Logging | 日志系统 |

## 系统要求

- **操作系统**: Windows 10 1809 (17763) 或更高版本
- **架构**: x64
- **依赖**: Windows App SDK Runtime

## 安装说明

### 从发布包安装

1. 从 [Releases](https://github.com/galbox/galbox/releases) 页面下载最新的 `.msix` 或 `.msixbundle` 安装包
2. 双击安装包进行安装
3. 如果遇到依赖问题，请先安装 [Windows App SDK Runtime](https://aka.ms/windowsappsdk/1.5/latest/download)

### 开发环境搭建

```bash
# 克隆仓库
git clone https://github.com/galbox/galbox.git
cd Galbox_v2

# 安装 Visual Studio 2022 或更高版本，确保包含以下组件：
# - .NET 8.0 SDK
# - Windows App SDK 1.5+
# - WinUI 3 模板

# 打开解决方案
# Galbox.sln

# 构建项目
dotnet build
```

## 快速开始

### 1. 添加游戏

- 点击左侧导航栏的 **Library**
- 点击 **Add Game** 按钮
- 选择游戏文件夹或输入安装路径
- 系统将自动识别游戏引擎并尝试刮削元数据

### 2. 刮削元数据

- 在游戏详情页点击 **Scrape** 按钮
- 系统将从 Bangumi、VNDB 等数据源搜索匹配结果
- 选择最匹配的结果进行应用

### 3. 管理存档

- 进入 **Save Manager** 页面
- 选择目标游戏
- 创建备份、恢复存档或切换存档槽位

### 4. 检查错误

- 在游戏详情页点击 **Error Check**
- 系统将自动检测兼容性问题并提供解决方案

## 项目结构

```
Galbox_v2/
├── src/
│   ├── Galbox.App/          # WinUI3 主应用程序
│   │   ├── Views/           # XAML 视图
│   │   ├── ViewModels/      # MVVM ViewModel
│   │   ├── Services/        # 应用服务
│   │   │   ├── ProcessMonitorService.cs    # 进程监控
│   │   │   ├── SaveManagementService.cs    # 存档管理
│   │   │   ├── GameScrapingService.cs     # 游戏刮削
│   │   │   ├── AutoScrapingService.cs      # 自动刮削
│   │   │   └── ErrorCheckingService.cs     # 错误检测
│   │   ├── Converters/      # XAML 转换器
│   │   └── Models/          # 数据模型
│   ├── Galbox.Core/         # 核心业务逻辑
│   │   └── Api/             # API 客户端 (Bangumi, VNDB, ymgal, cngal)
│   └── Galbox.Data/         # 数据层
│       └── Entities/        # 数据库实体
├── docs/                    # 文档
│   ├── USER_MANUAL.md       # 用户手册
│   ├── DEVELOPER_GUIDE.md   # 开发者指南
│   ├── API_DOCUMENTATION.md # API 文档
│   └── CHANGELOG.md         # 更新日志
└── README.md
```

## 数据源

Galbox 支持以下元数据数据源：

| 数据源 | 类型 | 优先级 | 状态 |
|--------|------|--------|------|
| [Bangumi](https://bgm.tv) | 中文游戏数据库 | 高 | 已实现 |
| [VNDB](https://vndb.org) | 视觉小说数据库 | 中 | 已实现 |
| [ymgal](https://www.ymgal.games/) | 中文 Galgame 数据库 | 低 | 待研究 |
| [cngal](https://www.cngal.org/) | 中文 Galgame 数据库 | 低 | 待研究 |

## 文档

- [用户手册](docs/USER_MANUAL.md) - 详细的功能使用指南
- [开发者指南](docs/DEVELOPER_GUIDE.md) - 参与项目开发
- [API 文档](docs/API_DOCUMENTATION.md) - API 接口说明
- [更新日志](docs/CHANGELOG.md) - 版本更新记录

## 许可证

本项目采用 MIT 许可证开源。详见 [LICENSE](LICENSE) 文件。

## 贡献指南

欢迎参与项目贡献！请参阅 [开发者指南](docs/DEVELOPER_GUIDE.md) 了解如何参与开发。

## 联系方式

- GitHub Issues: [提交问题](https://github.com/galbox/galbox/issues)
- 讨论: [GitHub Discussions](https://github.com/galbox/galbox/discussions)

---

*Made with love for Galgame enthusiasts.*