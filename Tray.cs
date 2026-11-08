using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
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
        private readonly ToolStripMenuItem _pauseItem;
        private readonly ToolStripMenuItem _autostartItem;
        private bool _syncing;

        public TrayAndStartupManager(Action openMainWindow, Action<bool> setPaused, Action exit,
                                     Action<HdrOverride, TimeSpan?> setOverride, Func<(HdrOverride Mode, bool Timed)> getOverride,
                                     Func<string> overrideShortcut)
        {
            var openItem = new ToolStripMenuItem("打开 Smart HDR Manager") { Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
            openItem.Click += (_, _) => openMainWindow();

            _pauseItem = new ToolStripMenuItem("暂停自动化") { CheckOnClick = true };
            _pauseItem.CheckedChanged += (_, _) =>
            {
                if (!_syncing) setPaused(_pauseItem.Checked);
            };

            _autostartItem = new ToolStripMenuItem("开机自启动");
            _autostartItem.Click += (_, _) =>
            {
                if (!SetAutostart(!IsAutostartEnabled()))
                    _icon!.ShowBalloonTip(3000, "Smart HDR Manager", "无法更新 Windows 启动项。", ToolTipIcon.Warning);
            };

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (_, _) => exit();

            var renderer = new DarkMenuRenderer();

            // Manual override submenu (also: the override shortcut, Win+Alt+O by default, cycles automatic -> HDR on -> SDR -> automatic)
            var overrideMenu = new ToolStripMenuItem("HDR 覆盖");
            var autoItem = new ToolStripMenuItem("自动（无覆盖）");
            var hdrHour = new ToolStripMenuItem("HDR 开启 1 小时");
            var hdrUntil = new ToolStripMenuItem("HDR 开启直至恢复");
            var sdrHour = new ToolStripMenuItem("SDR 1 小时");
            var sdrUntil = new ToolStripMenuItem("SDR 直至恢复");
            var hotkeyHint = new ToolStripMenuItem("快捷键") { Enabled = false };
            autoItem.Click += (_, _) => setOverride(HdrOverride.None, null);
            hdrHour.Click += (_, _) => setOverride(HdrOverride.ForceHdr, TimeSpan.FromHours(1));
            hdrUntil.Click += (_, _) => setOverride(HdrOverride.ForceHdr, null);
            sdrHour.Click += (_, _) => setOverride(HdrOverride.ForceSdr, TimeSpan.FromHours(1));
            sdrUntil.Click += (_, _) => setOverride(HdrOverride.ForceSdr, null);
            overrideMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                autoItem, new ToolStripSeparator(), hdrHour, hdrUntil, sdrHour, sdrUntil, new ToolStripSeparator(), hotkeyHint
            });
            overrideMenu.DropDown.Renderer = renderer;
            overrideMenu.DropDown.BackColor = Theme.Surface;
            overrideMenu.DropDown.ForeColor = Theme.Text;
            overrideMenu.DropDownOpening += (_, _) =>
            {
                var (mode, timed) = getOverride();
                string shortcut = overrideShortcut();
                hotkeyHint.Text = shortcut.Length > 0 ? $"快捷键: {shortcut}" : "未设置快捷键";
                autoItem.Checked = mode == HdrOverride.None;
                hdrHour.Checked = mode == HdrOverride.ForceHdr && timed;
                hdrUntil.Checked = mode == HdrOverride.ForceHdr && !timed;
                sdrHour.Checked = mode == HdrOverride.ForceSdr && timed;
                sdrUntil.Checked = mode == HdrOverride.ForceSdr && !timed;
            };

            var menu = new ContextMenuStrip
            {
                Renderer = renderer,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                ShowImageMargin = true
            };
            menu.Items.Add(openItem);
            menu.Items.Add(overrideMenu);
            menu.Items.Add(_pauseItem);
            menu.Items.Add(_autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            menu.Opening += (_, _) =>
            {
                _autostartItem.Checked = IsAutostartEnabled();
            };

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

            // Left click / double click opens the window; right click shows the menu.
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) openMainWindow();
            };
        }

        public void SetPaused(bool paused)
        {
            _syncing = true;
            try { _pauseItem.Checked = paused; }
            finally { _syncing = false; }
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

                // Only repair a broken entry: if the registered exe still exists (another copy of the app, e.g. a test
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

    // =========================================================================
    // GLOBAL HOTKEYS (Win+Alt or Ctrl+Alt + H / I / O) via RegisterHotKey
    // Event-driven instead of polling the keyboard 10x per second, and never misses a short key press.
    // Lives on the UI thread (a hidden message-only window); the actions are posted to the engine thread.
    // If another program already owns one of the shortcuts, registration fails and the engine falls back to polling.
    // =========================================================================
    public sealed class GlobalHotkeys : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_H = 0x48, VK_I = 0x49, VK_O = 0x4F;
        private const int ID_ADD = 1, ID_IGNORE = 2, ID_OVERRIDE = 3;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly Action _onAdd;
        private readonly Action _onIgnore;
        private readonly Action _onOverride;
        private readonly Action<int>? _onBrightnessHotkey; // TV brightness hotkey callback

        public bool Registered { get; }           // H and I shortcuts
        public bool OverrideRegistered { get; }   // O shortcut (registered on its own, so a conflict only affects it)
        public bool TvHotkeysRegistered { get; private set; }   // TV brightness shortcuts

        /// <param name="modifiers">HotkeySettings.ModifierFlags(...): Win+Alt (default) or Ctrl+Alt</param>
        public GlobalHotkeys(uint modifiers, Action onAdd, Action onIgnore, Action onOverride, Action<int>? onBrightnessHotkey = null)
        {
            _onAdd = onAdd;
            _onIgnore = onIgnore;
            _onOverride = onOverride;
            _onBrightnessHotkey = onBrightnessHotkey;

            CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

            bool add = RegisterHotKey(Handle, ID_ADD, modifiers | MOD_NOREPEAT, VK_H);
            bool ignore = RegisterHotKey(Handle, ID_IGNORE, modifiers | MOD_NOREPEAT, VK_I);
            OverrideRegistered = RegisterHotKey(Handle, ID_OVERRIDE, modifiers | MOD_NOREPEAT, VK_O);

            Registered = add && ignore;
            if (!Registered)
            {
                // H and I together, so the polling fallback handles both consistently
                if (add) UnregisterHotKey(Handle, ID_ADD);
                if (ignore) UnregisterHotKey(Handle, ID_IGNORE);
            }

            // Register TV brightness hotkeys: Alt+↑/↓ + Alt+0-9
            if (_onBrightnessHotkey != null)
            {
                TvHotkeysRegistered = RegisterTvHotkeys(modifiers);
            }
        }

        private bool RegisterTvHotkeys(uint modifiers)
        {
            string combo = ModifierText(modifiers);
            HdrEngine.WriteFileLog($"[TV-HOTKEY] register begin mods=0x{modifiers:X} combo={combo} handle={Handle}");
            bool allOk = true;
            allOk &= RegisterTvKey(2001, modifiers, 0x26, combo, "step_up");
            allOk &= RegisterTvKey(2002, modifiers, 0x28, combo, "step_down");
            for (int i = 0; i <= 9; i++)
            {
                RegisterTvKey(2010 + i, modifiers, (uint)(0x30 + i), combo, $"set_{i * 10}");
            }
            HdrEngine.WriteFileLog($"[TV-HOTKEY] register end allOk={allOk} combo={combo}");
            return allOk;
        }

        private bool RegisterTvKey(int id, uint modifiers, uint vk, string combo, string name)
        {
            bool ok = RegisterHotKey(Handle, id, modifiers | MOD_NOREPEAT, vk);
            if (ok)
                HdrEngine.WriteFileLog($"[TV-HOTKEY] ok id={id} key={combo}vk=0x{vk:X2} name={name}");
            else
                HdrEngine.WriteFileLog($"[TV-HOTKEY] FAIL id={id} key={combo}vk=0x{vk:X2} name={name} err={Marshal.GetLastWin32Error()}");
            return ok;
        }

        private static string ModifierText(uint modifiers)
        {
            string s = string.Empty;
            if ((modifiers & HotkeySettings.MOD_WIN) != 0) s += "Win+";
            if ((modifiers & HotkeySettings.MOD_CONTROL) != 0) s += "Ctrl+";
            if ((modifiers & HotkeySettings.MOD_ALT) != 0) s += "Alt+";
            return s.Length == 0 ? "None+" : s;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == ID_ADD) _onAdd();
                else if (id == ID_IGNORE) _onIgnore();
                else if (id == ID_OVERRIDE) _onOverride();
                else if ((id is 2001 or 2002) || (id >= 2010 && id <= 2019)) DispatchTvBrightness(id);
                else HdrEngine.WriteFileLog($"[TV-HOTKEY] unmapped wm_hotkey id={id}");
                return;
            }
            base.WndProc(ref m);
        }

        private void DispatchTvBrightness(int id)
        {
            try
            {
                int arg;
                string name;
                if (id == 2001) { arg = 1; name = "step_up"; }
                else if (id == 2002) { arg = -1; name = "step_down"; }
                else if (id >= 2010 && id <= 2019)
                {
                    arg = (id - 2010) * 10; // id=2010 -> 0, id=2011 -> 10, ..., id=2019 -> 90
                    name = $"set_{arg}";
                }
                else
                {
                    HdrEngine.WriteFileLog($"[TV-HOTKEY] 未识别的快捷键 ID: {id}");
                    return;
                }
                
                HdrEngine.WriteFileLog($"[TV-HOTKEY] 快捷键触发 id={id} name={name} arg={arg} handler={_onBrightnessHotkey != null}");
                
                if (_onBrightnessHotkey == null)
                {
                    HdrEngine.WriteFileLog("[TV-HOTKEY] 错误: 亮度快捷键回调函数未设置");
                    return;
                }
                
                _onBrightnessHotkey(arg);
                HdrEngine.WriteFileLog($"[TV-HOTKEY] 回调执行成功");
            }
            catch (Exception ex)
            {
                HdrEngine.WriteFileLog($"[TV-HOTKEY] 处理快捷键异常: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            if (Registered)
            {
                UnregisterHotKey(Handle, ID_ADD);
                UnregisterHotKey(Handle, ID_IGNORE);
            }
            if (OverrideRegistered) UnregisterHotKey(Handle, ID_OVERRIDE);
            if (TvHotkeysRegistered)
            {
                UnregisterHotKey(Handle, 2001);
                UnregisterHotKey(Handle, 2002);
                for (int i = 0; i <= 9; i++) UnregisterHotKey(Handle, 2010 + i);
            }
            DestroyHandle();
        }
    }

    // =========================================================================
    // OPTIONAL: hide a *console* window from the taskbar and Alt-Tab.
    // Only relevant if you build the project as OutputType=Exe (console subsystem). With the default
    // OutputType=WinExe there is no console, GetConsoleWindow() returns 0, and every method below does nothing.
    //
    // Why these styles: a top-level window is listed in the taskbar/Alt-Tab when it is visible and has
    // WS_EX_APPWINDOW (or is an un-owned window without WS_EX_TOOLWINDOW). WS_EX_TOOLWINDOW removes it from both.
    // The style must be changed while the window is hidden, then SWP_FRAMECHANGED makes the shell re-read it.
    //
    // Limitation: under Windows Terminal (default terminal host on Windows 11) GetConsoleWindow() returns a hidden
    // pseudo-console window, and the visible Terminal window belongs to another process that cannot be restyled.
    // That is why WinExe is the recommended setup.
    // =========================================================================
    public static class ConsoleWindowHider
    {
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_APPWINDOW = 0x00040000L;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const int SW_HIDE = 0;
        private const int SW_RESTORE = 9;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
                           SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        public static bool HasConsole => GetConsoleWindow() != IntPtr.Zero;

        /// <summary>Hide the console and take it out of the taskbar / Alt-Tab list.</summary>
        public static void HideCompletely()
        {
            IntPtr hWnd = GetConsoleWindow();
            if (hWnd == IntPtr.Zero) return;

            ShowWindow(hWnd, SW_HIDE);                                   // 1. hide first
            long ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            ex = (ex & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW;             // 2. tool window, not an app window
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(ex));
            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,                  // 3. make the shell re-read the style
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        /// <summary>Bring the console back (debugging). Restores the normal taskbar/Alt-Tab behaviour.</summary>
        public static void ShowAgain()
        {
            IntPtr hWnd = GetConsoleWindow();
            if (hWnd == IntPtr.Zero) return;

            long ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            ex = (ex & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW;
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(ex));
            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            ShowWindow(hWnd, SW_RESTORE);
        }

        /// <summary>
        /// Call periodically (the app calls it from a 500 ms timer). A console cannot be subclassed from outside its
        /// host process, so "minimize" is intercepted by noticing the iconic state and hiding the window.
        /// </summary>
        public static void HideIfMinimized()
        {
            IntPtr hWnd = GetConsoleWindow();
            if (hWnd != IntPtr.Zero && IsIconic(hWnd))
                ShowWindow(hWnd, SW_HIDE);
        }
    }
}

