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
    public sealed partial class MainForm
    {
        private const int RunningTitleMaxLength = 60;
        private const int RunningDialogWidth = 560;
        private const int RunningDialogHeight = 420;

        private void RebuildGames()
        {
            string filter = _search.Text.Trim();
            GameItem[] items = _engine.GetGameRows()
                .Where(row => filter.Length == 0 || MatchesFilter(row, filter))
                .Select(row => new GameItem(row))
                .ToArray();

            _games.SetItems(items, _games.SelectedItem?.Key);
        }

        private static bool MatchesFilter(GameRow row, string filter) =>
            row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            row.Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase);

        private void OnGameBrightnessChanged(GameItem item)
        {
            _engine.SetGameBrightness(item.Key, item.Processes, item.Brightness, item.Name);
            if (item.Hdr) ApplyGameBrightness(item.Brightness);
            BeginInvoke(new Action(RebuildGames));
        }

        private async void ApplyGameBrightness(int brightness)
        {
            try
            {
                await TvCli.SetBrightnessAsync(brightness);
            }
            catch (Exception ex)
            {
                HdrEngine.WriteFileLog($"[TV] 无法应用游戏亮度: {ex.Message}");
            }
        }

        private async Task ShowTvResultAsync(Label statusLabel, string busyText, Func<Task<(bool Success, string Message)>> action)
        {
            statusLabel.Text = busyText;
            statusLabel.ForeColor = Theme.Muted;

            try
            {
                var (success, message) = await action();
                statusLabel.Text = message;
                statusLabel.ForeColor = success ? Color.Green : Color.Red;
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"失败: {ex.Message}";
                statusLabel.ForeColor = Color.Red;
            }
        }

        private static async Task<(bool Success, string Message)> DescribePairAsync()
        {
            bool paired = await TvCli.PairAsync();
            return (paired, paired ? "配对成功" : "配对失败");
        }

        private static async Task<(bool Success, string Message)> DescribeBrightnessAsync(int brightness)
        {
            bool applied = await TvCli.SetBrightnessAsync(brightness);
            return (applied, applied ? "设置成功" : "设置失败");
        }

        private void SetBrightnessControlEnabled(bool enabled)
        {
            _engine.Update(config =>
            {
                config.Brightness ??= new BrightnessConfig();
                config.Brightness.Enabled = enabled;
            });
            _engine.ApplyTvControlSettings();
        }

        private void OnResetSettings()
        {
            var answer = MessageBox.Show(this,
                "是否将所有设置恢复为默认值？\n\n您的游戏、它们的 HDR 开关将保留。\"开机自启动\"不会改变。",
                "恢复默认设置", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _engine.ResetSettings();
            LoadSettings();
            RebuildGames();
        }

        private void ReloadConfig()
        {
            if (_engine.ReloadConfig())
            {
                LoadSettings();
                RebuildGames();
                return;
            }

            MessageBox.Show(this, "config.json 无法读取。请查看日志选项卡。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OnAddExe()
        {
            using var dialog = new OpenFileDialog
            {
                Title = "选择应使用 HDR 的游戏或程序（.exe）",
                Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
                Multiselect = true,
                InitialDirectory = _engine.GetInitialDirectory() ?? string.Empty
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            foreach (string file in dialog.FileNames)
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
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                    rows.Add((process.ProcessName.ToLowerInvariant(), process.MainWindowTitle, ProcessPaths.TryGetExePath(process.Id)));
                }
                catch { }
                finally { process.Dispose(); }
            }

            rows = rows.GroupBy(row => row.Name).Select(group => group.First()).OrderBy(row => row.Name).ToList();

            using var dialog = new Form
            {
                Text = "添加正在运行的程序",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                Padding = new Padding(12)
            };
            dialog.SuspendLayout();

            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            foreach (var row in rows)
            {
                string title = row.Title.Length > RunningTitleMaxLength
                    ? row.Title[..(RunningTitleMaxLength - 3)] + "..."
                    : row.Title;
                list.Items.Add($"{row.Name}   —   {title}");
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

            list.DoubleClick += (_, _) => { if (list.SelectedIndex >= 0) dialog.DialogResult = DialogResult.OK; };

            dialog.Controls.Add(list);
            dialog.Controls.Add(bottom);
            dialog.AutoScaleDimensions = new SizeF(96F, 96F);
            dialog.AutoScaleMode = AutoScaleMode.Dpi;
            dialog.ClientSize = new Size(RunningDialogWidth, RunningDialogHeight);
            dialog.ResumeLayout(false);
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            dialog.Icon = _windowIcon;
            Theme.ApplyForm(dialog);

            if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0) return null;
            return (rows[list.SelectedIndex].Name, rows[list.SelectedIndex].Path);
        }

        private void OnRemoveGame()
        {
            var selected = _games.SelectedItem;
            if (selected == null)
            {
                MessageBox.Show(this, "请先选择一个游戏或程序。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var answer = MessageBox.Show(this,
                $"是否移除 '{selected.Name}'？\n\n移除后，该游戏将不再自动触发 HDR。",
                "移除游戏", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _engine.RemoveGame(selected.Key, selected.Processes);
            RebuildGames();
        }

        private void OpenInNotepad(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(this, "文件尚不存在：\n" + path, Text);
                    return;
                }

                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
