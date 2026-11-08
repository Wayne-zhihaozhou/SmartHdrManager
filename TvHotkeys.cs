using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// 全局快捷键管理：Alt+↑/↓ 步进调节，Alt+数字键直接设亮度。
    /// 使用 RegisterHotKey / UnregisterHotKey API。
    /// </summary>
    public static class TvHotkeys
    {
        private const int WM_HOTKEY = 0x0312;
        private static readonly List<int> _registeredIds = new();
        private static Action<int> _onHotkey;
        private static bool _isRegistered;

        // 快捷键 ID 映射
        private const int HK_STEP_UP = 1001;
        private const int HK_STEP_DOWN = 1002;
        private const int HK_SET_0 = 1010; // Alt+0 = 亮度 100
        private const int HK_SET_1 = 1011; // Alt+1 = 亮度 10
        private const int HK_SET_2 = 1012; // Alt+2 = 亮度 20
        private const int HK_SET_3 = 1013; // Alt+3 = 亮度 30
        private const int HK_SET_4 = 1014; // Alt+4 = 亮度 40
        private const int HK_SET_5 = 1015; // Alt+5 = 亮度 50
        private const int HK_SET_6 = 1016; // Alt+6 = 亮度 60
        private const int HK_SET_7 = 1017; // Alt+7 = 亮度 70
        private const int HK_SET_8 = 1018; // Alt+8 = 亮度 80
        private const int HK_SET_9 = 1019; // Alt+9 = 亮度 90

        // VK 码
        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;
        private const int VK_0 = 0x30;
        private const int VK_1 = 0x31;
        private const int VK_2 = 0x32;
        private const int VK_3 = 0x33;
        private const int VK_4 = 0x34;
        private const int VK_5 = 0x35;
        private const int VK_6 = 0x36;
        private const int VK_7 = 0x37;
        private const int VK_8 = 0x38;
        private const int VK_9 = 0x39;

        // 修饰键
        private const int MOD_ALT = 0x0001;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>注册所有快捷键。</summary>
        public static void Register(IntPtr hwnd, Action<int> onHotkey)
        {
            if (_isRegistered) Unregister();

            _onHotkey = onHotkey;

            // 注册 Alt+↑/↓
            RegisterKey(hwnd, HK_STEP_UP, MOD_ALT, VK_UP);
            RegisterKey(hwnd, HK_STEP_DOWN, MOD_ALT, VK_DOWN);

            // 注册 Alt+数字键
            for (int i = 0; i <= 9; i++)
            {
                RegisterKey(hwnd, HK_SET_0 + i, MOD_ALT, (uint)(VK_0 + i));
            }

            _isRegistered = true;
            HdrEngine.WriteFileLog("[TV] 快捷键已注册: Alt+↑/↓ 步进, Alt+0-9 直接设亮度");
        }

        /// <summary>注销所有快捷键。</summary>
        public static void Unregister()
        {
            foreach (var id in _registeredIds)
            {
                try { UnregisterHotKey(IntPtr.Zero, id); } catch { }
            }
            _registeredIds.Clear();
            _isRegistered = false;
            _onHotkey = null;
        }

        private static bool RegisterKey(IntPtr hwnd, int id, uint modifiers, uint vk)
        {
            var ok = RegisterHotKey(hwnd, id, modifiers, vk);
            if (ok) _registeredIds.Add(id);
            else
            {
                var err = Marshal.GetLastWin32Error();
                HdrEngine.WriteFileLog($"[TV] 注册快捷键失败: id={id}, vk={vk}, error={err}");
            }
            return ok;
        }

        /// <summary>处理快捷键消息。</summary>
        public static bool ProcessHotKey(int wParam)
        {
            if (!_isRegistered || _onHotkey == null) return false;

            switch (wParam)
            {
                case HK_STEP_UP:
                case HK_STEP_DOWN:
                    _onHotkey(wParam);
                    return true;

                case >= HK_SET_0 and <= HK_SET_9:
                    _onHotkey(wParam);
                    return true;
            }
            return false;
        }

        /// <summary>将快捷键 ID 转换为亮度值。</summary>
        public static int HotKeyIdToBrightness(int id)
        {
            if (id >= HK_SET_0 && id <= HK_SET_9)
            {
                return (id - HK_SET_0) * 10;
            }
            return -1;
        }
    }
}
