using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SmartHdrManager
{
    /// <summary>Window, fullscreen and foreground-process detection.</summary>
    public static class WindowDetector
    {
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [ThreadStatic]
        private static StringBuilder? _titleBuffer;

        private static readonly object PidLock = new();
        private static IntPtr _cachedHwnd;
        private static uint _cachedPid;
        private static string _cachedName = string.Empty;

        /// <summary>
        /// Process name, window title, fullscreen state and process id of the window in front
        /// (empty strings and 0 when there is none).
        /// </summary>
        public static (string ProcessName, string WindowTitle, bool IsFullscreen, int ProcessId) GetActiveWindowInfo()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return (string.Empty, string.Empty, false, 0);

            // One reusable buffer per thread instead of a new StringBuilder 10x per second
            var title = _titleBuffer ??= new StringBuilder(512);
            title.Clear();
            GetWindowText(hWnd, title, title.Capacity);

            GetWindowThreadProcessId(hWnd, out uint pid);
            string processName = GetProcessNameCached(hWnd, pid);
            bool isFullscreen = !IsShellProcess(processName) && IsWindowFullscreen(hWnd);

            return (processName, title.ToString(), isFullscreen, (int)pid);
        }

        /// <summary>The desktop and the search window are never "fullscreen content".</summary>
        private static bool IsShellProcess(string processName) =>
            processName is "explorer" or "searchhost";

        /// <summary>
        /// Process.GetProcessById opens a process handle and builds a Process object. The foreground process rarely
        /// changes, so the name is only looked up again when the foreground window or its PID changes
        /// (keying on the window as well avoids returning a stale name if Windows reuses a PID).
        /// </summary>
        private static string GetProcessNameCached(IntPtr hWnd, uint pid)
        {
            if (pid == 0) return string.Empty;

            lock (PidLock)
            {
                if (hWnd == _cachedHwnd && pid == _cachedPid && _cachedName.Length > 0)
                    return _cachedName;
            }

            string name = ResolveProcessName(pid);

            if (name.Length > 0)
            {
                lock (PidLock)
                {
                    _cachedHwnd = hWnd;
                    _cachedPid = pid;
                    _cachedName = name;
                }
            }
            return name;
        }

        private static string ResolveProcessName(uint pid)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                return process.ProcessName.ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>True when the window covers its whole monitor (no windowed border).</summary>
        public static bool IsWindowFullscreen(IntPtr hWnd)
        {
            if (!GetWindowRect(hWnd, out RECT window)) return false;

            IntPtr hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            var monitor = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref monitor)) return false;

            int windowWidth = window.Right - window.Left;
            int windowHeight = window.Bottom - window.Top;
            int monitorWidth = monitor.rcMonitor.Right - monitor.rcMonitor.Left;
            int monitorHeight = monitor.rcMonitor.Bottom - monitor.rcMonitor.Top;
            return windowWidth >= monitorWidth && windowHeight >= monitorHeight;
        }
    }
}
