using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace HdrBrightControl
{
    public sealed partial class HdrEngine
    {
        /// <summary>
        /// One pass over all processes for the HDR game and the SDR program that are running. A program is found by
        /// its process name or, for programs that start a sibling exe, by its exe; the remembered window in front is
        /// dropped as soon as its process is gone.
        /// </summary>
        private void ScanRunningPrograms(Snapshot snapshot)
        {
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return; }

            string? game = null;
            string? sdrApp = null;
            bool contentRunning = _contentProcess.Length == 0 || IsSelf(_contentProcess);

            foreach (var process in processes)
            {
                try
                {
                    if (game != null && sdrApp != null && contentRunning) continue;

                    string name = process.ProcessName;
                    if (!contentRunning && string.Equals(name, _contentProcess, StringComparison.OrdinalIgnoreCase))
                        contentRunning = true;

                    var program = snapshot.Programs.MatchProcess(name, process.Id).Program;
                    if (program == null) continue;

                    if (program.IsHdr)
                    {
                        if (game != null) continue;

                        game = program.Entry;
                        if (KnownExePath(game) == null && ProcessPaths.TryGetExePath(process.Id) is string exePath)
                            RememberProgramPath(game, exePath);
                    }
                    else if (sdrApp == null)
                    {
                        sdrApp = program.Entry;
                    }
                }
                catch { }
                finally { process.Dispose(); }
            }

            _runningGame = game;
            _runningSdrApp = sdrApp;

            if (!contentRunning)
            {
                _contentProcess = string.Empty;
                _contentProcessId = 0;
                _contentProgram = null;
            }
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
