using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// The settings window: a segmented tab bar and four pages (games, test, settings, log).
    /// Layout values are in 96-DPI pixels; WinForms scales them for the actual display DPI.
    /// Closing or minimizing hides it to the tray; the tray context disposes it a few seconds later to free memory.
    /// </summary>
    public sealed partial class MainForm : Form
    {
        private const int CardWidth = 680;
        private const int MaxContentWidth = 900;
        private const int PageEdge = 20;
        private const int TabBarHeight = 56;
        private const int WindowWidth = 720;
        private const int WindowHeight = 660;
        private const int WindowMinWidth = 660;
        private const int WindowMinHeight = 560;
        private const int LogTrimThreshold = 200_000;
        private const int LogKeepLength = 100_000;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;

        private readonly HdrEngine _engine;
        private readonly Action<bool> _setPaused;
        private readonly Icon _windowIcon = AppIcons.Create(IconStyle.Vivid, 48);
        private readonly ToolTip _toolTip = new();

        private readonly SegmentedTabs _tabs = new()
        {
            Size = new Size(330, 40),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Margin = new Padding(0, 16, 0, 0)
        };
        private readonly Panel _content = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        private readonly List<Control> _pages = new();
        private TableLayoutPanel? _root;

        private readonly TextBox _searchHdr = new() { BorderStyle = BorderStyle.None, PlaceholderText = "搜索游戏…" };
        private readonly TextBox _searchSdr = new() { BorderStyle = BorderStyle.None, PlaceholderText = "搜索游戏…" };
        private readonly GameList _gamesHdr = new() { Dock = DockStyle.Fill };
        private readonly GameList _gamesSdr = new() { Dock = DockStyle.Fill };

        private readonly ToggleSwitch _swAutostart = new();
        private readonly ToggleSwitch _swHdrControl = new();
        private readonly ToggleSwitch _swBrightnessControl = new();
        private readonly NumberStepper _desktopBrightness = new() { Minimum = 0, Maximum = 100, Step = 10 };
        private Label _hdrStatusLabel = null!;
        private Label _activityLabel = null!;

        private readonly TextBox _log = new()
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            WordWrap = false,
            BorderStyle = BorderStyle.None
        };

        private bool _loadingSettings;
        private bool _reallyExit;
        private int _logCursor;

        public event EventHandler? HiddenToTray;

        public MainForm(HdrEngine engine, Action<bool> setPaused)
        {
            _engine = engine;
            _setPaused = setPaused;

            SuspendLayout();

            Text = "HDR";
            Icon = _windowIcon;
            Font = Theme.Body;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterScreen;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(PageEdge) };
            _root = root;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, TabBarHeight));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _tabs.AddTab("HDR");
            _tabs.AddTab("SDR");
            _tabs.AddTab("测试");
            _tabs.AddTab("设置");
            _tabs.AddTab("日志");
            root.Controls.Add(_tabs, 0, 0);

            AddPage(BuildGamesPage(sdr: false));
            AddPage(BuildGamesPage(sdr: true));
            AddPage(BuildTestPage());
            AddPage(BuildSettingsPage());
            AddPage(BuildLogPage());
            root.Controls.Add(_content, 0, 1);

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(WindowWidth, WindowHeight);
            MinimumSize = new Size(WindowMinWidth, WindowMinHeight);
            Controls.Add(root);

            ResumeLayout(false);
            PerformLayout();

            UpdateContentWidth();
            ShowPage(0);
            WireEvents();
            LoadSettings();
            RebuildAllGames();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.StyleWindow(Handle);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;

            _toolTip.Dispose();
            _windowIcon.Dispose();
        }

        private void AddPage(Control page)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _pages.Add(page);
            _content.Controls.Add(page);
        }

        private void ShowPage(int index)
        {
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].Visible = i == index;
        }

        /// <summary>Centres the content in a column of at most <see cref="MaxContentWidth"/> (wide / maximized windows).</summary>
        private void UpdateContentWidth()
        {
            if (_root == null) return;

            int edge = LogicalToDeviceUnits(PageEdge);
            int side = Math.Max(edge, (ClientSize.Width - LogicalToDeviceUnits(MaxContentWidth)) / 2);
            var padding = new Padding(side, edge, side, edge);
            if (_root.Padding != padding) _root.Padding = padding;
        }

        private void WireEvents()
        {
            _tabs.SelectedIndexChanged += (_, _) => ShowPage(_tabs.SelectedIndex);

            _searchHdr.TextChanged += (_, _) => RebuildGames(sdr: false);
            _searchSdr.TextChanged += (_, _) => RebuildGames(sdr: true);

            _gamesHdr.BrightnessToggled += (_, item) => OnGameBrightnessChanged(item, sdr: false);
            _gamesSdr.BrightnessToggled += (_, item) => OnGameBrightnessChanged(item, sdr: true);

            _swAutostart.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                if (TrayAndStartupManager.SetAutostart(_swAutostart.Checked)) return;

                MessageBox.Show(this, "无法更新 Windows 启动项。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ApplySwitch(_swAutostart, TrayAndStartupManager.IsAutostartEnabled());
            };

            _swHdrControl.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                _setPaused(!_swHdrControl.Checked);
            };

            _desktopBrightness.ValueChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                OnDesktopBrightnessChanged();
            };

            _swBrightnessControl.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                OnBrightnessControlChanged(_swBrightnessControl.Checked);
            };

            Resize += (_, _) =>
            {
                if (WindowState == FormWindowState.Minimized)
                    BeginInvoke(new Action(HideToTray));
                else
                    UpdateContentWidth();
            };

            FormClosing += (_, e) =>
            {
                if (_reallyExit || e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;
                HideToTray();
            };
        }

        /// <summary>Sets a switch without running its handler (the value comes from the engine).</summary>
        private void ApplySwitch(ToggleSwitch toggle, bool value)
        {
            _loadingSettings = true;
            try { toggle.Checked = value; }
            finally { _loadingSettings = false; }
        }

        public void ShowFromTray()
        {
            if (!Visible)
            {
                LoadSettings();
                RebuildAllGames();
                Show();
            }

            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true;
            TopMost = false;
            Activate();
            RefreshView();
        }

        public void HideToTray()
        {
            if (IsDisposed) return;

            Hide();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// The title bar's minimize button hides the window straight to the tray – no minimize animation to a taskbar
        /// button that disappears right after. (Win+D / Win+M still minimize; Resize then hides the window.)
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam.ToInt64() & 0xFFF0) == SC_MINIMIZE)
            {
                HideToTray();
                return;
            }
            base.WndProc(ref m);
        }

        public void AllowExit() => _reallyExit = true;

        /// <summary>Called on the UI thread while the window is visible.</summary>
        public void RefreshView()
        {
            var status = _engine.Status;

            UpdateStatusLabel(status);
            UpdateActivityLabel(status);
            AppendNewLogLines();

            if (_swHdrControl.Checked != !status.Paused) ApplySwitch(_swHdrControl, !status.Paused);
        }

        private void UpdateStatusLabel(EngineStatus status)
        {
            string text = status.Paused
                ? (status.HdrOn ? "HDR 开 · 已暂停" : "自动化已暂停")
                : status.HdrOn ? "HDR 已开启" : "HDR 已关闭";
            Color color = status.Paused ? Theme.Muted : status.HdrOn ? Theme.Accent : Theme.Text;

            if (_hdrStatusLabel.Text != text) _hdrStatusLabel.Text = text;
            if (_hdrStatusLabel.ForeColor != color) _hdrStatusLabel.ForeColor = color;
        }

        private void UpdateActivityLabel(EngineStatus status)
        {
            string activity = DescribeActivity(status);
            if (_activityLabel.Text != activity) _activityLabel.Text = activity;
        }

        private static string DescribeActivity(EngineStatus status)
        {
            if (status.Paused) return "在恢复前不会自动切换 HDR。";

            if (status.RunningGame != null) return $"HDR 游戏运行中：{status.RunningGame}";

            if (status.RunningSdrApp != null) return $"SDR 游戏运行中：{status.RunningSdrApp}（HDR 关闭）";

            if (status.FollowingLauncher != null) return $"{status.FollowingLauncher} 已关闭 · 等待其启动的游戏 …";

            if (string.IsNullOrEmpty(status.ActiveProcess)) return "等待中 …";

            string window = $"活动窗口：{status.ActiveProcess}{(status.IsFullscreen ? "（全屏）" : "")}";
            if (status.ForegroundEntry != null &&
                !status.ForegroundEntry.Equals(status.ActiveProcess, StringComparison.OrdinalIgnoreCase))
                window += $" → 列表条目 {status.ForegroundEntry}";

            return window;
        }

        private void AppendNewLogLines()
        {
            var lines = _engine.ReadLog(ref _logCursor);
            if (lines.Length == 0) return;

            _log.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
            if (_log.TextLength > LogTrimThreshold)
                _log.Text = _log.Text[^LogKeepLength..];
        }

        private void LoadSettings()
        {
            _loadingSettings = true;
            try
            {
                _swHdrControl.Checked = !_engine.Paused;
                _swAutostart.Checked = TrayAndStartupManager.IsAutostartEnabled();
                _swBrightnessControl.Checked = IsBrightnessControlEnabled();
                _desktopBrightness.Value = _engine.Read(config => config.DesktopBrightness);
            }
            finally { _loadingSettings = false; }
        }
    }
}
