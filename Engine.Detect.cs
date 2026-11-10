using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SmartHdrManager
{
    public sealed partial class HdrEngine
    {
        private readonly HashSet<string> _pathLookupDone = new(StringComparer.OrdinalIgnoreCase);

        private string? FindRunningApp(Snapshot snapshot, HashSet<string> games, out string? exePath)
        {
            exePath = null;
            if (games.Count == 0) return null;

            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return null; }

            string? found = null;
            foreach (var process in processes)
            {
                try
                {
                    if (found != null) continue;

                    string name = process.ProcessName;
                    if (!games.Contains(name)) continue;

                    found = name.ToLowerInvariant();
                    if (_pathLookupDone.Add(found))
                        exePath = ProcessPaths.TryGetExePath(process.Id);
                }
                catch { }
                finally { process.Dispose(); }
            }
            return found;
        }

        /// <summary>Exe path of a process name: from the game library, a scan, "Add program" or an earlier run.</summary>
        private string? KnownExePath(string process)
        {
            lock (Sync)
            {
                if (Config.ProgramPaths.TryGetValue(process, out var path) && File.Exists(path)) return path;
                return null;
            }
        }

        private void RememberProgramPath(string process, string path)
        {
            lock (Sync)
            {
                if (Config.ProgramPaths.TryGetValue(process, out var known) && File.Exists(known)) return;
                Config.ProgramPaths[process] = path;
                Save();
            }
        }

        private static string? FindRunningFromFolder(HashSet<string> names, string launcher, DateTime notBefore)
        {
            if (names.Count == 0) return null;

            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return null; }

            string? found = null;
            int ownProcessId = Environment.ProcessId;
            foreach (var process in processes)
            {
                try
                {
                    if (found != null || process.Id == ownProcessId) continue;

                    string name = process.ProcessName;
                    if (names.Contains(name) && !name.Equals(launcher, StringComparison.OrdinalIgnoreCase) &&
                        process.StartTime >= notBefore)
                        found = name.ToLowerInvariant();
                }
                catch { }
                finally { process.Dispose(); }
            }
            return found;
        }

    }
}
