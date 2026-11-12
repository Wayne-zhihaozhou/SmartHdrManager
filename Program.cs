using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HdrBrightControl
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

        public TrayAppContext(HdrEngine engine, EventWaitHandle showEvent, CommandEvents commands, CliOptions opts)
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
            if (opts.Command != CliCommand.None) RunCommand(opts.Command);

            if (!opts.StartHidden) ShowMain();
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
            string tooltip = $"HDR亮控 – {state}";
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
        private const string TogglePauseEventName = @"Local\HdrBrightControl.TogglePause";

        private readonly EventWaitHandle _togglePause = new(false, EventResetMode.AutoReset, TogglePauseEventName);

        public void Signal(CliCommand command)
        {
            if (command == CliCommand.TogglePause) _togglePause.Set();
        }

        /// <summary>Returns one pending command (and consumes it), or None.</summary>
        public CliCommand TakeSignaled() => _togglePause.WaitOne(0) ? CliCommand.TogglePause : CliCommand.None;

        public void Dispose()
        {
            _togglePause.Dispose();
        }
    }

    internal static class Program
    {
        private const string AppUserModelId = "VantaCode420.HdrBrightControl";
        private const string NotificationIconFileName = "app.ico";
        private const int NotificationIconSize = 64;
        private const int UpdateMutexWaitSeconds = 30;

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        private static void SetTaskbarIdentity(string id)
        {
            try { SetCurrentProcessExplicitAppUserModelID(id); } catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            SetTaskbarIdentity(AppUserModelId);
            var opts = CliOptions.Parse(args);
            bool startHidden = opts.StartHidden;

            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HdrBrightControl.ShowWindow");
            using var commands = new CommandEvents();
            using var mutex = new Mutex(true, @"Local\HdrBrightControl.SingleInstance", out bool isFirstInstance);

            if (!isFirstInstance && opts.JustUpdated)
            {
                try { isFirstInstance = mutex.WaitOne(TimeSpan.FromSeconds(UpdateMutexWaitSeconds)); }
                catch (AbandonedMutexException) { isFirstInstance = true; }
            }

            if (!isFirstInstance)
            {
                if (opts.Command != CliCommand.None) commands.Signal(opts.Command);
                else if (!startHidden) showEvent.Set();
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            var engine = new HdrEngine();
            TvCli.Initialize(engine);
            TvClientKeyStore.Initialize(engine);
            TrayAndStartupManager.Initialize(engine);

            Application.ThreadException += (_, e) => engine.WriteLine($"ERROR  界面异常：{e.Exception}");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => engine.WriteLine($"FATAL  {e.ExceptionObject}");

            using var context = new TrayAppContext(engine, showEvent, commands, opts);
            Application.Run(context);
        }
    }
}
