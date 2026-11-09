using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SmartHdrManager
{
    /// <summary>
    /// Owns the tray icon, the background engine and (only while it is open) the settings window.
    /// There is no ApplicationContext.MainForm, so closing the window never ends the process; only "Exit" does.
    /// </summary>
    internal sealed class TrayAppContext : ApplicationContext
    {
        private const int DisposeHiddenWindowAfterMs = 5000;

        private readonly HdrEngine _engine;
        private readonly EventWaitHandle _showEvent;
        private readonly CommandEvents _commands;
        private readonly TrayAndStartupManager _tray;
        private readonly System.Windows.Forms.Timer _timer;

        private MainForm? _form;
        private long _formHiddenAt;
        private bool _exiting;
        private string _lastTooltip = string.Empty;


        public TrayAppContext(HdrEngine engine, EventWaitHandle showEvent, CommandEvents commands, CliCommand startCommand, bool startHidden,
                              bool justUpdated)
        {
            _engine = engine;
            _showEvent = showEvent;
            _commands = commands;

            _tray = new TrayAndStartupManager(ShowMain, Exit);

            // Re-read the HDR state right away when monitors change or the PC wakes up
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            _timer = new System.Windows.Forms.Timer { Interval = 500 };
            _timer.Tick += OnTick;
            _timer.Start();

            _engine.Start();
            if (TrayAndStartupManager.RefreshAutostartPath())
                _engine.Log("INFO", "\"开机自启动\"指向了旧位置 – 已更新为本程序。");
            if (startCommand != CliCommand.None) RunCommand(startCommand);

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
                _form = new MainForm(_engine, SetPaused, Exit);
                _form.HiddenToTray += OnFormHidden;
            }
            _form.ShowFromTray();
        }

        // No notification when the window goes to the tray (2.6): the tray icon itself shows that the app still runs
        private void OnFormHidden(object? sender, EventArgs e) => _formHiddenAt = Environment.TickCount64;

        private void SetPaused(bool paused)
        {
            _engine.Paused = paused;
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
                if (_engine.Paused || st.BlockedBy)
                    _commands.RefuseLaunch();
                else
                    _engine.HoldHdrForLaunch();
            }
            if (_commands.TakeLaunchRelease()) _engine.ReleaseLaunchHold();   // the game / player was closed

            // Notifications from the engine (crash hint)
            while (_engine.TryTakeNotification(out string noteTitle, out string noteText))
                _tray.ShowBalloon(noteTitle, noteText);
            // Only does anything if the app was built as a console exe (see ConsoleWindowHider)
            ConsoleWindowHider.HideIfMinimized();

            var s = _engine.Status;
            string state = s.Paused ? "已暂停" : s.HdrOn ? "HDR 开" : "HDR 关";
            string tooltip = $"Smart HDR Manager – {state}";
            if (tooltip != _lastTooltip)
            {
                _lastTooltip = tooltip;
                _tray.UpdateTooltip(tooltip);
            }
            _tray.UpdateState(s.HdrOn, s.Paused);

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

        private void Exit()
        {
            if (_exiting) return;
            _exiting = true;

            _timer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
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

    internal enum CliCommand { None, TogglePause }

    /// <summary>Named events through which a second launch passes a command-line action to the running tray instance.</summary>
    internal sealed class CommandEvents : IDisposable
    {
        private readonly (CliCommand Command, EventWaitHandle Event)[] _events =
        {
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
            int launchIndex = Array.FindIndex(args, a => a.Equals("--launch", StringComparison.OrdinalIgnoreCase));
            if (launchIndex >= 0)
            {
                SetTaskbarIdentity(LauncherUserModelId);
                Application.EnableVisualStyles();
                Environment.ExitCode = HdrLauncher.Run(args, launchIndex, false);
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

