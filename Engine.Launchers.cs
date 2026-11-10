using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HdrBrightControl
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
                return;
            }

            _launcherTrackingStarted = true;
            if (_seenGame != null)
            {
                string closed = _seenGame;
                long ranMs = now - _seenGameSinceMs;
                _seenGame = null;

                _hdrOnForGame = null;

                if (_followLauncher == null && ranMs < LauncherShortRunMs && !Paused)
                    StartFollow(snapshot, now, closed, ranMs);
            }

            if (_followLauncher == null) return;

            var names = _followNames;
            string? child = names == null ? null : FindRunningFromFolder(names, _followLauncher, _followNotBefore);
            if (child != null)
            {
                string launcher = _followLauncher;
                EndFollow();
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
            }
        }

        private void StartFollow(Snapshot snapshot, long now, string closed, long ranMs)
        {
            if (snapshot.Programs.Match(closed) is not { IsHdr: true } || ProcessNames.IsRunning(closed)) return;

            string? exe = KnownExePath(closed);
            string? folder = exe == null ? null : GameFolder(exe);
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
                    foreach (string name in GameExeNames(folder))
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

        private bool IsLauncher(string process)
        {
            if (KnownLaunchers.Contains(process)) return true;
            lock (Sync) return Config.LauncherGames.ContainsKey(process);
        }

        private static bool IsExcludedExeName(string name)
        {
            foreach (string part in ExcludedExeParts)
                if (name.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
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

                InvalidateSnapshot();
                Save();
            }
            Log("HDR", $"正在跟踪 '{game}'（由 '{launcher}' 启动）。已记住：下次 '{game}' 一启动 HDR 就保持开启。");
        }

        /// <summary>The game's folder: the exe's folder, or its parent when the exe sits in a "bin\x64"-style subfolder.</summary>
        private static string? GameFolder(string exePath)
        {
            string[] binNames = { "bin", "bin64", "x64", "x86", "win64", "win32", "binaries", "retail" };
            string[] sharedFolders =
            {
                "games", "spiele", "program files", "program files (x86)", "steamlibrary", "common", "xboxgames",
                "epic games", "gog games"
            };

            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
                for (int i = 0; i < 3 && dir != null; i++)
                {
                    string name = Path.GetFileName(dir);
                    if (!binNames.Contains(name, StringComparer.OrdinalIgnoreCase)) break;
                    dir = Path.GetDirectoryName(dir);
                }

                if (dir == null || dir.TrimEnd('\\').IndexOf('\\') < 0) return null;
                if (sharedFolders.Contains(Path.GetFileName(dir.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase)) return null;
                return dir;
            }
            catch
            {
                return null;
            }
        }

        private static HashSet<string> GameExeNames(string folder)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 4,
                    IgnoreInaccessible = true
                };

                foreach (string exe in Directory.EnumerateFiles(folder, "*.exe", options))
                {
                    string name = Path.GetFileNameWithoutExtension(exe);
                    if (name.Contains("unins", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("redist", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                        continue;
                    names.Add(name);
                }
            }
            catch { }
            return names;
        }
    }
}
