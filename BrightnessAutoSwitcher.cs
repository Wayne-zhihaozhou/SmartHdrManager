using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// Follows the program in the foreground and applies the brightness rules of the TV configuration.
    /// </summary>
    public static class BrightnessAutoSwitcher
    {
        private const int PollIntervalMs = 2000;
        private const int MinBrightness = 0;
        private const int MaxBrightness = 100;
        private const int StopTimeoutMs = 2000;
        private const string AutoSource = "自动";

        private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(3);
        private static readonly object Sync = new();

        private static CancellationTokenSource? _cts;
        private static Task? _task;
        private static int _lastBrightness = -1;
        private static string? _lastRule;
        private static DateTime _lastSwitchUtc;

        public static void Start(HdrEngine engine)
        {
            lock (Sync)
            {
                if (_cts != null) return;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _task = Task.Run(() => RunLoop(engine, token));
            }
            HdrEngine.WriteFileLog("[TV] 自动亮度切换已启动");
        }

        public static void Stop()
        {
            CancellationTokenSource? cts;
            Task? task;
            lock (Sync)
            {
                cts = _cts;
                task = _task;
                _cts = null;
                _task = null;
            }
            if (cts == null) return;

            cts.Cancel();
            try { task?.Wait(StopTimeoutMs); } catch { }
            cts.Dispose();
            HdrEngine.WriteFileLog("[TV] 自动亮度切换已停止");
        }

        private static async Task RunLoop(HdrEngine engine, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (engine.Read(c => c.Brightness) is { Enabled: true, AutoSwitchEnabled: true } config)
                        await CheckForegroundAsync(config).ConfigureAwait(false);
                    else
                        ResetState();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    HdrEngine.WriteFileLog($"[TV] 自动亮度检查异常: {ex.Message}");
                }

                try { await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private static async Task CheckForegroundAsync(BrightnessConfig config)
        {
            var rules = config.Rules;
            if (rules == null || rules.Count == 0)
            {
                ResetState();
                return;
            }

            // Skip while Alt is pressed: the user is switching windows with Alt+Tab
            if ((Control.ModifierKeys & Keys.Alt) != 0) return;

            var (processName, windowTitle, isFullscreen, _) = WindowDetector.GetActiveWindowInfo();
            if (string.IsNullOrEmpty(processName)) return;

            var rule = FindMatchingRule(rules, processName, windowTitle, isFullscreen);
            if (rule == null)
            {
                ResetState();
                return;
            }

            await ApplyAsync(rule).ConfigureAwait(false);
        }

        private static async Task ApplyAsync(BrightnessRule rule)
        {
            int brightness = Math.Clamp(rule.Brightness, MinBrightness, MaxBrightness);
            if (brightness == _lastBrightness && rule.Name == _lastRule) return;
            if (DateTime.UtcNow - _lastSwitchUtc < Cooldown) return;

            _lastBrightness = brightness;
            _lastRule = rule.Name;
            _lastSwitchUtc = DateTime.UtcNow;

            HdrEngine.WriteFileLog($"[TV] 自动亮度: {rule.Name} -> 亮度 {brightness}");
            if (await TvCli.SetBrightnessAsync(brightness).ConfigureAwait(false))
                BrightnessOsd.Show(brightness, AutoSource);
        }

        private static void ResetState()
        {
            _lastBrightness = -1;
            _lastRule = null;
        }

        /// <summary>
        /// First rule that matches the window. A rule without processes, title keywords and fullscreen matching is the
        /// fallback rule and only applies when nothing else matches.
        /// </summary>
        private static BrightnessRule? FindMatchingRule(IReadOnlyList<BrightnessRule> rules, string processName,
                                                       string windowTitle, bool isFullscreen)
        {
            BrightnessRule? fallback = null;
            foreach (var rule in rules)
            {
                if (rule.IsFallback())
                {
                    fallback ??= rule;
                    continue;
                }

                if (rule.IgnoreProcesses.Any(token => Contains(processName, token))) continue;
                if (!Matches(rule, processName, windowTitle, isFullscreen)) continue;
                return rule;
            }
            return fallback;
        }

        private static bool Matches(BrightnessRule rule, string processName, string windowTitle, bool isFullscreen) =>
            (rule.MatchFullscreen && isFullscreen) ||
            rule.Processes.Any(token => Contains(processName, token)) ||
            rule.TitleKeywords.Any(token => Contains(windowTitle, token));

        private static bool Contains(string text, string token) =>
            token.Length > 0 && text.Contains(token, StringComparison.OrdinalIgnoreCase);
    }
}
