using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SmartHdrManager
{
    /// <summary>
    /// Owns the tray icon, the global hotkeys, the background engine and (only while it is open) the settings window.
    /// There is no ApplicationContext.MainForm, so closing the window never ends the process; only "Exit" does.
    /// </summary>
    internal sealed class TrayAppContext : ApplicationContext
    {
        private const int DisposeHiddenWindowAfterMs = 5000;

        private readonly HdrEngine _engine;
        private readonly EventWaitHandle _showEvent;
        private readonly CommandEvents _commands;
        private readonly TrayAndStartupManager _tray;
        private GlobalHotkeys? _hotkeys;
        private readonly System.Windows.Forms.Timer _timer;

        private MainForm? _form;
        private long _formHiddenAt;
        private bool _exiting;
        private string _lastTooltip = string.Empty;

        // Weekly update check (Settings › Updates, off by default): first look a minute after the start
        private const int UpdateCheckEveryMs = 6 * 3600 * 1000;
        private long _nextUpdateCheckMs = Environment.TickCount64 + 60000;
        private bool _updateCheckRunning;

        public TrayAppContext(HdrEngine engine, EventWaitHandle showEvent, CommandEvents commands, CliCommand startCommand, bool startHidden,
                              bool justUpdated)
        {
            _engine = engine;
            _showEvent = showEvent;
            _commands = commands;

            _tray = new TrayAndStartupManager(ShowMain, SetPaused, Exit, SetOverride, GetOverride, () => Shortcut('O'),
                                              () => _engine.OverrideMode == HdrOverride.None && !_engine.Paused ? _engine.VideoMenuState : 0,
                                              _engine.SkipCurrentVideo, _engine.ResumeVideoHdr);

            ApplyHotkeys();

            // Re-read the HDR state right away when monitors change or the PC wakes up
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            _timer = new System.Windows.Forms.Timer { Interval = 500 };
            _timer.Tick += OnTick;
            _timer.Start();

            _engine.Start();
            if (TrayAndStartupManager.RefreshAutostartPath())
                _engine.Log("INFO", "\"开机自启动\"指向了旧位置 – 已更新为本程序。");
            if (Environment.ProcessPath is string exe &&
                VideoPlayers.RefreshContextMenu(exe) + VideoPlayers.RefreshAppRegistrations(exe) > 0)
                _engine.Log("INFO", "\"以 HDR 播放\"条目指向了旧位置 – 已更新为本程序。");
            if (startCommand != CliCommand.None) RunCommand(startCommand);

            // After "Check for updates › Update now": remove the old exe, say what happened
            Updater.CleanUp();
            if (justUpdated)
            {
                _engine.Log("UPDATE", $"已更新到版本 {Updater.CurrentVersionText}。");
                _tray.ShowBalloon("Smart HDR Manager 已更新", $"当前运行版本 {Updater.CurrentVersionText}。新功能说明：程序旁的 CHANGELOG.md。");
            }

            if (!startHidden) ShowMain();
        }

        /// <summary>
        /// Command-line actions (--force-hdr, --force-sdr, --auto, --toggle-pause), e.g. for Stream Deck buttons,
        /// desktop shortcuts or scripts. A second launch only signals the running instance and exits.
        /// </summary>
        private void RunCommand(CliCommand command)
        {
            _engine.Log("MANUAL", $"命令行操作：{command}。");
            switch (command)
            {
                case CliCommand.ForceHdr:
                    SetOverride(HdrOverride.ForceHdr, null);
                    break;
                case CliCommand.ForceSdr:
                    SetOverride(HdrOverride.ForceSdr, null);
                    break;
                case CliCommand.Auto:
                    SetOverride(HdrOverride.None, null);
                    break;
                case CliCommand.TogglePause:
                    SetPaused(!_engine.Paused);
                    break;
            }
        }

        // The settings window only exists while it is open. When hidden it is disposed after a few seconds,
        // which frees its memory and window handles while the app sits in the tray.
        private void ShowMain()
        {
            if (_exiting) return;
            if (_form == null || _form.IsDisposed)
            {
                _form = new MainForm(_engine, SetPaused, SetOverride, ApplyHotkeys, Exit);
                _form.HiddenToTray += OnFormHidden;
            }
            _form.ShowFromTray();
        }

        // ---- keyboard shortcuts (Win+Alt or Ctrl+Alt + H / I / O; can be changed or switched off in Settings) ----
        private string HotkeyMode => _engine.Read(c => HotkeySettings.Normalize(c.HotkeyModifier));

        private string Shortcut(char key) => HotkeySettings.Describe(HotkeyMode, key);

        /// <summary>(Re-)registers the shortcuts with the current setting. Falls back to polling for any that are taken.</summary>
        private void ApplyHotkeys()
        {
            _hotkeys?.Dispose();
            _hotkeys = null;

            string mode = HotkeyMode;
            if (mode == HotkeySettings.Off)
            {
                _engine.PollHotkeys = false;
                _engine.PollOverrideHotkey = false;
                _engine.Log("INFO", "键盘快捷键已关闭。");
                return;
            }

            try
            {
                _hotkeys = new GlobalHotkeys(HotkeySettings.ModifierFlags(mode), _engine.HotkeyAddCurrent, _engine.HotkeyIgnoreCurrent, OnOverrideHotkey);
                _engine.PollHotkeys = !_hotkeys.Registered;
                _engine.PollOverrideHotkey = !_hotkeys.OverrideRegistered;
                _engine.Log("INFO", $"快捷键：{Shortcut('H')} 对当前前台游戏使用 HDR，{Shortcut('I')} 从不使用 HDR " +
                                    $"（前台为浏览器 / 视频播放器时：此视频使用 SDR），{Shortcut('O')} HDR 覆盖。");
                if (!_hotkeys.Registered)
                    _engine.Log("INFO", $"{Shortcut('H')} / {Shortcut('I')} 正被其他程序占用 – 改用键盘轮询。");
                if (!_hotkeys.OverrideRegistered)
                    _engine.Log("INFO", $"{Shortcut('O')} 正被其他程序占用 – 改用键盘轮询。");
            }
            catch (Exception ex)
            {
                _engine.PollHotkeys = true;
                _engine.PollOverrideHotkey = true;
                _engine.Log("ERROR", $"无法注册快捷键（{ex.Message}）– 改用键盘轮询。");
            }
        }

        // ---- manual override (tray menu + shortcut) ----
        private void SetOverride(HdrOverride mode, TimeSpan? duration)
        {
            _engine.SetOverride(mode, duration);
            if (_form != null && !_form.IsDisposed && _form.Visible) _form.RefreshView();
        }

        private (HdrOverride Mode, bool Timed) GetOverride()
        {
            var s = _engine.Status;
            return (_engine.OverrideMode, s.OverrideRemainingMs >= 0);
        }

        private void OnOverrideHotkey()
        {
            var mode = _engine.CycleOverride();
            System.Media.SystemSounds.Asterisk.Play();
            _tray.ShowBalloon("HDR 覆盖", mode switch
            {
                HdrOverride.ForceHdr => $"已强制开启 HDR，直到您手动关闭（再次按 {Shortcut('O')}：SDR）。",
                HdrOverride.ForceSdr => $"已强制使用 SDR，直到您手动关闭（再次按 {Shortcut('O')}：自动）。",
                _ => "已恢复自动 HDR 切换。"
            });
            if (_form != null && !_form.IsDisposed && _form.Visible) _form.RefreshView();
        }

        // No notification when the window goes to the tray (2.6): the tray icon itself shows that the app still runs
        private void OnFormHidden(object? sender, EventArgs e) => _formHiddenAt = Environment.TickCount64;

        private void SetPaused(bool paused)
        {
            _engine.Paused = paused;
            _tray.SetPaused(paused);
            _engine.Log("PAUSE", paused ? "自动化已暂停 – 不再自动切换 HDR。" : "自动化已重新启用。");
            if (_form != null && !_form.IsDisposed && _form.Visible) _form.RefreshView();
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _engine.InvalidateHdrState();

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                _engine.Log("INFO", "从睡眠中恢复 – 正在重新检查 HDR 状态。");
                _engine.InvalidateHdrState(resumed: true);
            }
        }

        private void OnTick(object? sender, EventArgs e)
        {
            // A second launch asks the running instance to show its window
            if (_showEvent.WaitOne(0)) ShowMain();

            // ... or to run a command-line action (--force-hdr, --force-sdr, --auto, --toggle-pause)
            var command = _commands.TakeSignaled();
            if (command != CliCommand.None) RunCommand(command);

            // "Launch with HDR": a launcher process (--launch) asks to hold HDR while its game starts and runs.
            // When HDR must stay off right now, tell it so it starts the game without waiting.
            if (_commands.TakeLaunchHold())
            {
                var st = _engine.Status;
                if (_engine.Paused || _engine.OverrideMode == HdrOverride.ForceSdr || st.BlockedBy != null)
                    _commands.RefuseLaunch();
                else
                    _engine.HoldHdrForLaunch();
            }
            if (_commands.TakeLaunchRelease()) _engine.ReleaseLaunchHold();   // the game / player was closed

            // Notifications from the engine (crash hint)
            while (_engine.TryTakeNotification(out string noteTitle, out string noteText))
                _tray.ShowBalloon(noteTitle, noteText);

            // Weekly update check, only if turned on (Settings › Updates)
            if (Environment.TickCount64 >= _nextUpdateCheckMs)
            {
                _nextUpdateCheckMs = Environment.TickCount64 + UpdateCheckEveryMs;
                _ = AutoCheckForUpdateAsync();
            }

            // Only does anything if the app was built as a console exe (see ConsoleWindowHider)
            ConsoleWindowHider.HideIfMinimized();

            var s = _engine.Status;
            string state = s.Override == HdrOverride.ForceHdr ? "HDR 已强制开启"
                : s.Override == HdrOverride.ForceSdr ? "SDR 已强制"
                : s.Paused ? "已暂停" : s.HdrOn ? "HDR 开" : "HDR 关";
            string tooltip = $"Smart HDR Manager – {state}";
            if (tooltip != _lastTooltip)
            {
                _lastTooltip = tooltip;
                _tray.UpdateTooltip(tooltip);
            }
            _tray.UpdateState(s.HdrOn, s.Paused && s.Override == HdrOverride.None);

            if (_form == null) return;

            if (_form.IsDisposed)
            {
                _form = null;
            }
            else if (_form.Visible)
            {
                _form.RefreshView();
            }
            else if (Environment.TickCount64 - _formHiddenAt > DisposeHiddenWindowAfterMs)
            {
                var form = _form;
                _form = null;
                form.HiddenToTray -= OnFormHidden;
                form.AllowExit();
                form.Dispose();
            }
        }

        /// <summary>
        /// "Check for updates automatically": at most once a week, only a notification – nothing is downloaded or
        /// installed without you (Settings › Updates › Check for updates). Each new version is mentioned once.
        /// </summary>
        private async Task AutoCheckForUpdateAsync()
        {
            if (_updateCheckRunning || _exiting) return;
            bool enabled = _engine.Read(c => c.CheckForUpdatesAutomatically);
            DateTime? last = _engine.Read(c => c.LastUpdateCheckUtc);
            if (!enabled || (last != null && DateTime.UtcNow - last.Value < TimeSpan.FromDays(7))) return;

            _updateCheckRunning = true;
            try
            {
                var info = await Updater.CheckAsync(CancellationToken.None);
                _engine.Update(c => c.LastUpdateCheckUtc = DateTime.UtcNow);
                if (!info.IsNewer)
                {
                    _engine.Log("UPDATE", $"定期检查：{Updater.CurrentVersionText} 已是最新版本。");
                    return;
                }
                bool alreadyMentioned = string.Equals(_engine.Read(c => c.LastNotifiedVersion), info.VersionText, StringComparison.OrdinalIgnoreCase);
                _engine.Log("UPDATE", $"定期检查：有新版本 {info.VersionText}（设置 › 更新 › 检查更新）。");
                if (alreadyMentioned || _exiting) return;
                _engine.Update(c => c.LastNotifiedVersion = info.VersionText);
                _tray.ShowBalloon($"Smart HDR Manager {info.VersionText} 可用",
                    "打开程序 › 设置 › 更新 › 检查更新即可安装。");
            }
            catch (Exception ex)
            {
                _engine.Log("UPDATE", $"定期更新检查失败：{ex.Message}");
            }
            finally
            {
                _updateCheckRunning = false;
            }
        }

        private void Exit()
        {
            if (_exiting) return;
            _exiting = true;

            _timer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _hotkeys?.Dispose();
            _engine.Stop();

            if (_form != null && !_form.IsDisposed)
            {
                _form.AllowExit();
                _form.Close();
                _form.Dispose();
            }
            _form = null;

            _tray.Dispose();
            ExitThread();
        }
    }

    internal enum CliCommand { None, ForceHdr, ForceSdr, Auto, TogglePause }

    /// <summary>Named events through which a second launch passes a command-line action to the running tray instance.</summary>
    internal sealed class CommandEvents : IDisposable
    {
        private readonly (CliCommand Command, EventWaitHandle Event)[] _events =
        {
            (CliCommand.ForceHdr, new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SmartHdrManager.ForceHdr")),
            (CliCommand.ForceSdr, new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SmartHdrManager.ForceSdr")),
            (CliCommand.Auto, new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SmartHdrManager.AutoHdr")),
            (CliCommand.TogglePause, new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SmartHdrManager.TogglePause"))
        };

        // "Launch with HDR" (see HdrLauncher): only "hold HDR" / "not now" signals, never a path or command
        private readonly EventWaitHandle _launchHold = new(false, EventResetMode.AutoReset, HdrLauncher.HoldEventName);
        private readonly EventWaitHandle _launchRefused = new(false, EventResetMode.AutoReset, HdrLauncher.RefusedEventName);

        private readonly EventWaitHandle _launchRelease = new(false, EventResetMode.AutoReset, HdrLauncher.ReleaseEventName);

        public bool TakeLaunchHold() => _launchHold.WaitOne(0);

        public bool TakeLaunchRelease() => _launchRelease.WaitOne(0);

        public void RefuseLaunch() => _launchRefused.Set();

        public void Signal(CliCommand command)
        {
            foreach (var (c, e) in _events)
                if (c == command) e.Set();
        }

        /// <summary>Returns one pending command (and consumes it), or None. Called twice a second by the tray timer.</summary>
        public CliCommand TakeSignaled()
        {
            foreach (var (c, e) in _events)
                if (e.WaitOne(0)) return c;
            return CliCommand.None;
        }

        public static CliCommand Parse(string[] args)
        {
            foreach (var a in args)
            {
                switch (a.Trim().ToLowerInvariant())
                {
                    case "--force-hdr": return CliCommand.ForceHdr;
                    case "--force-sdr": return CliCommand.ForceSdr;
                    case "--auto": return CliCommand.Auto;
                    case "--toggle-pause": return CliCommand.TogglePause;
                }
            }
            return CliCommand.None;
        }

        public void Dispose()
        {
            foreach (var (_, e) in _events) e.Dispose();
            _launchHold.Dispose();
            _launchRefused.Dispose();
            _launchRelease.Dispose();
        }
    }

    internal static class Program
    {
        // Own taskbar identity. Without it Windows groups the app by its exe path and may take the icon of ANY
        // shortcut that points to SmartHdrManager.exe – e.g. a "Launch with HDR" desktop shortcut that shows the
        // game's icon – for the app's taskbar button.
        private const string AppUserModelId = "VantaCode420.SmartHdrManager";
        private const string LauncherUserModelId = "VantaCode420.SmartHdrManager.Launcher";

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        private static void SetTaskbarIdentity(string id)
        {
            try { SetCurrentProcessExplicitAppUserModelID(id); } catch { }   // cosmetic only
        }

        /// <summary>
        /// Windows shows the app identity as the sender of notifications. Without a registered name that is the raw
        /// id ("VantaCode420.SmartHdrManager"); this gives it the name "Smart HDR Manager" and the app icon.
        /// Per user (HKCU), no administrator rights. Removed by hand only (see README › Uninstall).
        /// </summary>
        private static void RegisterNotificationName()
        {
            try
            {
                string iconPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(HdrEngine.LogPath)!, "app.ico");
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(iconPath)!);
                    if (!System.IO.File.Exists(iconPath))
                    {
                        using var icon = AppIcons.Create(IconStyle.Vivid, 64);
                        using var file = System.IO.File.Create(iconPath);
                        icon.Save(file);
                    }
                }
                catch { iconPath = string.Empty; }

                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
                key.SetValue("DisplayName", "Smart HDR Manager");
                if (iconPath.Length > 0) key.SetValue("IconUri", iconPath);
            }
            catch { }   // cosmetic only
        }

        [STAThread]
        private static void Main(string[] args)
        {
            // "Launch with HDR": SmartHdrManager.exe --launch <game> [arguments] – starts a game with HDR already on.
            // Runs as its own small process (no window, no tray icon) and exits when the game has closed.
            int launchIndex = Array.FindIndex(args, a => a.Equals("--launch", StringComparison.OrdinalIgnoreCase) ||
                                                         a.Equals("--launch-video", StringComparison.OrdinalIgnoreCase));
            if (launchIndex >= 0)
            {
                SetTaskbarIdentity(LauncherUserModelId);
                Application.EnableVisualStyles();
                bool videoPlayer = args[launchIndex].Equals("--launch-video", StringComparison.OrdinalIgnoreCase);
                Environment.ExitCode = HdrLauncher.Run(args, launchIndex, videoPlayer);
                return;
            }

            SetTaskbarIdentity(AppUserModelId);
            var command = CommandEvents.Parse(args);
            // A command-line action never opens the window: the app (re)starts quietly in the tray
            bool startHidden = command != CliCommand.None || args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SmartHdrManager.ShowWindow");
            using var commands = new CommandEvents();
            using var mutex = new Mutex(true, @"Local\SmartHdrManager.SingleInstance", out bool isFirstInstance);
            // Started by "Check for updates › Update now": the old version is still closing – wait for it (up to 30 s)
            bool justUpdated = args.Any(a => a.Equals("--updated", StringComparison.OrdinalIgnoreCase));
            if (!isFirstInstance && justUpdated)
            {
                try { isFirstInstance = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { isFirstInstance = true; }   // the old version has exited: the mutex is ours
            }
            if (!isFirstInstance)
            {
                if (command != CliCommand.None) commands.Signal(command);   // the running instance applies it
                else if (!startHidden) showEvent.Set();                    // bring the running instance's window to the front
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);   // physical pixels: needed for the fullscreen check
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Only after the DPI mode is set: it draws the app icon, and the first drawing call fixes the process DPI mode
            RegisterNotificationName();

            // Errors must never pop up dialogs or vanish silently while the app lives in the tray: write them to the log file.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => HdrEngine.WriteFileLog($"ERROR  界面异常：{e.Exception}");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => HdrEngine.WriteFileLog($"FATAL  {e.ExceptionObject}");

            // Built as console exe? Take the console out of the taskbar/Alt-Tab (no-op for WinExe).
            if (ConsoleWindowHider.HasConsole)
                ConsoleWindowHider.HideCompletely();

            var engine = new HdrEngine();
            using var context = new TrayAppContext(engine, showEvent, commands, command, startHidden, justUpdated);
            Application.Run(context);
        }
    }
}
