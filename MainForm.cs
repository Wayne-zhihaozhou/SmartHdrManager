using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// The settings window: a segmented tab bar, and four pages (Games, Test, Settings, Log).
    /// Layout values are in 96-DPI pixels; WinForms scales them for the actual display DPI.
    /// Closing or minimizing hides it to the tray; the tray context disposes it a few seconds later to free memory.
    /// </summary>
    public sealed class MainForm : Form
    {
        private const int CardWidth = 680;

        private readonly HdrEngine _engine;
        private readonly Action<bool> _setPaused;
        private readonly Action _exitApp;
        private readonly Icon _windowIcon = AppIcons.Create(IconStyle.Vivid, 48);
        private readonly ToolTip _toolTip = new();

        // Settings page
        // Automatic drive scan: off / at every start / daily / weekly / monthly (2.6)
        // Tabs
        private readonly SegmentedTabs _tabs = new() { Size = new Size(330, 40), Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0, 16, 0, 0) };
        private readonly Panel _content = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };

        // Content column: never wider than this (96-DPI pixels). A maximized window on a 4K screen would otherwise
        // stretch every card to the full screen width – harder to read, and much more to repaint while scrolling.
        private const int MaxContentWidth = 900;
        private TableLayoutPanel? _root;
        private readonly List<Control> _pages = new();

        // Games page
        private readonly TextBox _search = new() { BorderStyle = BorderStyle.None, PlaceholderText = "搜索游戏…" };
        private readonly GameList _games = new() { Dock = DockStyle.Fill };
        // Settings page
        private readonly ToggleSwitch _swAutostart = new();
        private readonly ToggleSwitch _swHdrControl = new();
        private readonly ToggleSwitch _swBrightnessControl = new();
        private Label _hdrStatusLabel = null!;
        private Label _activityLabel = null!;

        // Log page
        private readonly TextBox _log = new()
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = false,
            BorderStyle = BorderStyle.None
        };

        private bool _loadingSettings;

        private bool _reallyExit;
        private int _logCursor;

        public event EventHandler? HiddenToTray;

        public MainForm(HdrEngine engine, Action<bool> setPaused, Action exitApp)
        {
            _engine = engine;
            _setPaused = setPaused;
            _exitApp = exitApp;

            SuspendLayout();

            Text = "HDR";
            Icon = _windowIcon;
            Font = Theme.Body;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterScreen;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(20) };
            _root = root;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _tabs.AddTab("HDR");
            _tabs.AddTab("测试");
            _tabs.AddTab("设置");
            _tabs.AddTab("日志");
            root.Controls.Add(_tabs, 0, 0);

            AddPage(BuildGamesPage());
            AddPage(BuildTestPage());
            AddPage(BuildSettingsPage());
            AddPage(BuildLogPage());
            root.Controls.Add(_content, 0, 1);

            // Designer-style auto scaling: everything above is in 96-DPI pixels
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 660);
            MinimumSize = new Size(660, 560);
            Controls.Add(root);

            ResumeLayout(false);
            PerformLayout();

            UpdateContentWidth();
            ShowPage(0);
            WireEvents();
            LoadSettings();
            RebuildGames();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.StyleWindow(Handle);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _toolTip.Dispose();
                _windowIcon.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------
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

        private Control BuildGamesPage()
        {
            var page = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // hint
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));    // search
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // list
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));    // buttons

            // Hint
            var hintRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = new Padding(0) };
            hintRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var hint = new Label
            {
                Text = "开启应用后，其运行期间将使用 HDR",
                Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(2, 0, 0, 0)
            };
            hintRow.Controls.Add(hint, 0, 0);
            page.Controls.Add(hintRow, 0, 0);

            // Search box: rounded field with a search icon
            var searchCard = new RoundedPanel
            {
                Radius = 8, BackColor = Theme.Input, BorderColor = Theme.Border,
                Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 10), Size = new Size(CardWidth, 34)
            };
            var searchIcon = new Label
            {
                Text = Theme.GlyphSearch, Font = Theme.IconFont, ForeColor = Theme.Muted, BackColor = Theme.Input,
                TextAlign = ContentAlignment.MiddleCenter, AutoSize = false
            };
            searchIcon.SetBounds(8, 0, 26, 34);
            _search.BackColor = Theme.Input;
            _search.ForeColor = Theme.Text;
            _search.Font = Theme.Body;
            _search.SetBounds(38, (34 - _search.PreferredHeight) / 2, CardWidth - 50, _search.PreferredHeight);
            searchCard.Controls.Add(searchIcon);
            searchCard.Controls.Add(_search);
            searchCard.Layout += (_, _) =>   // after DPI scaling
            {
                // explicit layout instead of anchoring (DPI-safe), text box vertically centred
                int S(int v) => searchCard.LogicalToDeviceUnits(v);
                int ch = searchCard.ClientSize.Height;
                searchIcon.SetBounds(S(8), S(1), S(26), Math.Max(0, ch - S(2)));
                int x = S(40);
                _search.SetBounds(x, Math.Max(0, (ch - _search.Height) / 2), Math.Max(0, searchCard.ClientSize.Width - x - S(12)), _search.Height);
            };
            page.Controls.Add(searchCard, 0, 1);

            // Game list card
            var listCard = new RoundedPanel { Radius = 12, BackColor = Theme.Surface, Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(6) };
            listCard.Controls.Add(_games);
            page.Controls.Add(listCard, 0, 2);

            // Actions
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 12, 0, 0), Padding = new Padding(0) };
            var addExe = new ModernButton { Glyph = Theme.GlyphAdd, Text = "添加程序", AutoSize = true };
            var addRunning = new ModernButton { Glyph = Theme.GlyphAdd, Text = "添加正在运行", AutoSize = true };
            var removeBtn = new ModernButton { Glyph = Theme.GlyphDelete, Text = "移除", AutoSize = true };
            _toolTip.SetToolTip(addExe, "选择游戏的 .exe 文件");
            _toolTip.SetToolTip(addRunning, "从正在运行的程序中选择");
            _toolTip.SetToolTip(removeBtn, "移除选中的游戏或程序");
            addExe.Click += (_, _) => OnAddExe();
            addRunning.Click += (_, _) => OnAddRunning();
            removeBtn.Click += (_, _) => OnRemoveGame();
            foreach (var b in new Control[] { addExe, addRunning, removeBtn })
            {
                b.Margin = new Padding(0, 0, 8, 0);
                buttons.Controls.Add(b);
            }
            page.Controls.Add(buttons, 0, 3);

            return page;
        }

        private Control BuildSettingsPage()
        {
            var page = new SmoothScrollTable { ColumnCount = 1, Margin = new Padding(0), AutoScroll = true, Padding = new Padding(0, 0, SystemInformation.VerticalScrollBarWidth, 0) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Theme.UseDarkScrollbars(page);

            void Section(string title, string description, bool first = false)
            {
                page.AddRow(SectionHeader(title), first ? 34 : 46);
                page.AddRow(SectionDescription(description), 30);
            }

            // ---- General ----
            Section("常规", "启动和自动化开关。", first: true);
            page.AddRow(SettingRow("开机自启动", "登录时隐藏启动到托盘。", _swAutostart), 68);
            page.AddRow(SettingRow("启用 HDR 控制", "关闭后不再自动切换 HDR。", _swHdrControl), 68);
            page.AddRow(SettingRow("启用亮度控制", "自动按亮度规则调整电视亮度。", _swBrightnessControl), 68);

            // ---- Advanced ----
            Section("高级", "设置文件、日志和重置。");
            var advanced = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            var openConfig = new ModernButton { Glyph = Theme.GlyphDocument, Text = "打开 config.json", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            var reload = new ModernButton { Glyph = Theme.GlyphRefresh, Text = "重新加载 config.json", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            var openLog = new ModernButton { Glyph = Theme.GlyphFolder, Text = "打开日志文件", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            var reset = new ModernButton { Text = "恢复默认设置", AutoSize = true, Margin = new Padding(0) };
            openConfig.Click += (_, _) => OpenInNotepad(HdrEngine.ConfigPath);
            openLog.Click += (_, _) => OpenInNotepad(HdrEngine.LogPath);
            reload.Click += (_, _) =>
            {
                if (_engine.ReloadConfig()) { LoadSettings(); RebuildGames(); }
                else MessageBox.Show(this, "config.json 无法读取。请查看日志选项卡。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            reset.Click += (_, _) => OnResetSettings();
            advanced.Controls.Add(openConfig);
            advanced.Controls.Add(reload);
            advanced.Controls.Add(openLog);
            advanced.Controls.Add(reset);
            page.AddRow(advanced, 52);

            page.AddFillerRow();
            return page;
        }

        private static Label SectionDescription(string text) => new()
        {
            UseMnemonic = false,   // "&" must show as "&", not underline the next letter
            Text = text, Font = Theme.Small, ForeColor = Theme.Muted, Dock = DockStyle.Fill, AutoEllipsis = true,
            TextAlign = ContentAlignment.TopLeft, Margin = new Padding(2, 0, 0, 6), AutoSize = false
        };

        private void OnResetSettings()
        {
            var answer = MessageBox.Show(this,
                "是否将所有设置恢复为默认值？\n\n您的游戏、它们的 HDR 开关和每游戏选项将保留。" +
                "\"开机自启动\"不会改变。",
                "恢复默认设置", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            _engine.ResetSettings();
            LoadSettings();
            RebuildGames();
        }

        private static Label SectionHeader(string text) => new()
        {
            UseMnemonic = false,   // "&" must show as "&", not underline the next letter
            Text = text, Font = Theme.Section, ForeColor = Theme.Text, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft, Margin = new Padding(2, 0, 0, 2), AutoSize = false
        };

        private static RoundedPanel SettingRow(string title, string description, Control control)
        {
            const int w = CardWidth - 8;   // a little narrower: room for the scrollbar
            const int h = 60;

            var row = new RoundedPanel { Radius = 10, BackColor = Theme.Surface, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Size = new Size(w, h) };

            int textWidth = w - 18 - control.Width - 34;
            var titleLabel = new Label { UseMnemonic = false, Text = title, Font = Theme.Semibold, ForeColor = Theme.Text, AutoSize = false, AutoEllipsis = true };
            titleLabel.SetBounds(18, 10, textWidth, 22);

            var descLabel = new Label { UseMnemonic = false, Text = description, Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, AutoEllipsis = true };
            descLabel.SetBounds(18, 32, textWidth, 20);

            control.Location = new Point(w - 18 - control.Width, (h - control.Height) / 2);

            row.Controls.Add(control);
            row.Controls.Add(titleLabel);
            row.Controls.Add(descLabel);

            // Explicit layout (DPI-safe): control on the right, text uses the remaining width
            row.Layout += (_, _) =>   // after DPI scaling
            {
                int S(int v) => row.LogicalToDeviceUnits(v);
                int rw = row.ClientSize.Width;
                int rh = row.ClientSize.Height;
                control.Location = new Point(rw - S(18) - control.Width, (rh - control.Height) / 2);

                int textWidth2 = Math.Max(0, control.Left - S(16) - S(18));
                int titleHeight = titleLabel.PreferredHeight;
                int descHeight = descLabel.PreferredHeight;
                int top = Math.Max(0, (rh - titleHeight - descHeight) / 2);
                titleLabel.SetBounds(S(18), top, textWidth2, titleHeight);
                descLabel.SetBounds(S(18), top + titleHeight, textWidth2, descHeight);
            };
            return row;
        }

        private Control BuildTestPage()
        {
            var page = new SmoothScrollTable { ColumnCount = 1, Margin = new Padding(0), AutoScroll = true, Padding = new Padding(0, 0, SystemInformation.VerticalScrollBarWidth, 0) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Theme.UseDarkScrollbars(page);

            void Section(string title, string description, bool first = false)
            {
                page.AddRow(SectionHeader(title), first ? 34 : 46);
                page.AddRow(SectionDescription(description), 30);
            }

            // ---- Test ----
            Section("测试", "测试电视连接和设置亮度。");

            // ---- Activity ----
            Section("活动窗口", "当前 HDR 状态和活动窗口信息。");

            var activityPanel = new Panel { Height = 52, Margin = new Padding(0, 0, 0, 6) };
            var actTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            actTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            actTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _hdrStatusLabel = new Label { AutoSize = true, Font = Theme.Semibold, ForeColor = Theme.Text };
            _activityLabel = new Label { AutoSize = false, AutoEllipsis = true, Font = Theme.Small, ForeColor = Theme.Muted, Dock = DockStyle.Fill };
            actTable.Controls.Add(_hdrStatusLabel, 0, 0);
            actTable.Controls.Add(_activityLabel, 0, 1);
            activityPanel.Controls.Add(actTable);
            page.AddRow(activityPanel, 52);

            var testBtn = new ModernButton { Glyph = Theme.GlyphRefresh, Text = "测试连接", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            var pairBtn = new ModernButton { Glyph = Theme.GlyphLink, Text = "重新配对", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            var btnBrightness0 = new ModernButton { Text = "亮度 0", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            var btnBrightness100 = new ModernButton { Text = "亮度 100", AutoSize = true };
            var testStatusLabel = new Label { UseMnemonic = false, Text = "就绪", Font = Theme.Small, ForeColor = Theme.Muted, Dock = DockStyle.Left, AutoSize = true };
            var testPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            testPanel.Controls.Add(testBtn);
            testPanel.Controls.Add(pairBtn);
            testPanel.Controls.Add(btnBrightness0);
            testPanel.Controls.Add(btnBrightness100);
            testPanel.Controls.Add(testStatusLabel);
            page.AddRow(testPanel, 52);

            testBtn.Click += async (_, _) =>
            {
                testStatusLabel.Text = "测试中...";
                testStatusLabel.ForeColor = Theme.Muted;
                try
                {
                    var (success, message) = await TvCli.TestConnectionAsync();
                    testStatusLabel.Text = message;
                    testStatusLabel.ForeColor = success ? Color.Green : Color.Red;
                }
                catch (Exception ex)
                {
                    testStatusLabel.Text = $"失败: {ex.Message}";
                    testStatusLabel.ForeColor = Color.Red;
                }
            };

            pairBtn.Click += async (_, _) =>
            {
                testStatusLabel.Text = "配对中...";
                testStatusLabel.ForeColor = Theme.Muted;
                var ok = await TvCli.PairAsync();
                testStatusLabel.Text = ok ? "配对成功" : "配对失败";
                testStatusLabel.ForeColor = ok ? Color.Green : Color.Red;
            };

            btnBrightness0.Click += async (_, _) =>
            {
                testStatusLabel.Text = "设置中...";
                testStatusLabel.ForeColor = Theme.Muted;
                try
                {
                    var result = await TvCli.SetBrightnessAsync(0);
                    testStatusLabel.Text = result ? "设置成功" : "设置失败";
                    testStatusLabel.ForeColor = result ? Color.Green : Color.Red;
                }
                catch (Exception ex)
                {
                    testStatusLabel.Text = $"失败: {ex.Message}";
                    testStatusLabel.ForeColor = Color.Red;
                }
            };

            btnBrightness100.Click += async (_, _) =>
            {
                testStatusLabel.Text = "设置中...";
                testStatusLabel.ForeColor = Theme.Muted;
                try
                {
                    var result = await TvCli.SetBrightnessAsync(100);
                    testStatusLabel.Text = result ? "设置成功" : "设置失败";
                    testStatusLabel.ForeColor = result ? Color.Green : Color.Red;
                }
                catch (Exception ex)
                {
                    testStatusLabel.Text = $"失败: {ex.Message}";
                    testStatusLabel.ForeColor = Color.Red;
                }
            };

            page.AddFillerRow();
            return page;
        }

        private void SetBrightnessControlEnabled(bool enabled) =>
            _engine.Update(config =>
            {
                config.Brightness ??= new BrightnessConfig();
                config.Brightness.Enabled = enabled;
            });

        private Control BuildLogPage()
        {
            var card = new RoundedPanel { Radius = 12, BackColor = Theme.Surface, Padding = new Padding(14, 12, 6, 12), Margin = new Padding(0) };
            _log.BackColor = Theme.Surface;
            _log.ForeColor = Theme.Text;
            _log.Font = Theme.Mono;
            Theme.UseDarkScrollbars(_log);
            card.Controls.Add(_log);
            return card;
        }

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------
        private void WireEvents()
        {
            _tabs.SelectedIndexChanged += (_, _) => ShowPage(_tabs.SelectedIndex);
            _search.TextChanged += (_, _) => RebuildGames();

            _games.BrightnessToggled += (_, item) =>
            {
                _engine.SetGameBrightness(item.Key, item.Processes, item.Brightness, item.Name);
                if (item.Hdr)
                {
                    ApplyGameBrightness(item.Brightness);
                }
                BeginInvoke(new Action(RebuildGames));
            };

            _swAutostart.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                if (!TrayAndStartupManager.SetAutostart(_swAutostart.Checked))
                {
                    MessageBox.Show(this, "无法更新 Windows 启动项。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _loadingSettings = true;
                    _swAutostart.Checked = TrayAndStartupManager.IsAutostartEnabled();
                    _loadingSettings = false;
                }
            };

            _swHdrControl.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                _setPaused(!_swHdrControl.Checked);
            };

            _swBrightnessControl.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                SetBrightnessControlEnabled(_swBrightnessControl.Checked);
            };

            Resize += (_, _) =>
            {
                // Minimize = hide to tray (no taskbar button, not in Alt-Tab)
                if (WindowState == FormWindowState.Minimized)
                    BeginInvoke(new Action(HideToTray));
                else
                    UpdateContentWidth();
            };

            FormClosing += (_, e) =>
            {
                if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideToTray();
                }
            };
        }

        /// <summary>Centres the content in a column of at most <see cref="MaxContentWidth"/> (wide / maximized windows).</summary>
        private void UpdateContentWidth()
        {
            if (_root == null) return;
            int edge = LogicalToDeviceUnits(20);
            int side = Math.Max(edge, (ClientSize.Width - LogicalToDeviceUnits(MaxContentWidth)) / 2);
            var padding = new Padding(side, edge, side, edge);
            if (_root.Padding != padding) _root.Padding = padding;
        }

        // ------------------------------------------------------------------
        // Show / hide
        // ------------------------------------------------------------------
        public void ShowFromTray()
        {
            if (!Visible)
            {
                LoadSettings();
                RebuildGames();
                Show();
            }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true; TopMost = false;   // reliably bring to front from a background process
            Activate();
            RefreshView();
        }

        public void HideToTray()
        {
            if (IsDisposed) return;
            // Hide first: restoring a minimized window while it is still visible made it flash up before it vanished
            Hide();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;

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

        // ------------------------------------------------------------------
        // Refresh (called every 500 ms by the tray context while the window is visible)
        // ------------------------------------------------------------------
        public void RefreshView()
        {
            var s = _engine.Status;

            // Update HDR status label
            if (_hdrStatusLabel != null)
            {
                string statusText = s.Paused
                    ? (s.HdrOn ? "HDR 开 · 已暂停" : "自动化已暂停")
                    : s.HdrOn ? "HDR 已开启" : "HDR 已关闭";
                if (_hdrStatusLabel.Text != statusText)
                    BeginInvoke(new Action(() => { if (!_hdrStatusLabel.IsDisposed) _hdrStatusLabel.Text = statusText; }));
            }

            // Update activity label
            if (_activityLabel != null)
            {
                string activity;
                if (s.Paused)
                {
                    activity = "在恢复前不会自动切换 HDR。";
                }
                else if (s.LaunchHold)
                {
                    activity = s.RunningGame != null ? $"以 HDR 启动 · {s.RunningGame}" : "以 HDR 启动 · 正在启动 …";
                }
                else if (s.RunningGame != null)
                {
                    activity = $"HDR 游戏运行中：{s.RunningGame}";
                }
                else if (s.FollowingLauncher != null)
                {
                    activity = $"{s.FollowingLauncher} 已关闭 · 等待其启动的游戏 …";
                }
                else
                {
                    activity = string.IsNullOrEmpty(s.ActiveProcess)
                        ? "等待中 …"
                        : $"活动窗口：{s.ActiveProcess}{(s.IsFullscreen ? "（全屏）" : "")}";
                }
                if (_activityLabel.Text != activity)
                    BeginInvoke(new Action(() => { if (!_activityLabel.IsDisposed) _activityLabel.Text = activity; }));
            }

            // Update HDR status label color
            if (_hdrStatusLabel != null)
            {
                Color color;
                if (s.Paused) color = Theme.Muted;
                else color = s.HdrOn ? Theme.Accent : Theme.Text;
                if (_hdrStatusLabel.ForeColor != color)
                    BeginInvoke(new Action(() => { if (!_hdrStatusLabel.IsDisposed) _hdrStatusLabel.ForeColor = color; }));
            }

            // Update HDR control switch state
            bool controlEnabled = !_engine.Paused;
            if (_swHdrControl.Checked != controlEnabled)
                BeginInvoke(new Action(() =>
                {
                    if (!_swHdrControl.IsDisposed) _swHdrControl.Checked = controlEnabled;
                }));

            var lines = _engine.ReadLog(ref _logCursor);
            if (lines.Length > 0)
            {
                _log.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
                if (_log.TextLength > 200_000)
                    _log.Text = _log.Text[^100_000..];
            }
        }

        // ------------------------------------------------------------------
        // Games page
        // ------------------------------------------------------------------
        private void RebuildGames()
        {
            string filter = _search.Text.Trim();
            var items = _engine.GetGameRows()
                .Where(r => filter.Length == 0 ||
                            r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            r.Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(r => new GameItem(r))
                .ToArray();

            // Keeps the selection and scroll position
            _games.SetItems(items, _games.SelectedItem?.Key);
        }

        private async void ApplyGameBrightness(int brightness)
        {
            try
            {
                await TvCli.SetBrightnessAsync(brightness);
            }
            catch
            {
                // Ignore brightness apply errors
            }
        }

        private void OnAddExe()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "选择应使用 HDR 的游戏或程序（.exe）",
                Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
                Multiselect = true,
                InitialDirectory = _engine.GetInitialDirectory() ?? string.Empty
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            foreach (var file in dlg.FileNames)
                _engine.AddGame(HdrEngine.CleanExeInput(file), exePath: file);
            RebuildGames();
        }

        private void OnAddRunning()
        {
            var picked = PickRunningProcess();
            if (picked == null) return;
            _engine.AddGame(picked.Value.Name, exePath: picked.Value.Path);
            RebuildGames();
        }

        private (string Name, string? Path)? PickRunningProcess()
        {
            var rows = new List<(string Name, string Title, string? Path)>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    {
                        string? path = ProcessPaths.TryGetExePath(p.Id);
                        rows.Add((p.ProcessName.ToLowerInvariant(), p.MainWindowTitle, path));
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            rows = rows.GroupBy(r => r.Name).Select(g => g.First()).OrderBy(r => r.Name).ToList();

            using var dlg = new Form
            {
                Text = "添加正在运行的程序", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                Padding = new Padding(12)
            };
            dlg.SuspendLayout();
            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            foreach (var r in rows)
            {
                string t = r.Title.Length > 60 ? r.Title[..57] + "..." : r.Title;
                list.Items.Add($"{r.Name}   —   {t}");
            }
            var ok = new Button { Text = "添加", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(ok);
            list.DoubleClick += (_, _) => { if (list.SelectedIndex >= 0) dlg.DialogResult = DialogResult.OK; };
            dlg.Controls.Add(list);
            dlg.Controls.Add(bottom);
            dlg.AutoScaleDimensions = new SizeF(96F, 96F);
            dlg.AutoScaleMode = AutoScaleMode.Dpi;
            dlg.ClientSize = new Size(560, 420);
            dlg.ResumeLayout(false);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            dlg.Icon = _windowIcon;
            Theme.ApplyForm(dlg);

            if (dlg.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0) return null;
            return (rows[list.SelectedIndex].Name, rows[list.SelectedIndex].Path);
        }

        private void OnRemoveGame()
        {
            var selectedItem = _games.SelectedItem;
            if (selectedItem == null)
            {
                MessageBox.Show(this, "请先选择一个游戏或程序。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var answer = MessageBox.Show(this,
                $"是否移除 '{selectedItem.Name}'？\n\n移除后，该游戏将不再自动触发 HDR。",
                "移除游戏", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _engine.RemoveGame(selectedItem.Key, selectedItem.Processes);
            RebuildGames();
        }

        // ------------------------------------------------------------------
        // Settings page
        // ------------------------------------------------------------------
        private void LoadSettings()
        {
            _loadingSettings = true;
            try
            {
                _swHdrControl.Checked = !_engine.Paused;
                _swAutostart.Checked = TrayAndStartupManager.IsAutostartEnabled();
                _swBrightnessControl.Checked = _engine.Read(c => c.Brightness?.Enabled ?? true);
            }
            finally { _loadingSettings = false; }
        }

        private void OpenInNotepad(string path)
        {
            try
            {
                if (!File.Exists(path)) { MessageBox.Show(this, "文件尚不存在：\n" + path, Text); return; }
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
