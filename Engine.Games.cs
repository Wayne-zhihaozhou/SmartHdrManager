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

        /// <summary>Rows for one of the two lists: every library game (real name, source, icon) plus its own programs.</summary>
        /// <param name="sdr">True for the SDR list, false for the HDR list.</param>
        public List<GameRow> GetGameRows(bool sdr = false)
        {
            lock (Sync)
            {
                var list = sdr ? Config.SdrGamesAndApps : Config.HdrGamesAndApps;
                var tracked = new HashSet<string>(list ?? new List<string>(), Cmp);
                var paths = Config.ProgramPaths ?? new Dictionary<string, string>();
                var brightnessMap = Config.PerGameBrightness ?? new Dictionary<string, int>();
                var hdrModes = Config.GameHdrModes;

                var rows = new List<GameRow>();

                foreach (string process in list ?? new List<string>())
                {
                    rows.Add(new GameRow
                    {
                        Key = "proc:" + process,
                        Name = process,
                        Subtitle = $"程序  ·  {process}.exe",
                        Processes = new List<string> { process },
                        ExePath = paths.TryGetValue(process, out var path) ? path : null,
                        Tracked = sdr || IsHdrEnabledFor(hdrModes, process),
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
                var brightnessMap = Config.PerGameBrightness ?? new Dictionary<string, int>();
                foreach (string process in list) brightnessMap[process] = value;
                Config.PerGameBrightness = brightnessMap;

                Save();
            }
            Log("BRIGHT", $"'{displayName}' 亮度设为 {value}");
        }

        /// <summary>Adds a program to one of the two lists; it is removed from the other one.</summary>
        /// <param name="sdr">True for the SDR list, false for the HDR list.</param>
        public bool AddGame(string procName, string? exePath = null, bool sdr = false)
        {
            if (string.IsNullOrWhiteSpace(procName) || procName is "explorer" or "smarthdrmanager")
            {
                Log("ERROR", "无效的程序名。");
                return false;
            }

            procName = procName.ToLowerInvariant();
            string listName = sdr ? "SDR" : "HDR";

            lock (Sync)
            {
                var target = sdr ? Config.SdrGamesAndApps : Config.HdrGamesAndApps;
                var other = sdr ? Config.HdrGamesAndApps : Config.SdrGamesAndApps;

                if (!target.Contains(procName, Cmp))
                {
                    target.Add(procName);
                    target.Sort(Cmp);
                    Log("LIST", $"+ '{procName}' 已添加到 {listName} 列表");
                }
                else
                {
                    Log("LIST", $"'{procName}' 已在 {listName} 列表中");
                }

                if (other.RemoveAll(name => name.Equals(procName, StringComparison.OrdinalIgnoreCase)) > 0)
                    Log("LIST", $"'{procName}' 已从 {(sdr ? "HDR" : "SDR")} 列表移除");

                if (sdr) Config.GameHdrModes.Remove(procName);
                else Config.GameHdrModes[procName] = true;

                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    Config.ProgramPaths[procName] = exePath;

                InvalidateSnapshot();
                Save();
            }

            return true;
        }

        public void RemoveGame(string key, IEnumerable<string> processes, bool sdr = false)
        {
            var procList = processes.ToList();
            lock (Sync)
            {
                var target = sdr ? Config.SdrGamesAndApps : Config.HdrGamesAndApps;

                foreach (string process in procList)
                {
                    target.RemoveAll(name => name.Equals(process, StringComparison.OrdinalIgnoreCase));
                    Config.ProgramPaths.Remove(process);
                    if (!sdr) Config.GameHdrModes.Remove(process);
                }

                target.Sort(Cmp);
                InvalidateSnapshot();
                Save();
            }

            if (procList.Count > 0) Log("LIST", $"- '{procList[0]}' 已移除");
        }
    }
}
