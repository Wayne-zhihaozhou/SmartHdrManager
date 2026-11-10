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

        private void RebuildGames(bool sdr)
        {
            var list = sdr ? _gamesSdr : _gamesHdr;
            var search = sdr ? _searchSdr : _searchHdr;

            string filter = search.Text.Trim();
            GameItem[] items = _engine.GetGameRows(sdr)
                .Where(row => filter.Length == 0 || MatchesFilter(row, filter))
                .Select(row => new GameItem(row))
                .ToArray();

            list.SetItems(items, list.SelectedItem?.Key);
        }

        private void RebuildAllGames()
        {
            RebuildGames(sdr: false);
            RebuildGames(sdr: true);
        }

        private static bool MatchesFilter(GameRow row, string filter) =>
            row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            row.Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase);

        private bool IsBrightnessControlEnabled() => _engine.Read(config => config.Brightness?.Enabled ?? true);

        private void OnBrightnessControlChanged(bool enabled)
        {
            _engine.Update(config =>
            {
                config.Brightness ??= new BrightnessConfig();
                config.Brightness.Enabled = enabled;
            });
            _engine.ApplyBrightnessControlSettings();
        }

        private void OnGameBrightnessChanged(GameItem item, bool sdr)
        {
            _engine.SetGameBrightness(item.Key, item.Processes, item.Brightness, item.Name);

            // Only the application that owns the screen changes the TV right now; the others keep their value for later
            if (item.Tracked && IsBrightnessControlEnabled() && OwnsScreen(item)) ApplyGameBrightness(item.Brightness);

            BeginInvoke(new Action(() => RebuildGames(sdr)));
        }

        private bool OwnsScreen(GameItem item)
        {
            var status = _engine.Status;
            return item.Processes.Contains(status.ActiveProcess, StringComparer.OrdinalIgnoreCase) ||
                   (status.RunningGame != null &&
                    item.Processes.Contains(status.RunningGame, StringComparer.OrdinalIgnoreCase));
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

        private async Task<(bool Success, string Message)> DescribePairAsync()
        {
            bool paired = await TvCli.PairAsync();
            return (paired, paired ? "配对成功" : "配对失败");
        }

        private async Task<(bool Success, string Message)> DescribeBrightnessAsync(int brightness)
        {
            if (!IsBrightnessControlEnabled()) return (false, "亮度控制已关闭");

            bool applied = await TvCli.SetBrightnessAsync(brightness);
            return (applied, applied ? "设置成功" : "设置失败");
        }

        private void OnDesktopBrightnessChanged()
        {
            int value = _desktopBrightness.Value;
            _engine.Update(config => config.DesktopBrightness = value);

            if (!IsBrightnessControlEnabled()) return;

            // Nothing from either list owns the screen right now: show the new value at once
            var status = _engine.Status;
            if (status.RunningGame == null && status.RunningSdrApp == null) ApplyGameBrightness(value);
        }

        private void OnResetSettings()
        {
            var answer = MessageBox.Show(this,
                "是否将所有设置恢复为默认值？\n\n您的游戏、它们的 HDR 开关将保留。\"开机自启动\"不会改变。",
                "恢复默认设置", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _engine.ResetSettings();
            LoadSettings();
            RebuildAllGames();
        }

        private void ReloadConfig()
        {
            if (_engine.ReloadConfig())
            {
                LoadSettings();
                RebuildAllGames();
                return;
            }

            MessageBox.Show(this, "config.json 无法读取。请查看日志选项卡。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OnAddExe(bool sdr)
        {
            using var dialog = new OpenFileDialog
            {
                Title = sdr ? "选择应保持 SDR 的游戏或程序（.exe）" : "选择应使用 HDR 的游戏或程序（.exe）",
                Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
                Multiselect = true,
                InitialDirectory = string.Empty
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            foreach (string file in dialog.FileNames)
                _engine.AddGame(HdrEngine.CleanExeInput(file), exePath: file, sdr: sdr);

            RebuildGames(sdr);
        }

        private void OnAddRunning(bool sdr)
        {
            var picked = PickRunningProcess();
            if (picked == null) return;

            _engine.AddGame(picked.Value.Name, exePath: picked.Value.Path, sdr: sdr);
            RebuildGames(sdr);
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

        private void OnRemoveGame(bool sdr)
        {
            var list = sdr ? _gamesSdr : _gamesHdr;
            var selected = list.SelectedItem;
            if (selected == null)
            {
                MessageBox.Show(this, "请先选择一个游戏或程序。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string consequence = sdr
                ? "移除后，该游戏将不再强制保持 SDR。"
                : "移除后，该游戏将不再自动触发 HDR。";
            var answer = MessageBox.Show(this,
                $"是否移除 '{selected.Name}'？\n\n{consequence}",
                "移除游戏", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _engine.RemoveGame(selected.Key, selected.Processes, sdr);
            RebuildGames(sdr);
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
