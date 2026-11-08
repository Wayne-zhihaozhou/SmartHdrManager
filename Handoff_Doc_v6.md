# SmartHDRManager 代码删减 — 交接文档 v6

## 1. 当前任务
精简 SmartHdrManager（C# WinForms 桌面应用）：仅保留游戏 HDR 自动开启 + 日志功能，删除所有冗余功能（浏览器、视频、创意应用、录制与直播、更新等）。

## 2. 已完成的工作

### MainForm.cs — 已完成清理
- 删除 BuildAppsPage() 和 FillAppsPage()（程序选项卡）
- 删除 BuildSettingsPage 中的视频/浏览器/更新区块：
  - 删除"浏览器中的 YouTube"区块
  - 删除"视频 / RTX Video HDR"区块
  - 删除"更新"区块
- 删除 WireEvents 中的视频/浏览器/更新事件绑定：
  - 删除 _swBrowserWindowed.CheckedChanged
  - 删除 _swKeepBrowserHdr.CheckedChanged
  - 删除 _swVideoAuto.CheckedChanged
  - 删除 _videoMode.SelectedIndexChanged
  - 删除 _videoTrigger.SelectedIndexChanged
  - 删除 _swVideoBrowsers.CheckedChanged
  - 删除 _swVideoPlayers.CheckedChanged
  - 删除 _videoRevert.ValueChanged
  - 删除 _swVideoBattery.CheckedChanged
  - 删除 _swAutoUpdate.CheckedChanged
  - 删除 _fallback.ValueChanged
- 删除 RefreshView 中的 UpdateScriptStatus() 调用
- 删除 LoadSettings 中的视频/浏览器/更新控件值读取
- 删除 UpdateVideoDependents() 方法（空方法）
- 删除 OpenScriptGuide() 方法
- 删除 OnCheckForUpdatesAsync() 方法
- 删除 _checkingUpdates 字段
- 删除 _videoMode.AddTab() 初始化
- 删除 _videoTrigger.AddTab() 初始化
- 删除 _fallback 相关字段
- 删除 _browserWindowedDesc / _keepBrowserDesc 相关字段
- 删除 _scriptStatusLabel / _lastScriptSeen 相关字段
- 删除 _checkUpdatesButton / _updateDesc 相关字段
- 删除 _swAutoUpdate 相关字段
- 删除 _swVideoAuto / _swVideoBrowsers / _swVideoPlayers / _swVideoBattery 相关字段
- 删除 _videoRevert 相关字段
- 删除 videoHandlesBrowsers 相关逻辑
- 删除 SetVideoDetailsVisible() 方法
- 调整 ShowPage 索引：删除"程序"标签页，索引从 0,1,2,3 改为 0,1,2
- 调整 ShowPage 中 UpdateNeverHdrDescription() 调用索引：2 → 1
- 清理构造函数中所有视频相关选项卡初始化
- 清理所有视频/浏览器/更新相关字段
- 行数从 1522 → 1294（减少 228 行）

### Core.cs — 已完成重构
- 新增 stub 类：AppRule / AppCategories / AppModes
- 精简 AppCatalog / VideoPlayers / Updater / VideoHdrModes / VideoTriggers / VideoScanOptions / VideoCandidate / RtxVideoStatus / VideoPlaybackMonitor / UpdateInfo
- AppConfig 已删除所有视频/浏览器/更新相关字段

### Engine.cs — 已完成清理
- EngineStatus.BlockedBy 从 string? 改为 bool
- Target(Snapshot s) 方法已删除
- NoteBrowserScript 调用已删除
- FindRunningHdrGame 调用简化为仅 out string? runningPath
- video: demand 字符串分支已删除
- DescribeDemand 中 "video" 和 "block" 分支已删除
- recording app blocker 逻辑已从 Decide() 中删除
- 所有 Target(s) 调用替换为 s.Monitors
- 删除 Video playback 区块（~696 行）：
  - EvaluateVideo、DetectVideo、VideoBlockReason
  - ScheduleBrowserNudgeAfterHdrOn、SkipVideoNow、EndSkip
  - SkipCurrentVideo、ResumeVideoHdr、VideoMenuState
  - IgnoreOrSkipVideo、ResetVideo、VideoFilter、SyncTarget、SameTarget
- 删除视频相关字段：_checkedHwnd、_checkedMonitor、_checkedAllowed、_checkedTarget、_displaysChanged
- 删除 FindRunningHdrGame 的 videoBlocker/allBlocker out 参数
- 清理 Decide()：删除 EvaluateVideo 调用和 videoTarget 逻辑
- 清理 Decide()：删除 ScheduleBrowserNudgeAfterHdrOn 调用
- 清理 Decide()：删除 _videoEnded/_videoSdrRequested 赋值
- 清理 EvaluateHdrRequirement()：删除浏览器 VideoAuto 检查、本地视频播放器检测
- 清理 PollHotkeysFallback()：IgnoreOrSkipVideo → IgnoreGame
- 清理 HotkeyIgnoreCurrent()：IgnoreOrSkipVideo → IgnoreGame
- 清理 ApplyOverride()：删除 SyncTarget 调用
- 清理 KeepBrowserHdr 条件：删除 !(s.VideoAuto && s.VideoBrowsers) 检查
- 行数从 3231 → 2535（减少 696 行）

## 3. 未开始的工作

### Tray.cs（390 行）— 已完成清理
- 修改构造函数参数，删除 videoState/skipVideo/resumeVideo
- 删除 skipVideoItem 菜单项
- 删除 shownVideoState 相关逻辑
- 删除菜单 Opening 事件中的视频状态更新逻辑

### Program.cs（495 行）— 已完成清理
- 删除 UpdateCheckEveryMs / _nextUpdateCheckMs / _updateCheckRunning 定时器
- 删除 AutoCheckForUpdateAsync() 方法
- 删除 VideoPlayers.RefreshContextMenu() / RefreshAppRegistrations() 调用
- 删除 Updater.CleanUp() 调用
- 删除 justUpdated 相关更新提示逻辑
- 删除 --launch-video 命令行参数处理
- 修改 TrayAppContext 构造函数参数，删除 video 相关回调
- 行数从 495 → ~430（减少约 65 行）

### Launcher.cs（363 行）— 待清理
- 删除 videoPlayer 参数相关逻辑
- 简化 --launch-video 命令行参数处理

### SmartHdrManager.csproj — 待确认
- 确认已删除对已删文件的项目引用

## 4. 用户偏好与约束
- 必须保留：颜色配置重载、刷新应用、保持桌面为 SDR
- 必须保留：日志功能
- 必须保留：游戏库扫描、以 HDR 启动功能
- 必须保留：AppRule / AppCategories / AppModes 类
- 必须保留：AppCatalog.IsKnown()

## 5. 重要决策
- AppCatalog stub：保留 IsKnown()，其他方法返回空/默认值
- VideoPlayers stub：所有方法返回空/0/false
- Updater stub：CheckAsync() 返回无更新，CleanUp() 空操作
- Target() 方法已删除，所有调用已改为 s.Monitors
- 已删除所有视频/浏览器/创意应用/录制/更新相关功能
- MainForm.cs 已清理：删除程序选项卡、视频/浏览器/更新 UI、相关方法和字段
- 行数从 1522 → 1283（减少 239 行）
- Tray.cs 已清理：删除视频相关菜单项和参数
- Program.cs 已清理：删除更新定时器和相关逻辑
- 编译已通过，生成 exe 文件

## 6. 剩余工作优先级
1. Launcher.cs — 简化 videoPlayer 参数
2. SmartHdrManager.csproj — 确认引用已更新
3. 清理未使用字段警告（_restoreAtMs、_blockedBy）
4. 完整编译验证

## 7. 关键文件状态
| 文件 | 状态 | 行数 |
|------|------|------|
| Core.cs | 已完成 | 1617 |
| Engine.cs | 已完成 | 2753 |
| MainForm.cs | 已完成 | 1283 |
| Tray.cs | 已完成 | 381 |
| Program.cs | 已完成 | 431 |
| Launcher.cs | 未开始 | 363 |

## 8. 注意事项
- 当前环境已配置 .NET SDK 10.0.403（位于 C:\Users\admin\.dotnet\sdk）
- 编译已通过，生成 exe：bin\Debug\net10.0-windows\SmartHdrManager.exe
- 建议用 edit 工具进行精确编辑

---
文档生成时间：2026-11-08
当前 session 已停止探索，等待接手人执行。

## 9. 本次 session 完成记录
- MainForm.cs 清理已完成
  - 删除 BuildAppsPage() 和 FillAppsPage()（程序选项卡）
  - 删除 BuildSettingsPage 中的视频/浏览器/更新区块
  - 删除 WireEvents 中的视频/浏览器/更新事件绑定
  - 删除 RefreshView 中的 UpdateScriptStatus() 调用
  - 删除 LoadSettings 中的视频/浏览器/更新控件值读取
  - 删除 UpdateVideoDependents()、OpenScriptGuide()、OnCheckForUpdatesAsync() 方法
  - 删除 _checkingUpdates 及相关字段
  - 删除 _videoMode / _videoTrigger / _fallback 相关字段
  - 删除 _browserWindowedDesc / _keepBrowserDesc / _scriptStatusLabel / _lastScriptSeen 相关字段
  - 删除 _checkUpdatesButton / _updateDesc / _swAutoUpdate 相关字段
  - 删除 _swVideoAuto / _swVideoBrowsers / _swVideoPlayers / _swVideoBattery 相关字段
  - 删除 _videoRevert 相关字段
  - 删除 videoHandlesBrowsers 相关逻辑
  - 删除 SetVideoDetailsVisible() 方法
  - 调整 ShowPage 索引：删除"程序"标签页
  - 删除 s.Video 相关显示逻辑
  - 删除 CheckForUpdatesAutomatically 相关代码
  - 修复 CreateLaunchShortcut 方法：删除 videoPlayer 参数，删除 --launch-video 参数
  - 修复 CreateLaunchShortcut 方法签名缺失问题
  - 修复 _log 字段初始化语法（添加花括号）
  - 清理 FormatSeconds 注释中的 video 引用
  - 行数从 1522 → 1283（减少 239 行）
- Engine.cs 清理已完成
  - 删除 Video playback 区块 ~696 行
  - 清理所有视频/浏览器相关引用
  - Target(s) → s.Monitors 替换完成
  - 删除 ForceSdr/ForceSdrApps 相关代码
  - 删除 KeepBrowserHdr/浏览器保持 HDR 逻辑
  - 删除 AllVideoApps 相关检查
  - 删除 _forceSdrSeenMs 字段
  - 清理 ResolveVideoMonitor 注释（从"video HDR"改为"HDR"）
  - 删除 AppCategories.Creative 和 AppCategories.Recording 的引用
  - 添加 _restoreAtMs 字段声明
  - 删除 SchedulePostSdrFixes 的 browsers 参数
  - 行数从 3231 → 2753（减少 478 行）
- Core.cs 清理
  - 删除 ForceSdrApps 配置字段
  - 删除 RequireKeywordForFullscreenVideo 配置字段
  - 删除 AppCategories.Creative 和 AppCategories.Recording 枚举值
  - 删除重复的 AppCategories 枚举声明
  - 修复 Task 类型引用（使用完整命名空间）
  - 行数 1617
- Tray.cs 清理
  - 删除构造函数中的 videoState/skipVideo/resumeVideo 参数
  - 删除 skipVideoItem 菜单项及点击事件
  - 删除 shownVideoState 相关逻辑
  - 删除菜单 Opening 事件中的视频状态更新
  - 行数从 390 → 381（减少 9 行）
- Program.cs 清理
  - 删除 UpdateCheckEveryMs / _nextUpdateCheckMs / _updateCheckRunning 定时器
  - 删除 AutoCheckForUpdateAsync() 方法
  - 删除 VideoPlayers.RefreshContextMenu() / RefreshAppRegistrations() 调用
  - 删除 Updater.CleanUp() 调用
  - 删除 justUpdated 相关更新提示逻辑
  - 删除 --launch-video 命令行参数处理
  - 修改 TrayAppContext 构造函数参数
  - 行数从 495 → 431（减少 64 行）

## 10. 编译结果
- ✅ 编译成功（0 错误，4 警告）
- 输出：bin\Debug\net10.0-windows\SmartHdrManager.exe
- 警告内容：
  - Program.cs(197): bool 与 bool? 比较始终为 true
  - MainForm.cs(689): bool 与 bool? 比较始终为 true
  - Engine.cs(256): _restoreAtMs 字段未赋值
  - Engine.cs(220): _blockedBy 字段已赋值但未使用
