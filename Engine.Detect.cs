using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SmartHdrManager
{
    public sealed partial class HdrEngine
    {
        private const int LocationCacheLimit = 256;

        private readonly HashSet<string> _pathLookupDone = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cached exe location check per process: true = runs from its game's folder (or the location is unknown).</summary>
        private readonly Dictionary<(int Pid, string Name), bool> _locationOk = new();
        private Snapshot? _locationSnapshot;

        /// <summary>The program in front has an HDR game's name but runs from another folder.</summary>
        private bool _foregroundElsewhere;

        /// <summary>
        /// False when an HDR library game's process name runs from outside that game's install folder – another program
        /// with the same name. Unknown locations count as a match: a game without a known exe, a game whose known exes
        /// are all gone (moved or reinstalled), or a process whose path cannot be read. Uses limited query access only.
        /// </summary>
        private bool IsGameLocation(Snapshot snapshot, string process, int pid)
        {
            if (!snapshot.GameFolders.TryGetValue(process, out var known)) return true;

            if (!ReferenceEquals(snapshot, _locationSnapshot) || _locationOk.Count > LocationCacheLimit)
            {
                _locationOk.Clear();
                _locationSnapshot = snapshot;
            }

            var key = (pid, process.ToLowerInvariant());
            if (_locationOk.TryGetValue(key, out bool ok)) return ok;

            string? path = ProcessPaths.TryGetExePath(pid);
            ok = path == null || path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
                 known.Folders.Any(folder => path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase));

            if (!ok) ok = !known.Exes.Any(File.Exists);
            if (!ok)
            {
                string real = RealPath(path!);
                ok = known.Folders.Any(folder => real.StartsWith(RealPath(folder) + "\\", StringComparison.OrdinalIgnoreCase)) ||
                     known.Exes.Any(exe => Path.GetFileName(exe).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) &&
                                           SameFile(path!, exe));
            }

            if (!ok) Log("HDR", $"'{process}' 运行自 {path} – 不是您列表中游戏的安装文件夹，已忽略。");
            _locationOk[key] = ok;
            return ok;
        }

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
                    if (!games.Contains(name) || !IsGameLocation(snapshot, name, process.Id)) continue;

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

                foreach (var game in Config.LibraryGames)
                {
                    if (game.ExePath == null) continue;
                    if (!Path.GetFileNameWithoutExtension(game.ExePath).Equals(process, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(game.ExePath)) return game.ExePath;
                }
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

        /// <summary>
        /// A path with every folder link and junction in it resolved ("C:\Games\X" → "D:\Games\X" when C:\Games links to
        /// D:\Games). The path itself when it cannot be resolved.
        /// </summary>
        private static string RealPath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path).TrimEnd('\\');
                string current = Path.GetPathRoot(full) ?? string.Empty;
                if (current.Length == 0) return full;

                foreach (string part in full.Substring(current.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, part);
                    var info = new DirectoryInfo(current);
                    if (info.LinkTarget != null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                        current = target.FullName;
                }
                return current.TrimEnd('\\');
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// The same exe reached through another path, such as a mapped drive. Compared by size and time stamp;
        /// a file that cannot be read counts as the same (unknown = no change in behaviour).
        /// </summary>
        private static bool SameFile(string a, string b)
        {
            try
            {
                var fileA = new FileInfo(a);
                var fileB = new FileInfo(b);
                if (!fileA.Exists || !fileB.Exists) return !fileA.Exists;
                return fileA.Length == fileB.Length && fileA.LastWriteTimeUtc == fileB.LastWriteTimeUtc;
            }
            catch
            {
                return true;
            }
        }
    }
}
