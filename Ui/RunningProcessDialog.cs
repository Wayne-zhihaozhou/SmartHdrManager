using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HdrBrightControl
{
    internal sealed class RunningProcessDialog : Form
    {
        private const int DialogWidth = 560;
        private const int DialogHeight = 420;
        private const int TitleMaxLength = 60;

        private readonly ListBox _list;
        private readonly List<(string Name, string Title, string? Path)> _rows;

        public (string Name, string? Path)? Selected { get; private set; }

        public RunningProcessDialog(IEnumerable<(string Name, string Title, string? Path)> processes, Icon? icon)
        {
            _rows = new List<(string Name, string Title, string? Path)>(processes);

            Text = "添加正在运行的程序";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Padding = new Padding(12);
            SuspendLayout();

            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            foreach (var row in _rows)
            {
                string title = row.Title.Length > TitleMaxLength
                    ? row.Title[..(TitleMaxLength - 3)] + "..."
                    : row.Title;
                _list.Items.Add($"{row.Name}   —   {title}");
            }

            var ok = new Button { Text = "添加", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 10, 0, 0)
            };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(ok);

            _list.DoubleClick += (_, _) => { if (_list.SelectedIndex >= 0) DialogResult = DialogResult.OK; };

            Controls.Add(_list);
            Controls.Add(bottom);
            Font = SystemFonts.MenuFont;
            ClientSize = new Size(DialogWidth, DialogHeight);
            AcceptButton = ok;
            CancelButton = cancel;
            Icon = icon;
            ResumeLayout(false);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK && _list.SelectedIndex >= 0)
                Selected = (_rows[_list.SelectedIndex].Name, _rows[_list.SelectedIndex].Path);
            base.OnFormClosing(e);
        }
    }
}
