using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SmartHdrManager
{
    public sealed partial class HdrEngine
    {
        private const int MaxFollowExeNames = 150;

        private static readonly string[] ExcludedExeParts =
        {
            "unins", "uninst", "setup", "install", "redist", "vcredist", "vc_redist", "dxsetup", "directx", "dotnet",
            "crash", "report", "bugsplat", "sentry", "easyanticheat", "eac_", "battleye", "beservice", "anticheat",
            "prereq", "cleanup", "touchup", "helper", "updater", "update", "cefprocess", "webhelper", "quicksfv",
            "dxwebsetup", "physx", "ue4prereq", "ueprereq", "unitycrashhandler", "crs-handler", "cmd", "python", "netfx",
            "companion", "supporttool", "watchdog", "pbsvc", "punkbuster", "javaw"
        };

        private static readonly HashSet<string> KnownLaunchers = new(StringComparer.OrdinalIgnoreCase)
        {
            "redprelauncher", "redlauncher", "gtavlauncher", "playgtav", "playrdr2", "rdr2launcher", "launcher",
            "startprotectedgame", "launcherpatcher", "gamelauncher", "bootstrapper", "dowser", "playlan", "ffxivboot",
            "ffxivboot64", "edlaunch", "runme"
        };

        /// <summary>
        /// Runs after every process scan. When a watched HDR game closes within <see cref="LauncherShortRunMs"/> of
        /// appearing, it was probably only a launcher (REDprelauncher, PlayGTAV, …). For up to
        /// <see cref="LauncherFollowMs"/> HDR stays on (if it was on) and the app looks for a program from the same
        /// game folder. If one runs, it is followed and remembered (LauncherGames). Only process names are compared –
        /// no handles are opened to the game (anti-cheat safe).
        /// </summary>
        private void TrackLauncherFollow(Snapshot snapshot, long now)
        {
            string? game = _runningGame;
            if (game != null)
            {
                if (!string.Equals(game, _seenGame, StringComparison.OrdinalIgnoreCase))
                {
                    _seenGame = game;
                    _seenGameSinceMs = _launcherTrackingStarted ? now : now - LauncherShortRunMs;
                }
                _launcherTrackingStarted = true;
                if (_followLauncher != null) EndFollow();

                if (_quickExitGame != null && string.Equals(game, _quickExitGame, StringComparison.OrdinalIgnoreCase))
                    ReportQuickExit();
                _quickExitGame = null;

                if (_hdrOnForGame != null && string.Equals(game, _hdrOnForGame, StringComparison.OrdinalIgnoreCase) &&
                    now - _hdrOnForGameAtMs > QuickExitClearMs)
                {
                    ClearQuickExit(game);
                    _hdrOnForGame = null;
                }
                return;
            }

            _launcherTrackingStarted = true;
            if (_seenGame != null)
            {
                string closed = _seenGame;
                long ranMs = now - _seenGameSinceMs;
                _seenGame = null;

                if (_hdrOnForGame != null && string.Equals(closed, _hdrOnForGame, StringComparison.OrdinalIgnoreCase) &&
                    now - _hdrOnForGameAtMs <= QuickExitMs && !IsLauncher(closed))
                {
                    _quickExitGame = closed;
                    _quickExitAfterMs = now - _hdrOnForGameAtMs;
                }
                _hdrOnForGame = null;

                if (_followLauncher == null && ranMs < LauncherShortRunMs && !Paused)
                    StartFollow(snapshot, now, closed, ranMs);

                if (_quickExitGame != null && _followLauncher == null) ReportQuickExit();
            }

            if (_followLauncher == null) return;

            var names = _followNames;
            string? child = names == null ? null : FindRunningFromFolder(names, _followLauncher, _followNotBefore);
            if (child != null)
            {
                string launcher = _followLauncher;
                EndFollow();
                _quickExitGame = null;
                LearnLauncherGame(launcher, child);

                _runningGame = child;
                _activeHdrGameProcess = child;
                _seenGame = child;
                _seenGameSinceMs = now;
            }
            else if (now >= _followUntilMs)
            {
                Log("HDR", $"'{_followLauncher}' 在 {LauncherFollowMs / 1000} 秒内未启动游戏 – 恢复正常切换。");
                EndFollow();
                if (_quickExitGame != null) ReportQuickExit();
            }
        }

        private void StartFollow(Snapshot snapshot, long now, string closed, long ranMs)
        {
            if (!snapshot.HdrGames.Contains(closed) || ProcessNames.IsRunning(closed)) return;

            string? exe = KnownExePath(closed);
            string? folder = exe == null ? null : HdrLauncher.GameFolder(exe);
            if (folder == null) return;

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0 && folder.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return;

            _followLauncher = closed;
            _followUntilMs = now + LauncherFollowMs;
            _followNotBefore = DateTime.Now - TimeSpan.FromMilliseconds(Math.Max(0, ranMs)) - TimeSpan.FromSeconds(3);
            _followHoldsHdr = _hdrAny;
            _followNames = null;
            int generation = ++_followGeneration;

            Log("HDR", $"'{closed}' 运行 {Math.Max(1, ranMs / 1000)} 秒后关闭 – 它可能是一个启动器。 " +
                       (_hdrAny ? $"在等待它启动的游戏期间，最长保持 HDR 开启 {LauncherFollowMs / 1000} 秒。"
                                : $"监视它启动的游戏，最长 {LauncherFollowMs / 1000} 秒。"));

            Task.Run(() =>
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string name in HdrLauncher.GameExeNames(folder))
                    {
                        if (name.Equals(closed, StringComparison.OrdinalIgnoreCase) || IsExcludedExeName(name)) continue;
                        names.Add(name);
                    }
                }
                catch { }

                if (names.Count > MaxFollowExeNames) names.Clear();
                if (Volatile.Read(ref _followGeneration) == generation) _followNames = names;
            });
        }

        private void EndFollow()
        {
            _followLauncher = null;
            _followNames = null;
            _followHoldsHdr = false;
            Interlocked.Increment(ref _followGeneration);
        }

        /// <summary>
        /// Some games crash when Windows switches to HDR while they start (seen with NVIDIA RTX HDR). The first time, a
        /// HINT goes to the log; from the second time on also a notification.
        /// </summary>
        private void ReportQuickExit()
        {
            string? game = _quickExitGame;
            _quickExitGame = null;
            if (game == null) return;

            int seconds = (int)Math.Max(1, Math.Round(_quickExitAfterMs / 1000.0));

            int count;
            lock (Sync)
            {
                Config.QuickExitCounts.TryGetValue(game, out count);
                count = Math.Min(MaxQuickExitCount, count + 1);
                Config.QuickExitCounts[game] = count;
                Save();
            }

            Log("HINT", $"'{game}' 在为其开启 HDR 后 {seconds} 秒关闭。如果它崩溃了：请关闭 NVIDIA RTX HDR " +
                        "（NVIDIA App › 图形 › 全局设置 › RTX HDR），或使用 游戏 › 选项 › 以 HDR 启动。");
            if (count >= 2)
                _notifications.Enqueue(($"是否 {game} 崩溃了？",
                    $"它在 HDR 开启后 {seconds} 秒关闭了（共 {count} 次）。请在 NVIDIA App 中关闭 RTX HDR，或使用以 HDR 启动。"));
        }

        private bool IsLauncher(string process)
        {
            if (KnownLaunchers.Contains(process)) return true;
            lock (Sync) return Config.LauncherGames.ContainsKey(process);
        }

        private void ClearQuickExit(string game)
        {
            lock (Sync)
            {
                if (Config.QuickExitCounts.Remove(game)) Save();
            }
        }

        private static bool IsExcludedExeName(string name)
        {
            foreach (string part in ExcludedExeParts)
                if (name.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Tray notifications from the engine (crash hint). Called by the tray timer on the UI thread.</summary>
        public bool TryTakeNotification(out string title, out string text)
        {
            if (_notifications.TryDequeue(out var notification))
            {
                title = notification.Title;
                text = notification.Text;
                return true;
            }
            title = text = string.Empty;
            return false;
        }

        /// <summary>Remembers launcher -> game and turns the game on for HDR.</summary>
        private void LearnLauncherGame(string launcher, string game)
        {
            lock (Sync)
            {
                if (!Config.LauncherGames.TryGetValue(launcher, out var games))
                    Config.LauncherGames[launcher] = games = new List<string>();
                if (!games.Contains(game, Cmp)) games.Add(game);

                if (!Config.HdrGamesAndApps.Contains(game, Cmp))
                {
                    Config.HdrGamesAndApps.Add(game);
                    Config.HdrGamesAndApps.Sort(Cmp);
                }

                foreach (var libraryGame in Config.LibraryGames)
                    if (libraryGame.Processes.Contains(launcher, Cmp) && !libraryGame.Processes.Contains(game, Cmp))
                        libraryGame.Processes.Add(game);

                InvalidateSnapshot();
                Save();
            }
            Log("HDR", $"正在跟踪 '{game}'（由 '{launcher}' 启动）。已记住：下次 '{game}' 一启动 HDR 就保持开启。");
        }
    }
}
