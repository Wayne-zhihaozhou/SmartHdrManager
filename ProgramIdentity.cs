using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SmartHdrManager
{
    /// <summary>How a running process was recognised as a program of one of the two lists.</summary>
    public enum ProgramMatchKind
    {
        None,
        Name,
        ExePath,
        Product
    }

    /// <summary>One program of the HDR or SDR list: the name the config uses, plus its exe when one is known.</summary>
    public sealed class ListedProgram
    {
        public string Entry { get; init; } = string.Empty;

        public bool IsHdr { get; init; }

        public string? ExePath { get; init; }
    }

    /// <summary>Result of identifying a running process: the list entry it belongs to, and how it was recognised.</summary>
    public readonly struct ProgramMatch
    {
        public static readonly ProgramMatch None = default;

        public ProgramMatch(ListedProgram program, ProgramMatchKind kind)
        {
            Program = program;
            Kind = kind;
        }

        public ListedProgram? Program { get; }

        public ProgramMatchKind Kind { get; }
    }

    /// <summary>
    /// Maps a running process to the list entry it belongs to. The process name is compared first; a program that
    /// starts a sibling exe instead of the one that was added (PotPlayer64.exe vs PotPlayerMini64.exe, a game's
    /// shipping exe) is recognised by the exe itself: the same file, or another exe of the same folder with the same
    /// product name. Exes in the Windows folder are never matched by product name, so system tools cannot collide.
    /// </summary>
    public sealed class ProgramMatcher
    {
        private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
        private const char ProductKeySeparator = '|';

        private readonly Dictionary<string, ListedProgram> _byName = new(Cmp);
        private readonly Dictionary<string, ListedProgram> _byExe = new(Cmp);
        private readonly Dictionary<string, ListedProgram> _byProduct = new(Cmp);
        private readonly HashSet<string> _productFolders = new(Cmp);

        public ProgramMatcher(IEnumerable<ListedProgram> programs)
        {
            foreach (var program in programs)
            {
                if (string.IsNullOrWhiteSpace(program.Entry)) continue;

                _byName[program.Entry] = program;
                IndexExe(program);
            }
        }

        /// <summary>The entry with exactly this name, or null.</summary>
        public ListedProgram? Match(string? processName) =>
            !string.IsNullOrEmpty(processName) && _byName.TryGetValue(processName, out var program) ? program : null;

        /// <summary>The entry this running process belongs to, or a miss.</summary>
        public ProgramMatch MatchProcess(string processName, int processId)
        {
            var byName = Match(processName);
            if (byName != null) return new ProgramMatch(byName, ProgramMatchKind.Name);
            if (processId <= 0 || (_byExe.Count == 0 && _productFolders.Count == 0)) return ProgramMatch.None;

            string? exePath = ProgramIdentity.ExePath(processId, processName);
            if (exePath == null) return ProgramMatch.None;
            if (_byExe.TryGetValue(exePath, out var byPath)) return new ProgramMatch(byPath, ProgramMatchKind.ExePath);

            // Only exes that share a folder with a listed program are worth their version info
            string? folder = Folder(exePath);
            if (folder == null || !_productFolders.Contains(folder)) return ProgramMatch.None;

            string? product = ProgramIdentity.ProductName(exePath);
            if (product == null) return ProgramMatch.None;

            return _byProduct.TryGetValue(ProductKey(folder, product), out var byProduct)
                ? new ProgramMatch(byProduct, ProgramMatchKind.Product)
                : ProgramMatch.None;
        }

        private void IndexExe(ListedProgram program)
        {
            if (string.IsNullOrWhiteSpace(program.ExePath)) return;

            string? exePath = ProgramIdentity.FullPath(program.ExePath);
            if (exePath == null) return;

            _byExe[exePath] = program;

            string? folder = Folder(exePath);
            string? product = ProgramIdentity.ProductName(exePath);
            if (folder == null || product == null || ProgramIdentity.IsSystemFolder(folder)) return;

            _byProduct[ProductKey(folder, product)] = program;
            _productFolders.Add(folder);
        }

        private static string? Folder(string exePath) => Path.GetDirectoryName(exePath);

        private static string ProductKey(string folder, string product) =>
            string.Concat(folder, ProductKeySeparator, product);
    }

    /// <summary>
    /// Exe path per process name and product name per exe path, each read once: both are stable while the app runs,
    /// and the exe path is queried with the same limited access Task Manager uses (no memory access to games).
    /// </summary>
    public static class ProgramIdentity
    {
        private static readonly object CacheLock = new();
        private static readonly Dictionary<string, string> ExePaths = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string?> ProductNames = new(StringComparer.OrdinalIgnoreCase);

        private static readonly string WindowsFolder =
            Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar);

        public static string? ExePath(int processId, string processName)
        {
            if (processName.Length > 0)
            {
                lock (CacheLock)
                    if (ExePaths.TryGetValue(processName, out var known)) return known;
            }

            string? path = FullPath(ProcessPaths.TryGetExePath(processId));
            if (path == null) return null;

            if (processName.Length > 0)
                lock (CacheLock) ExePaths[processName] = path;

            return path;
        }

        public static string? FullPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try { return Path.GetFullPath(path); }
            catch { return null; }
        }

        /// <summary>Product name from the exe's version info; null when the file has none or cannot be read.</summary>
        public static string? ProductName(string exePath)
        {
            lock (CacheLock)
                if (ProductNames.TryGetValue(exePath, out var known)) return known;

            string? product = ReadProductName(exePath);

            lock (CacheLock) ProductNames[exePath] = product;
            return product;
        }

        /// <summary>A listed program is never recognised by product name inside the Windows folder.</summary>
        public static bool IsSystemFolder(string folder) =>
            WindowsFolder.Length > 0 && folder.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase);

        private static string? ReadProductName(string exePath)
        {
            try
            {
                if (!File.Exists(exePath)) return null;

                string product = FileVersionInfo.GetVersionInfo(exePath).ProductName?.Trim() ?? string.Empty;
                return product.Length == 0 ? null : product;
            }
            catch
            {
                return null;
            }
        }
    }
}
