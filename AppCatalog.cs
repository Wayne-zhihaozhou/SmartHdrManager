using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace SmartHdrManager
{
    // =========================================================================
    // APPS TAB (2.6): every program that is not a game but that the app reacts to – video players, web browsers,
    // creative apps that need SDR, recording / streaming apps – with one mode per program.
    // Programs that are not listed (and not in the Games tab) are never touched.
    // =========================================================================

    public static class AppCategories
    {
        public const string Player = "player";
        public const string Browser = "browser";
        public const string Creative = "creative";
        public const string Recording = "recording";

        public static string? Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            Player => Player,
            Browser => Browser,
            Creative => Creative,
            Recording => Recording,
            _ => null
        };
    }

    public static class AppModes
    {
        public const string Auto = "auto";           // players / browsers: as set in Settings › Videos (and the YouTube script)
        public const string HdrOnly = "hdronly";     // players / browsers: only HDR videos switch HDR on
        public const string Sdr = "sdr";             // creative apps: switch to SDR while the app is in front
        public const string NoVideoHdr = "novideo";  // test builds of 2.6 only ("no HDR for videos") – read as "off"
        public const string NoHdr = "nohdr";         // recording apps: nothing switches HDR on while the app runs
        public const string Off = "off";             // don't touch HDR for this program

        public static string Normalize(string category, string? mode)
        {
            string m = (mode ?? string.Empty).Trim().ToLowerInvariant();
            return category switch
            {
                AppCategories.Player or AppCategories.Browser => m is HdrOnly or Off ? m : Auto,
                AppCategories.Creative => m == Off ? Off : Sdr,
                AppCategories.Recording => m == NoHdr ? NoHdr : Off,
                _ => Off
            };
        }
    }

    /// <summary>One program in the Apps tab (config.json "AppRules", keyed by process name in lower case).</summary>
    public sealed class AppRule
    {
        public string Category { get; set; } = AppCategories.Player;
        public string Mode { get; set; } = AppModes.Auto;
        public string? ExePath { get; set; }      // added by hand (shown in the Apps tab)
        public bool AddedByUser { get; set; }     // added by hand: can be removed from the list again
    }

    /// <summary>An installed program from the built-in catalogue.</summary>
    public sealed class FoundApp
    {
        public string Process { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string ExePath { get; init; } = string.Empty;
        public string DefaultMode { get; init; } = string.Empty;
    }

    public static class AppCatalog
    {
        private sealed record Entry(string Name, string Category, string Exe, string DefaultMode, string[] Paths, string[] Hints);

        // Default modes: the programs that 2.5 already handled keep their behaviour; programs new in this list start
        // with "don't touch" (except browsers, which only react to the browser script anyway).
        private static readonly Entry[] Known =
        {
            // ---- web browsers ----
            new("Google Chrome", AppCategories.Browser, "chrome.exe", AppModes.Auto,
                new[] { @"%ProgramFiles%\Google\Chrome\Application\chrome.exe", @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe", @"%LocalAppData%\Google\Chrome\Application\chrome.exe" },
                new[] { "Google Chrome" }),
            new("Microsoft Edge", AppCategories.Browser, "msedge.exe", AppModes.Auto,
                new[] { @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe", @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe" },
                new[] { "Microsoft Edge" }),
            new("Mozilla Firefox", AppCategories.Browser, "firefox.exe", AppModes.Auto,
                new[] { @"%ProgramFiles%\Mozilla Firefox\firefox.exe", @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe" },
                new[] { "Mozilla Firefox" }),
            new("Brave", AppCategories.Browser, "brave.exe", AppModes.Auto,
                new[] { @"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe", @"%LocalAppData%\BraveSoftware\Brave-Browser\Application\brave.exe" },
                new[] { "Brave" }),
            new("Opera", AppCategories.Browser, "opera.exe", AppModes.Auto,
                new[] { @"%LocalAppData%\Programs\Opera\opera.exe", @"%LocalAppData%\Programs\Opera GX\opera.exe" },
                new[] { "Opera" }),
            new("Vivaldi", AppCategories.Browser, "vivaldi.exe", AppModes.Auto,
                new[] { @"%LocalAppData%\Vivaldi\Application\vivaldi.exe", @"%ProgramFiles%\Vivaldi\Application\vivaldi.exe" },
                new[] { "Vivaldi" }),

            // ---- creative apps (colour-critical: SDR while in front) ----
            new("Adobe Photoshop", AppCategories.Creative, "Photoshop.exe", AppModes.Sdr,
                new[] { @"%ProgramFiles%\Adobe\Adobe Photoshop*\Photoshop.exe" }, new[] { "Adobe Photoshop" }),
            new("Adobe Lightroom", AppCategories.Creative, "Lightroom.exe", AppModes.Sdr,
                new[] { @"%ProgramFiles%\Adobe\Adobe Lightroom Classic\Lightroom.exe", @"%ProgramFiles%\Adobe\Adobe Lightroom\lightroom.exe" },
                new[] { "Lightroom" }),
            new("Adobe Premiere Pro", AppCategories.Creative, "Adobe Premiere Pro.exe", AppModes.Sdr,
                new[] { @"%ProgramFiles%\Adobe\Adobe Premiere Pro*\Adobe Premiere Pro.exe" }, new[] { "Adobe Premiere Pro" }),
            new("Adobe After Effects", AppCategories.Creative, "AfterFX.exe", AppModes.Sdr,
                new[] { @"%ProgramFiles%\Adobe\Adobe After Effects*\Support Files\AfterFX.exe" }, new[] { "Adobe After Effects" }),
            new("DaVinci Resolve", AppCategories.Creative, "Resolve.exe", AppModes.Sdr,
                new[] { @"%ProgramFiles%\Blackmagic Design\DaVinci Resolve\Resolve.exe" }, new[] { "DaVinci Resolve" }),
            new("Adobe Illustrator", AppCategories.Creative, "Illustrator.exe", AppModes.Off,
                new[] { @"%ProgramFiles%\Adobe\Adobe Illustrator*\Support Files\Contents\Windows\Illustrator.exe" }, new[] { "Adobe Illustrator" }),
            new("Adobe Media Encoder", AppCategories.Creative, "Adobe Media Encoder.exe", AppModes.Off,
                new[] { @"%ProgramFiles%\Adobe\Adobe Media Encoder*\Adobe Media Encoder.exe" }, new[] { "Adobe Media Encoder" }),
            new("Capture One", AppCategories.Creative, "CaptureOne.exe", AppModes.Off,
                new[] { @"%ProgramFiles%\Capture One\Capture One*\CaptureOne.exe" }, new[] { "Capture One" }),
            new("Krita", AppCategories.Creative, "krita.exe", AppModes.Off,
                new[] { @"%ProgramFiles%\Krita (x64)\bin\krita.exe" }, new[] { "Krita" }),

            // ---- recording & streaming ----
            new("OBS Studio", AppCategories.Recording, "obs64.exe", AppModes.Off,
                new[] { @"%ProgramFiles%\obs-studio\bin\64bit\obs64.exe" }, new[] { "OBS Studio" }),
            new("Streamlabs Desktop", AppCategories.Recording, "Streamlabs OBS.exe", AppModes.Off,
                new[] { @"%ProgramFiles%\Streamlabs OBS\Streamlabs OBS.exe" }, new[] { "Streamlabs" }),
        };

        // Video players the app knows (the Videos / Apps tab finds them): not "added by you" when moved over from 2.5
        private static readonly HashSet<string> KnownPlayers = new(StringComparer.OrdinalIgnoreCase)
        {
            "vlc", "mpc-hc64", "mpc-hc", "mpc-be64", "mpc-be", "potplayermini64", "potplayermini", "mpv", "mpvnet", "kodi"
        };

        /// <summary>Process name of an exe path or file name: "C:\…\Adobe Premiere Pro.exe" -> "adobe premiere pro".</summary>
        public static string ProcessOf(string exe) => Path.GetFileNameWithoutExtension(exe.Trim().Trim('"')).ToLowerInvariant();

        /// <summary>The list in 2.6 for a new config.json (same programs as the default lists of 2.5).</summary>
        public static Dictionary<string, AppRule> DefaultRules()
        {
            var rules = new Dictionary<string, AppRule>(StringComparer.OrdinalIgnoreCase);
            foreach (string b in new[] { "chrome", "msedge", "firefox", "brave" })
                rules[b] = new AppRule { Category = AppCategories.Browser, Mode = AppModes.Auto };
            foreach (string p in new[] { "vlc", "mpc-hc64", "potplayermini64", "mpv", "mpvnet", "mpc-be64", "kodi" })
                rules[p] = new AppRule { Category = AppCategories.Player, Mode = AppModes.Auto };
            foreach (string c in new[] { "photoshop", "lightroom", "resolve", "adobe premiere pro", "afterfx" })
                rules[c] = new AppRule { Category = AppCategories.Creative, Mode = AppModes.Sdr };
            foreach (string r in new[] { "obs64", "obs32" })
                rules[r] = new AppRule { Category = AppCategories.Recording, Mode = AppModes.Off };
            return rules;
        }

        /// <summary>
        /// Update from 2.5: builds the list from the old config.json lists (Browsers, LocalVideoPlayers, ForceSdrApps,
        /// VideoHdrBlockingApps + "No HDR while OBS is running", players added in the Videos tab).
        /// </summary>
        public static Dictionary<string, AppRule> FromLegacy(AppConfig c)
        {
            var rules = new Dictionary<string, AppRule>(StringComparer.OrdinalIgnoreCase);
            var defaults = DefaultRules();
            void Add(IEnumerable<string>? names, string category, string mode)
            {
                foreach (string n in names ?? Enumerable.Empty<string>())
                {
                    string key = (n ?? string.Empty).Trim().ToLowerInvariant();
                    if (key.Length == 0 || rules.ContainsKey(key)) continue;
                    // A name that is neither a default nor a known program was typed into config.json by hand:
                    // show it in the Apps tab as "added by you" (so it can be seen, changed and removed)
                    bool byHand = !defaults.ContainsKey(key) && !IsKnown(key) && !KnownPlayers.Contains(key);
                    rules[key] = new AppRule { Category = category, Mode = mode, AddedByUser = byHand };
                }
            }

            Add(c.Browsers, AppCategories.Browser, AppModes.Auto);
            Add(c.LocalVideoPlayers, AppCategories.Player, AppModes.Auto);
            // "premiere" never matched anything (Premiere Pro's process is "Adobe Premiere Pro")
            Add(c.ForceSdrApps?.Where(n => !string.Equals(n?.Trim(), "premiere", StringComparison.OrdinalIgnoreCase)),
                AppCategories.Creative, AppModes.Sdr);
            // "No HDR while OBS is running" on: No HDR. Off: OBS changes nothing (2.5 only kept videos in SDR meanwhile)
            Add(c.VideoHdrBlockingApps, AppCategories.Recording, c.NoHdrWhileObsRunning ? AppModes.NoHdr : AppModes.Off);

            // Players added by hand in the Videos tab were only used for "Launch with HDR" – now they are watched too
            foreach (string path in c.HdrPlayerPaths ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string key = ProcessOf(path);
                if (key.Length > 0 && !rules.ContainsKey(key))
                    rules[key] = new AppRule { Category = AppCategories.Player, Mode = AppModes.Auto, ExePath = path, AddedByUser = true };
            }
            return rules;
        }

        /// <summary>
        /// Keeps the 2.5 lists in config.json in step with the Apps tab (they are no longer read by 2.6, but an older
        /// version started after this one then still finds sensible lists).
        /// </summary>
        public static void WriteLegacyLists(AppConfig c)
        {
            var rules = c.AppRules ?? new Dictionary<string, AppRule>();
            List<string> Names(Func<AppRule, bool> match) =>
                rules.Where(kv => kv.Value != null && match(kv.Value)).Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

            c.Browsers = Names(r => r.Category == AppCategories.Browser && r.Mode != AppModes.Off);
            c.LocalVideoPlayers = Names(r => r.Category == AppCategories.Player && r.Mode != AppModes.Off);
            c.ForceSdrApps = Names(r => r.Category == AppCategories.Creative && r.Mode == AppModes.Sdr);
            c.VideoHdrBlockingApps = Names(r => r.Category == AppCategories.Recording && r.Mode != AppModes.Off);
            c.NoHdrWhileObsRunning = rules.Values.Any(r => r != null && r.Category == AppCategories.Recording && r.Mode == AppModes.NoHdr);
        }

        /// <summary>Display name: the catalogue name, else the exe's description, else the process name.</summary>
        public static string NameFor(string process, string? exePath)
        {
            foreach (var e in Known)
                if (string.Equals(ProcessOf(e.Exe), process, StringComparison.OrdinalIgnoreCase)) return e.Name;
            if (!string.IsNullOrEmpty(exePath))
            {
                try
                {
                    string? description = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
                    if (!string.IsNullOrWhiteSpace(description) && description.Trim().Length <= 40) return description.Trim();
                }
                catch { }
            }
            return process;
        }

        /// <summary>True for programs of the built-in catalogue (browsers, creative and recording apps).</summary>
        public static bool IsKnown(string process) =>
            Known.Any(e => string.Equals(ProcessOf(e.Exe), process, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Installed browsers, creative and recording apps (video players: <see cref="VideoPlayers.Find"/>).
        /// Registry and a few folders only – takes a few milliseconds. One entry per process name.
        /// </summary>
        public static List<FoundApp> Find()
        {
            var result = new List<FoundApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uninstall = ReadUninstallEntries();

            var running = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // process -> exe path
            try
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        string name = proc.ProcessName.ToLowerInvariant();
                        if (!running.ContainsKey(name) && Known.Any(e => ProcessOf(e.Exe) == name))
                        {
                            string? path = ProcessPaths.TryGetExePath(proc.Id);   // limited access only
                            if (path != null) running[name] = path;
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }
            catch { }

            foreach (var e in Known)
            {
                string process = ProcessOf(e.Exe);
                if (seen.Contains(process)) continue;

                string? found = AppPath(e.Exe);
                if (!Exists(found))
                    found = e.Paths.Select(ExpandPattern).FirstOrDefault(Exists);
                if (!Exists(found))
                {
                    foreach (var (displayName, location, icon) in uninstall)
                    {
                        if (!e.Hints.Any(h => displayName.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;
                        if (location.Length > 0 && Exists(Combine(location, e.Exe))) { found = Combine(location, e.Exe); break; }
                        if (icon.Length > 0 && string.Equals(Path.GetFileName(icon), e.Exe, StringComparison.OrdinalIgnoreCase) && Exists(icon))
                        {
                            found = icon;
                            break;
                        }
                    }
                }
                if (!Exists(found) && running.TryGetValue(process, out var runningPath)) found = runningPath;
                if (!Exists(found)) continue;

                seen.Add(process);
                result.Add(new FoundApp { Process = process, Name = e.Name, Category = e.Category, ExePath = found!, DefaultMode = e.DefaultMode });
            }
            return result;
        }

        /// <summary>Suggested mode of a catalogue program (null when it is not in the catalogue).</summary>
        public static string? DefaultModeFor(string process)
        {
            foreach (var e in Known)
                if (string.Equals(ProcessOf(e.Exe), process, StringComparison.OrdinalIgnoreCase)) return e.DefaultMode;
            return null;
        }

        private static bool Exists(string? path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
            catch { return false; }
        }

        private static string Combine(string dir, string file)
        {
            try { return Path.Combine(dir, file); }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Expands environment variables and one "*" folder pattern, e.g. "%ProgramFiles%\Adobe\Adobe Photoshop*\Photoshop.exe".
        /// Several matches: the newest version (highest folder name, e.g. "… 2026" before "… 2025").
        /// </summary>
        private static string? ExpandPattern(string pattern)
        {
            try
            {
                string path = Environment.ExpandEnvironmentVariables(pattern);
                if (path.Contains('%')) return null;   // variable not defined (e.g. no ProgramFiles(x86) on ARM)
                int star = path.IndexOf('*');
                if (star < 0) return path;

                int segmentStart = path.LastIndexOf('\\', star) + 1;
                int segmentEnd = path.IndexOf('\\', star);
                if (segmentStart <= 0 || segmentEnd < 0) return null;
                string parent = path.Substring(0, segmentStart - 1);
                string segment = path.Substring(segmentStart, segmentEnd - segmentStart);
                string rest = path.Substring(segmentEnd + 1);
                if (!Directory.Exists(parent)) return null;

                foreach (string dir in Directory.GetDirectories(parent, segment).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string candidate = Path.Combine(dir, rest);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return null;
        }

        // "App Paths": where installers register their exe (per user first, then for all users)
        private static string? AppPath(string exe)
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                    if (key?.GetValue(null) is string path && path.Length > 0) return path.Trim().Trim('"');
                }
                catch { }
            }
            return null;
        }

        // "Apps & features" entries: name, install folder, icon (often the program's exe)
        private static List<(string Name, string Location, string Icon)> ReadUninstallEntries()
        {
            var list = new List<(string, string, string)>();
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (string path in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                {
                    try
                    {
                        using var key = root.OpenSubKey(path);
                        if (key == null) continue;
                        foreach (string sub in key.GetSubKeyNames())
                        {
                            try
                            {
                                using var entry = key.OpenSubKey(sub);
                                string name = entry?.GetValue("DisplayName") as string ?? string.Empty;
                                if (name.Length == 0) continue;
                                string location = (entry?.GetValue("InstallLocation") as string ?? string.Empty).Trim().Trim('"');
                                string icon = (entry?.GetValue("DisplayIcon") as string ?? string.Empty).Trim();
                                int comma = icon.LastIndexOf(',');
                                if (comma > 0 && !icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) icon = icon.Substring(0, comma);
                                list.Add((name, location, icon.Trim().Trim('"')));
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            return list;
        }
    }
}
