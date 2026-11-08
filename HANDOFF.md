# 交接文档：SmartHdrManager 界面汉化（已完成）

## 任务
将本项目（C# WinForms，`SmartHdrManager`）的**用户可见界面字符串**全部汉化为中文。

## ✅ 当前状态：已全部完成
- 11 个 .cs 文件共 539 处字符串已汉化（`git diff --stat` 确认，近乎 1:1 替换，无逻辑改动）
- 构建通过：0 警告 0 错误
- 已用 grep 复查，无遗漏的用户可见英文字符串
- 唯一未纳入范围：`SmartHdrManager_YouTube.user.js`（Tampermonkey 脚本，用户确认不汉化）

## 已汉化文件
| 文件 | 内容 |
|------|------|
| `Tray.cs` | 托盘菜单、气泡通知、tooltip |
| `VideoPlayers.cs` | 播放器注册/右键菜单/AppName 后缀（"（HDR 已开启）"） |
| `VideoFileInfo.cs` | 视频信息描述 |
| `ModernControls.cs` | 通用控件文本 |
| `Launcher.cs` | 以 HDR 启动对话框 |
| `VideoPlayback.cs` | 视频状态描述（Describe 系列） |
| `Updater.cs` | 更新对话框（立即更新/发布页面/稍后/取消）、下载进度 |
| `Program.cs` | 快捷键日志、托盘气泡、tooltip 状态 |
| `Engine.cs` | 全部 Log 消息、通知、游戏行副标题、reason 字符串 |
| `Core.cs` | MonitorFilter.Describe、DisplayColorState、HdrApiName 等 |
| `MainForm.cs` | 状态卡、设置页、游戏页、程序页、所有对话框 |

`AppCatalog.cs` / `Library.cs` / `Sqlite.cs` / `SteamAppInfo.cs` / `Theme.cs`：无用户可见字符串，未改动。

## 核心约束（后续修改必须遵守）
1. **只改用户可见字符串**：按钮/标签/对话框/托盘气泡/进度文本/Log 标签页消息/状态卡文本/设置页标题与描述
2. **绝不翻译 C# 标识符**（参数名、变量名、方法名）。⚠️ 事故史：曾把 `overrideShortcut` 误改为 `override快捷键` 导致编译失败，后已修复
3. **绝不翻译哨兵值**（用于程序内比较的字符串）：
   - `Engine.cs` `"not determined yet"`（HdrApiName 比较）
   - `Engine.cs` `"unknown"`（前台窗口比较）
   - `Core.cs` `problem` 文本与 `Engine.cs:1423` 的 `Contains("未在 设置 › 显示器 中开启切换")` 已同步翻译，**两处必须一致**
   - `MainForm.cs` `OnManageNeverHdr` 的 `const string prefix = "从不 HDR  ·  "` 与 `Engine.cs GetExcludedRows()` 的 Subtitle 前缀**必须一致**（改一处两处都要改）
4. **保留英文**：注释、注册表键/GUID、文件名（config.json/README.md/CHANGELOG.md）、Steam 启动语法、进程名、技术关键词（PQ/HLG/Dolby Vision/RTX/RTX Video HDR/NVIDIA App/madVR/OBS Studio/Streamlabs/Tampermonkey）、日志标签（INFO/ERROR/CONFIG/VIDEO/UPDATE/MANUAL/PAUSE/SCAN/LAUNCH/HDR/SDR/COLOR/LIST/APPS/HINT）、命令行参数（--launch/--force-hdr/--updated/--tray）、枚举值常量（Smart/NativeOnly/Always/Fullscreen/Any/Off/HdrOnly/NoHdr）、DLL 名、box 类型码、颜色格式名（RGB/YCbCr 4:4:4 等）
5. **Program.cs `RegisterNotificationName()`** 的 `key.SetValue("DisplayName", "Smart HDR Manager")` 是注册表值（Windows 通知发件人名），保留英文
6. 中文标点全角（）「」""；保留 `·` 分隔、`›` 面包屑、`…` 省略号、`–` 短破折号
7. **不改变行为逻辑**，只改字符串字面量内部文字

## 术语对照表
| 英文 | 中文 |
|------|------|
| Launch with HDR | 以 HDR 启动 |
| Play with HDR | 以 HDR 播放 |
| Open with | 打开方式 |
| Pause automation / Resume automation / Back to automatic | 暂停自动化 / 恢复自动化 / 恢复自动 |
| Start with Windows | 开机自启动 |
| Never HDR | 从不 HDR |
| HDR override / Manual override | HDR 覆盖 / 手动覆盖 |
| Apps tab | 程序选项卡 |
| Games tab | 游戏选项卡 |
| Scan games / Scanning… | 扫描游戏 / 扫描中… |
| Smart mode | 智能 |
| SDR for this video | 此视频使用 SDR |
| Back to SDR | 切回 SDR |
| Keep the desktop in SDR | 保持桌面为 SDR |
| Weekly update check / Check for updates | 定期更新检查 / 检查更新 |
| Check automatically | 自动检查 |
| Reset to defaults | 恢复默认设置 |
| Default app… | 默认应用… |
| Video players / Web browsers / Creative apps / Recording & streaming | 视频播放器 / 浏览器 / 创意应用 / 录制与直播 |
| Added by you / Found automatically | 您添加的 / 自动发现 |
| Auto / HDR only / Off (模式显示) | 自动 / 仅 HDR / 关闭 |
| Restore (从不 HDR 列表按钮) | 恢复 |
| Game options | 游戏选项 |
| Delay before HDR turns on | HDR 开启前延迟 |
| Keep HDR on after the game closes | 游戏关闭后保持 HDR |
| Copy Steam launch option / Create HDR desktop shortcut | 复制 Steam 启动选项 / 创建 HDR 桌面快捷方式 |
| Open config.json / Reload config.json / Open log file | 打开 config.json / 重新加载 config.json / 打开日志文件 |
| Video players status | 打开方式 ✓ / 右键 ✓ |
| 游戏库来源专名 | 保留英文（Steam/Epic/GOG/Xbox/Game Bar） |
| 显示器描述 | "1 个选定显示器"/"主显示器"/"所有显示器" |

## 验证方法
```bash
# 构建（SDK 不在 PATH，必须全路径；在 Git Bash 中）
cd /c/0_Code/SmartHdrManager && "$HOME/.dotnet/dotnet.exe" build -v q -nologo

# 查看已改内容（HEAD 为汉化前干净状态）
git diff --stat
git diff -- MainForm.cs | head -100

# 复查遗漏（应无输出）
grep -nE 'Text = "[A-Za-z]|Title = "[A-Za-z]|SetToolTip\([a-z]+, "[A-Za-z]' *.cs | grep -v "Smart HDR Manager"
```

## 翻译方法（供后续参考）
- 用 `edit_file_tool` 精确替换；`replace_from` 必须**完整包含引号**（`"..."`/`$"..."`），避免短串误匹配
- 重复串需带上下文区分
- 每次替换后确认 "Replaced N unique matches" 数量符合预期，再 build 验证

## 建议的后续工作（可选）
1. 运行程序逐页人工检查（游戏/程序/设置三个选项卡 + 托盘菜单 + 各对话框）
2. 如用户希望，可考虑把文件对话框 Filter（`"Programs (*.exe)|*.exe"` 等）也汉化，当前保留英文
3. Tampermonkey 脚本 `SmartHdrManager_YouTube.user.js` 如需汉化需单独处理（其字符串含 `// @name` 等元数据，勿动）
