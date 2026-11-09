using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SmartHdrManager
{
    /// <summary>
    /// The tray icon and the "start with Windows" registry entry. The tray icon is the only thing that stays visible
    /// while the app runs in the background; the "start with Windows" entry launches it with "--tray".
    /// </summary>
    public sealed class TrayAndStartupManager : IDisposable
    {
        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryName = "SmartHdrManager";
        private const string AutostartArguments = "--tray";
        private const int TooltipMaxLength = 63;
        private const int BalloonTimeoutMs = 4000;
        private const int MinIconSize = 16;

        private readonly NotifyIcon _icon;
        private readonly Icon _iconOff;
        private readonly Icon _iconOn;
        private readonly Icon _iconPaused;
        private int _iconState = -1;

        public TrayAndStartupManager(Action openMainWindow, Action exit)
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
                Text = "Smart HDR Manager",
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

        public void UpdateTooltip(string text)
        {
            _icon.Text = text.Length > TooltipMaxLength ? text[..TooltipMaxLength] : text;
        }

        public void ShowBalloon(string title, string message) =>
            _icon.ShowBalloonTip(BalloonTimeoutMs, title, message, ToolTipIcon.Info);

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _iconOff.Dispose();
            _iconOn.Dispose();
            _iconPaused.Dispose();
        }

        public static bool IsAutostartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
                return key?.GetValue(AppRegistryName) != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool SetAutostart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                if (key == null) return false;

                if (enable)
                {
                    string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
                    key.SetValue(AppRegistryName, $"\"{exePath}\" {AutostartArguments}");
                }
                else
                {
                    key.DeleteValue(AppRegistryName, false);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// "开机自启动" stores the full exe path. After the app was moved or updated into a new folder, the entry still
        /// points at the old location and nothing starts at sign-in. Called at every start: an entry whose exe is gone
        /// is updated to this exe. Returns true when the entry was changed.
        /// </summary>
        public static bool RefreshAutostartPath()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return false;

                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                if (key?.GetValue(AppRegistryName) is not string current) return false;

                string wanted = $"\"{exePath}\" {AutostartArguments}";
                if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)) return false;

                string registered = current.TrimStart();
                registered = registered.StartsWith('"')
                    ? registered[1..Math.Max(1, registered.IndexOf('"', 1))]
                    : registered.Split(' ')[0];
                if (registered.Length > 0 && File.Exists(registered)) return false;

                key.SetValue(AppRegistryName, wanted);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
