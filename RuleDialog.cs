using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// 亮度规则编辑对话框。
    /// </summary>
    public sealed class RuleDialog : Form
    {
        private readonly BrightnessRule _rule;
        private readonly TextBox _nameBox = new();
        private readonly NumericUpDown _brightnessNum = new();
        private readonly CheckBox _matchFullscreen = new();
        private readonly TextBox _processesBox = new();
        private readonly TextBox _keywordsBox = new();
        private readonly TextBox _ignoreBox = new();
        private readonly Label _statusLabel = new();

        public RuleDialog(BrightnessRule rule)
        {
            _rule = rule;
            Text = "编辑亮度规则";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            SizeGripStyle = SizeGripStyle.Hide;
            ShowInTaskbar = false;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;

            var mainTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 8,
                Padding = new Padding(16, 16, 16, 16)
            };
            mainTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            mainTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
            mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

            // Name
            var nameLabel = new Label { Text = "规则名称:", Font = Theme.Semibold, ForeColor = Theme.Text, Dock = DockStyle.Left, AutoSize = true };
            mainTable.Controls.Add(nameLabel, 0, 0);
            _nameBox.Text = _rule.Name;
            _nameBox.Font = Theme.Body;
            _nameBox.Padding = new Padding(6, 4, 6, 4);
            mainTable.Controls.Add(_nameBox, 1, 0);

            // Brightness
            var brightnessLabel = new Label { Text = "亮度值 (0-100):", Font = Theme.Semibold, ForeColor = Theme.Text, Dock = DockStyle.Left, AutoSize = true };
            mainTable.Controls.Add(brightnessLabel, 0, 1);
            _brightnessNum.Minimum = 0;
            _brightnessNum.Maximum = 100;
            _brightnessNum.Value = _rule.Brightness;
            _brightnessNum.Width = 120;
            _brightnessNum.Font = Theme.Body;
            _brightnessNum.Increment = 1;
            mainTable.Controls.Add(_brightnessNum, 1, 1);

            // Match fullscreen
            _matchFullscreen.Text = "匹配全屏窗口";
            _matchFullscreen.Font = Theme.Body;
            _matchFullscreen.Checked = _rule.MatchFullscreen;
            _matchFullscreen.BackColor = Color.Transparent;
            mainTable.Controls.Add(_matchFullscreen, 1, 2);

            // Processes
            var procLabel = new Label { Text = "进程名 (逗号分隔):", Font = Theme.Semibold, ForeColor = Theme.Text, Dock = DockStyle.Left, AutoSize = true };
            mainTable.Controls.Add(procLabel, 0, 3);
            _processesBox = new TextBox
            {
                Font = Theme.Mono,
                Width = 300,
                Height = 60,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Padding = new Padding(6, 4, 6, 4)
            };
            _processesBox.Text = string.Join(",", _rule.Processes);
            mainTable.Controls.Add(_processesBox, 1, 3);

            // Title keywords
            var kwLabel = new Label { Text = "标题关键词 (逗号分隔):", Font = Theme.Semibold, ForeColor = Theme.Text, Dock = DockStyle.Left, AutoSize = true };
            mainTable.Controls.Add(kwLabel, 0, 4);
            _keywordsBox = new TextBox
            {
                Font = Theme.Mono,
                Width = 300,
                Height = 60,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Padding = new Padding(6, 4, 6, 4)
            };
            _keywordsBox.Text = string.Join(",", _rule.TitleKeywords);
            mainTable.Controls.Add(_keywordsBox, 1, 4);

            // Ignore processes
            var ignoreLabel = new Label { Text = "忽略进程 (逗号分隔):", Font = Theme.Semibold, ForeColor = Theme.Text, Dock = DockStyle.Left, AutoSize = true };
            mainTable.Controls.Add(ignoreLabel, 0, 5);
            _ignoreBox = new TextBox
            {
                Font = Theme.Mono,
                Width = 300,
                Height = 40,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Padding = new Padding(6, 4, 6, 4)
            };
            _ignoreBox.Text = string.Join(",", _rule.IgnoreProcesses);
            mainTable.Controls.Add(_ignoreBox, 1, 5);

            // Help text
            _statusLabel.Text = "留空 = 兜底规则（无匹配时使用）";
            _statusLabel.Font = Theme.Small;
            _statusLabel.ForeColor = Theme.Muted;
            _statusLabel.AutoEllipsis = true;
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.AutoSize = false;
            mainTable.Controls.Add(_statusLabel, 1, 6);

            // Buttons
            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 8, 0, 0)
            };
            var okBtn = new ModernButton { Text = "确定", AutoSize = true, Primary = true, Margin = new Padding(0, 0, 6, 0) };
            var cancelBtn = new ModernButton { Text = "取消", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            okBtn.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
            cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            btnPanel.Controls.Add(okBtn);
            btnPanel.Controls.Add(cancelBtn);
            mainTable.Controls.Add(btnPanel, 1, 7);

            Controls.Add(mainTable);
        }

        public BrightnessRule GetRule()
        {
            _rule.Name = _nameBox.Text.Trim() ?? "新规则";
            _rule.Brightness = (int)_brightnessNum.Value;
            _rule.MatchFullscreen = _matchFullscreen.Checked;

            _rule.Processes.Clear();
            if (!string.IsNullOrWhiteSpace(_processesBox.Text))
            {
                foreach (var p in _processesBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    _rule.Processes.Add(p.ToLowerInvariant());
                }
            }

            _rule.TitleKeywords.Clear();
            if (!string.IsNullOrWhiteSpace(_keywordsBox.Text))
            {
                foreach (var kw in _keywordsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    _rule.TitleKeywords.Add(kw);
                }
            }

            _rule.IgnoreProcesses.Clear();
            if (!string.IsNullOrWhiteSpace(_ignoreBox.Text))
            {
                foreach (var ig in _ignoreBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    _rule.IgnoreProcesses.Add(ig.ToLowerInvariant());
                }
            }

            return _rule;
        }
    }

    /// <summary>
    /// 颜色选择按钮：点击弹出颜色对话框。
    /// </summary>
    public sealed class ColorPickerButton : Control
    {
        private Color _color = Color.White;
        private readonly Label _preview = new();
        private readonly Label _text = new();

        public ColorPickerButton()
        {
            Size = new Size(160, 36);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            ForeColor = Theme.Muted;
            Font = Theme.Small;
            Text = "选择颜色";

            _preview = new Label
            {
                Size = new Size(20, 16),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = _color,
                Location = new Point(6, 10),
                AutoSize = false
            };
            Controls.Add(_preview);

            _text = new Label
            {
                Text = Text,
                Font = Font,
                ForeColor = ForeColor,
                Location = new Point(32, 10),
                AutoSize = true
            };
            Controls.Add(_text);

            Click += (_, _) =>
            {
                using var dlg = new ColorDialog { Color = _color };
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _color = dlg.Color;
                    _preview.BackColor = _color;
                    Text = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
                    Invalidate();
                }
            };
        }

        public Color SelectedColor => _color;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Transparent
        }
    }
}
