using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SmartHdrManager
{
    /// <summary>
    /// Exe path of another process using only PROCESS_QUERY_LIMITED_INFORMATION (the same access Task Manager uses).
    /// Never reads the process's memory (Process.MainModule does), which matters for games protected by kernel
    /// anti-cheat systems: they watch for handles with memory access to the game.
    /// </summary>
    public static class ProcessPaths
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int PathBufferSize = 1024;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static string? TryGetExePath(int processId)
        {
            if (processId <= 0) return null;

            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
            if (handle == IntPtr.Zero) return null;

            try
            {
                var buffer = new StringBuilder(PathBufferSize);
                uint size = (uint)buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }

    /// <summary>Process name of another process: exe name without ".exe", lower case.</summary>
    public static class ProcessNames
    {
        /// <summary>True while at least one process with this name runs.</summary>
        public static bool IsRunning(string processName)
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName(processName);
                foreach (var process in processes) process.Dispose();
                return processes.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>The real Windows version for the log, e.g. "Windows 11 25H2 (build 26200.9711)".</summary>
    public static class WindowsVersion
    {
        private const int WINDOWS_11_BUILD = 22000;

        private static string? _cached;

        public static string Describe()
        {
            if (_cached != null) return _cached;
            string result;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                string display = (key?.GetValue("DisplayVersion") as string ?? string.Empty).Trim();   // "25H2"
                string buildText = (key?.GetValue("CurrentBuild") as string ?? string.Empty).Trim();   // "26200"
                object? ubr = key?.GetValue("UBR");                                                     // 9711
                int build = int.TryParse(buildText, out int parsed) ? parsed : Environment.OSVersion.Version.Build;
                // "ProductName" still says "Windows 10" on Windows 11, so the build number decides
                string name = build >= WINDOWS_11_BUILD ? "Windows 11" : "Windows 10";
                string fullBuild = ubr is int updateBuild ? $"{build}.{updateBuild}" : build.ToString();
                result = display.Length > 0 ? $"{name} {display} (build {fullBuild})" : $"{name} (build {fullBuild})";
            }
            catch
            {
                result = $"Windows (build {Environment.OSVersion.Version.Build})";
            }
            return _cached = result;
        }
    }
}
