using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// "Launch with HDR":  SmartHdrManager.exe --launch "C:\Games\Game\game.exe" [game arguments]
    /// Steam launch option: "C:\Tools\Smart HDR Manager\SmartHdrManager.exe" --launch %command%
    ///
    /// Some games only check at startup whether Windows HDR is on and hide their HDR option otherwise. Switching HDR
    /// when the game process appears is too late for them. This launcher turns HDR on first, waits until Windows
    /// reports it on, then starts the game and keeps HDR on until the game has closed.
    ///
    /// Security: the program to start is taken ONLY from this process's own command line (a shortcut or a Steam
    /// launch option the user created). The running tray app never receives a path: the launcher only sends it a
    /// "hold HDR" signal, which any program could send anyway – at most it keeps HDR on.
    /// </summary>
    internal static class HdrLauncher
    {
        public const string HoldEventName = @"Local\SmartHdrManager.LaunchHold";
        public const string RefusedEventName = @"Local\SmartHdrManager.LaunchRefused";
        public const string ReleaseEventName = @"Local\SmartHdrManager.LaunchRelease";
        private const string InstanceMutexName = @"Local\SmartHdrManager.SingleInstance";

        private const int SignalIntervalMs = 2000;    // the tray app holds HDR 6 s after the last signal
        private const int WaitForHdrMs = 10000;       // start the game anyway if HDR is not on by then
        private const int SettleAfterSwitchMs = 1500; // give the driver a moment after the switch before the game checks
        private const int ShortRunMs = 15000;         // a "game" that closes this fast was probably a launcher stub …
        private const int StubGraceMs = 15000;        // … so look a little longer for the real game it started

        /// <param name="videoPlayer">
        /// --launch-video (Apps tab › Video players): the program is a video player. HDR ends as soon as its window is closed –
        /// players like MPC-HC keep running for several seconds after that while they shut down.
        /// </param>
        public static int Run(string[] args, int launchIndex, bool videoPlayer = false)
        {
            var target = args.Skip(launchIndex + 1).ToList();
            if (target.Count == 0 || string.IsNullOrWhiteSpace(target[0]))
            {
                ShowError("没有要启动的程序。\n\n用法：\nSmartHdrManager.exe --launch \"C:\\Path\\To\\Game.exe\" [参数]\n\n" +
                          "Steam（游戏 属性 › 启动选项）：\n\"<路径>\\SmartHdrManager.exe\" --launch %command%");
                return 2;
            }

            string file = target[0].Trim().Trim('"');
            var arguments = target.Skip(1).ToList();
            var filter = LoadMonitorFilter();

            using var hold = new EventWaitHandle(false, EventResetMode.AutoReset, HoldEventName);
            using var refused = new EventWaitHandle(false, EventResetMode.AutoReset, RefusedEventName);
            using var release = new EventWaitHandle(false, EventResetMode.AutoReset, ReleaseEventName);
            refused.Reset();

            bool appRunning = IsTrayAppRunning();
            bool turnedOnHere = false;
            Log($"以 HDR 启动：{Path.GetFileName(file)}（托盘应用 {(appRunning ? "正在运行" : "未运行")}）");

            HdrController.QueryHdrState(filter, out _, out bool alreadyOn);
            if (!alreadyOn)
            {
                if (appRunning)
                {
                    hold.Set();   // the tray app switches the right monitors and keeps its own state consistent
                }
                else
                {
                    // Tray app not running: switch HDR here and switch it back after the game
                    turnedOnHere = HdrController.SetHdrState(true, filter);
                    if (!turnedOnHere) Log($"以 HDR 启动：无法开启 HDR（{HdrController.LastError}）");
                }

                if ((appRunning || turnedOnHere) && WaitForHdr(filter, refused, hold, appRunning))
                    Thread.Sleep(SettleAfterSwitchMs);
            }

            // ---- start the game ----
            Process? game;
            var started = Stopwatch.StartNew();
            try
            {
                game = Start(file, arguments);
            }
            catch (Exception ex)
            {
                ShowError($"无法启动该程序：\n{file}\n\n{ex.Message}");
                if (turnedOnHere) HdrController.SetHdrState(false, filter);
                return 1;
            }

            // ---- keep HDR while the game runs ----
            int exitCode = 0;
            bool windowClosed = false;   // video player: its window was open and has been closed – nothing to follow
            if (game != null)
            {
                using (game)
                {
                    if (videoPlayer)
                    {
                        windowClosed = WaitForPlayerWindowClosed(game, hold, appRunning);
                    }
                    else
                    {
                        while (!game.WaitForExit(SignalIntervalMs))
                            if (appRunning) hold.Set();
                    }
                    try { if (game.HasExited) exitCode = game.ExitCode; } catch { }
                }
            }

            // Launcher stubs (Ubisoft, Rockstar, some Steam games) start the real game and exit at once. Look for the
            // real game – any program from the game's own folder – and keep HDR on until it has closed too.
            // Switching HDR off under a running game could make it flicker or crash.
            if (!windowClosed && (game == null || started.ElapsedMilliseconds < ShortRunMs))
                FollowRealGame(GameFolder(file), hold, appRunning, videoPlayer);

            // Done: tell the tray app right away, so it switches back to SDR now instead of waiting for the hold to run out
            if (appRunning) release.Set();
            if (turnedOnHere) HdrController.SetHdrState(false, filter);
            Log("以 HDR 启动：程序已关闭。");
            return exitCode;
        }

        /// <summary>
        /// After a launcher stub exited: waits up to <see cref="StubGraceMs"/> for a program from the game folder and
        /// keeps HDR held while it runs (also when it starts the next one). Only process names are compared –
        /// no handles are opened to the game, so anti-cheat systems see nothing unusual.
        /// </summary>
        private static void FollowRealGame(string? folder, EventWaitHandle hold, bool appRunning, bool videoPlayer)
        {
            var names = folder == null ? new HashSet<string>() : GameExeNames(folder);
            var grace = Stopwatch.StartNew();
            while (grace.ElapsedMilliseconds < StubGraceMs)
            {
                string? running = names.Count > 0 ? FindRunning(names) : null;
                if (running == null)
                {
                    if (appRunning) hold.Set();
                    Thread.Sleep(SignalIntervalMs);
                    continue;
                }

                Log($"以 HDR 启动：正在跟踪「{running}」（由启动器启动）");
                var ran = Stopwatch.StartNew();
                if (videoPlayer)
                {
                    // e.g. MPC-HC was already open and took over the file: follow its window, not its process
                    var proc = Process.GetProcessesByName(running).FirstOrDefault();
                    if (proc != null)
                    {
                        using (proc) WaitForPlayerWindowClosed(proc, hold, appRunning);
                        return;
                    }
                }
                while (IsRunning(running))
                {
                    if (appRunning) hold.Set();
                    Thread.Sleep(SignalIntervalMs);
                }
                if (ran.ElapsedMilliseconds >= ShortRunMs) return;   // the real game ran and has closed: done
                grace.Restart();                                    // another short step: look for the next program
            }
        }

        /// <summary>
        /// Video players: waits until the player has no visible main window any more (or has exited). A player that
        /// is minimized still counts as open. Short gaps (e.g. switching to full screen recreates the window) are ignored.
        /// </summary>
        /// <returns>True if the player had a window that was closed (false: it exited without ever showing one).</returns>
        private static bool WaitForPlayerWindowClosed(Process player, EventWaitHandle hold, bool appRunning)
        {
            const int PollMs = 250;
            const int WindowGoneMs = 1000;
            bool sawWindow = false;
            var signal = Stopwatch.StartNew();
            var gone = new Stopwatch();
            while (!player.WaitForExit(PollMs))
            {
                if (appRunning && signal.ElapsedMilliseconds >= SignalIntervalMs)
                {
                    hold.Set();
                    signal.Restart();
                }

                bool hasWindow;
                try
                {
                    player.Refresh();
                    hasWindow = player.MainWindowHandle != IntPtr.Zero;
                }
                catch
                {
                    return sawWindow;
                }

                if (hasWindow)
                {
                    sawWindow = true;
                    gone.Reset();
                }
                else if (sawWindow)
                {
                    if (!gone.IsRunning) gone.Start();
                    if (gone.ElapsedMilliseconds >= WindowGoneMs)
                    {
                        Log("以 HDR 启动：播放器窗口已关闭。");
                        return true;
                    }
                }
            }
            return sawWindow;
        }

        /// <summary>The game's folder: the exe's folder, or its parent when the exe sits in a "bin\x64"-style subfolder.</summary>
        internal static string? GameFolder(string exePath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
                string[] binNames = { "bin", "bin64", "x64", "x86", "win64", "win32", "binaries", "retail" };
                for (int i = 0; i < 3 && dir != null; i++)
                {
                    string name = Path.GetFileName(dir);
                    if (!binNames.Contains(name, StringComparer.OrdinalIgnoreCase)) break;
                    dir = Path.GetDirectoryName(dir);
                }
                // Never a drive root or a shared folder like "S:\Games": that would match other programs
                if (dir == null || dir.TrimEnd('\\').IndexOf('\\') < 0) return null;
                string[] shared = { "games", "spiele", "program files", "program files (x86)", "steamlibrary", "common", "xboxgames", "epic games", "gog games" };
                if (shared.Contains(Path.GetFileName(dir.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase)) return null;
                return dir;
            }
            catch
            {
                return null;
            }
        }

        internal static HashSet<string> GameExeNames(string folder)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
                foreach (string exe in Directory.EnumerateFiles(folder, "*.exe", options))
                {
                    string name = Path.GetFileNameWithoutExtension(exe);
                    // installers / crash reporters may linger after the game closed
                    if (name.Contains("unins", StringComparison.OrdinalIgnoreCase) || name.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("redist", StringComparison.OrdinalIgnoreCase) || name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                        continue;
                    names.Add(name);
                }
            }
            catch { }
            return names;
        }

        private static string? FindRunning(HashSet<string> names)
        {
            int own = Environment.ProcessId;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != own && names.Contains(p.ProcessName)) return p.ProcessName;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return null;
        }

        private static bool IsRunning(string processName)
        {
            var list = Process.GetProcessesByName(processName);
            foreach (var p in list) p.Dispose();
            return list.Length > 0;
        }

        /// <summary>Waits until the selected monitors are in HDR. Returns true if HDR came on.</summary>
        private static bool WaitForHdr(MonitorFilter filter, EventWaitHandle refused, EventWaitHandle hold, bool appRunning)
        {
            var sw = Stopwatch.StartNew();
            long nextSignal = SignalIntervalMs;
            while (sw.ElapsedMilliseconds < WaitForHdrMs)
            {
                // The tray app refuses while it is paused, SDR is forced or a recording app set to "No HDR" runs
                if (refused.WaitOne(200))
                {
                    Log("以 HDR 启动：托盘应用当前保持 SDR（已暂停、已强制 SDR，或有设置为「从不 HDR」的录屏应用正在运行）– 将不以 HDR 启动。");
                    return false;
                }
                if (HdrController.QueryHdrState(filter, out _, out bool allOn) && allOn) return true;
                if (appRunning && sw.ElapsedMilliseconds >= nextSignal)
                {
                    hold.Set();
                    nextSignal += SignalIntervalMs;
                }
            }
            Log("以 HDR 启动：10 秒内 HDR 未开启 – 仍将继续启动游戏。");
            return false;
        }

        private static Process? Start(string file, List<string> arguments)
        {
            bool isExe = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            if (isExe)
            {
                if (!File.Exists(file)) throw new FileNotFoundException("未找到文件。", file);
                var psi = new ProcessStartInfo(file)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(file) ?? string.Empty
                };
                foreach (string a in arguments) psi.ArgumentList.Add(a);
                return Process.Start(psi);
            }

            // Shortcut (.lnk), URL (steam://rungameid/…) or anything else Windows knows how to open
            var shell = new ProcessStartInfo(file) { UseShellExecute = true };
            if (arguments.Count > 0) shell.Arguments = string.Join(" ", arguments.Select(Quote));
            var p = Process.Start(shell);
            return p;
        }

        private static string Quote(string a) =>
            a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

        private static bool IsTrayAppRunning()
        {
            if (!Mutex.TryOpenExisting(InstanceMutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }

        /// <summary>The monitors chosen in Settings › Monitors (all HDR monitors if config.json cannot be read).</summary>
        private static MonitorFilter LoadMonitorFilter()
        {
            try
            {
                if (File.Exists(HdrEngine.ConfigPath))
                {
                    var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(HdrEngine.ConfigPath));
                    if (c != null) return new MonitorFilter(c.OnlyPrimaryMonitor, c.SelectedMonitors);
                }
            }
            catch { }
            return new MonitorFilter(true, null);   // same as the default setting: primary monitor
        }

        private static void Log(string message) =>
            HdrEngine.WriteFileLog($"[{DateTime.Now:HH:mm:ss}] LAUNCH {message}");

        private static void ShowError(string text) =>
            MessageBox.Show(text, "Smart HDR Manager – 以 HDR 启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
