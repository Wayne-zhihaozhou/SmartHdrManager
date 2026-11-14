using System;
using System.IO;
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
        private readonly TvBrightnessService _tvService;
        private readonly ITrayService _tray;
        private readonly IStartupManager _startup;
        private readonly System.Windows.Forms.Timer _timer;

        private MainForm? _form;
        private long _formHiddenAt;
        private bool _exiting;

        public ITrayService TrayService => _tray;

        public IStartupManager StartupManager => _startup;

        public TrayAppContext(HdrEngine engine, TvBrightnessService tvService, IStartupManager startup)
        {
            _engine = engine;
            _tvService = tvService;
            _startup = startup;

            _tray = new TrayService(ShowMain, Exit);

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            _timer = new System.Windows.Forms.Timer { Interval = TrayTimerIntervalMs };
            _timer.Tick += OnTick;
            _timer.Start();

            _engine.Start();
            if (_startup.RefreshPath())
                _engine.Log("INFO", "\"开机自启动\"指向了旧位置 – 已更新为本程序。");

            ShowMain();
        }

        private void ShowMain()
        {
            if (_exiting) return;

            if (_form == null || _form.IsDisposed)
            {
                _form = new MainForm(_engine, SetPaused, _startup);
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

    internal static class Program
    {
        private const string AppUserModelId = "Wayne-zhihaozhou.HdrBrightControl";
        private const string NotificationIconFileName = "app.ico";
        private const int NotificationIconSize = 64;

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

            using var mutex = new Mutex(true, @"Local\HdrBrightControl.SingleInstance", out bool isFirstInstance);

            if (!isFirstInstance) return;

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            var engine = new HdrEngine();
            var keyStore = new DiskTvClientKeyStore(Path.GetDirectoryName(HdrEngine.ConfigPath) ?? AppContext.BaseDirectory);
            var tvService = new TvBrightnessService(engine, keyStore);
            TvCli.Initialize(tvService);
            TvClientKeyStore.Initialize(engine);

            var startupManager = new RegistryStartupManager();

            Application.ThreadException += (_, e) => engine.WriteLine($"ERROR  界面异常：{e.Exception}");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => engine.WriteLine($"FATAL  {e.ExceptionObject}");

            var context = new TrayAppContext(engine, tvService, startupManager);
            var trayService = context.TrayService;
            startupManager.RefreshPath();

            using (context)
            {
                Application.Run(context);
            }
        }
    }
}
