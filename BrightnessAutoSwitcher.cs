using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SmartHdrManager
{
    /// <summary>
    /// 自动亮度切换：监控前台进程，匹配 BrightnessRule，自动设置电视亮度。
    /// </summary>
    public static class BrightnessAutoSwitcher
    {
        private static CancellationTokenSource? _cts;
        private static Task? _task;
        private static int _lastBrightness;
        private static string? _lastMatchingRule;
        private static DateTime _lastSwitchTime;
        private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(3);

        // Win32 API for foreground window detection
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // Alt key state check
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int VK_MENU = 0x12; // Alt key

        public static void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            _task = Task.Run(() => RunLoop(_cts.Token));
            HdrEngine.WriteFileLog("[TV] 自动亮度切换已启动");
        }

        public static void Stop()
        {
            _cts?.Cancel();
            _cts = null;
            _task = null;
            HdrEngine.WriteFileLog("[TV] 自动亮度切换已停止");
        }

        private static async Task RunLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await CheckForegroundProcess(ct);
                }
                catch (Exception ex)
                {
                    HdrEngine.WriteFileLog($"[TV] 自动亮度检查异常: {ex.Message}");
                }
                await Task.Delay(2000, ct);
            }
        }

        private static async Task CheckForegroundProcess(CancellationToken ct)
        {
            // Skip while Alt key is pressed (user is switching windows with Alt+Tab)
            if (GetAsyncKeyState(VK_MENU) != 0) return;

            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return;

            if (GetWindowThreadProcessId(foreground, out uint processId) == 0) return;

            var process = Process.GetProcessById((int)processId);
            if (process == null || string.IsNullOrEmpty(process.ProcessName)) return;

            var processName = process.ProcessName.ToLowerInvariant();

            // Get window title for keyword matching
            string windowTitle = process.MainWindowTitle?.ToLowerInvariant() ?? "";

            // Check cooldown
            if ((DateTime.UtcNow - _lastSwitchTime) < Cooldown) return;

            // Find matching rule
            var rule = FindMatchingRule(processName, windowTitle);
            if (rule == null) return;

            int targetBrightness = rule.Brightness;

            // Only switch if brightness changed or different rule matched
            if (targetBrightness != _lastBrightness || _lastMatchingRule != rule.Name)
            {
                _lastBrightness = targetBrightness;
                _lastMatchingRule = rule.Name;
                _lastSwitchTime = DateTime.UtcNow;

                HdrEngine.WriteFileLog($"[TV] 自动亮度: {rule.Name} -> 亮度 {targetBrightness}");
                await TvCli.SetBrightnessAsync(targetBrightness);
                BrightnessOsd.Show(targetBrightness, "自动");
            }
        }

        private static BrightnessRule? FindMatchingRule(string processName, string windowTitle)
        {
            var rules = TvConfig.Rules;
            if (rules == null || rules.Count == 0) return null;

            // Find matching rules (non-fallback first)
            var matches = new List<BrightnessRule>();
            foreach (var rule in rules)
            {
                // Check process match
                bool processMatch = false;
                if (rule.Processes != null && rule.Processes.Count > 0)
                {
                    foreach (var p in rule.Processes)
                    {
                        if (processName.Contains(p.ToLowerInvariant()) ||
                            p.ToLowerInvariant().Contains(processName))
                        {
                            processMatch = true;
                            break;
                        }
                    }
                }

                // Check title keyword match
                bool titleMatch = false;
                if (rule.TitleKeywords != null && rule.TitleKeywords.Count > 0 && !string.IsNullOrEmpty(windowTitle))
                {
                    foreach (var kw in rule.TitleKeywords)
                    {
                        if (windowTitle.Contains(kw.ToLowerInvariant()))
                        {
                            titleMatch = true;
                            break;
                        }
                    }
                }

                // Check ignore list
                bool ignored = false;
                if (rule.IgnoreProcesses != null && rule.IgnoreProcesses.Count > 0)
                {
                    foreach (var ig in rule.IgnoreProcesses)
                    {
                        if (processName.Contains(ig.ToLowerInvariant()))
                        {
                            ignored = true;
                            break;
                        }
                    }
                }

                if (ignored) continue;

                if (processMatch || titleMatch || rule.MatchFullscreen)
                {
                    matches.Add(rule);
                }
            }

            // If no matches, return fallback rule
            if (matches.Count == 0)
            {
                foreach (var rule in rules)
                {
                    if (rule.IsFallback()) return rule;
                }
                return null;
            }

            // Return the first non-fallback match
            foreach (var rule in matches)
            {
                if (!rule.IsFallback()) return rule;
            }
            return matches[0];
        }

        public static void Shutdown()
        {
            Stop();
        }
    }
}
