# HDR亮控 (HDR Bright Control)

一个 Windows 托盘应用，用于**自动切换 HDR/SDR** 并根据当前运行的程序**自动调节 LG webOS 电视亮度**。

> 已在 **LG G6 电视**上验证电视亮度调节功能可用。

---

## 功能特性

### 🎮 HDR/SDR 自动切换
- 监控前台运行程序，根据预设列表自动开启或关闭 HDR
- 支持**启动时自动触发 HDR**（不仅依赖前台窗口）
- 智能识别游戏启动器（Steam、Epic 等），自动跟踪其启动的游戏
- 检测外部 HDR 变更（Win+Alt+B、游戏内切换等），避免冲突
- 无 HDR 内容结束后自动切回 SDR（可配置延迟）

### 📺 LG 电视亮度控制
- 通过 **LG SSAP 协议**（WebSocket）直接连接电视，无需额外软件
- 根据当前运行的程序/桌面**自动调节电视亮度**
- 每个游戏可独立设置亮度值（0–100）
- 支持手动测试连接、重新配对、设置亮度
- 配对密钥持久化存储，避免重复确认

### ⚙️ 其他特性
- **无控制台窗口**的纯托盘应用
- **自包含部署**：内置 .NET 运行时，目标机器无需安装 .NET
- **开机自启动**：通过 Windows 注册表实现
- **单实例运行**：多次启动仅激活已有进程
- **命令行支持**：`--toggle-pause` 切换 HDR 控制开关
- **便携模式**：放置 `portable.txt` 使配置与 exe 同目录
- **深色主题**界面，DPI 自适应

---

## 系统要求

| 项目 | 要求 |
|------|------|
| 操作系统 | Windows 10 / Windows 11 |
| .NET 运行时 | 无需安装（自包含部署） |
| 网络 | 仅电视亮度控制需要：PC 与电视在同一局域网 |
| LG webOS 电视 | 支持 SSAP 协议的 webOS 3.0+ |

---

## 下载安装

### 方式一：独立安装包（推荐）

下载自包含发布包，解压后运行 `HdrBrightControl.exe` 即可。

- 无需在目标机器安装 .NET
- 体积较大但完全独立

### 方式二：便携模式

1. 将 `HdrBrightControl.exe` 复制到任意目录
2. 在同目录创建空文件 `portable.txt`
3. 配置文件将保存在 exe 所在目录，方便迁移

---

## 使用说明

### 首次启动

1. 运行 `HdrBrightControl.exe`
2. 托盘图标出现（默认隐藏窗口）
3. 左击托盘图标打开设置窗口

### 添加游戏/程序到列表

**HDR 列表**：运行时自动开启 HDR 的程序  
**SDR 列表**：运行时强制保持 SDR 的程序

#### 方法一：从文件添加
1. 切换到对应标签页（HDR / SDR）
2. 点击「添加程序」，选择游戏的 `.exe` 文件

#### 方法二：从运行中的进程添加
1. 确保游戏正在运行
2. 点击「添加正在运行」，从列表中选择

### 设置单游戏亮度

1. 在 HDR/SDR 列表中选中目标游戏
2. 使用亮度滑块设置该游戏的电视亮度值（0–100）
3. 切换至该游戏时自动应用

### 电视连接测试

1. 打开「测试」标签页
2. 点击「测试连接」验证与电视的通信
3. 如需要，点击「重新配对」并在电视上确认

### 命令行操作

```powershell
HdrBrightControl.exe --toggle-pause
```

在已有实例运行时，此命令会切换 HDR 控制的暂停/恢复状态。

---

## 配置文件

配置文件路径：

- **安装模式**：`%APPDATA%\HdrBrightControl\config.json`
- **便携模式**：与 `HdrBrightControl.exe` 同目录的 `config.json`

### config.json 结构

```json
{
  "ConfigVersion": 1,
  "HdrGamesAndApps": ["game1", "game2"],
  "SdrGamesAndApps": ["app1", "app2"],
  "LauncherGames": {
    "launcher.exe": ["game1.exe"]
  },
  "ProgramPaths": {
    "game1": "C:\\Games\\game1\\game1.exe"
  },
  "PerGameBrightness": {
    "game1": 70,
    "app1": 40
  },
  "GameHdrModes": {
    "game1": true
  },
  "EnableHdrOnProcessStart": true,
  "ProcessScanIntervalMs": 100,
  "SdrFallbackDelaySeconds": 3,
  "DesktopBrightness": 50,
  "Brightness": {
    "TvIp": "192.168.1.4",
    "Enabled": true
  }
}
```

### 关键配置项说明

| 配置项 | 说明 |
|--------|------|
| `HdrGamesAndApps` | HDR 游戏/程序列表（进程名） |
| `SdrGamesAndApps` | SDR 游戏/程序列表 |
| `LauncherGames` | 启动器映射：launcher.exe → 其启动的游戏 |
| `PerGameBrightness` | 每个游戏的电视亮度值 |
| `EnableHdrOnProcessStart` | 是否在进程启动时即触发 HDR（而非仅前台） |
| `SdrFallbackDelaySeconds` | 无 HDR 内容后多久切回 SDR（秒） |
| `DesktopBrightness` | 桌面模式的电视亮度 |
| `Brightness.TvIp` | LG 电视的 IP 地址 |
| `Brightness.Enabled` | 是否启用电视亮度控制 |

---

## 电视亮度控制原理

### 连接方式

通过 **SSAP (SmartShare AllShare Protocol)** 与 LG webOS TV 通信：

1. WebSocket 连接到电视的 `wss://TV_IP:3001`（或 `ws://TV_IP:3000`）
2. 执行 hello → getSystemInfo → register 握手流程
3. 通过 SSAP API 设置/读取亮度

### 配对机制

- 首次连接需在电视上确认配对提示
- 配对密钥 (`client-key`) 持久化存储，后续启动自动使用
- 支持手动重新配对

### 网络路由

- 自动绑定与电视同网段的网卡直连
- 避免 VPN/虚拟网络干扰 TV 通信
- 绕过系统代理（TV 使用自签名证书）

---

## 编译指南

### 环境要求

- **.NET SDK 10.0** 或更高版本

### 编译命令

```powershell
dotnet build HdrBrightControl.csproj -c Release
```

### 输出位置

- **EXE**：`bin\Release\net10.0-windows\win-x64\publish\HdrBrightControl.exe`
- **完整发布包**：`bin\Release\net10.0-windows\win-x64\publish\`（含 .NET 运行时）

### 编译注意事项

- 仅允许 Release 配置，禁止 Debug
- 若进程被占用，先执行 `Stop-Process -Name HdrBrightControl -Force`
- 提交前清理 `obj/` 和 `bin/` 中的 Debug 产物：`dotnet clean HdrBrightControl.csproj`

---

## 技术栈

| 层面 | 技术 |
|------|------|
| 语言 | C# (latest) |
| 框架 | .NET 10.0 Windows Forms |
| HDR 控制 | Windows DisplayConfig API |
| 电视通信 | WebSocket (SSAP 协议，纯 C# 实现) |
| 配置存储 | JSON 文件 |
| 部署方式 | 自包含发布 (Self-Contained) |

---

## 参考项目

基于 [SmartHdrManager](https://github.com/VantaCode420/SmartHdrManager) by VantaCode420

---

## 许可证

Copyright (c) 2026 Wayne-zhihaozhou. Based on SmartHdrManager by VantaCode420.
