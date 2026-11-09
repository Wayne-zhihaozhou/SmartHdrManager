using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace SmartHdrManager
{
    // =========================================================================
    // 1. CONFIGURATION
    // =========================================================================
    public class AppConfig
    {
        // Format version of this file: lets newer versions fix default lists in existing config.json files once
        // (missing in files from 2.5.0 and older = 0).
        public int ConfigVersion { get; set; } = 0;

        // Only toggle HDR on the primary monitor (main display at desktop coordinates 0,0)
        public bool OnlyPrimaryMonitor { get; set; } = true;

        // Extra folders to search for games (the common Steam / Epic / GOG / Xbox / "Games" folders are found automatically)
        public List<string> CustomGameFolders { get; set; } = new();



        // Active HDR list: Only games in this list will automatically trigger HDR (empty: the user picks the games)
        public List<string> HdrGamesAndApps { get; set; } = new();

        // Catalog of all games found on drives (selectable via menu [G] before starting a game)
        public List<string> DiscoveredGames { get; set; } = new();

        // After leaving HDR, tell open windows that the display changed so apps like Chrome stop rendering
        // with their cached HDR brightness (which makes them look dimmed until minimized/restored).
        public bool RefreshAppsAfterSdr { get; set; } = true;

        // Monitors to switch, by Windows device path. Empty = use OnlyPrimaryMonitor (primary only / all monitors).
        public List<string> SelectedMonitors { get; set; } = new();

        // Games found in the Steam / Epic / GOG / Xbox libraries (refreshed at startup and with "Scan drives")
        public List<LibraryGame> LibraryGames { get; set; } = new();

        // Games tab: also list programs from the drive scan that are not in a game library
        public bool ShowOtherPrograms { get; set; } = false;

        // ---- v2.5.2 ----
        // Learned launchers: launcher process -> the game process(es) it started (e.g. "redprelauncher" -> "witcher3").
        // Filled automatically when a watched game closes within seconds and a program from the same folder takes over.
        public Dictionary<string, List<string>> LauncherGames { get; set; } = new();

        // Games that closed within seconds after HDR was switched on for them (possible crash, e.g. with NVIDIA RTX HDR)
        public Dictionary<string, int> QuickExitCounts { get; set; } = new();

        // Full exe paths of programs added by hand (for their icons in the Games tab)
        public Dictionary<string, string> ProgramPaths { get; set; } = new();

        public bool EnableHdrOnProcessStart { get; set; } = true;
        public int ProcessScanIntervalMs { get; set; } = 100;
        // Off by default: the Windows task it uses needs administrator rights on most PCs
        public bool ReloadColorProfileAfterSdr { get; set; } = false;
        public int SdrFallbackDelaySeconds { get; set; } = 3;

        // ---- v2.6 ----
        // Legacy field kept for old config files; no longer used internally.
        public Dictionary<string, AppRule> AppRules { get; set; } = new();

        // ---- TV brightness control (pc-control-tv migration) ----
        public BrightnessConfig? Brightness { get; set; }
    }

    // =========================================================================
    // TV BRIGHTNESS CONFIG (migrated from pc-control-tv)
    // =========================================================================
    public class BrightnessConfig
    {
        public const int HotkeyStep = 5;  // 写死步进值，不可配置

        public string TvIp { get; set; } = "192.168.1.4";
        public string LocalAddr { get; set; } = "192.168.1.5";
        public bool Enabled { get; set; } = true;
        public bool AutoSwitchEnabled { get; set; } = true;
        public List<BrightnessRule> Rules { get; set; } = new();
    }

    public class BrightnessRule
    {
        public string Name { get; set; } = "新规则";
        public int Brightness { get; set; } = 50;
        public bool MatchFullscreen { get; set; }
        public List<string> Processes { get; set; } = new();
        public List<string> TitleKeywords { get; set; } = new();
        public List<string> IgnoreProcesses { get; set; } = new();

        public bool IsFallback()
        {
            return Processes.Count == 0 && TitleKeywords.Count == 0 && !MatchFullscreen;
        }

        public string ConditionSummary()
        {
            var parts = new List<string>();
            if (MatchFullscreen) parts.Add("全屏");
            if (Processes.Count > 0) parts.Add($"进程({Processes.Count}个)");
            if (TitleKeywords.Count > 0) parts.Add($"标题关键词({TitleKeywords.Count}个)");
            if (parts.Count == 0) return "其他(兜底)";
            return string.Join(" 或 ", parts);
        }
    }

    // =========================================================================
    // APP RULES (migrated from AppCatalog.cs)
    // =========================================================================
    public static class AppCategories
    {
        public static string? Normalize(string input)
        {
            switch (input)
            {
                case "browser": return "browser";
                case "player": return "player";
                case "creative": return "creative";
                default: return null;
            }
        }
    }

    public static class AppModes
    {
        public const string Off = "off";
        public const string Sdr = "sdr";
        public const string HdrOnly = "hdr_only";
        public const string Auto = "auto";

        public static string Normalize(string category, string? mode)
        {
            switch (category)
            {
                case "browser":
                case "player":
                    return mode switch { Off => Off, HdrOnly => HdrOnly, _ => Auto };
                case "creative":
                    return mode switch { Sdr => Sdr, _ => Off };
                default:
                    return Off;
            }
        }
    }

    public sealed class AppRule
    {
        public string Category { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string? ExePath { get; set; }
        public bool AddedByUser { get; set; }
    }

    // =========================================================================
    // AppCatalog stubs (migrated from AppCatalog.cs; Apps tab deleted)
    // =========================================================================
    public static class AppCatalog
    {
        // Minimal known-processes list (was used to filter non-game executables during drive scanning)
        private static readonly HashSet<string> _knownProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "smarthdrmanager", "code", "visualstudio", "devenv",
            "notepad", "notepad++", "sublime_text", "atom", "webstorm",
            "intellij", "pycharm", "webstorm", "phpstorm", "clion",
            "taskhostw", "dwm", "csrss", "svchost", "services", "lsass",
            "winlogon", "spoolsv", "explorer", "sihost", "inputhost",
            "shellexperiencehost", "startmenuexperiencehost", "searchui",
            "runtimebroker", "compattelrunner", "fontdrvhost",
            "store", "msedge", "chrome", "firefox", "brave",
            "vlc", "mpc-hc64", "mpc-be64", "mpv", "potplayer",
            "photoshop", "lightroom", "premiere", "afterfx", "resolve",
            "obs64", "obs32", "streamlabs",
            "spotify", "discord", "telegram", "whatsapp",
            "onedrive", "dropbox", "icloud",
            "rstrty", "comsurrogate", "dfsshare",
            "searchindex", "searchprotocol", "searchhost",
            "fontdrvhost", "dwm", "wininit", "winlogon",
            "conhost", "regedit", "msconfig", "msdtc",
            "msedge_update", "edgeupdate", "edgeupdatem",
            "chrome_updater", "firefoxupdater",
            "steam", "steamwebhelper", "epicgameslauncher",
            "epicgames-launcher", "goggalaxy", "gog-galaxy",
            "xboxapp", "xboxgamebar", "xboxgamingoverlay",
            "ubisoftconnect", "ubisoftconnect", "uplay",
            "battle.net", "blizzard", "rockstar", "epicgames",
            "riotclient", "heroic", "itch",
            "taskmgr", "dwm", "csrss",
            "wlms", "WUDFHost", "SecurityHealthSystray",
            "ccSvcHst", "ccSvcHst64", "ccSetMgr",
            "avastui", "avgui", "mssecess", "msmpeng",
            "nvvsvc", "NVIDIA Container", "nvudisp",
            "RtkAudUService", "AudioSrv", "Audiodg",
            "ctfmon", "SearchApp", "WidgetService",
            "ShellExperienceHost", "TextInputHost",
            "StartMenuExperienceHost", "explorer",
            "OneDrive", "OneDriveSetup", "OneDriveUpdater",
            "Dropbox", "DropboxUpdater",
            "MailApp", "Microsoft.Winget.Source",
            "GameBar", "GameBarNative", "GameConfigService",
            "XboxGameOverlay", "XboxAccessibiity",
            "XboxGfxMod", "XboxNetApi",
            "esearch", "epicgames",
            "launcher", "launcherclient", "launcherupdater",
            "gogclient", "gog-client",
            "uplay", "uplayinstaller",
            "battle", "battleye", "easyanticheat",
            "r5apiere", "r5launcher", "r5poster",
            "redprelauncher", "playgtav", "rsglauncher",
            "riotclientcrashreport",
        };

        public static bool IsKnown(string process) => _knownProcesses.Contains(process);

        public static string NameFor(string process, string? exePath)
        {
            if (!string.IsNullOrEmpty(exePath))
            {
                try
                {
                    var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                    if (!string.IsNullOrEmpty(info.ProductName) && info.ProductName.Length >= 3)
                        return info.ProductName;
                }
                catch { }
            }
            return process;
        }

        public static string ProcessOf(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return string.Empty;
            return System.IO.Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        }

        public static void WriteLegacyLists(AppConfig config) { }
    }

    public sealed class PlayerInfo
    {
        public string Name { get; set; } = string.Empty;
        public string ExePath { get; set; } = string.Empty;
        public bool Custom { get; set; }
    }

    // =========================================================================
    // Updater stub (Updater.cs deleted; update functionality removed)
    // =========================================================================
    public static class Updater
    {
        public const string CurrentVersionText = "2.6.1";
        public const string BuildDescription = "精简版";
        public static void CleanUp() { }
        public static System.Threading.Tasks.Task<UpdateInfo> CheckAsync(System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new UpdateInfo { IsNewer = false, VersionText = CurrentVersionText, ReleaseUrl = string.Empty });
    }

    public sealed class UpdateInfo
    {
        public bool IsNewer { get; init; }
        public string VersionText { get; init; } = string.Empty;
        public string ReleaseUrl { get; init; } = string.Empty;
    }

    public sealed class LibraryGame
    {
        public string Id { get; set; } = string.Empty;          // e.g. "steam:1091500"
        public string Name { get; set; } = string.Empty;        // display name, e.g. "Cyberpunk 2077"
        public string Source { get; set; } = string.Empty;      // Steam / Epic / GOG / Xbox / Ubisoft / EA app / Rockstar / Battle.net / Amazon Games / itch.io / Riot Games / Heroic (…) / Game Bar
        public List<string> Processes { get; set; } = new();    // process names (exe without .exe, lower case)
        public string? ExePath { get; set; }                    // main exe, used for the icon
        public int ScanVersion { get; set; }                    // GameLibraryScanner.CurrentScanVersion when it was detected
    }

    /// <summary>
    /// Exe path of another process using only PROCESS_QUERY_LIMITED_INFORMATION (the same access Task Manager uses).
    /// Never reads the process's memory (Process.MainModule does), which matters for games protected by kernel
    /// anti-cheat systems: they watch for handles with memory access to the game.
    /// </summary>
    public static class ProcessPaths
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static string? TryGetExePath(int processId)
        {
            if (processId <= 0) return null;
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                uint size = (uint)buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }

    /// <summary>The real Windows version for the log, e.g. "Windows 11 25H2 (build 26200.9711)".</summary>
    public static class WindowsVersion
    {
        private static string? _cached;

        public static string Describe()
        {
            if (_cached != null) return _cached;
            string result;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                string display = (key?.GetValue("DisplayVersion") as string ?? string.Empty).Trim();   // "25H2"
                string buildText = (key?.GetValue("CurrentBuild") as string ?? string.Empty).Trim();   // "26200"
                object? ubr = key?.GetValue("UBR");                                                        // 9711
                int build = int.TryParse(buildText, out int b) ? b : Environment.OSVersion.Version.Build;
                // "ProductName" still says "Windows 10" on Windows 11, so the build number decides
                string name = build >= 22000 ? "Windows 11" : "Windows 10";
                string fullBuild = ubr is int u ? $"{build}.{u}" : build.ToString();
                result = display.Length > 0 ? $"{name} {display} (build {fullBuild})" : $"{name} (build {fullBuild})";
            }
            catch
            {
                result = $"Windows (build {Environment.OSVersion.Version.Build})";
            }
            return _cached = result;
        }
    }

    /// <summary>Which monitors to switch: a set of device paths, or "primary only" / "all".</summary>
    public sealed class MonitorFilter
    {
        public MonitorFilter(bool onlyPrimary, IEnumerable<string>? devicePaths)
        {
            OnlyPrimary = onlyPrimary;
            var paths = devicePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            DevicePaths = paths != null && paths.Count > 0 ? new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase) : null;
        }

        public bool OnlyPrimary { get; }
        public HashSet<string>? DevicePaths { get; }   // null = not customised

        public string Describe() =>
            DevicePaths != null
                ? (DevicePaths.Count == 1 ? "1 个选定显示器" : $"{DevicePaths.Count} 个选定显示器")
                : OnlyPrimary ? "主显示器" : "所有显示器";
    }

    /// <summary>A display's colour output at one moment (for the "after SDR" check).</summary>
    public sealed class DisplayColorState
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string GdiName { get; init; } = string.Empty;
        public bool HdrOn { get; init; }
        public int Encoding { get; init; } = -1;   // 0 RGB, 1 YCbCr 4:4:4, 2 YCbCr 4:2:2, 3 YCbCr 4:2:0
        public uint Bits { get; init; }
        public int Mode { get; init; } = -1;       // 24H2+: 0 SDR, 1 WCG (Auto Color Management), 2 HDR; -1 unknown
        public ushort[]? Ramp { get; init; }        // gamma curve: 256 red, 256 green, 256 blue

        public bool RampIsIdentity => Ramp != null && RampDistance(Ramp, null) <= 300;

        public static string EncodingName(int e) => e switch
        {
            0 => "RGB", 1 => "YCbCr 4:4:4", 2 => "YCbCr 4:2:2", 3 => "YCbCr 4:2:0", 4 => "Intensity", _ => "未知格式"
        };

        public static string ModeName(int m) => m switch
        {
            0 => "SDR", 1 => "WCG / 自动颜色管理", 2 => "HDR", _ => "未知模式"
        };

        public string Describe() =>
            $"{EncodingName(Encoding)}, {(Bits > 0 ? $"{Bits}-bit" : "? bit")}" + (Mode >= 0 ? $", {ModeName(Mode)}" : "") +
            (Ramp == null ? ", 伽马曲线不可读" : RampIsIdentity ? ", 标准伽马曲线" : ", 自定义伽马曲线");

        /// <summary>Largest difference between two curves (null = the neutral curve).</summary>
        public static int RampDistance(ushort[] a, ushort[]? b)
        {
            int max = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int other = b != null ? b[i] : (i % 256) * 257;
                max = Math.Max(max, Math.Abs(a[i] - other));
            }
            return max;
        }
    }

    public sealed class MonitorInfo
    {
        public string DevicePath { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool IsPrimary { get; init; }
        public bool HdrSupported { get; init; }
        public bool HdrEnabled { get; init; }
    }

    // =========================================================================
    // 3. AUTOMATIC GAME SCANNER FOR ALL DRIVES
    // =========================================================================
    public static class GameScanner
    {
        private static readonly string[] BlacklistSubstrings = new[]
        {
            "unins", "setup", "redist", "vcredist", "dxsetup", "directx",
            "crash", "report", "handler", "launcher", "eac", "easyanticheat",
            "battleye", "beservice", "beclient", "cefprocess", "webhelper",
            "benchmark", "config", "updater", "patcher", "cleanup", "touchup",
            "activation", "installer", "dotnet", "physx", "ue4prereq", "ueprereq",
            "steamworks", "eosoverlay", "gamingservices", "unitycrashhandler"
        };

        /// <summary>
        /// Scans all drives and adds found executables to DiscoveredGames
        /// so the user can conveniently select which ones should use HDR via menu [G].
        /// </summary>
        public static int ScanAndAddGames(AppConfig config, Dictionary<string, string>? foundPaths = null)
        {
            var searchRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in config.CustomGameFolders)
            {
                if (Directory.Exists(folder)) searchRoots.Add(folder);
            }

            foreach (var steamLib in GetSteamLibraryDirectories())
            {
                searchRoots.Add(steamLib);
            }

            string[] commonFolderNames = new[]
            {
                @"SteamLibrary\steamapps\common",
                @"Program Files (x86)\Steam\steamapps\common",
                @"Program Files\Epic Games",
                @"Epic Games",
                @"Games",
                @"Spiele",
                @"XboxGames",
                @"GOG Games",
                @"Program Files (x86)\GOG Galaxy\Games",
                @"Program Files\EA Games",
                @"EA Games",
                @"Ubisoft Game Launcher\games",
                @"Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games",
                @"Battle.net"
            };

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                foreach (var relPath in commonFolderNames)
                {
                    string fullPath = Path.Combine(drive.RootDirectory.FullName, relPath);
                    if (Directory.Exists(fullPath))
                    {
                        searchRoots.Add(fullPath);
                    }
                }
            }

            var discoveredSet = new HashSet<string>(config.DiscoveredGames, StringComparer.OrdinalIgnoreCase);
            int newlyDiscoveredCount = 0;

            foreach (var root in searchRoots)
            {
                try
                {
                    foreach (var gameDir in Directory.EnumerateDirectories(root))
                    {
                        string dirName = Path.GetFileName(gameDir);
                        if (dirName.Contains("Steamworks", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Contains("Redist", StringComparison.OrdinalIgnoreCase))
                            continue;

                        foreach (var exePath in EnumerateExeFilesSafe(gameDir, maxDepth: 4))
                        {
                            string exeName = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
                            if (!IsValidGameExecutable(exePath, exeName)) continue;

                            // Remember where each program lives (for its icon in the Games tab)
                            if (foundPaths != null && !foundPaths.ContainsKey(exeName))
                                foundPaths[exeName] = exePath;

                            if (!discoveredSet.Contains(exeName))
                            {
                                config.DiscoveredGames.Add(exeName);
                                discoveredSet.Add(exeName);
                                newlyDiscoveredCount++;
                            }
                        }
                    }
                }
                catch { }
            }

            if (newlyDiscoveredCount > 0)
            {
                config.DiscoveredGames.Sort();
            }

            return newlyDiscoveredCount;
        }

        private static IEnumerable<string> GetSteamLibraryDirectories()
        {
            var results = new List<string>();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                string? steamPath = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(steamPath))
                {
                    steamPath = steamPath.Replace('/', '\\');
                    string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdfPath))
                    {
                        string content = File.ReadAllText(vdfPath);
                        var matches = Regex.Matches(content, "\"path\"\\s+\"([^\"]+)\"");
                        foreach (Match match in matches)
                        {
                            string libPath = match.Groups[1].Value.Replace("\\\\", "\\");
                            string commonPath = Path.Combine(libPath, "steamapps", "common");
                            if (Directory.Exists(commonPath))
                            {
                                results.Add(commonPath);
                            }
                        }
                    }
                }
            }
            catch { }
            return results;
        }

        private static IEnumerable<string> EnumerateExeFilesSafe(string rootPath, int maxDepth)
        {
            var results = new List<string>();
            var dirs = new Stack<(string Path, int Depth)>();
            dirs.Push((rootPath, 0));

            while (dirs.Count > 0)
            {
                var (currentDir, depth) = dirs.Pop();
                try
                {
                    results.AddRange(Directory.GetFiles(currentDir, "*.exe"));
                    if (depth < maxDepth)
                    {
                        foreach (var subDir in Directory.GetDirectories(currentDir))
                        {
                            dirs.Push((subDir, depth + 1));
                        }
                    }
                }
                catch { }
            }
            return results;
        }

        private static bool IsValidGameExecutable(string fullPath, string exeName)
        {
            if (exeName.Length < 2) return false;
            foreach (var badWord in BlacklistSubstrings)
            {
                if (exeName.Contains(badWord)) return false;
            }

            try
            {
                var info = new FileInfo(fullPath);
                if (info.Length < 300 * 1024) return false;
            }
            catch { return false; }

            return true;
        }
    }

    // =========================================================================
    // 4. WINDOWS DISPLAYCONFIG API (native HDR control + Primary Monitor Filter)
    // =========================================================================
    public static class HdrController
    {
        private const int QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const int ERROR_SUCCESS = 0;
        private const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
        private const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xffffffff;

        private enum DISPLAYCONFIG_DEVICE_INFO_TYPE : int
        {
            GET_SOURCE_NAME = 1,
            GET_TARGET_NAME = 2,
            GET_ADVANCED_COLOR_INFO = 9,
            GET_ADVANCED_COLOR_INFO_2 = 15,   // Windows 11 24H2+
            SET_HDR_STATE = 16,               // Windows 11 24H2+
            SET_ADVANCED_COLOR_STATE = 10
        }

        [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
            public int colorEncoding;
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint enableAdvancedColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
            public uint rotation; public uint scaling;
            public uint refreshRateNumerator; public uint refreshRateDenominator;
            public uint scanLineOrdering;
            public int targetAvailable; public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType; public uint id; public LUID adapterId;
            public ulong u0, u1, u2, u3, u4, u5;
        }

        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        /// <summary>Friendly name (e.g. "LG ULTRAGEAR") and stable device path of a display target.</summary>
        private static (string Name, string DevicePath) GetTargetName(LUID adapterId, uint targetId)
        {
            var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                monitorFriendlyDeviceName = string.Empty,
                monitorDevicePath = string.Empty
            };
            request.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_TARGET_NAME;
            request.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            request.header.adapterId = adapterId;
            request.header.id = targetId;

            if (DisplayConfigGetDeviceInfo(ref request) != ERROR_SUCCESS)
                return (string.Empty, string.Empty);
            return (request.monitorFriendlyDeviceName ?? string.Empty, request.monitorDevicePath ?? string.Empty);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;   // e.g. "\\.\DISPLAY1"
        }

        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public int monLeft, monTop, monRight, monBottom, workLeft, workTop, workRight, workBottom;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

        private static string GetSourceGdiName(DISPLAYCONFIG_PATH_INFO path)
        {
            var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME { viewGdiDeviceName = string.Empty };
            request.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_SOURCE_NAME;
            request.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            request.header.adapterId = path.sourceInfo.adapterId;
            request.header.id = path.sourceInfo.id;
            return DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS ? request.viewGdiDeviceName ?? string.Empty : string.Empty;
        }

        /// <summary>Keys of the displays a filter switches right now (same rules as switching).</summary>
        public static HashSet<string> ResolveTargetKeys(MonitorFilter filter)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (QueryActivePaths(out var paths, out var modes) != ERROR_SUCCESS) return keys;
            var matches = BuildMatcher(paths, modes, filter);
            foreach (var path in paths)
                if (matches(path)) keys.Add(MonitorKey(path));
            return keys;
        }

        /// <summary>
        /// For HDR: the display a window is on (by its HMONITOR). Returns its key if that display is
        /// HDR-capable and one of the monitors allowed in Settings; otherwise null with the reason.
        /// </summary>
        public static string? ResolveVideoMonitor(IntPtr hMonitor, MonitorFilter allowed, out string problem)
        {
            problem = string.Empty;
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
            if (hMonitor == IntPtr.Zero || !GetMonitorInfo(hMonitor, ref info) || string.IsNullOrEmpty(info.szDevice))
            {
                problem = "the monitor of the video window could not be determined";
                return null;
            }
            if (QueryActivePaths(out var paths, out var modes) != ERROR_SUCCESS)
            {
                problem = "the display configuration could not be read";
                return null;
            }

            var matches = BuildMatcher(paths, modes, allowed);
            foreach (var path in paths)
            {
                if (!string.Equals(GetSourceGdiName(path), info.szDevice, StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryGetHdrState(path.targetInfo.adapterId, path.targetInfo.id, out bool supported, out _) || !supported)
                    continue;   // cloned displays share a source: keep looking for an HDR-capable one
                if (!matches(path))
                {
                    problem = "该显示器未在 设置 › 显示器 中开启切换";
                    return null;
                }
                return MonitorKey(path);
            }
            if (problem.Length == 0) problem = "the video is on a monitor without HDR";
            return null;
        }

        /// <summary>Stable key of a display: its device path, or adapter+target id when Windows reports none.</summary>
        private static string MonitorKey(DISPLAYCONFIG_PATH_INFO path)
        {
            string devicePath = GetTargetName(path.targetInfo.adapterId, path.targetInfo.id).DevicePath;
            return devicePath.Length > 0 ? devicePath : $"{path.targetInfo.adapterId.LowPart}:{path.targetInfo.id}";
        }

        /// <summary>
        /// Decides which active display paths are switched. With a monitor selection: the selected ones; if none of the
        /// selected monitors is connected right now, it falls back to "primary only" / "all" instead of switching nothing.
        /// </summary>
        private static Func<DISPLAYCONFIG_PATH_INFO, bool> BuildMatcher(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes, MonitorFilter filter)
        {
            if (filter.DevicePaths != null)
            {
                var keys = paths.Select(MonitorKey).ToArray();
                if (keys.Any(filter.DevicePaths.Contains))
                {
                    var selected = new HashSet<string>(keys.Where(filter.DevicePaths.Contains), StringComparer.OrdinalIgnoreCase);
                    return p => selected.Contains(MonitorKey(p));
                }
            }
            return p => !filter.OnlyPrimary || IsPrimaryDisplayPath(p, modes);
        }

        /// <summary>All active displays (one entry per monitor), for the monitor list in Settings.</summary>
        public static List<MonitorInfo> ListMonitors()
        {
            var result = new List<MonitorInfo>();
            if (QueryActivePaths(out var paths, out var modes) != ERROR_SUCCESS) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = new List<(string Key, string Name, bool Primary, bool Supported, bool Enabled, int X, int Y)>();
            int number = 0;
            foreach (var path in paths)
            {
                number++;
                var (name, _) = GetTargetName(path.targetInfo.adapterId, path.targetInfo.id);
                string key = MonitorKey(path);
                if (!seen.Add(key)) continue;   // cloned displays appear once
                bool ok = TryGetHdrState(path.targetInfo.adapterId, path.targetInfo.id, out bool supported, out bool enabled);
                TryGetSourcePosition(path, modes, out int x, out int y);
                found.Add((key, string.IsNullOrWhiteSpace(name) ? $"Display {number}" : name.Trim(),
                           IsPrimaryDisplayPath(path, modes), ok && supported, ok && enabled, x, y));
            }

            // Two identical monitors have the same friendly name: tell them apart by where they sit on the desktop.
            // Only the label changes – the HDR switches are keyed by the device path.
            var labels = found.Select(f => f.Name).ToArray();
            foreach (var group in found.Select((f, i) => (f, i)).GroupBy(t => t.f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var members = group.OrderBy(t => t.f.X).ThenBy(t => t.f.Y).ToList();
                bool distinctX = members.Select(t => t.f.X).Distinct().Count() == members.Count;
                bool distinctY = members.Select(t => t.f.Y).Distinct().Count() == members.Count;
                if (members.Count == 2 && distinctX)
                {
                    labels[members[0].i] += " (left)";
                    labels[members[1].i] += " (right)";
                }
                else if (members.Count == 2 && distinctY)
                {
                    members = members.OrderBy(t => t.f.Y).ToList();
                    labels[members[0].i] += " (top)";
                    labels[members[1].i] += " (bottom)";
                }
                else
                {
                    for (int n = 0; n < members.Count; n++)
                        labels[members[n].i] += $" #{n + 1}";   // numbered left to right
                }
            }

            for (int i = 0; i < found.Count; i++)
            {
                var f = found[i];
                result.Add(new MonitorInfo
                {
                    DevicePath = f.Key,
                    Name = labels[i],
                    IsPrimary = f.Primary,
                    HdrSupported = f.Supported,
                    HdrEnabled = f.Enabled
                });
            }
            return result;
        }
        [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE setPacket);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 requestPacket);
        [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_HDR_STATE setPacket);

        // Windows 11 24H2 separated "HDR" from "advanced colour" (Auto Color Management, ACM). With the old call, an SDR
        // monitor that only supports ACM looks HDR-capable and switching it toggles ACM. The new calls report and switch
        // HDR itself. They are used when Windows supports them; older Windows versions use the old calls.
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;               // bit 4: highDynamicRangeSupported, bit 5: highDynamicRangeUserEnabled
            public int colorEncoding;
            public uint bitsPerColorChannel;
            public int activeColorMode;      // 0 = SDR, 1 = WCG, 2 = HDR
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_HDR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;               // bit 0: enableHdr
        }

        private const int ERROR_NOT_SUPPORTED = 50;
        private const int ERROR_INVALID_PARAMETER = 87;
        private static volatile int _hdrApi = -1;   // -1 unknown, 0 legacy (before 24H2), 1 new HDR API

        /// <summary>Which Windows HDR API is used (for the log).</summary>
        public static string HdrApiName => _hdrApi switch
        {
            1 => "modern HDR API (Windows 11 24H2 或更新版本)",
            0 => "classic 高级颜色 API",
            _ => "not determined yet"
        };

        /// <summary>HDR support / state of one display, preferring the 24H2+ API.</summary>
        private static bool TryGetHdrState(LUID adapterId, uint targetId, out bool supported, out bool enabled) =>
            TryGetHdrState(adapterId, targetId, out supported, out enabled, out _);

        /// <param name="usedNewApi">which API answered, so the switch uses the same one</param>
        private static bool TryGetHdrState(LUID adapterId, uint targetId, out bool supported, out bool enabled, out bool usedNewApi)
        {
            supported = false;
            enabled = false;
            usedNewApi = false;

            if (_hdrApi != 0)
            {
                var info2 = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2();
                info2.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_ADVANCED_COLOR_INFO_2;
                info2.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>();
                info2.header.adapterId = adapterId;
                info2.header.id = targetId;

                int rc = DisplayConfigGetDeviceInfo(ref info2);
                if (rc == ERROR_SUCCESS)
                {
                    _hdrApi = 1;
                    usedNewApi = true;
                    supported = (info2.value & 0x10) != 0;
                    enabled = supported && ((info2.value & 0x20) != 0 || info2.activeColorMode == 2);
                    return true;
                }

                // Only decide "old Windows" before the new API ever worked. Once it has worked, an error is transient
                // (e.g. a monitor disappearing during hot-plug or resume) and must not switch APIs for the session.
                if (_hdrApi == -1 && (rc == ERROR_INVALID_PARAMETER || rc == ERROR_NOT_SUPPORTED) && !IsWindows24H2OrNewer)
                    _hdrApi = 0;
                if (_hdrApi == 1)
                    return false;   // new API known to exist: don't mix in the classic one for this display
            }

            var info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
            info.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_ADVANCED_COLOR_INFO;
            info.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
            info.header.adapterId = adapterId;
            info.header.id = targetId;
            if (DisplayConfigGetDeviceInfo(ref info) != ERROR_SUCCESS) return false;

            supported = (info.value & 0x1) != 0;
            enabled = supported && (info.value & 0x2) != 0;
            return true;
        }

        // ------------------------------------------------------------------
        // Colour state diagnostics (what changed between "before HDR" and "after SDR")
        // ------------------------------------------------------------------
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr initData);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] ramp);
        [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

        /// <summary>Colour format, colour mode and gamma curve of every display a filter switches.</summary>
        public static List<DisplayColorState> CaptureColorState(MonitorFilter filter)
        {
            var result = new List<DisplayColorState>();
            if (QueryActivePaths(out var paths, out var modes) != ERROR_SUCCESS) return result;
            var matches = BuildMatcher(paths, modes, filter);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (!matches(path)) continue;
                string key = MonitorKey(path);
                if (!seen.Add(key)) continue;
                if (!TryGetHdrState(path.targetInfo.adapterId, path.targetInfo.id, out bool supported, out bool hdrOn) || !supported) continue;

                ReadColorInfo(path.targetInfo.adapterId, path.targetInfo.id, out int encoding, out uint bits, out int mode);
                string gdi = GetSourceGdiName(path);
                var (name, _) = GetTargetName(path.targetInfo.adapterId, path.targetInfo.id);
                result.Add(new DisplayColorState
                {
                    Key = key,
                    Name = string.IsNullOrWhiteSpace(name) ? gdi : name.Trim(),
                    GdiName = gdi,
                    HdrOn = hdrOn,
                    Encoding = encoding,
                    Bits = bits,
                    Mode = mode,
                    Ramp = ReadGammaRamp(gdi)
                });
            }
            return result;
        }

        private static void ReadColorInfo(LUID adapterId, uint targetId, out int encoding, out uint bits, out int mode)
        {
            encoding = -1; bits = 0; mode = -1;
            if (_hdrApi == 1)
            {
                var info2 = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2();
                info2.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_ADVANCED_COLOR_INFO_2;
                info2.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>();
                info2.header.adapterId = adapterId;
                info2.header.id = targetId;
                if (DisplayConfigGetDeviceInfo(ref info2) == ERROR_SUCCESS)
                {
                    encoding = info2.colorEncoding; bits = info2.bitsPerColorChannel; mode = info2.activeColorMode;
                    return;
                }
            }
            var info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
            info.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_ADVANCED_COLOR_INFO;
            info.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
            info.header.adapterId = adapterId;
            info.header.id = targetId;
            if (DisplayConfigGetDeviceInfo(ref info) == ERROR_SUCCESS)
            {
                encoding = info.colorEncoding; bits = info.bitsPerColorChannel;
            }
        }

        private static ushort[]? ReadGammaRamp(string gdiName)
        {
            if (string.IsNullOrEmpty(gdiName)) return null;
            IntPtr hdc = CreateDC("DISPLAY", gdiName, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return null;
            try
            {
                var ramp = new ushort[3 * 256];
                return GetDeviceGammaRamp(hdc, ramp) ? ramp : null;
            }
            finally
            {
                DeleteDC(hdc);
            }
        }

        /// <summary>Puts a display's gamma curve (calibration, night light, NVIDIA colour settings) back. No admin rights needed.</summary>
        public static bool RestoreGammaRamp(DisplayColorState state)
        {
            if (state.Ramp == null || string.IsNullOrEmpty(state.GdiName)) return false;
            IntPtr hdc = CreateDC("DISPLAY", state.GdiName, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;
            try { return SetDeviceGammaRamp(hdc, state.Ramp); }
            finally { DeleteDC(hdc); }
        }

        /// <summary>Windows 11 24H2 is build 26100; the new HDR requests exist from there on.</summary>
        private static readonly bool IsWindows24H2OrNewer = Environment.OSVersion.Version.Build >= 26100;

        /// <summary>Switches HDR on one display with the same API that reported its state.</summary>
        private static int SetHdrOnDisplay(LUID adapterId, uint targetId, bool enable, bool useNewApi)
        {
            if (useNewApi)
            {
                var set = new DISPLAYCONFIG_SET_HDR_STATE();
                set.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.SET_HDR_STATE;
                set.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_HDR_STATE>();
                set.header.adapterId = adapterId;
                set.header.id = targetId;
                set.value = enable ? 1u : 0u;
                return DisplayConfigSetDeviceInfo(ref set);
            }

            var legacy = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE();
            legacy.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.SET_ADVANCED_COLOR_STATE;
            legacy.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>();
            legacy.header.adapterId = adapterId;
            legacy.header.id = targetId;
            legacy.enableAdvancedColor = enable ? 1u : 0u;
            return DisplayConfigSetDeviceInfo(ref legacy);
        }

        public static string LastError { get; private set; } = string.Empty;

        /// <summary>
        /// Checks whether the display path belongs to the Windows primary monitor (position 0,0).
        /// </summary>
        private static bool IsPrimaryDisplayPath(DISPLAYCONFIG_PATH_INFO path, DISPLAYCONFIG_MODE_INFO[] modes)
            => TryGetSourcePosition(path, modes, out int posX, out int posY) && posX == 0 && posY == 0;

        /// <summary>Desktop position (top-left corner) of the display's source mode.</summary>
        private static bool TryGetSourcePosition(DISPLAYCONFIG_PATH_INFO path, DISPLAYCONFIG_MODE_INFO[] modes, out int posX, out int posY)
        {
            posX = posY = 0;
            uint idx = path.sourceInfo.modeInfoIdx;
            if (idx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID || idx >= modes.Length)
                return false;

            var mode = modes[idx];
            if (mode.infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                return false;

            // DISPLAYCONFIG_SOURCE_MODE layout inside union (u0, u1, u2):
            // u0: width (32 bit), height (32 bit)
            // u1: pixelFormat (32 bit), position.x (32 bit signed)
            // u2: position.y (32 bit signed)
            posX = (int)(mode.u1 >> 32);
            posY = (int)(mode.u2 & 0xFFFFFFFF);
            return true;
        }

        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        /// <summary>
        /// Reads the active display paths. Retries when the configuration changes between the two calls
        /// (monitor hot-plug, mode change), which Windows reports as ERROR_INSUFFICIENT_BUFFER.
        /// </summary>
        private static int QueryActivePaths(out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
        {
            paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
            modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();
            int rc = ERROR_SUCCESS;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
                if (rc != ERROR_SUCCESS) return rc;

                paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

                rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (rc == ERROR_SUCCESS)
                {
                    if (pathCount < paths.Length) Array.Resize(ref paths, (int)pathCount);
                    if (modeCount < modes.Length) Array.Resize(ref modes, (int)modeCount);
                    return rc;
                }
                if (rc != ERROR_INSUFFICIENT_BUFFER) return rc;
            }
            return rc;
        }

        public static bool SetHdrState(bool enableHdr, bool onlyPrimaryMonitor) =>
            SetHdrState(enableHdr, new MonitorFilter(onlyPrimaryMonitor, null));

        public static bool SetHdrState(bool enableHdr, MonitorFilter filter)
        {
            LastError = string.Empty;

            int rc = QueryActivePaths(out var paths, out var modes);
            if (rc != ERROR_SUCCESS)
            {
                LastError = $"Reading the display configuration failed (code {rc})";
                return false;
            }

            bool success = false;
            bool anyFailed = false;   // one display refusing counts as failure, so the engine's retry cooldown applies
            int supportedDisplays = 0;

            var matches = BuildMatcher(paths, modes, filter);
            foreach (var path in paths)
            {
                if (!matches(path))
                    continue;

                if (TryGetHdrState(path.targetInfo.adapterId, path.targetInfo.id, out bool isHdrSupported, out bool isHdrEnabled, out bool newApi))
                {
                    if (isHdrSupported)
                    {
                        supportedDisplays++;

                        if (isHdrEnabled == enableHdr)
                        {
                            success = true;
                            continue;
                        }

                        int res = SetHdrOnDisplay(path.targetInfo.adapterId, path.targetInfo.id, enableHdr, newApi);
                        if (res == ERROR_SUCCESS)
                        {
                            success = true;
                        }
                        else
                        {
                            anyFailed = true;
                            LastError = $"DisplayConfigSetDeviceInfo failed (code {res})";
                        }
                    }
                }
            }

            if (!success && string.IsNullOrEmpty(LastError))
            {
                LastError = supportedDisplays == 0
                    ? "No active HDR-capable display found among the monitors selected for switching"
                    : "HDR could not be switched";
            }
            return success && !anyFailed;
        }

        public static bool IsHdrCurrentlyEnabled(bool onlyPrimaryMonitor)
        {
            QueryHdrState(new MonitorFilter(onlyPrimaryMonitor, null), out bool anyEnabled, out _);
            return anyEnabled;
        }

        /// <summary>
        /// anyEnabled: at least one HDR-capable (primary) display has HDR on.
        /// allEnabled: every HDR-capable (primary) display has HDR on. Used so that in all-monitors mode a second
        /// monitor that is still in SDR also gets switched on. Returns false if the configuration could not be read.
        /// </summary>
        public static bool QueryHdrState(MonitorFilter filter, out bool anyEnabled, out bool allEnabled)
        {
            anyEnabled = false;
            allEnabled = false;

            if (QueryActivePaths(out var paths, out var modes) != ERROR_SUCCESS)
                return false;

            int supported = 0;
            int enabled = 0;

            var matches = BuildMatcher(paths, modes, filter);
            foreach (var path in paths)
            {
                if (!matches(path))
                    continue;

                if (TryGetHdrState(path.targetInfo.adapterId, path.targetInfo.id, out bool isHdrSupported, out bool isHdrEnabled) &&
                    isHdrSupported)
                {
                    supported++;
                    if (isHdrEnabled) enabled++;
                }
            }

            anyEnabled = enabled > 0;
            allEnabled = supported > 0 && enabled == supported;
            return true;
        }
    }

    // =========================================================================
    // 5. WINDOW, FULLSCREEN & HOTKEY DETECTOR
    // =========================================================================
    public static class WindowDetector
    {
        /// <summary>The monitor (HMONITOR) of the window in front.</summary>
        public static IntPtr ForegroundMonitor() => MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [ThreadStatic] private static StringBuilder? _titleBuffer;
        private static readonly object _pidLock = new();
        private static IntPtr _cachedHwnd;
        private static uint _cachedPid;
        private static string _cachedName = string.Empty;

        /// <summary>
        /// Process.GetProcessById opens a process handle and builds a Process object. The foreground process rarely
        /// changes, so the name is only looked up again when the foreground window or its PID changes
        /// (keying on the window as well avoids returning a stale name if Windows reuses a PID).
        /// </summary>
        private static string GetProcessNameCached(IntPtr hWnd, uint pid)
        {
            if (pid == 0) return string.Empty;

            lock (_pidLock)
            {
                if (hWnd == _cachedHwnd && pid == _cachedPid && _cachedName.Length > 0)
                    return _cachedName;
            }

            string name = ResolveProcessName(pid);

            if (name.Length > 0)
            {
                lock (_pidLock)
                {
                    _cachedHwnd = hWnd;
                    _cachedPid = pid;
                    _cachedName = name;
                }
            }
            return name;
        }

        /// <summary>Full path of the foreground window's executable, or null (e.g. for elevated processes).</summary>
        public static string? GetForegroundExePath()
        {
            try
            {
                IntPtr hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero) return null;
                GetWindowThreadProcessId(hWnd, out uint pid);
                return ProcessPaths.TryGetExePath((int)pid);   // limited access only (anti-cheat safe)
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveProcessName(uint pid)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                return proc.ProcessName.ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        public static (string ProcessName, string WindowTitle, bool IsFullscreen) GetActiveWindowState()
        {
            var (processName, title, isFullscreen, _) = GetActiveWindowInfo();
            return (processName, title, isFullscreen);
        }

        /// <summary>Like <see cref="GetActiveWindowState"/>, plus the process id of the same window (0 = no window).</summary>
        public static (string ProcessName, string WindowTitle, bool IsFullscreen, int ProcessId) GetActiveWindowInfo()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return (string.Empty, string.Empty, false, 0);

            // One reusable buffer per thread instead of a new StringBuilder 10x per second
            var sb = _titleBuffer ??= new StringBuilder(512);
            sb.Clear();
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            GetWindowThreadProcessId(hWnd, out uint pid);
            string processName = GetProcessNameCached(hWnd, pid);

            bool isFullscreen = processName != "explorer" && processName != "searchhost" && IsWindowFullscreen(hWnd);

            return (processName, title, isFullscreen, (int)pid);
        }

        private static bool IsWindowFullscreen(IntPtr hWnd)
        {
            if (!GetWindowRect(hWnd, out RECT winRect)) return false;

            IntPtr hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            var monInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref monInfo)) return false;

            int winWidth = winRect.Right - winRect.Left;
            int winHeight = winRect.Bottom - winRect.Top;
            int monWidth = monInfo.rcMonitor.Right - monInfo.rcMonitor.Left;
            int monHeight = monInfo.rcMonitor.Bottom - monInfo.rcMonitor.Top;
            return winWidth >= monWidth && winHeight >= monHeight;
        }

        private const int WM_DISPLAYCHANGE = 0x007E;
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
                           SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200, SWP_ASYNCWINDOWPOS = 0x4000;
        private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_FRAME = 0x0400;

        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

        /// <summary>
        /// Posts WM_DISPLAYCHANGE to all top-level windows. Switching HDR off does not change the resolution, so Windows
        /// often does not send it; Chromium-based apps (Chrome, Edge, Discord, VS Code, ...) re-read the display's colour
        /// space and SDR brightness when they receive it. Non-blocking (PostMessage), so hung windows cannot stall us.
        /// </summary>
        public static void BroadcastDisplayChange()
        {
            int width = GetSystemMetrics(0);    // SM_CXSCREEN
            int height = GetSystemMetrics(1);   // SM_CYSCREEN
            var lParam = new IntPtr((height << 16) | (width & 0xFFFF));
            PostMessage(HWND_BROADCAST, WM_DISPLAYCHANGE, new IntPtr(32), lParam);
        }

        /// <summary>
        /// Forces visible, non-minimized windows of the given processes to recalculate their frame and repaint
        /// (no move, resize or focus change). Returns the number of windows nudged.
        /// </summary>
        public static int NudgeWindows(ICollection<string> processNames)
        {
            if (processNames.Count == 0) return 0;
            int count = 0;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!processNames.Contains(ResolveProcessName(pid))) return true;

                SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED |
                    SWP_ASYNCWINDOWPOS);   // async: a hung window cannot block us
                RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN);
                count++;
                return true;
            }, IntPtr.Zero);
            return count;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

        /// <summary>
        /// Looks through all visible, non-minimized top-level windows for a browser window whose title contains the
        /// token (i.e. a tab with a playing HDR video is the active tab of that window). Returns the browser's
        /// process name, or null. Only called while HDR is on and the focused window is not HDR content.
        /// </summary>
        public static string? FindBrowserWindowWithToken(ICollection<string> browserNames, string token, bool requireFullscreen)
        {
            if (string.IsNullOrEmpty(token) || browserNames.Count == 0) return null;

            string? found = null;
            var sb = _titleBuffer ??= new StringBuilder(512);

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;

                sb.Clear();
                if (GetWindowText(hWnd, sb, sb.Capacity) < token.Length) return true;
                if (sb.ToString().IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) return true;
                if (requireFullscreen && !IsWindowFullscreen(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                string name = ResolveProcessName(pid);   // not the foreground cache, so that one stays valid
                if (browserNames.Contains(name))
                {
                    found = name;
                    return false;   // stop enumerating
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }
    }

}
