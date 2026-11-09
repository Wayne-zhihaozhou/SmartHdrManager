using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SmartHdrManager
{
    public sealed partial class HdrEngine
    {
        private const int DefaultBrightness = 50;

        public static string CleanExeInput(string rawInput)
        {
            string trimmed = rawInput.Trim().Trim('"', '\'');
            if (trimmed.Contains('\\') || trimmed.Contains('/') || trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                trimmed = Path.GetFileNameWithoutExtension(trimmed);
            return trimmed.ToLowerInvariant();
        }

        public string? GetInitialDirectory()
        {
            lock (Sync)
                return Config.CustomGameFolders.FirstOrDefault(Directory.Exists);
        }

        /// <summary>Rows for the games list: every library game (real name, source, icon).</summary>
        public List<GameRow> GetGameRows()
        {
            lock (Sync)
            {
                var tracked = new HashSet<string>(Config.HdrGamesAndApps ?? new List<string>(), Cmp);
                var paths = Config.ProgramPaths ?? new Dictionary<string, string>();
                var brightnessMap = Config.PerGameBrightness ?? new Dictionary<string, int>();
                var hdrModes = Config.GameHdrModes;

                var rows = new List<GameRow>();
                var covered = new HashSet<string>(Cmp);

                foreach (var game in Config.LibraryGames ?? new List<LibraryGame>())
                {
                    covered.UnionWith(game.Processes);
                    var processes = game.Processes.ToList();
                    if (processes.Count == 0) continue;

                    rows.Add(new GameRow
                    {
                        Key = game.Id,
                        Name = game.Name,
                        Subtitle = processes.Count > 1
                            ? $"{game.Source}  ·  {processes[0]}.exe +{processes.Count - 1}"
                            : $"{game.Source}  ·  {processes[0]}.exe",
                        Processes = processes,
                        ExePath = game.ExePath ?? processes.Select(p => paths.TryGetValue(p, out var known) ? known : null)
                                                           .FirstOrDefault(p => p != null),
                        Hdr = processes.Any(tracked.Contains) && processes.All(p => IsHdrEnabledFor(hdrModes, p)),
                        Brightness = game.Brightness
                    });
                }

                foreach (string process in Config.HdrGamesAndApps ?? new List<string>())
                {
                    if (covered.Contains(process)) continue;

                    rows.Add(new GameRow
                    {
                        Key = "proc:" + process,
                        Name = process,
                        Subtitle = $"程序  ·  {process}.exe",
                        Processes = new List<string> { process },
                        ExePath = paths.TryGetValue(process, out var path) ? path : null,
                        Hdr = IsHdrEnabledFor(hdrModes, process),
                        Brightness = brightnessMap.TryGetValue(process, out int brightness) ? brightness : DefaultBrightness
                    });
                }

                return rows.OrderBy(r => r.Name, Cmp).ToList();
            }
        }

        public void SetGameBrightness(string gameKey, IEnumerable<string> processes, int brightness, string displayName)
        {
            var list = processes.ToList();
            int value = Math.Clamp(brightness, 0, 100);

            lock (Sync)
            {
                bool isLibraryGame = false;
                foreach (var game in Config.LibraryGames ?? new List<LibraryGame>())
                {
                    if (game.Id != gameKey) continue;
                    game.Brightness = value;
                    isLibraryGame = true;
                    break;
                }

                if (!isLibraryGame)
                {
                    var brightnessMap = Config.PerGameBrightness ?? new Dictionary<string, int>();
                    foreach (string process in list) brightnessMap[process] = value;
                    Config.PerGameBrightness = brightnessMap;
                }

                Save();
            }
            Log("BRIGHT", $"'{displayName}' 亮度设为 {value}");
        }

        public bool AddGame(string procName, string? exePath = null)
        {
            if (string.IsNullOrWhiteSpace(procName) || procName is "explorer" or "smarthdrmanager")
            {
                Log("ERROR", "无效的程序名。");
                return false;
            }

            procName = procName.ToLowerInvariant();
            lock (Sync)
            {
                if (!Config.HdrGamesAndApps.Contains(procName, Cmp))
                {
                    Config.HdrGamesAndApps.Add(procName);
                    Config.HdrGamesAndApps.Sort(Cmp);
                    Log("LIST", $"+ '{procName}' 已添加到 HDR 列表");
                }
                else
                {
                    Log("LIST", $"'{procName}' 已在 HDR 列表中");
                }

                Config.GameHdrModes[procName] = true;

                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    Config.ProgramPaths[procName] = exePath;

                InvalidateSnapshot();
                Save();
            }

            return true;
        }

        public void RemoveGame(string key, IEnumerable<string> processes)
        {
            var procList = processes.ToList();
            lock (Sync)
            {
                foreach (string process in procList)
                {
                    Config.HdrGamesAndApps.RemoveAll(g => g.Equals(process, StringComparison.OrdinalIgnoreCase));
                    Config.ProgramPaths.Remove(process);
                    Config.GameHdrModes.Remove(process);
                }

                var libraryGames = Config.LibraryGames ?? new List<LibraryGame>();
                var gameToRemove = libraryGames.FirstOrDefault(g => g.Id == key);
                if (gameToRemove != null)
                {
                    libraryGames.Remove(gameToRemove);
                    Log("LIST", $"'- {gameToRemove.Name}' 已从游戏库移除");
                }

                Config.HdrGamesAndApps.Sort(Cmp);
                InvalidateSnapshot();
                Save();
            }

            if (procList.Count > 0) Log("LIST", $"- '{procList[0]}' 已移除");
        }
    }
}
