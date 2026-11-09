using System;
using System.Drawing;
using System.Windows.Forms;

namespace SmartHdrManager
{
    public sealed partial class MainForm
    {
        private const int SectionHeaderFirstHeight = 34;
        private const int SectionHeaderHeight = 46;
        private const int SectionDescriptionHeight = 30;
        private const int SettingRowHeight = 68;
        private const int ActionRowHeight = 52;
        private const int SearchRowHeight = 46;
        private const int HintRowHeight = 30;
        private const int RowHorizontalMargin = 18;
        private const int RowTitleTop = 10;
        private const int RowTitleHeight = 22;
        private const int RowDescriptionTop = 32;
        private const int RowDescriptionHeight = 20;
        private const int ButtonSpacing = 8;

        private Control BuildGamesPage()
        {
            var page = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, HintRowHeight));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, SearchRowHeight));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, ActionRowHeight));

            page.Controls.Add(BuildGamesHint(), 0, 0);
            page.Controls.Add(BuildSearchCard(), 0, 1);

            var listCard = new RoundedPanel
            {
                Radius = 12,
                BackColor = Theme.Surface,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(6)
            };
            listCard.Controls.Add(_games);
            page.Controls.Add(listCard, 0, 2);
            page.Controls.Add(BuildGamesActions(), 0, 3);

            return page;
        }

        private static Control BuildGamesHint()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(new Label
            {
                Text = "开启应用后，其运行期间将使用 HDR",
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(2, 0, 0, 0)
            }, 0, 0);
            return row;
        }

        private Control BuildSearchCard()
        {
            var card = new RoundedPanel
            {
                Radius = 8,
                BackColor = Theme.Input,
                BorderColor = Theme.Border,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 10),
                Size = new Size(CardWidth, 34)
            };

            var icon = new Label
            {
                Text = Theme.GlyphSearch,
                Font = Theme.IconFont,
                ForeColor = Theme.Muted,
                BackColor = Theme.Input,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false
            };

            _search.BackColor = Theme.Input;
            _search.ForeColor = Theme.Text;
            _search.Font = Theme.Body;

            card.Controls.Add(icon);
            card.Controls.Add(_search);
            card.Layout += (_, _) => LayoutSearchCard(card, icon);
            return card;
        }

        private void LayoutSearchCard(RoundedPanel card, Label icon)
        {
            int S(int value) => card.LogicalToDeviceUnits(value);

            int height = card.ClientSize.Height;
            icon.SetBounds(S(8), S(1), S(26), Math.Max(0, height - S(2)));

            int textLeft = S(40);
            _search.SetBounds(textLeft, Math.Max(0, (height - _search.Height) / 2),
                              Math.Max(0, card.ClientSize.Width - textLeft - S(12)), _search.Height);
        }

        private Control BuildGamesActions()
        {
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };

            var addExe = new ModernButton { Glyph = Theme.GlyphAdd, Text = "添加程序", AutoSize = true };
            var addRunning = new ModernButton { Glyph = Theme.GlyphAdd, Text = "添加正在运行", AutoSize = true };
            var remove = new ModernButton { Glyph = Theme.GlyphDelete, Text = "移除", AutoSize = true };

            _toolTip.SetToolTip(addExe, "选择游戏的 .exe 文件");
            _toolTip.SetToolTip(addRunning, "从正在运行的程序中选择");
            _toolTip.SetToolTip(remove, "移除选中的游戏或程序");

            addExe.Click += (_, _) => OnAddExe();
            addRunning.Click += (_, _) => OnAddRunning();
            remove.Click += (_, _) => OnRemoveGame();

            foreach (var button in new Control[] { addExe, addRunning, remove })
            {
                button.Margin = new Padding(0, 0, ButtonSpacing, 0);
                buttons.Controls.Add(button);
            }
            return buttons;
        }

        private Control BuildSettingsPage()
        {
            var page = CreateScrollPage();

            AddSection(page, "常规", "启动和自动化开关。", first: true);
            page.AddRow(SettingRow("开机自启动", "登录时隐藏启动到托盘。", _swAutostart), SettingRowHeight);
            page.AddRow(SettingRow("启用 HDR 控制", "关闭后不再自动切换 HDR。", _swHdrControl), SettingRowHeight);
            page.AddRow(SettingRow("启用亮度控制", "自动按亮度规则调整电视亮度。", _swBrightnessControl), SettingRowHeight);

            AddSection(page, "高级", "设置文件、日志和重置。");
            page.AddRow(BuildAdvancedActions(), ActionRowHeight);
            page.AddFillerRow();
            return page;
        }

        private Control BuildAdvancedActions()
        {
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };

            var openConfig = new ModernButton { Glyph = Theme.GlyphDocument, Text = "打开 config.json", AutoSize = true, Margin = new Padding(0, 0, ButtonSpacing, 0) };
            var reload = new ModernButton { Glyph = Theme.GlyphRefresh, Text = "重新加载 config.json", AutoSize = true, Margin = new Padding(0, 0, ButtonSpacing, 0) };
            var openLog = new ModernButton { Glyph = Theme.GlyphFolder, Text = "打开日志文件", AutoSize = true, Margin = new Padding(0, 0, ButtonSpacing, 0) };
            var reset = new ModernButton { Text = "恢复默认设置", AutoSize = true, Margin = new Padding(0) };

            openConfig.Click += (_, _) => OpenInNotepad(HdrEngine.ConfigPath);
            openLog.Click += (_, _) => OpenInNotepad(HdrEngine.LogPath);
            reload.Click += (_, _) => ReloadConfig();
            reset.Click += (_, _) => OnResetSettings();

            actions.Controls.Add(openConfig);
            actions.Controls.Add(reload);
            actions.Controls.Add(openLog);
            actions.Controls.Add(reset);
            return actions;
        }

        private Control BuildTestPage()
        {
            var page = CreateScrollPage();

            AddSection(page, "测试", "测试电视连接和设置亮度。");
            AddSection(page, "活动窗口", "当前 HDR 状态和活动窗口信息。");
            page.AddRow(BuildActivityPanel(), ActionRowHeight);

            var statusLabel = new Label
            {
                UseMnemonic = false,
                Text = "就绪",
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                Dock = DockStyle.Left,
                AutoSize = true
            };

            var testButton = new ModernButton { Glyph = Theme.GlyphRefresh, Text = "测试连接", AutoSize = true, Margin = new Padding(0, 0, ButtonSpacing, 0) };
            var pairButton = new ModernButton { Glyph = Theme.GlyphLink, Text = "重新配对", AutoSize = true, Margin = new Padding(0, 0, ButtonSpacing, 0) };
            var brightnessLow = new ModernButton { Text = "亮度 0", AutoSize = true, Margin = new Padding(0, 0, ButtonSpacing, 0) };
            var brightnessHigh = new ModernButton { Text = "亮度 100", AutoSize = true, Margin = new Padding(0, 0, ButtonSpacing, 0) };

            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            panel.Controls.Add(testButton);
            panel.Controls.Add(pairButton);
            panel.Controls.Add(brightnessLow);
            panel.Controls.Add(brightnessHigh);
            panel.Controls.Add(statusLabel);
            page.AddRow(panel, ActionRowHeight);

            testButton.Click += async (_, _) => await ShowTvResultAsync(statusLabel, "测试中...", TvCli.TestConnectionAsync);
            pairButton.Click += async (_, _) => await ShowTvResultAsync(statusLabel, "配对中...", DescribePairAsync);
            brightnessLow.Click += async (_, _) => await ShowTvResultAsync(statusLabel, "设置中...", () => DescribeBrightnessAsync(0));
            brightnessHigh.Click += async (_, _) => await ShowTvResultAsync(statusLabel, "设置中...", () => DescribeBrightnessAsync(100));

            page.AddFillerRow();
            return page;
        }

        private Control BuildActivityPanel()
        {
            var panel = new Panel { Height = ActionRowHeight, Margin = new Padding(0, 0, 0, 6) };

            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _hdrStatusLabel = new Label { AutoSize = true, Font = Theme.Semibold, ForeColor = Theme.Text };
            _activityLabel = new Label { AutoSize = false, AutoEllipsis = true, Font = Theme.Small, ForeColor = Theme.Muted, Dock = DockStyle.Fill };

            table.Controls.Add(_hdrStatusLabel, 0, 0);
            table.Controls.Add(_activityLabel, 0, 1);
            panel.Controls.Add(table);
            return panel;
        }

        private Control BuildLogPage()
        {
            var card = new RoundedPanel
            {
                Radius = 12,
                BackColor = Theme.Surface,
                Padding = new Padding(14, 12, 6, 12),
                Margin = new Padding(0)
            };

            _log.BackColor = Theme.Surface;
            _log.ForeColor = Theme.Text;
            _log.Font = Theme.Mono;
            Theme.UseDarkScrollbars(_log);
            card.Controls.Add(_log);
            return card;
        }

        private static SmoothScrollTable CreateScrollPage()
        {
            var page = new SmoothScrollTable
            {
                ColumnCount = 1,
                Margin = new Padding(0),
                AutoScroll = true,
                Padding = new Padding(0, 0, SystemInformation.VerticalScrollBarWidth, 0)
            };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Theme.UseDarkScrollbars(page);
            return page;
        }

        private static void AddSection(SmoothScrollTable page, string title, string description, bool first = false)
        {
            page.AddRow(SectionHeader(title), first ? SectionHeaderFirstHeight : SectionHeaderHeight);
            page.AddRow(SectionDescription(description), SectionDescriptionHeight);
        }

        private static Label SectionHeader(string text) => new()
        {
            UseMnemonic = false,
            Text = text,
            Font = Theme.Section,
            ForeColor = Theme.Text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Margin = new Padding(2, 0, 0, 2),
            AutoSize = false
        };

        private static Label SectionDescription(string text) => new()
        {
            UseMnemonic = false,
            Text = text,
            Font = Theme.Small,
            ForeColor = Theme.Muted,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(2, 0, 0, 6),
            AutoSize = false
        };

        /// <summary>Card with a title, a description on the left and a control on the right.</summary>
        private static RoundedPanel SettingRow(string title, string description, Control control)
        {
            const int width = CardWidth - 8;
            const int height = 60;

            var row = new RoundedPanel
            {
                Radius = 10,
                BackColor = Theme.Surface,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 8),
                Size = new Size(width, height)
            };

            var titleLabel = new Label
            {
                UseMnemonic = false,
                Text = title,
                Font = Theme.Semibold,
                ForeColor = Theme.Text,
                AutoSize = false,
                AutoEllipsis = true
            };
            int initialTextWidth = width - RowHorizontalMargin - control.Width - 34;
            titleLabel.SetBounds(RowHorizontalMargin, RowTitleTop, initialTextWidth, RowTitleHeight);

            var descriptionLabel = new Label
            {
                UseMnemonic = false,
                Text = description,
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = false,
                AutoEllipsis = true
            };
            descriptionLabel.SetBounds(RowHorizontalMargin, RowDescriptionTop, initialTextWidth, RowDescriptionHeight);

            control.Location = new Point(width - RowHorizontalMargin - control.Width, (height - control.Height) / 2);

            row.Controls.Add(control);
            row.Controls.Add(titleLabel);
            row.Controls.Add(descriptionLabel);

            row.Layout += (_, _) => LayoutSettingRow(row, control, titleLabel, descriptionLabel);
            return row;
        }

        private static void LayoutSettingRow(RoundedPanel row, Control control, Label titleLabel, Label descriptionLabel)
        {
            int S(int value) => row.LogicalToDeviceUnits(value);

            int rowWidth = row.ClientSize.Width;
            int rowHeight = row.ClientSize.Height;
            control.Location = new Point(rowWidth - S(RowHorizontalMargin) - control.Width, (rowHeight - control.Height) / 2);

            int textWidth = Math.Max(0, control.Left - S(16) - S(RowHorizontalMargin));
            int titleHeight = titleLabel.PreferredHeight;
            int descriptionHeight = descriptionLabel.PreferredHeight;
            int top = Math.Max(0, (rowHeight - titleHeight - descriptionHeight) / 2);

            titleLabel.SetBounds(S(RowHorizontalMargin), top, textWidth, titleHeight);
            descriptionLabel.SetBounds(S(RowHorizontalMargin), top + titleHeight, textWidth, descriptionHeight);
        }
    }
}
