using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HdrBrightControl
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
            _engine.SetGameBrightness(item.Processes, item.Brightness, item.Name);

            // Only the program the engine assigns the brightness to right now changes the TV; the others keep their
            // value for later, and the engine applies it as soon as they take the screen back.
            if (item.Tracked && IsBrightnessControlEnabled() && OwnsScreen(item)) ApplyGameBrightness(item.Brightness);

            BeginInvoke(new Action(() => (sdr ? _gamesSdr : _gamesHdr).Invalidate()));
        }

        private bool OwnsScreen(GameItem item)
        {
            string? owner = _engine.Status.BrightnessOwner;
            return owner != null && item.Processes.Contains(owner, StringComparer.OrdinalIgnoreCase);
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

        private async Task ShowTvResultAsync(string busyText, Func<Task<TvResult>> action)
        {
            _tvStatusLabel.Text = busyText;
            _tvStatusLabel.ForeColor = Theme.Muted;

            try
            {
                var result = await action();
                _tvStatusLabel.Text = result.Detail;
                _tvStatusLabel.ForeColor = result.Success ? Color.Green : Color.Red;
            }
            catch (Exception ex)
            {
                _tvStatusLabel.Text = $"失败: {ex.Message}";
                _tvStatusLabel.ForeColor = Color.Red;
            }
        }

        private async Task<TvResult> SetTestBrightnessAsync(int brightness)
        {
            if (!IsBrightnessControlEnabled()) return new TvResult(false, "亮度控制已关闭");

            return await TvCli.SetBrightnessAsync(brightness);
        }

        private void OnDesktopBrightnessChanged()
        {
            int value = _desktopBrightness.Value;
            _engine.Update(config => config.DesktopBrightness = value);

            if (!IsBrightnessControlEnabled()) return;

            // The TV is showing the desktop brightness right now: change it at once
            if (_engine.Status.BrightnessOwner == null) ApplyGameBrightness(value);
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

            _engine.RemoveGame(selected.Processes, sdr);
            RebuildGames(sdr);
        }

        /// <summary>Launches the program of a row that was double-clicked on the HDR or SDR page.</summary>
        private void OnGameItemActivated(GameItem item)
        {
            string? exePath = item.ExePath;

            if (string.IsNullOrWhiteSpace(exePath))
            {
                MessageBox.Show(this,
                    $"'{item.Name}' 没有记录可执行文件路径，无法启动。\n\n请移除该条目，再用「添加程序」重新添加它的 .exe。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!File.Exists(exePath))
            {
                MessageBox.Show(this,
                    $"找不到 '{item.Name}' 的可执行文件：\n{exePath}\n\n请确认文件是否已被移动或删除。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (IsSameExecutable(exePath, Application.ExecutablePath))
            {
                MessageBox.Show(this, "无法从列表中启动 HDR亮控自身。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty
                };
                Process.Start(startInfo)?.Dispose();
                _engine.Log("LAUNCH", $"已启动 '{item.Name}'：{exePath}");
            }
            catch (Exception ex)
            {
                _engine.Log("ERROR", $"无法启动 '{item.Name}'：{ex.Message}");
                MessageBox.Show(this, $"无法启动 '{item.Name}'。\n\n{ex.Message}",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static bool IsSameExecutable(string first, string second)
        {
            string? firstPath = ProgramIdentity.FullPath(first);
            string? secondPath = ProgramIdentity.FullPath(second);
            return firstPath != null && secondPath != null &&
                   string.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase);
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
