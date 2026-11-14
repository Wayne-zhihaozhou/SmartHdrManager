using System;
using System.Drawing;
using System.Windows.Forms;

namespace HdrBrightControl
{
    public interface ITrayService
    {
        void UpdateState(bool hdrOn, bool paused);
        void Dispose();
    }

    public interface IStartupManager
    {
        bool IsEnabled();
        bool Set(bool enable);
        bool RefreshPath();
        bool IsAutostartEnabled();
        bool SetAutostart(bool enable);
        bool ShouldStartHidden();
    }

    public sealed class TrayService : ITrayService, IDisposable
    {
        private const int MinIconSize = 16;

        private readonly NotifyIcon _icon;
        private readonly Icon _iconOff;
        private readonly Icon _iconOn;
        private readonly Icon _iconPaused;
        private int _iconState = -1;

        public TrayService(Action openMainWindow, Action exit)
        {
            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (_, _) => exit();

            var menu = new ContextMenuStrip
            {
                Renderer = new DarkMenuRenderer(),
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                ShowImageMargin = true
            };
            menu.Items.Add(exitItem);

            int iconSize = Math.Max(MinIconSize, SystemInformation.SmallIconSize.Width);
            _iconOff = AppIcons.Create(IconStyle.Gray, iconSize);
            _iconOn = AppIcons.Create(IconStyle.Vivid, iconSize);
            _iconPaused = AppIcons.Create(IconStyle.Amber, iconSize);

            _icon = new NotifyIcon
            {
                Icon = _iconOff,
                Text = "HDR亮控",
                ContextMenuStrip = menu,
                Visible = true
            };

            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) openMainWindow();
            };
        }

        public void UpdateState(bool hdrOn, bool paused)
        {
            int state = paused ? 2 : hdrOn ? 1 : 0;
            if (state == _iconState) return;

            _iconState = state;
            _icon.Icon = state == 2 ? _iconPaused : state == 1 ? _iconOn : _iconOff;
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _iconOff.Dispose();
            _iconOn.Dispose();
            _iconPaused.Dispose();
        }
    }

    public sealed class RegistryStartupManager : IStartupManager
    {
        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryName = "HdrBrightControl";
        private const string SettingsKey = @"Software\HdrBrightControl";
        private const string StartHiddenValue = "StartHidden";

        public bool IsEnabled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
                return key?.GetValue(AppRegistryName) != null;
            }
            catch
            {
                return false;
            }
        }

        public bool Set(bool enable)
        {
            try
            {
                using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                if (runKey == null) return false;

                if (enable)
                {
                    string exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
                    runKey.SetValue(AppRegistryName, $"\"{exePath}\"");
                    SetStartHidden(true);
                }
                else
                {
                    runKey.DeleteValue(AppRegistryName, false);
                    SetStartHidden(false);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool RefreshPath()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return false;

                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                if (key?.GetValue(AppRegistryName) is not string current) return false;

                string wanted = $"\"{exePath}\"";
                if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)) return false;

                string? registered = ParseRegisteredExePath(current);
                if (registered == null) return false;
                if (registered.Length > 0 && System.IO.File.Exists(registered)) return false;

                key.SetValue(AppRegistryName, wanted);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool IsAutostartEnabled()
        {
            return IsEnabled();
        }

        public bool SetAutostart(bool enable)
        {
            return Set(enable);
        }

        private void SetStartHidden(bool value)
        {
            try
            {
                using var settingsKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsKey);
                if (settingsKey == null) return;
                settingsKey.SetValue(StartHiddenValue, value ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }
        }

        public bool ShouldStartHidden()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsKey, false);
                return key?.GetValue(StartHiddenValue) is int v && v != 0;
            }
            catch
            {
                return false;
            }
        }

        private static string? ParseRegisteredExePath(string registryValue)
        {
            string value = registryValue.Trim();
            if (value.Length == 0) return null;

            if (value[0] == '"')
            {
                int closing = value.IndexOf('"', 1);
                return closing > 1 ? value.Substring(1, closing - 1) : null;
            }

            int space = value.IndexOf(' ');
            string candidate = space < 0 ? value : value.Substring(0, space);
            return candidate.Length == 0 ? null : candidate;
        }
    }
}
