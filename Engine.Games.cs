using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HdrBrightControl
{
    public sealed partial class HdrEngine
    {
        public static string CleanExeInput(string rawInput)
        {
            string trimmed = rawInput.Trim().Trim('"', '\'');
            if (trimmed.Contains('\\') || trimmed.Contains('/') || trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                trimmed = Path.GetFileNameWithoutExtension(trimmed);
            return trimmed.ToLowerInvariant();
        }

        /// <summary>Rows for one of the two lists: every library game (real name, source, icon) plus its own programs.</summary>
        public List<GameRow> GetGameRows(ProgramListKind kind = ProgramListKind.Hdr)
        {
            lock (Sync)
            {
                var list = kind == ProgramListKind.Sdr ? Config.SdrGamesAndApps : Config.HdrGamesAndApps;
                var tracked = new HashSet<string>(list ?? new List<string>(), Cmp);
                var paths = Config.ProgramPaths ?? new Dictionary<string, string>();
                var brightnessMap = Config.PerGameBrightness ?? new Dictionary<string, int>();
                var hdrModes = Config.GameHdrModes;

                var rows = new List<GameRow>();
                var processes = list ?? new List<string>();

                foreach (string process in processes)
                {
                    rows.Add(new GameRow
                    {
                        Key = "proc:" + process,
                        Name = process,
                        Subtitle = $"程序  ·  {process}.exe",
                        Processes = new List<string> { process },
                        ExePath = paths.TryGetValue(process, out var path) ? path : null,
                        Tracked = kind == ProgramListKind.Sdr || IsHdrEnabledFor(hdrModes, process),
                        Brightness = brightnessMap.TryGetValue(process, out int brightness) ? brightness : BrightnessRange.Default
                    });
                }

                return rows.OrderBy(r => r.Name, Cmp).ToList();
            }
        }

        public void SetGameBrightness(IEnumerable<string> processes, int brightness, string displayName)
        {
            int value = Math.Clamp(brightness, BrightnessRange.Min, BrightnessRange.Max);

            lock (Sync)
            {
                var brightnessMap = Config.PerGameBrightness ?? new Dictionary<string, int>(Cmp);
                foreach (string process in processes) brightnessMap[process] = value;
                Config.PerGameBrightness = brightnessMap;

                InvalidateSnapshot();
                Save();
            }

            Log("BRIGHT", $"'{displayName}' 亮度设为 {value}");
        }

        /// <summary>Adds a program to one of the two lists; it is removed from the other one.</summary>
        public bool AddGame(string procName, string? exePath = null, ProgramListKind kind = ProgramListKind.Hdr)
        {
            if (string.IsNullOrWhiteSpace(procName) ||
                procName.Equals(ExplorerProcessName, StringComparison.OrdinalIgnoreCase) ||
                IsSelf(procName))
            {
                Log("ERROR", "无效的程序名。");
                return false;
            }

            procName = procName.ToLowerInvariant();
            string listName = kind == ProgramListKind.Sdr ? "SDR" : "HDR";

            lock (Sync)
            {
                var target = kind == ProgramListKind.Sdr ? Config.SdrGamesAndApps : Config.HdrGamesAndApps;
                var other = kind == ProgramListKind.Sdr ? Config.HdrGamesAndApps : Config.SdrGamesAndApps;

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
                    Log("LIST", $"'{procName}' 已从 {(kind == ProgramListKind.Sdr ? "SDR" : "HDR")} 列表移除");

                if (kind == ProgramListKind.Sdr) Config.GameHdrModes.Remove(procName);
                else Config.GameHdrModes[procName] = true;

                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    Config.ProgramPaths[procName] = exePath;

                InvalidateSnapshot();
                Save();
            }

            return true;
        }

        public void RemoveGame(IEnumerable<string> processes, ProgramListKind kind = ProgramListKind.Hdr)
        {
            var procList = processes.ToList();
            lock (Sync)
            {
                var target = kind == ProgramListKind.Sdr ? Config.SdrGamesAndApps : Config.HdrGamesAndApps;

                foreach (string process in procList)
                {
                    target.RemoveAll(name => name.Equals(process, StringComparison.OrdinalIgnoreCase));
                    Config.ProgramPaths.Remove(process);
                    if (kind == ProgramListKind.Hdr) Config.GameHdrModes.Remove(process);
                }

                target.Sort(Cmp);
                InvalidateSnapshot();
                Save();
            }

            if (procList.Count > 0) Log("LIST", $"- '{procList[0]}' 已移除");
        }
    }
}
