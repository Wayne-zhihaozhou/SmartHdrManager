using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SmartHdrManager
{
    /// <summary>
    /// Owns the tray icon, the background engine and (only while it is open) the settings window.
    /// There is no MainForm in the application context, so closing the window never ends the process; only "Exit" does.
    /// </summary>
    internal sealed class TrayAppContext : ApplicationContext
    {
        private const int DisposeHiddenWindowAfterMs = 5000;
        private const int TrayTimerIntervalMs = 500;

        private readonly HdrEngine _engine;
        private readonly EventWaitHandle _showEvent;
        private readonly CommandEvents _commands;
        private readonly TrayAndStartupManager _tray;
        private readonly System.Windows.Forms.Timer _timer;

        private MainForm? _form;
        private long _formHiddenAt;
        private bool _exiting;
        private string _lastTooltip = string.Empty;

        public TrayAppContext(HdrEngine engine, EventWaitHandle showEvent, CommandEvents commands, CliCommand startCommand,
                              bool startHidden)
        {
            _engine = engine;
            _showEvent = showEvent;
            _commands = commands;

            _tray = new TrayAndStartupManager(ShowMain, Exit);

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            _timer = new System.Windows.Forms.Timer { Interval = TrayTimerIntervalMs };
            _timer.Tick += OnTick;
            _timer.Start();

            _engine.Start();
            if (TrayAndStartupManager.RefreshAutostartPath())
                _engine.Log("INFO", "\"开机自启动\"指向了旧位置 – 已更新为本程序。");
            if (startCommand != CliCommand.None) RunCommand(startCommand);

            if (!startHidden) ShowMain();
        }

        /// <summary>Command-line action of this launch (a second launch only signals the running instance).</summary>
        private void RunCommand(CliCommand command)
        {
            if (command != CliCommand.TogglePause) return;

            _engine.Log("MANUAL", "命令行操作：切换 HDR 控制开关。");
            SetPaused(!_engine.Paused);
        }

        private void ShowMain()
        {
            if (_exiting) return;

            if (_form == null || _form.IsDisposed)
            {
                _form = new MainForm(_engine, SetPaused);
                _form.HiddenToTray += OnFormHidden;
            }
            _form.ShowFromTray();
        }

        private void OnFormHidden(object? sender, EventArgs e) => _formHiddenAt = Environment.TickCount64;

        private void SetPaused(bool paused)
        {
            _engine.Paused = paused;
            _engine.Log("PAUSE", paused ? "自动化已暂停 – 不再自动切换 HDR。" : "自动化已重新启用。");
            if (_form is { IsDisposed: false, Visible: true }) _form.RefreshView();
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _engine.InvalidateHdrState();

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;

            _engine.Log("INFO", "从睡眠中恢复 – 正在重新检查 HDR 状态。");
            _engine.InvalidateHdrState(resumed: true);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_showEvent.WaitOne(0)) ShowMain();

            var command = _commands.TakeSignaled();
            if (command != CliCommand.None) RunCommand(command);

            // "Launch with HDR": a launcher asks to hold HDR while its game starts. When HDR must stay off right now,
            // tell it so that it starts the game without waiting.
            if (_commands.TakeLaunchHold())
            {
                if (_engine.Paused) _commands.RefuseLaunch();
                else _engine.HoldHdrForLaunch();
            }
            if (_commands.TakeLaunchRelease()) _engine.ReleaseLaunchHold();   // the game / player was closed

            while (_engine.TryTakeNotification(out string noteTitle, out string noteText))
                _tray.ShowBalloon(noteTitle, noteText);

            UpdateTrayStatus();

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

        private void UpdateTrayStatus()
        {
            var status = _engine.Status;
            string state = status.Paused ? "已暂停" : status.HdrOn ? "HDR 开" : "HDR 关";
            string tooltip = $"Smart HDR Manager – {state}";
            if (tooltip != _lastTooltip)
            {
                _lastTooltip = tooltip;
                _tray.UpdateTooltip(tooltip);
            }
            _tray.UpdateState(status.HdrOn, status.Paused);
        }

        private void Exit()
        {
            if (_exiting) return;
            _exiting = true;

            _timer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _engine.Stop();

            if (_form is { IsDisposed: false })
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

    internal enum CliCommand { None, TogglePause }

    /// <summary>Named events through which a second launch passes a command-line action to the running tray instance.</summary>
    internal sealed class CommandEvents : IDisposable
    {
        private const string TogglePauseEventName = @"Local\SmartHdrManager.TogglePause";

        private readonly EventWaitHandle _togglePause = new(false, EventResetMode.AutoReset, TogglePauseEventName);

        // "Launch with HDR" (see HdrLauncher): only "hold HDR" / "not now" signals, never a path or command
        private readonly EventWaitHandle _launchHold = new(false, EventResetMode.AutoReset, HdrLauncher.HoldEventName);
        private readonly EventWaitHandle _launchRefused = new(false, EventResetMode.AutoReset, HdrLauncher.RefusedEventName);
        private readonly EventWaitHandle _launchRelease = new(false, EventResetMode.AutoReset, HdrLauncher.ReleaseEventName);

        public bool TakeLaunchHold() => _launchHold.WaitOne(0);

        public bool TakeLaunchRelease() => _launchRelease.WaitOne(0);

        public void RefuseLaunch() => _launchRefused.Set();

        public void Signal(CliCommand command)
        {
            if (command == CliCommand.TogglePause) _togglePause.Set();
        }

        /// <summary>Returns one pending command (and consumes it), or None.</summary>
        public CliCommand TakeSignaled() => _togglePause.WaitOne(0) ? CliCommand.TogglePause : CliCommand.None;

        public static CliCommand Parse(string[] args)
        {
            foreach (string arg in args)
                if (arg.Trim().Equals("--toggle-pause", StringComparison.OrdinalIgnoreCase)) return CliCommand.TogglePause;
            return CliCommand.None;
        }

        public void Dispose()
        {
            _togglePause.Dispose();
            _launchHold.Dispose();
            _launchRefused.Dispose();
            _launchRelease.Dispose();
        }
    }

    internal static class Program
    {
        // Own taskbar identity: without it Windows groups the app by its exe path and may take the icon of ANY shortcut
        // that points to SmartHdrManager.exe for the app's taskbar button.
        private const string AppUserModelId = "VantaCode420.SmartHdrManager";
        private const string LauncherUserModelId = "VantaCode420.SmartHdrManager.Launcher";
        private const string NotificationIconFileName = "app.ico";
        private const int NotificationIconSize = 64;
        private const int UpdateMutexWaitSeconds = 30;

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        private static void SetTaskbarIdentity(string id)
        {
            try { SetCurrentProcessExplicitAppUserModelID(id); } catch { }   // cosmetic only
        }

        /// <summary>
        /// Windows shows the app identity as the sender of notifications. Without a registered name that is the raw id;
        /// this gives it the name "Smart HDR Manager" and the app icon. Per user (HKCU), no administrator rights.
        /// </summary>
        private static void RegisterNotificationName()
        {
            try
            {
                string iconPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(HdrEngine.LogPath)!,
                                                          NotificationIconFileName);
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(iconPath)!);
                    if (!System.IO.File.Exists(iconPath))
                    {
                        using var icon = AppIcons.Create(IconStyle.Vivid, NotificationIconSize);
                        using var stream = System.IO.File.Create(iconPath);
                        icon.Save(stream);
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
            int launchIndex = Array.FindIndex(args, arg => arg.Equals("--launch", StringComparison.OrdinalIgnoreCase));
            if (launchIndex >= 0)
            {
                SetTaskbarIdentity(LauncherUserModelId);
                Application.EnableVisualStyles();
                Environment.ExitCode = HdrLauncher.Run(args, launchIndex);
                return;
            }

            SetTaskbarIdentity(AppUserModelId);
            var command = CommandEvents.Parse(args);
            bool startHidden = command != CliCommand.None ||
                               args.Any(arg => arg.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SmartHdrManager.ShowWindow");
            using var commands = new CommandEvents();
            using var mutex = new Mutex(true, @"Local\SmartHdrManager.SingleInstance", out bool isFirstInstance);

            // Started by an update: the old version is still closing – wait for it
            bool justUpdated = args.Any(arg => arg.Equals("--updated", StringComparison.OrdinalIgnoreCase));
            if (!isFirstInstance && justUpdated)
            {
                try { isFirstInstance = mutex.WaitOne(TimeSpan.FromSeconds(UpdateMutexWaitSeconds)); }
                catch (AbandonedMutexException) { isFirstInstance = true; }   // the old version has exited: it is ours
            }

            if (!isFirstInstance)
            {
                if (command != CliCommand.None) commands.Signal(command);
                else if (!startHidden) showEvent.Set();
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            RegisterNotificationName();

            // Errors must never pop up dialogs or vanish silently while the app lives in the tray: write them to the log.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => HdrEngine.WriteFileLog($"ERROR  界面异常：{e.Exception}");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => HdrEngine.WriteFileLog($"FATAL  {e.ExceptionObject}");

            var engine = new HdrEngine();
            using var context = new TrayAppContext(engine, showEvent, commands, command, startHidden);
            Application.Run(context);
        }
    }
}
