using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SmartHdrManager
{
    // =========================================================================
    // SYSTEM TRAY ICON & WINDOWS STARTUP (registry Run key, "--tray")
    // The tray icon is the only thing that stays visible while the app runs in the background.
    // =========================================================================
    public sealed class TrayAndStartupManager : IDisposable
    {
        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryName = "SmartHdrManager";

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

            // Tray icon shows the state at a glance: colourful = HDR on, grey = SDR, amber = paused
            int iconSize = Math.Max(16, SystemInformation.SmallIconSize.Width);
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

            // Left click opens the window; right click shows the menu.
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
            // NotifyIcon.Text is limited to 63 characters
            _icon.Text = text.Length > 63 ? text[..63] : text;
        }

        public void ShowBalloon(string title, string message)
        {
            _icon.ShowBalloonTip(4000, title, message, ToolTipIcon.Info);
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _iconOff.Dispose();
            _iconOn.Dispose();
            _iconPaused.Dispose();
        }

        // ---- Windows startup (unchanged behaviour: starts minimized in the tray) ----
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
                    key.SetValue(AppRegistryName, $"\"{exePath}\" --tray");
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
        /// "开机自启动" stores the full exe path. After the app was moved or updated into a new folder
        /// (e.g. ...-v2.5.0 -> ...-v2.5.1), the entry still points at the old location and nothing starts at sign-in.
        /// Called at every start: if the entry exists but its exe is gone, it is updated to this exe.
        /// Returns true when the entry was changed.
        /// </summary>
        public static bool RefreshAutostartPath()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return false;
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                if (key?.GetValue(AppRegistryName) is not string current) return false;   // autostart is off

                string wanted = $"\"{exePath}\" --tray";
                if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)) return false;

                // Only fix a broken entry: if the registered exe still exists (another copy of the app, e.g. a test
                // build started from a different folder), leave it alone.
                string registered = current.TrimStart();
                registered = registered.StartsWith("\"")
                    ? registered.Substring(1, Math.Max(0, registered.IndexOf('"', 1) - 1))
                    : registered.Split(' ')[0];
                if (registered.Length > 0 && System.IO.File.Exists(registered)) return false;

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
