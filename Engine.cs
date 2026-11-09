using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SmartHdrManager
{
    public enum HdrOverride { None, ForceHdr, ForceSdr }

    public sealed class EngineStatus
    {
        public bool HdrOn { get; init; }
        public bool Paused { get; init; }
        public string ActiveProcess { get; init; } = string.Empty;
        public bool IsFullscreen { get; init; }
        public string? RunningGame { get; init; }
        public HdrOverride Override { get; init; }
        public long OverrideRemainingMs { get; init; } = -1;   // -1 = until turned back
        public string? WaitingForGame { get; init; }            // per-game start delay in progress
        public int WaitingSecondsLeft { get; init; }
        public int HoldSecondsLeft { get; init; }               // minimum HDR time / keep-after-exit in progress
        public bool BlockedBy { get; init; }
        public bool LaunchHold { get; init; }                   // "Launch with HDR": HDR is held for a game being started
        public string? FollowingLauncher { get; init; }         // a launcher closed: waiting for the game it starts
    }

    /// <summary>One row of the Games tab: a library game (several processes possible) or a single program.</summary>
    public sealed class GameRow
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
        public List<string> Processes { get; init; } = new();
        public string? ExePath { get; init; }
        public bool Hdr { get; init; }
        public int Brightness { get; init; } = 50;
    }

    /// <summary>
    /// The HDR/SDR decision loop. Runs on its own background thread and owns every display switch.
    /// The UI only changes the configuration (under <see cref="Sync"/>) or posts work with <see cref="Post"/>.
    ///
    /// Cost per tick (default every 100 ms): one foreground-window lookup with a cached process name.
    /// The process list is scanned once per second and the display HDR state is read every 2 seconds,
    /// or immediately after a switch, a display change or resume from sleep.
    /// </summary>
    public sealed class HdrEngine
    {
        /// <summary>Old location (next to the exe). Not writable when the app is installed in "Program Files".</summary>
        public static readonly string LegacyConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");

        /// <summary>
        /// Portable mode: a file named "portable.txt" next to the exe keeps the settings next to the exe
        /// (e.g. for a USB stick). Otherwise settings live in %AppData%\SmartHdrManager\config.json.
        /// </summary>
        public static readonly bool IsPortable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

        public static readonly string ConfigPath = IsPortable
            ? LegacyConfigPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartHdrManager", "config.json");
        public static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartHdrManager", "log.txt");

        private const int ProcessScanIntervalMs = 1000;   // how often running processes are checked for HDR games
        private const int HdrRequeryIntervalMs = 2000;    // how often the real HDR state is re-read from Windows
        private const int SwitchSpacingMs = 1500;         // minimum time between two automatic switch attempts
        private const int FailuresBeforeCooldown = 3;
        private const int FailureCooldownMs = 30000;      // stop retrying for 30 s after repeated failures
        private const int StartupScanDelayMs = 20000;     // let Windows finish logging in before scanning drives

        private const int DisplayEventGraceMs = 5000;     // HDR lost this soon after a display change / wake-up = not a user decision
        private const int QuickExitMs = 12000;            // a game closing this soon after HDR came on may have crashed (crash hint)
        private const int QuickExitClearMs = 60000;       // a game that ran this long with HDR is fine again (hint cleared)


        // A watched game that closes this soon after it appeared may only have been a launcher (REDprelauncher,
        // PlayGTAV, …): HDR stays on while the app waits for a program from the same folder to take over.
        private const int LauncherShortRunMs = 15000;
        private const int LauncherFollowMs = 45000;   // Steam can take 15 s or more to hand over (seen with The Witcher 3)

        private static readonly object FileLogLock = new();
        private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

        public AppConfig Config { get; private set; }
        public object Sync { get; } = new();
        public volatile bool Paused;
        public EngineStatus Status { get; private set; } = new();

        private Thread? _thread;
        private volatile bool _stop;
        private readonly AutoResetEvent _wake = new(false);
        private readonly ConcurrentQueue<Action> _posted = new();

        // ---- precomputed lookup sets, rebuilt only when the configuration changes ----
        private sealed class Snapshot
        {
            public readonly HashSet<string> HdrGames;
            public readonly bool EnableOnProcessStart, ReloadProfile, RefreshApps;
            public readonly int FallbackSeconds, TickMs;
            public readonly MonitorFilter Monitors;
            public readonly Dictionary<string, (string[] Folders, string[] Exes)> GameFolders;

            public Snapshot(AppConfig c)
            {
                HdrGames = new HashSet<string>(c.HdrGamesAndApps ?? new List<string>(), Cmp);
                HdrGames.RemoveWhere(process => !IsHdrEnabledFor(c.GameHdrModes, process));
                ReloadProfile = c.ReloadColorProfileAfterSdr;
                RefreshApps = c.RefreshAppsAfterSdr;
                Monitors = new MonitorFilter(c.OnlyPrimaryMonitor, c.SelectedMonitors);
                EnableOnProcessStart = c.EnableHdrOnProcessStart;
                FallbackSeconds = Math.Max(0, c.SdrFallbackDelaySeconds);
                TickMs = Math.Clamp(c.ProcessScanIntervalMs, 50, 1000);
                GameFolders = BuildGameFolders(c, HdrGames);
            }

            /// <summary>
            /// Install folders and known exes of the HDR games from the launcher libraries, by process name. Strings only:
            /// the snapshot is built under the config lock, so nothing here touches the disk (a sleeping drive or an
            /// offline NAS would block the UI). Programs added by hand are not listed and stay matched by name.
            /// </summary>
            private static Dictionary<string, (string[] Folders, string[] Exes)> BuildGameFolders(AppConfig c, HashSet<string> hdrGames)
            {
                var folders = new Dictionary<string, HashSet<string>>(Cmp);
                var exes = new Dictionary<string, HashSet<string>>(Cmp);
                void Add(string process, string? exePath)
                {
                    if (string.IsNullOrEmpty(exePath)) return;
                    try
                    {
                        string? folder = InstallFolder(exePath);
                        if (string.IsNullOrEmpty(folder)) return;
                        if (!folders.TryGetValue(process, out var set)) folders[process] = set = new HashSet<string>(Cmp);
                        set.Add(folder.TrimEnd('\\'));
                        if (!exes.TryGetValue(process, out var files)) exes[process] = files = new HashSet<string>(Cmp);
                        files.Add(Path.GetFullPath(exePath));
                    }
                    catch { }
                }

                // Library exes only: ProgramPaths holds the first exe of that name found anywhere (drive scan, Game Bar,
                // program caches), which may be the other program. A moved game passes because its old exe is gone.
                foreach (var g in c.LibraryGames ?? new List<LibraryGame>())
                    foreach (string p in g.Processes ?? new List<string>())
                        if (hdrGames.Contains(p)) Add(p, g.ExePath);   // the game's other exes (Unreal shipping exe, learned games) live in the same folder
                return folders.ToDictionary(kv => kv.Key, kv => (kv.Value.ToArray(), exes[kv.Key].ToArray()), Cmp);
            }

            /// <summary>
            /// The game's install folder for an exe: its game folder, or the folder above when the exe sits in one of the
            /// game's own subfolders ("boot\", "Launcher\", "game\", "_retail_\"). Never a shared folder.
            /// </summary>
            private static string? InstallFolder(string exePath)
            {
                string? folder = HdrLauncher.GameFolder(exePath) ?? Path.GetDirectoryName(Path.GetFullPath(exePath));
                if (string.IsNullOrEmpty(folder)) return null;
                string name = Path.GetFileName(folder);
                if (!name.StartsWith('_') && !GameSubfolders.Contains(name)) return folder;
                return HdrLauncher.GameFolder(folder) ?? folder;   // GameFolder of a folder: its parent, same checks
            }

            private static readonly HashSet<string> GameSubfolders = new(StringComparer.OrdinalIgnoreCase)
            {
                "boot", "launcher", "game", "client", "retailclient", "system", "engine", "content", "live", "release", "shipping"
            };
        }

        /// <summary>The Games tab switch of one process: on unless the config disables it.</summary>
        private static bool IsHdrEnabledFor(Dictionary<string, bool>? modes, string process) =>
            modes == null || !modes.TryGetValue(process, out bool enabled) || enabled;

        private volatile Snapshot? _snapshot;
        private Snapshot? _lastSnapshot;

        // ---- decision state (engine thread only) ----
        private bool _hdrAny, _hdrAll;
        private bool _verifyAllOn;   // after a "successful" ON: confirm on the next re-read that every display really is on
        private long _nextHdrQueryMs;
        private int _forceHdrQuery = 1;
        private MonitorFilter? _queriedMonitors;
        private long _hdrOnSinceMs;            // when HDR was last switched/seen on (0 = off)
        private bool _firstHdrQueryDone;
        private bool _apiLogged;

        // manual override (written from any thread, read by the engine thread)
        private volatile int _overrideMode;    // (int)HdrOverride
        private long _overrideUntilMs;         // 0 = until turned back
        private readonly object _overrideLock = new();
        private int _overrideChanged;          // 1 = engine thread must reset its timers (set by any thread)

        // per-game options (removed)
        private long _holdHdrUntilMs;          // keep-after-exit
        private long _holdMsLeft;

        private string? _runningGame;
        private long _nextProcessScanMs;
        private string? _activeHdrGameProcess;



        // "Launch with HDR" (SmartHdrManager.exe --launch): a launcher process keeps signalling while its game runs
        private const int LaunchHoldMs = 6000;   // the launcher signals every 2 s; HDR is held 6 s after the last signal
        private long _launchHoldUntilMs;
        private bool _launchHoldLogged;
        private int _launchReleased;     // set by ReleaseLaunchHold (any thread)
        private bool _launchEnded;       // switch back to SDR right away, no "return to SDR after" wait
        private string? _monitorCheckedGame;   // multi-monitor hint: game already checked

        // Launchers (2.5.2): a watched game that closes within seconds may only have started the real game
        private string? _seenGame;             // HDR game found by the last process scan
        private long _seenGameSinceMs;
        private bool _launcherTrackingStarted; // false until the first process scan after the app started
        private string? _followLauncher;       // launcher that just closed: waiting for its game (HDR stays on meanwhile)
        private long _followUntilMs;
        private bool _followHoldsHdr;          // HDR was on when the launcher closed: keep it on while waiting
        private DateTime _followNotBefore;     // the game it starts must be newer than the launcher
        private int _followGeneration;         // a new follow ignores the folder list of an older one
        private volatile HashSet<string>? _followNames;   // exe names in the launcher's game folder (filled in the background)

        // HDR switched on by this app (and not off since): switched back to SDR when the app exits (2.5.2).
        // 2.6: the app only ever switches back HDR that it turned on itself (unless "Keep the desktop in SDR" is on).
        private bool _hdrOnByApp;

        // Ownership (2.6). "Demand" = what wants HDR right now, e.g. "game:witcher3", "video:…", "sdr:photoshop", "none".
        // When HDR is changed by something else (you, a game, another program), the app keeps its hands off until the
        // demand changes – no switching back, no switching on again – so it never fights you.
        private string? _handOffDemand;
        private string _lastDemand = "none";
        private bool? _expectAny;                   // HDR state after the app's own last switch (or the last state seen)
        private bool _switchConfirmed = true;       // Windows has reported that state since the switch
        private long _displayEventMs = long.MinValue / 2;   // last display change / wake-up (any thread, Interlocked)
        private string? _monitorSignature;          // connected monitors (device paths) at the last display event
        private readonly object _monitorSignatureLock = new();
        private const int RestoreAfterMs = 3000;            // no flip-flop while e.g. a splash screen hands over to the app
        private long _restoreAtMs;                         // when to turn HDR back on after SDR app closes
        private bool _restoreHdr;                   // HDR that was not ours was switched off for an SDR / recording app
        private string _restoreFor = string.Empty;  // ... for this program: HDR comes back on once it is gone
        private bool _externalEnded;                // the full-screen game that switched HDR on itself has closed
        private string _externalEndedWhat = string.Empty;

        // Crash hint (2.6): a game that closes within seconds after the app switched HDR on for it
        private string? _hdrOnForGame;
        private long _hdrOnForGameAtMs;
        private string? _quickExitGame;             // waiting for the launcher check before it counts
        private long _quickExitAfterMs;
        private readonly ConcurrentQueue<(string Title, string Text)> _notifications = new();

        // Automatic drive scan (2.6: interval setting, also checked while the app runs)




        // HDR switched by something else (game, Win+Alt+B, another tool): logged with the window in front
        private const int ExternalChangeGraceMs = 8000;   // changes this soon after an own switch attempt are our own
        private string _foregroundProcess = string.Empty;
        private bool _foregroundFullscreen;
        // A full-screen game switched Windows HDR on by itself: keep it on while that game runs instead of fighting it
        private string? _externalHoldProcess;
        private long _nextExternalCheckMs;

        private List<DisplayColorState>? _sdrBaseline;   // colour state right before HDR was switched on

        private long _sdrFallbackAtMs = -1;
        private long _lastAttemptMs = long.MinValue / 2;
        private long _cooldownUntilMs;
        private int _consecutiveFailures;

        // ---- in-memory log ring buffer (Log tab) + file log ----
        private readonly object _logLock = new();
        private readonly List<string> _log = new();
        private int _logBase;

        public HdrEngine()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                // Never delete the log at startup (after a crash it is exactly what is needed): a large one moves to
                // log.old.txt, like the rotation while running
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                    File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.txt"), overwrite: true);
            }
            catch { }

            MigrateLegacyConfig();
            Config = LoadOrCreateConfig();
            UpgradeConfig(Config);
            Save();
        }

        /// <summary>
        /// Settings › Reset to defaults: every setting back to its default value. Kept: the game lists,
        /// found games and program locations, extra game folders.
        /// "Start with Windows" is a Windows setting, not part of config.json, and stays as it is.
        /// </summary>
        public void ResetSettings()
        {
            lock (Sync)
            {
                var c = Config;
                var fresh = new AppConfig
                {
                    ConfigVersion = c.ConfigVersion,
                    HdrGamesAndApps = c.HdrGamesAndApps,
                    LibraryGames = c.LibraryGames,
                    ProgramPaths = c.ProgramPaths,
                    CustomGameFolders = c.CustomGameFolders,
                    LauncherGames = c.LauncherGames,
                    QuickExitCounts = c.QuickExitCounts
                };
                Normalize(fresh);
                foreach (var prop in typeof(AppConfig).GetProperties())
                    if (prop.CanWrite) prop.SetValue(Config, prop.GetValue(fresh));
                InvalidateSnapshot();
                Save();
            }
            Log("CONFIG", "设置已恢复默认值（游戏列表已保留）。");
        }

        // ------------------------------------------------------------------
        // One-time fixes of default lists in existing config.json files (AppConfig.ConfigVersion)
        // ------------------------------------------------------------------
        private const int CurrentConfigVersion = 5;

        private void UpgradeConfig(AppConfig c)
        {
            if (c.ConfigVersion >= CurrentConfigVersion) return;
            bool existingFile = File.Exists(ConfigPath);

            if (c.ConfigVersion < 2)
            {
                var installed = new HashSet<string>(c.ProgramPaths.Keys, Cmp);
                foreach (var g in c.LibraryGames)
                    foreach (var p in g.Processes) installed.Add(p);

                foreach (string example in new[] { "cyberpunk2077", "eldenring", "alanwake2" })
                {
                    if (installed.Contains(example)) continue;
                    if (c.HdrGamesAndApps.RemoveAll(n => string.Equals(n, example, StringComparison.OrdinalIgnoreCase)) > 0)
                        Log("CONFIG", $"已从 HDR 游戏中移除示例条目 '{example}'（本机未安装）。");
                }
            }

            c.ConfigVersion = CurrentConfigVersion;
        }

        private static void AddMissing(List<string> list, params string[] names)
        {
            foreach (string name in names)
                if (!list.Contains(name, Cmp)) list.Add(name);
        }

        /// <summary>
        /// One-time move of config.json from the exe folder (versions up to 2.3) to %AppData%\SmartHdrManager.
        /// The old file is renamed to config.json.old so it is clear which file is used.
        /// </summary>
        private void MigrateLegacyConfig()
        {
            if (IsPortable || File.Exists(ConfigPath) || !File.Exists(LegacyConfigPath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.Copy(LegacyConfigPath, ConfigPath);
                Log("CONFIG", $"设置已迁移到 {ConfigPath}。");
                try { File.Move(LegacyConfigPath, LegacyConfigPath + ".old", overwrite: true); } catch { }   // read-only folder: keep it
            }
            catch (Exception ex)
            {
                Log("ERROR", $"旧设置无法迁移（{ex.Message}）。");
            }
        }

        private static long Now => Environment.TickCount64;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------
        public void Start()
        {
            Log("INFO", "Smart HDR Manager 已启动 – 监控中。");

            // TV brightness control initialization
            var tvIp = Config.Brightness?.TvIp ?? "192.168.1.4";
            var localAddr = Config.Brightness?.LocalAddr ?? "192.168.1.5";
            TvCli.Init(tvIp, localAddr);
            if (TvConfig.Enabled)
            {
                if (TvConfig.AutoSwitchEnabled)
                {
                    BrightnessAutoSwitcher.Start();
                }
            }

            MonitorsChanged();                             // remember the connected monitors (for display events)

            _thread = new Thread(Run) { IsBackground = true, Name = "HdrEngine" };
            _thread.Start();
        }


        /// <summary>True when the set of connected monitors changed since the last call (first call: false).</summary>
        private bool MonitorsChanged()
        {
            string signature;
            try
            {
                signature = string.Join("|", HdrController.ListMonitors().Select(m => m.DevicePath)
                                                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                return true;   // unknown: treat it like a monitor change (as before 2.6)
            }
            lock (_monitorSignatureLock)
            {
                bool changed = _monitorSignature != null && !string.Equals(signature, _monitorSignature, StringComparison.OrdinalIgnoreCase);
                _monitorSignature = signature;
                return changed;
            }
        }

        // ------------------------------------------------------------------
        // Manual override (tray menu / the override shortcut)
        // ------------------------------------------------------------------
        public HdrOverride OverrideMode => (HdrOverride)_overrideMode;

        /// <summary>Force HDR on/off. duration null = until turned back. HdrOverride.None = back to automatic.</summary>
        public void SetOverride(HdrOverride mode, TimeSpan? duration)
        {
            long until = mode != HdrOverride.None && duration.HasValue ? Now + (long)duration.Value.TotalMilliseconds : 0;
            lock (_overrideLock)
            {
                Interlocked.Exchange(ref _overrideUntilMs, until);
                _overrideMode = (int)mode;
            }
            Interlocked.Exchange(ref _overrideChanged, 1);

            string length = duration.HasValue ? $"持续 {FormatDuration(duration.Value)}" : "，直到手动恢复";
            Log("MANUAL", mode switch
            {
                HdrOverride.ForceHdr => $"HDR 已强制开启{length}。",
                HdrOverride.ForceSdr => $"HDR 已强制关闭（SDR）{length}。",
                _ => "已恢复自动 HDR 切换。"
            });
            _wake.Set();
        }

        /// <summary>the override shortcut: automatic -> HDR on -> SDR -> automatic. Returns the new mode.</summary>
        public HdrOverride CycleOverride()
        {
            var next = OverrideMode switch
            {
                HdrOverride.None => HdrOverride.ForceHdr,
                HdrOverride.ForceHdr => HdrOverride.ForceSdr,
                _ => HdrOverride.None
            };
            SetOverride(next, null);
            return next;
        }

        private static string FormatDuration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{t.TotalHours:0.#} 小时" : $"{t.TotalMinutes:0} 分钟";

        public List<MonitorInfo> GetMonitors() => HdrController.ListMonitors();

        public void Stop()
        {
            _stop = true;
            _wake.Set();
            bool finished = true;
            try { finished = _thread == null || _thread.Join(2000); } catch { }
            if (finished)
            {
                TvCli.Shutdown();
                BrightnessAutoSwitcher.Shutdown();
                RestoreSdrOnExit();   // never while the engine thread might still switch
            }
            lock (Sync) Save();
        }

        /// <summary>
        /// Exit: if this app switched HDR on, go back to SDR (2.5.2 – before, HDR stayed on after "Exit"). HDR that was
        /// on before the app started or was switched by something else is left alone, and so is HDR for a game that is
        /// still running (switching now would interrupt it).
        /// </summary>
        private void RestoreSdrOnExit()
        {
            try
            {
                if (!_hdrAny || !_hdrOnByApp) return;
                string? busy = _runningGame != null ? $"'{_runningGame}' 仍在运行"
                    : _externalHoldProcess != null ? $"'{_externalHoldProcess}' 仍在运行"
                    : Now < Interlocked.Read(ref _launchHoldUntilMs) ? "通过\"以 HDR 启动\"启动的游戏仍在运行"
                    : null;
                if (busy != null)
                {
                    Log("SDR", $"程序已关闭 – 因为 {busy}，HDR 保持开启。");
                    return;
                }

                var target = GetSnapshot().Monitors;
                Log("SDR", $"程序已关闭 – 切回 SDR（{target.Describe()}），HDR 是本程序开启的。");
                if (!HdrController.SetHdrState(false, target)) Log("ERROR", HdrController.LastError);
            }
            catch (Exception ex)
            {
                Log("ERROR", $"退出时无法切回 SDR：{ex.Message}");
            }
        }

        /// <summary>Runs an action on the engine thread (all display switching stays on one thread).</summary>
        public void Post(Action action)
        {
            _posted.Enqueue(action);
            _wake.Set();
        }

        /// <summary>
        /// "Launch with HDR": a launcher process (SmartHdrManager.exe --launch ...) asks to hold HDR while its game starts
        /// and runs. It repeats the request every 2 s; the hold ends a few seconds after the last one. Only holds HDR –
        /// nothing is started or read from the request, so any program sending it can at most keep HDR on.
        /// </summary>
        public void HoldHdrForLaunch()
        {
            Interlocked.Exchange(ref _launchHoldUntilMs, Now + LaunchHoldMs);
            _wake.Set();
        }

        /// <summary>"Launch with HDR": the launcher's game / player was closed – end the hold and go back to SDR now.</summary>
        public void ReleaseLaunchHold()
        {
            Interlocked.Exchange(ref _launchHoldUntilMs, 0);
            Interlocked.Exchange(ref _launchReleased, 1);
            _wake.Set();
        }

        /// <summary>Forces the HDR state to be re-read on the next tick (display change, resume from sleep).</summary>
        public void InvalidateHdrState(bool resumed = false)
        {
            // HDR lost right after a monitor was plugged in / switched on, or after waking up, was not turned off by you:
            // switch it on again if needed. Windows also reports "display settings changed" when HDR itself is
            // toggled (e.g. Win+Alt+B in a game), so only a changed monitor list counts – not every event.
            if (resumed || MonitorsChanged()) Interlocked.Exchange(ref _displayEventMs, Now);
            Interlocked.Exchange(ref _forceHdrQuery, 1);
            _wake.Set();
        }

        private void Run()
        {
            while (!_stop)
            {
                int waitMs = 100;
                try
                {
                    while (_posted.TryDequeue(out var action))
                    {
                        try { action(); }
                        catch (Exception ex) { Log("ERROR", $"操作失败：{ex.Message}"); }
                    }

                    var s = GetSnapshot();
                    waitMs = s.TickMs;
                    Tick(s);
                }
                catch (Exception ex)
                {
                    Log("ERROR", $"引擎错误：{ex.Message}");
                    waitMs = 1000;
                }

                _wake.WaitOne(waitMs);   // returns early when work is posted or the display changes
            }
        }

        private Snapshot GetSnapshot()
        {
            var s = _snapshot;
            if (s != null) return s;
            lock (Sync)
            {
                s = _snapshot ??= new Snapshot(Config);
                return s;
            }
        }

        /// <summary>Call after every configuration change (while holding <see cref="Sync"/>).</summary>
        private void InvalidateSnapshot()
        {
            _snapshot = null;
            _wake.Set();
        }

        // ------------------------------------------------------------------
        // One monitoring tick
        // ------------------------------------------------------------------
        private void Tick(Snapshot s)
        {
            long now = Now;

            // Configuration changed: re-check running games right away
            if (!ReferenceEquals(s, _lastSnapshot))
            {
                _lastSnapshot = s;
                _nextProcessScanMs = 0;
            }

            var (procName, title, isFullscreen, procId) = WindowDetector.GetActiveWindowInfo();
            _foregroundProcess = procName;
            _foregroundFullscreen = isFullscreen;
            _foregroundElsewhere = s.HdrGames.Contains(procName) && s.GameFolders.ContainsKey(procName) &&
                                   !IsGameLocation(s, procName, procId);


            RefreshHdrState(s, now);

            if (now >= _nextProcessScanMs)
            {
                _runningGame = FindRunningHdrGame(s, out string? runningPath);
                _nextProcessScanMs = now + ProcessScanIntervalMs;
                if (_runningGame != null && runningPath != null) RememberProgramPath(_runningGame, runningPath);
                TrackLauncherFollow(s, now);
            }

            // Manual override has priority over everything (also over "pause")
            HdrOverride mode;
            long until;
            bool expired = false;
            lock (_overrideLock)
            {
                mode = (HdrOverride)_overrideMode;
                until = Interlocked.Read(ref _overrideUntilMs);
                if (mode != HdrOverride.None && until > 0 && now >= until)
                {
                    // expire only this exact override (a new one chosen meanwhile is not touched: same lock)
                    _overrideMode = (int)HdrOverride.None;
                    Interlocked.Exchange(ref _overrideUntilMs, 0);
                    mode = HdrOverride.None;
                    until = 0;
                    expired = true;
                }
            }
            if (expired)
            {
                Log("MANUAL", "覆盖时间已到 – 已恢复自动 HDR 切换。");
                Interlocked.Exchange(ref _overrideChanged, 1);
            }

            // After any override change, start the automatic logic fresh (no stale timers or "game closed" events)
            if (Interlocked.Exchange(ref _overrideChanged, 0) == 1)
            {
                _sdrFallbackAtMs = -1;
                _holdHdrUntilMs = 0;
                _activeHdrGameProcess = null;
                _handOffDemand = null;   // you decided: the automatic logic starts fresh
                _restoreHdr = false;
                _externalEnded = false;
            }

            _holdMsLeft = 0;

            if (mode == HdrOverride.None && !Paused)
                Decide(s, now, procName, title, isFullscreen);
            else if (mode != HdrOverride.None)
                ApplyOverride(s, now, mode);

            Status = new EngineStatus
            {
                HdrOn = _hdrAny,
                Paused = Paused,
                ActiveProcess = procName,
                IsFullscreen = isFullscreen,
                RunningGame = _runningGame,
                Override = mode,
                OverrideRemainingMs = mode != HdrOverride.None && until > 0 ? Math.Max(0, until - now) : -1,
                HoldSecondsLeft = (int)Math.Ceiling(_holdMsLeft / 1000.0),
                BlockedBy = false,
                LaunchHold = now < Interlocked.Read(ref _launchHoldUntilMs),
                FollowingLauncher = _followLauncher
            };

        }

        private void ApplyOverride(Snapshot s, long now, HdrOverride mode)
        {
            bool canAttempt = now - _lastAttemptMs >= SwitchSpacingMs && now >= _cooldownUntilMs;
            if (!canAttempt) return;

            if (mode == HdrOverride.ForceHdr && !_hdrAll)
                TurnHdrOn("Manual override", s.Monitors);
            else if (mode == HdrOverride.ForceSdr && _hdrAny)
                TurnHdrOff("Manual override", s.Monitors, s.ReloadProfile);
        }

        private void RefreshHdrState(Snapshot s, long now)
        {
            bool forced = Interlocked.Exchange(ref _forceHdrQuery, 0) == 1;
            var target = s.Monitors;
            if (!forced && now < _nextHdrQueryMs && ReferenceEquals(_queriedMonitors, target))
                return;

            // Keep the last known state if Windows could not be queried
            if (HdrController.QueryHdrState(target, out bool any, out bool all))
            {
                if (!_apiLogged && HdrController.HdrApiName != "not determined yet")
                {
                    _apiLogged = true;
                    Log("INFO", $"显示控制：{HdrController.HdrApiName} – {WindowsVersion.Describe()}。");
                }
                bool sameTarget = ReferenceEquals(_queriedMonitors, target);
                if (!_firstHdrQueryDone || !sameTarget)
                {
                    // First look (or other monitors): this is the starting point, nothing was switched by anyone yet
                    _expectAny = any;
                    _switchConfirmed = true;
                    if (!_firstHdrQueryDone && any)
                        Log("HDR", "HDR 已开启 – 保持现状（本程序只切回自己开启的 HDR）。");
                }

                // HDR changed although this app did not switch it: a game, Win+Alt+B, Windows Settings or another tool did
                // it. Logged with the window in front, so reports show which games switch HDR themselves. Right after an
                // own switch, Windows may still report the old state for a moment: that is not a change by someone else
                // (until the new state has been seen once, or 8 s have passed).
                bool towardOwn = _expectAny.HasValue && any == _expectAny.Value;
                if (towardOwn) _switchConfirmed = true;
                if (_firstHdrQueryDone && any != _hdrAny && sameTarget && !towardOwn &&
                    (_switchConfirmed || now - _lastAttemptMs > ExternalChangeGraceMs))
                {
                    string front = string.IsNullOrEmpty(_foregroundProcess) ? "unknown" : _foregroundProcess;
                    Log("INFO", $"Windows HDR 在未经本程序的情况下变为 {(any ? "开启" : "关闭")} – 由游戏、Win+Alt+B、Windows " +
                                $"设置或显示器重新连接触发（前台窗口：{front}）。");
                    _hdrOnByApp = false;   // not ours any more: never switched back by this app, left as it is on exit
                    _restoreHdr = false;   // you decided: nothing to put back later
                    _hdrOnForGame = null;  // crash hint: closing the game now is not a crash after the HDR switch
                    _expectAny = any;
                    _switchConfirmed = true;

                    // A full-screen game turned HDR on itself (its own HDR option): switching it back off a few
                    // seconds later would fight the game – flicker, black screens. Keep HDR while that game runs, and
                    // switch back once it has closed (some games leave HDR on). Not for video players (madVR, MPC …)
                    // or browsers: they handle HDR themselves and are simply left alone.
                    if (any && _foregroundFullscreen && front != "unknown" && front != "explorer" && front != "smarthdrmanager")
                    {
                        _externalHoldProcess = front;
                        _externalEnded = false;
                        Log("HDR", $"'{front}' 自行开启了 HDR – 其运行期间保持 HDR 开启。");
                    }
                    else if (!Paused && OverrideMode == HdrOverride.None)
                    {
                        // Hands off until the next game / video (or until the current one ends). HDR lost right after a
                        // display change or wake-up is not your decision: that one is switched on again if needed.
                        bool displayEvent = now - Interlocked.Read(ref _displayEventMs) < DisplayEventGraceMs;
                        if (any || !displayEvent)
                        {
                            _handOffDemand = _lastDemand;
                            if (any)
                                Log("HDR", "HDR 由您或其他程序开启 – 保持开启。本程序只切回自己开启的 " +
                                           "HDR。");
                            else if (_lastDemand != "none")
                                Log("HDR", $"HDR 由您或其他程序关闭 – 在 {DescribeDemand(_lastDemand)} " +
                                           "结束前（或下一个游戏 / 视频启动前）不再开启。");
                        }
                    }
                }
                if (!any) _externalHoldProcess = null;

                // e.g. switched on by hand (Win+Alt+B). At app start (first query) HDR counts as "on for long already".
                if (any && !_hdrAny && _hdrOnSinceMs == 0) _hdrOnSinceMs = _firstHdrQueryDone ? now : 1;
                _firstHdrQueryDone = true;
                if (!any) _hdrOnSinceMs = 0;
                _hdrAny = any;
                _hdrAll = all;

                // Windows reported success, but a display did not actually switch on: count it as a failure,
                // so a monitor that refuses HDR cannot cause an endless retry loop (cooldown after 3 tries).
                if (_verifyAllOn && !forced)
                {
                    _verifyAllOn = false;
                    if (!all)
                    {
                        Log("ERROR", "HDR 已切换开启，但并非所有支持 HDR 的显示器都报告 HDR 处于活动状态。");
                        RegisterFailure(now);
                    }
                    else
                    {
                        _consecutiveFailures = 0;
                    }
                }
            }
            _queriedMonitors = target;
            _nextHdrQueryMs = now + HdrRequeryIntervalMs;
        }

        private void Decide(Snapshot s, long now, string procName, string title, bool isFullscreen)
        {
            bool shouldEnableHdr = EvaluateHdrRequirement(s, procName, title, isFullscreen, _foregroundElsewhere, out string reason);

            // "Launch with HDR": a game is being started through SmartHdrManager.exe --launch. Hold HDR from before the
            // game starts until a few seconds after it closes (games that only check HDR at startup then see it on).
            bool launchHold = now < Interlocked.Read(ref _launchHoldUntilMs);
            if (Interlocked.Exchange(ref _launchReleased, 0) == 1)
            {
                launchHold = false;
                if (_launchHoldLogged) _launchEnded = true;   // only if this hold really kept HDR on
            }
            if (launchHold)
            {
                if (!_launchHoldLogged)
                {
                    _launchHoldLogged = true;
                    Log("HDR", "以 HDR 启动：在游戏或播放器启动前开启 HDR。");
                }
                if (!shouldEnableHdr)
                {
                    shouldEnableHdr = true;
                    reason = "Launch with HDR";
                }
            }
            else if (_launchHoldLogged)
            {
                _launchHoldLogged = false;
                Log("HDR", "以 HDR 启动：游戏或播放器已关闭。");
            }

            // A full-screen game switched HDR on by itself: keep HDR on until that game has closed, then switch back
            if (_externalHoldProcess != null)
            {
                if (now >= _nextExternalCheckMs)
                {
                    _nextExternalCheckMs = now + 2000;
                    if (!IsProcessRunning(_externalHoldProcess))
                    {
                        Log("HDR", $"'{_externalHoldProcess}' 已关闭 – 切回其自行开启的 HDR。");
                        _externalEndedWhat = _externalHoldProcess;
                        _externalEnded = true;
                        _externalHoldProcess = null;
                    }
                }
                if (_externalHoldProcess != null && !shouldEnableHdr)
                {
                    shouldEnableHdr = true;
                    reason = $"'{_externalHoldProcess}' switched HDR on itself";
                }
            }

            // A watched game closed within seconds (probably a launcher): keep HDR on while waiting for the real game
            if (_followLauncher != null && _followHoldsHdr && !shouldEnableHdr)
            {
                shouldEnableHdr = true;
                reason = $"等待 '{_followLauncher}' 启动的游戏";
            }

            // Early detection when a selected HDR game process is running
            if (!shouldEnableHdr && s.EnableOnProcessStart && _runningGame != null)
            {
                shouldEnableHdr = true;
                reason = $"HDR 游戏已启动 / 正在运行（{_runningGame}）";
            }

            // Per-game start delay: wait N seconds after the game appears before switching HDR on
            string? hdrGame = s.HdrGames.Contains(procName) && !_foregroundElsewhere ? procName : _runningGame;
            if (hdrGame == null)
            {
                // No HDR game detected
            }

            if (_runningGame != null)
                _activeHdrGameProcess = _runningGame;

            // Multi-monitor hint (once per game start): the game window is on a monitor that is not selected under
            // Settings › Monitors, so HDR gets switched on the other screen and the game does not see it.
            if (hdrGame == null)
            {
                _monitorCheckedGame = null;
            }
            else if (shouldEnableHdr && s.HdrGames.Contains(procName) && !_foregroundElsewhere &&
                     !string.Equals(_monitorCheckedGame, procName, StringComparison.OrdinalIgnoreCase))
            {
                _monitorCheckedGame = procName;
                if (HdrController.ResolveVideoMonitor(WindowDetector.ForegroundMonitor(), s.Monitors, out string problem) == null &&
                    problem.Contains("未在 设置 › 显示器 中开启切换", StringComparison.OrdinalIgnoreCase))
                {
                    Log("HINT", $"'{procName}' 运行在未被选中用于 HDR 切换的显示器上，因此 HDR 被切换到了 " +
                                "另一台显示器。请在 设置 › 显示器 中选中游戏的显示器。");
                }
            }

            bool canAttempt = now - _lastAttemptMs >= SwitchSpacingMs && now >= _cooldownUntilMs;


            // What wants HDR right now (stable while the same game / video runs, e.g. also when you alt-tab)
            string demand;
            if (!shouldEnableHdr) demand = "none";
            else if (_runningGame != null) demand = "game:" + _runningGame;
            else if (s.HdrGames.Contains(procName) && !_foregroundElsewhere) demand = "game:" + procName;
            else if (launchHold) demand = "launch";
            else if (_externalHoldProcess != null) demand = "ext:" + _externalHoldProcess;
            else if (_followLauncher != null && _followHoldsHdr) demand = "follow:" + _followLauncher;

            else demand = "focus:" + procName;

            // HDR was changed by you / another program: hands off until the demand changes
            if (HandedOff(s, demand))
            {
                _sdrFallbackAtMs = -1;
                _launchEnded = false;
                _externalEnded = false;
                if (_runningGame == null) _activeHdrGameProcess = null;
                return;
            }

            // The app only undoes what it did itself: HDR that you or another program turned on is never switched
            // back here (2.6).
            bool owns = _hdrOnByApp;

            if (shouldEnableHdr)
            {
                _sdrFallbackAtMs = -1;
                _holdHdrUntilMs = 0;   // game came back during "keep HDR after exit": nothing to wait for any more
                _launchEnded = false;
                _externalEnded = false;
                if (!_hdrAll && canAttempt)
                {
                    string? game = _runningGame ?? (s.HdrGames.Contains(procName) && !_foregroundElsewhere ? procName : null);
                    TurnHdrOn(reason, s.Monitors);
                    if (_hdrAll && game != null && !launchHold)
                    {
                        // Crash hint: remember when HDR came on for this game
                        _hdrOnForGame = game;
                        _hdrOnForGameAtMs = now;
                    }
                }
            }
            else if (_hdrAny)
            {
                // Hold HDR: keep-after-exit. Colour-critical apps still switch at once.
                long holdUntil = _holdHdrUntilMs;
                if (owns && now < holdUntil)
                {
                    _holdMsLeft = holdUntil - now;
                    // The "return to SDR after" delay already counts during the hold, so it is not added on top
                    if (_sdrFallbackAtMs < 0) _sdrFallbackAtMs = now + s.FallbackSeconds * 1000L;
                    return;
                }

                // Instant turn-off: a monitored HDR game was running and just closed
                if (_activeHdrGameProcess != null && _runningGame == null)
                {
                    // During a failure cooldown, keep the state and switch off as soon as the cooldown ends
                    if (now >= _cooldownUntilMs)
                    {
                        string closedGame = _activeHdrGameProcess;
                        _activeHdrGameProcess = null;
                        _sdrFallbackAtMs = -1;
                        if (owns) UndoOwnHdr($"HDR game closed ({closedGame})", s);
                        else Log("HDR", $"'{closedGame}' 已关闭 – HDR 保持开启（不是本程序开启的）。");
                    }
                }
                else if (_externalEnded)
                {
                    // The full-screen game that switched HDR on by itself has closed (some games leave HDR on)
                    if (canAttempt)
                    {
                        _externalEnded = false;
                        _sdrFallbackAtMs = -1;
                        TurnHdrOff($"'{_externalEndedWhat}' 已关闭（它曾自行开启 HDR）", s.Monitors, s.ReloadProfile);
                    }
                }
                else if (_launchEnded)
                {
                    if (canAttempt)
                    {
                        _launchEnded = false;
                        _sdrFallbackAtMs = -1;
                        if (owns) UndoOwnHdr("以 HDR 启动：游戏或播放器已关闭", s);
                    }
                }

                else if (!owns)
                {
                    _sdrFallbackAtMs = -1;   // HDR turned on by you or another program: left alone
                }
                else if (_sdrFallbackAtMs < 0)
                {
                    _sdrFallbackAtMs = now + s.FallbackSeconds * 1000L;
                }
                else if (now >= _sdrFallbackAtMs && canAttempt)
                {
                    _sdrFallbackAtMs = -1;
                    UndoOwnHdr("无 HDR 内容处于活动状态（桌面模式）", s);
                }
            }
            else
            {
                _activeHdrGameProcess = null;
                _sdrFallbackAtMs = -1;
                _launchEnded = false;
                _externalEnded = false;

                // HDR that you had on was switched off for an SDR app or a recording app: back on once it is gone
                if (_restoreHdr && canAttempt && now - _restoreAtMs >= RestoreAfterMs)
                {
                    string what = _restoreFor;
                    _restoreHdr = false;
                    TurnHdrOn($"恢复 HDR – '{what}' 之前 HDR 是开启的", s.Monitors);
                    if (_hdrAny) _hdrOnByApp = false;   // yours again: never switched back by this app
                }
            }
        }

        /// <summary>
        /// HDR was changed by something else (you, a game, another program): the app keeps its hands off until what
        /// wants HDR changes – a new game or video starts, or the current one ends. Returns true while handed off.
        /// Remembers the demand of this tick (the starting point of the next hand-off).
        /// </summary>
        private bool HandedOff(Snapshot s, string demand)
        {
            _lastDemand = demand;
            if (_handOffDemand == null) return false;
            if (string.Equals(demand, _handOffDemand, StringComparison.OrdinalIgnoreCase)) return true;
            _handOffDemand = null;
            Log("HDR", $"自动切换重新接管（{(demand == "none" ? "您的列表中已无活动项目" : DescribeDemand(demand))}）。");
            return false;
        }

        /// <summary>"game:witcher3" -> "'witcher3'", "video:…" -> "the video" – for the log.</summary>
        private static string DescribeDemand(string demand)
        {
            int colon = demand.IndexOf(':');
            string kind = colon < 0 ? demand : demand.Substring(0, colon);
            string what = colon < 0 ? string.Empty : demand.Substring(colon + 1);
            return kind switch
            {
                "game" or "ext" or "focus" => $"'{what}'",
                "launch" => "通过\"以 HDR 启动\"启动的游戏",
                "follow" => $"由 '{what}' 启动的游戏",
                "sdr" => $"'{what}'（SDR 程序）",
                _ => "此项目"
            };
        }

        /// <summary>
        /// HDR that was not switched on by this app has just been switched off for an SDR app / recording app (Apps tab):
        /// it is switched back on once that app is gone.
        /// </summary>
        private void RememberRestore(string what)
        {
            _restoreHdr = true;
            _restoreFor = what;
            _restoreAtMs = Now;
            Log("SDR", $"之前 HDR 是开启的（不是本程序开启的）– '{what}' 不再活动后恢复开启。");
        }

        /// <summary>
        /// The reason this app switched HDR on has ended: back to SDR. Exception: HDR was on before (yours) and was only
        /// interrupted by an SDR / recording app – then it stays on and is yours again.
        /// </summary>
        private void UndoOwnHdr(string reason, Snapshot s)
        {
            if (_restoreHdr)
            {
                _restoreHdr = false;
                _hdrOnByApp = false;
                Log("HDR", $"{reason} – HDR 保持开启：'{_restoreFor}' 之前 HDR 就是开启的（由您或其他程序开启）。");
                return;
            }
            TurnHdrOff(reason, s.Monitors, s.ReloadProfile);
        }

        // ------------------------------------------------------------------
        // Detection
        // ------------------------------------------------------------------
        // Programs whose exe path was already looked up (once per program per session)
        private readonly HashSet<string> _pathLookupDone = new(StringComparer.OrdinalIgnoreCase);

        // Exe location check, by process id and name: true = runs from its game's folder (or the location is unknown)
        private readonly Dictionary<(int Pid, string Name), bool> _locationOk = new();
        private Snapshot? _locationSnapshot;
        private bool _foregroundElsewhere;   // the program in front has an HDR game's name but runs from another folder

        /// <summary>
        /// False when an HDR library game's process name runs from outside that game's install folder – another program
        /// with the same name. Unknown locations count as a match (as before): a game without a known exe, a game whose
        /// known exes are all gone (moved or reinstalled), or a process whose path cannot be read. Folder links and
        /// junctions are followed. Uses limited query access only, once per process (anti-cheat safe).
        /// </summary>
        private bool IsGameLocation(Snapshot s, string process, int pid)
        {
            if (!s.GameFolders.TryGetValue(process, out var known)) return true;
            if (!ReferenceEquals(s, _locationSnapshot) || _locationOk.Count > 256)
            {
                _locationOk.Clear();   // new configuration (folders may have changed) or a long session: check again
                _locationSnapshot = s;
            }
            var key = (pid, process.ToLowerInvariant());
            if (_locationOk.TryGetValue(key, out bool ok)) return ok;

            string? path = ProcessPaths.TryGetExePath(pid);
            // Xbox / Microsoft Store games run from a WindowsApps path that is not their XboxGames folder
            ok = path == null || path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
                 known.Folders.Any(f => path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));
            // Slower checks only for a process outside the known folders (once per process)
            if (!ok) ok = !known.Exes.Any(File.Exists);
            if (!ok)
            {
                string real = RealPath(path!);
                ok = known.Folders.Any(f => real.StartsWith(RealPath(f) + "\\", StringComparison.OrdinalIgnoreCase)) ||
                     known.Exes.Any(e => Path.GetFileName(e).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) && SameFile(path!, e));
            }
            if (!ok) Log("HDR", $"'{process}' 运行自 {path} – 不是您列表中游戏的安装文件夹，已忽略。");
            _locationOk[key] = ok;
            return ok;
        }

        private string? FindRunningHdrGame(Snapshot s, out string? exePath)
        {
            exePath = null;
            if (s.HdrGames.Count == 0) return null;   // nothing to look for: skip the process list entirely

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return null; }

            string? found = null;
            foreach (var p in all)
            {
                try
                {
                    if (found == null)
                    {
                        string name = p.ProcessName;
                        if (s.HdrGames.Contains(name) && IsGameLocation(s, name, p.Id))
                        {
                            found = name.ToLowerInvariant();
                            // First time this game is seen running: note its exe for the icon in the Games tab
                            if (_pathLookupDone.Add(found))
                            {
                                exePath = ProcessPaths.TryGetExePath(p.Id);   // limited access only, never reads game memory
                            }
                        }
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            return found;
        }

        // ------------------------------------------------------------------
        // Launchers (2.5.2, GitHub issue #1)
        // ------------------------------------------------------------------
        /// <summary>
        /// Runs after every process scan. When a watched HDR game closes within <see cref="LauncherShortRunMs"/> of
        /// appearing, it was probably only a launcher (REDprelauncher, PlayGTAV, …). For up to
        /// <see cref="LauncherFollowMs"/> HDR stays on (if it was on) and the app looks for a program from the same
        /// game folder. If one runs, it is followed and remembered (LauncherGames), so next time HDR switches as soon as
        /// the real game starts. Only process names are compared – no handles are opened to the game (anti-cheat safe).
        /// </summary>
        private void TrackLauncherFollow(Snapshot s, long now)
        {
            string? game = _runningGame;
            if (game != null)
            {
                if (!string.Equals(game, _seenGame, StringComparison.OrdinalIgnoreCase))
                {
                    _seenGame = game;
                    // Already running when the app started: it has been running for a while, never a "short run"
                    _seenGameSinceMs = _launcherTrackingStarted ? now : now - LauncherShortRunMs;
                }
                _launcherTrackingStarted = true;
                if (_followLauncher != null) EndFollow();   // the launcher came back, or another HDR game took over
                // Crash hint: the same game started again right after it closed (restarted after a crash) still counts;
                // another game from the list taking over means the first one was a launcher
                if (_quickExitGame != null && string.Equals(game, _quickExitGame, StringComparison.OrdinalIgnoreCase))
                    ReportQuickExit();
                _quickExitGame = null;
                // Crash hint: a game that runs fine with HDR for a minute no longer gets the hint
                if (_hdrOnForGame != null && string.Equals(game, _hdrOnForGame, StringComparison.OrdinalIgnoreCase) &&
                    now - _hdrOnForGameAtMs > QuickExitClearMs)
                {
                    ClearQuickExit(game);
                    _hdrOnForGame = null;
                }
                return;
            }

            _launcherTrackingStarted = true;
            if (_seenGame != null)
            {
                string closed = _seenGame;
                long ran = now - _seenGameSinceMs;
                _seenGame = null;

                // Crash hint: closed within seconds after this app switched HDR on for it
                if (_hdrOnForGame != null && string.Equals(closed, _hdrOnForGame, StringComparison.OrdinalIgnoreCase) &&
                    now - _hdrOnForGameAtMs <= QuickExitMs && !IsLauncher(closed))
                {
                    _quickExitGame = closed;
                    _quickExitAfterMs = now - _hdrOnForGameAtMs;
                }
                _hdrOnForGame = null;

                if (_followLauncher == null && ran < LauncherShortRunMs && !Paused && OverrideMode == HdrOverride.None)
                    StartFollow(s, now, closed, ran);

                // A launcher hands over to the real game: only count it once the launcher check is over
                if (_quickExitGame != null && _followLauncher == null) ReportQuickExit();
            }

            if (_followLauncher == null) return;

            var names = _followNames;
            string? child = names == null ? null : FindRunningFromFolder(s, names, _followLauncher, _followNotBefore);
            if (child != null)
            {
                string launcher = _followLauncher;
                EndFollow();
                _quickExitGame = null;   // it was a launcher, not a crash
                LearnLauncherGame(launcher, child);
                // Take over right away (the next process scan finds it on its own): no SDR switch in between
                _runningGame = child;
                _activeHdrGameProcess = child;
                _seenGame = child;
                _seenGameSinceMs = now;
            }
            else if (now >= _followUntilMs)
            {
                Log("HDR", $"'{_followLauncher}' 在 {LauncherFollowMs / 1000} 秒内未启动游戏 – 恢复正常切换。");
                EndFollow();
                if (_quickExitGame != null) ReportQuickExit();
            }
        }

        private void StartFollow(Snapshot s, long now, string closed, long ranMs)
        {
            // Only a game that really closed: not one that was just taken off the list
            if (!s.HdrGames.Contains(closed) || IsProcessRunning(closed)) return;

            string? exe = KnownExePath(closed);
            string? folder = exe == null ? null : HdrLauncher.GameFolder(exe);
            if (folder == null) return;   // unknown location (or a shared folder like "Games"): nothing to follow
            // Never a Windows folder: its programs (svchost, explorer, …) are always running and are no game
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0 && folder.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return;

            _followLauncher = closed;
            _followUntilMs = now + LauncherFollowMs;
            // The game must have started after the launcher (not a program from that folder that was already running)
            _followNotBefore = DateTime.Now - TimeSpan.FromMilliseconds(Math.Max(0, ranMs)) - TimeSpan.FromSeconds(3);
            _followHoldsHdr = _hdrAny;
            _followNames = null;
            int generation = ++_followGeneration;

            Log("HDR", $"'{closed}' 运行 {Math.Max(1, ranMs / 1000)} 秒后关闭 – 它可能是一个启动器。 " +
                       (_hdrAny ? $"在等待它启动的游戏期间，最长保持 HDR 开启 {LauncherFollowMs / 1000} 秒。"
                                : $"监视它启动的游戏，最长 {LauncherFollowMs / 1000} 秒。"));

            // The folder listing can take a moment on a hard disk: done in the background, HDR is held meanwhile
            Task.Run(() =>
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string n in HdrLauncher.GameExeNames(folder))
                    {
                        if (n.Equals(closed, StringComparison.OrdinalIgnoreCase) ||
                            IsExcludedExeName(n)) continue;
                        names.Add(n);
                    }
                }
                catch { }
                if (names.Count > 150) names.Clear();   // a tools / system folder, not a game folder: follow nothing
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

        // ------------------------------------------------------------------
        // Crash hint (2.6): a game closed within seconds after HDR was switched on for it
        // ------------------------------------------------------------------
        /// <summary>
        /// Some games crash when Windows switches to HDR while they start (seen with NVIDIA RTX HDR). The first time, a
        /// HINT goes to the log; from the second time on also a notification and a hint in the game's options.
        /// "Launch with HDR" (HDR on before the game starts) or a start delay avoids the switch during startup.
        /// </summary>
        private void ReportQuickExit()
        {
            string? game = _quickExitGame;
            _quickExitGame = null;
            if (game == null) return;
            int seconds = (int)Math.Max(1, Math.Round(_quickExitAfterMs / 1000.0));

            int count;
            lock (Sync)
            {
                Config.QuickExitCounts.TryGetValue(game, out count);
                count = Math.Min(99, count + 1);
                Config.QuickExitCounts[game] = count;
                Save();
            }

            Log("HINT", $"'{game}' 在为其开启 HDR 后 {seconds} 秒关闭。如果它崩溃了：请关闭 NVIDIA RTX HDR " +
                        "（NVIDIA App › 图形 › 全局设置 › RTX HDR），或使用 游戏 › 选项 › 以 HDR 启动。");
            if (count >= 2)
                _notifications.Enqueue(($"是否 {game} 崩溃了？",
                    $"它在 HDR 开启后 {seconds} 秒关闭了（共 {count} 次）。请在 NVIDIA App 中关闭 RTX HDR，或使用以 HDR 启动。"));
        }

        /// <summary>Launchers (by name, or learned from a game they started) close quickly on purpose.</summary>
        private bool IsLauncher(string process)
        {
            if (LooksLikeLauncher(process)) return true;
            lock (Sync) return Config.LauncherGames.ContainsKey(process);
        }

        private void ClearQuickExit(string game)
        {
            lock (Sync)
            {
                if (Config.QuickExitCounts.Remove(game)) Save();
            }
        }

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
            "startprotectedgame", "launcherpatcher", "gamelauncher", "bootstrapper", "dowser", "playlan", "ffxivboot", "ffxivboot64", "edlaunch", "runme"
        };

        private static bool IsExcludedExeName(string name)
        {
            foreach (var part in ExcludedExeParts)
            {
                if (name.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool LooksLikeLauncher(string process)
        {
            return KnownLaunchers.Contains(process);
        }

        /// <summary>Tray notifications from the engine (crash hint). Called by the tray timer on the UI thread.</summary>
        public bool TryTakeNotification(out string title, out string text)
        {
            if (_notifications.TryDequeue(out var n))
            {
                title = n.Title;
                text = n.Text;
                return true;
            }
            title = text = string.Empty;
            return false;
        }

        /// <summary>Exe path of a process name: from the game library, a scan, "Add program" or an earlier run.</summary>
        private string? KnownExePath(string process)
        {
            lock (Sync)
            {
                if (Config.ProgramPaths.TryGetValue(process, out var path) && File.Exists(path)) return path;
                foreach (var g in Config.LibraryGames)
                {
                    if (g.ExePath != null && Path.GetFileNameWithoutExtension(g.ExePath).Equals(process, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(g.ExePath))
                        return g.ExePath;
                }
                return null;
            }
        }

        private static string? FindRunningFromFolder(Snapshot s, HashSet<string> names, string launcher, DateTime notBefore)
        {
            if (names.Count == 0) return null;
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return null; }

            string? found = null;
            int own = Environment.ProcessId;
            foreach (var p in all)
            {
                try
                {
                    if (found == null && p.Id != own)
                    {
                        string name = p.ProcessName;
                        // StartTime needs only limited query access (like Task Manager); unreadable = not a candidate
                        if (names.Contains(name) && !name.Equals(launcher, StringComparison.OrdinalIgnoreCase) &&
                            p.StartTime >= notBefore)
                            found = name.ToLowerInvariant();
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            return found;
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

                // Same row in the Games tab as the launcher's library game
                foreach (var g in Config.LibraryGames)
                    if (g.Processes.Contains(launcher, Cmp) && !g.Processes.Contains(game, Cmp))
                        g.Processes.Add(game);

                InvalidateSnapshot();
                Save();
            }
            Log("HDR", $"正在跟踪 '{game}'（由 '{launcher}' 启动）。已记住：下次 '{game}' 一启动 HDR 就保持开启。");
        }

        private static bool IsProcessRunning(string processName)
        {
            try
            {
                var list = Process.GetProcessesByName(processName);
                foreach (var p in list) p.Dispose();
                return list.Length > 0;
            }
            catch
            {
                return false;
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
                var fa = new FileInfo(a);
                var fb = new FileInfo(b);
                if (!fa.Exists || !fb.Exists) return !fa.Exists;
                return fa.Length == fb.Length && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc;
            }
            catch
            {
                return true;
            }
        }

        /// <param name="otherProgram">The program in front only shares an HDR game's process name (see <see cref="IsGameLocation"/>).</param>
        private static bool EvaluateHdrRequirement(Snapshot s, string procName, string title, bool isFullscreen, bool otherProgram, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrEmpty(procName)) return false;

            // 1. Selected HDR games
            if (s.HdrGames.Contains(procName) && !otherProgram)
            {
                reason = $"HDR 游戏 / 应用在前台（{procName}）";
                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Switching HDR / SDR (engine thread only)
        // ------------------------------------------------------------------
        private void TurnHdrOn(string reason, MonitorFilter monitors)
        {
            long now = Now;
            _lastAttemptMs = now;
            Log("HDR", $"开启 HDR（{monitors.Describe()}）– {reason}");

            CancelColorProfileReload();   // a reload must not run while we are going back into HDR
            if (!_hdrAny)
            {
                // Remember how the displays looked in SDR, to check (and repair) them after switching back
                try { _sdrBaseline = HdrController.CaptureColorState(monitors); }
                catch { _sdrBaseline = null; }
            }
            bool ok = HdrController.SetHdrState(true, monitors);
            if (ok)
            {
                if (!_hdrAny || _hdrOnSinceMs == 0) _hdrOnSinceMs = now;
                _hdrOnByApp = true;
                _hdrAny = true;
                _hdrAll = true;
                _verifyAllOn = true;
                _expectAny = true;          // until Windows reports it, an "off" reading is not a change by someone else
                _switchConfirmed = false;
            }
            else
            {
                Log("ERROR", HdrController.LastError);
            }
            AfterSwitch(ok, now);
        }

        /// <param name="partial">Only some monitors (HDR moves to another monitor): HDR stays on elsewhere and stays ours.</param>
        private void TurnHdrOff(string reason, MonitorFilter monitors, bool reloadProfile, bool partial = false)
        {
            long now = Now;
            _lastAttemptMs = now;
            Log("SDR", $"切换到 SDR（{monitors.Describe()}）– {reason}");

            bool ok = HdrController.SetHdrState(false, monitors);
            if (ok)
            {
                _hdrOnSinceMs = 0;
                if (!partial)
                {
                    _hdrOnByApp = false;
                    _expectAny = false;
                    _switchConfirmed = false;
                }
                _holdHdrUntilMs = 0;
                _hdrAny = false;
                _hdrAll = false;
                _verifyAllOn = false;
                var snapshot = GetSnapshot();
                var baseline = _sdrBaseline;
                if (reloadProfile || snapshot.RefreshApps || baseline != null)
                    SchedulePostSdrFixes(monitors, reloadProfile, snapshot.RefreshApps, baseline);
            }
            else
            {
                Log("ERROR", HdrController.LastError);
            }
            AfterSwitch(ok, now);
        }

        private void AfterSwitch(bool ok, long now)
        {
            // Re-read the real state once Windows has applied the change
            _nextHdrQueryMs = now + SwitchSpacingMs;

            if (ok)
            {
                if (!_verifyAllOn) _consecutiveFailures = 0;   // an ON is only "good" once verified
                return;
            }

            RegisterFailure(now);
        }

        private void RegisterFailure(long now)
        {
            if (++_consecutiveFailures >= FailuresBeforeCooldown)
            {
                _consecutiveFailures = 0;
                _cooldownUntilMs = now + FailureCooldownMs;
                Log("ERROR", $"HDR 切换连续失败 {FailuresBeforeCooldown} 次 – 暂停自动尝试 {FailureCooldownMs / 1000} 秒。");
            }
        }

        private int _profileReloadGeneration;
        private volatile bool _calibrationLoaderDenied;   // the Windows task needs admin rights on some PCs

        /// <summary>
        /// Clean-up after leaving HDR, run in the background:
        /// 1. waits until Windows really reports SDR (up to 6 s), then lets the display settle (1.5 s);
        /// 2. re-applies the colour profile (ICC calibration / gamma ramp), if enabled;
        /// 3. tells open windows that the display changed and nudges browser windows to repaint, if enabled
        ///    (fixes Chrome & co. staying dimmed with their cached HDR brightness);
        /// 4. repeats steps 2-3 once a few seconds later, in case the driver reset things again.
        /// Cancels itself if HDR is switched back on in the meantime.
        /// </summary>
        private void SchedulePostSdrFixes(MonitorFilter monitors, bool reloadProfile, bool refreshApps,
                                          List<DisplayColorState>? baseline)
        {
            int generation = Interlocked.Increment(ref _profileReloadGeneration);
            bool Cancelled() => Volatile.Read(ref _profileReloadGeneration) != generation || _stop;

            Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 24; i++)
                    {
                        if (Cancelled()) return;
                        if (HdrController.QueryHdrState(monitors, out bool any, out _) && !any) break;
                        Thread.Sleep(250);
                    }

                    Thread.Sleep(1500);
                    if (Cancelled()) return;

                    if (baseline != null) CheckColorsAfterSdr(baseline, monitors, report: true);

                    bool profileOk = reloadProfile && !_calibrationLoaderDenied;
                    if (profileOk)
                    {
                        profileOk = RunCalibrationLoader(out string error);
                        if (!profileOk)
                        {
                            if (error.Contains("denied", StringComparison.OrdinalIgnoreCase))
                            {
                                _calibrationLoaderDenied = true;   // don't try again until the app restarts
                                Log("INFO", "重新加载颜色配置文件在本机上需要管理员权限，已跳过。 " +
                                            "除非您使用校准配置文件并以管理员身份运行本程序，否则请关闭“重新加载颜色配置文件”。");
                            }
                            else
                            {
                                Log("ERROR", $"颜色配置文件重新加载失败：{error}");
                            }
                        }
                    }

                    int nudged = 0;
                    if (refreshApps)
                    {
                        WindowDetector.BroadcastDisplayChange();
                        nudged = WindowDetector.NudgeWindows(Array.Empty<string>());
                    }

                    Thread.Sleep(4000);
                    if (Cancelled()) return;

                    // Some drivers reset the gamma curve a second time a few seconds after the switch
                    if (baseline != null) CheckColorsAfterSdr(baseline, monitors, report: false);

                    if (reloadProfile && profileOk) RunCalibrationLoader(out _);
                    if (refreshApps)
                    {
                        WindowDetector.BroadcastDisplayChange();
                        WindowDetector.NudgeWindows(Array.Empty<string>());
                    }

                    if (reloadProfile && profileOk) Log("SDR", "颜色配置文件已重新应用。");
                    if (refreshApps) Log("SDR", $"已通知打开的应用程序显示已变化（{nudged} 个浏览器窗口已刷新）。");
                }
                catch (Exception ex)
                {
                    Log("ERROR", $"SDR 后清理失败：{ex.Message}");
                }
            });
        }

        /// <summary>
        /// Compares every display with its state right before HDR was switched on and logs what changed.
        /// A gamma curve that was reset to neutral (lost calibration, night light, NVIDIA desktop colour settings)
        /// is put back. Colour format / bit depth / colour mode changes are logged only (they belong to the driver).
        /// </summary>
        private void CheckColorsAfterSdr(List<DisplayColorState> before, MonitorFilter monitors, bool report)
        {
            List<DisplayColorState> after;
            try { after = HdrController.CaptureColorState(monitors); }
            catch (Exception ex)
            {
                if (report) Log("COLOR", $"SDR 后颜色检查失败：{ex.Message}");
                return;
            }

            foreach (var b in before)
            {
                var a = after.FirstOrDefault(x => string.Equals(x.Key, b.Key, StringComparison.OrdinalIgnoreCase));
                if (a == null || a.HdrOn || b.HdrOn) continue;   // unplugged, back in HDR, or was not in SDR before

                var changes = new List<string>();
                if (a.Encoding >= 0 && b.Encoding >= 0 && a.Encoding != b.Encoding)
                    changes.Add($"颜色格式 {DisplayColorState.EncodingName(a.Encoding)}（原为 {DisplayColorState.EncodingName(b.Encoding)}）– " +
                                "请在 NVIDIA 控制面板 › 更改分辨率 › 输出颜色格式中改回");
                if (a.Bits > 0 && b.Bits > 0 && a.Bits != b.Bits)
                    changes.Add($"{a.Bits}-bit（原为 {b.Bits}-bit）");
                if (a.Mode >= 0 && b.Mode >= 0 && a.Mode != b.Mode)
                    changes.Add($"{DisplayColorState.ModeName(a.Mode)}（原为 {DisplayColorState.ModeName(b.Mode)}）");

                bool rampReset = b.Ramp != null && !b.RampIsIdentity && a.RampIsIdentity;
                if (rampReset)
                {
                    bool restored = HdrController.RestoreGammaRamp(b);
                    changes.Add(restored ? "伽马曲线被重置为标准值 – 已恢复" : "伽马曲线被重置为标准值 – 无法恢复");
                }
                else if (report && b.Ramp != null && a.Ramp != null && DisplayColorState.RampDistance(a.Ramp, b.Ramp) > 300)
                {
                    changes.Add("伽马曲线发生变化（夜灯或颜色设置？）");
                }

                if (report)
                    Log("COLOR", changes.Count == 0
                        ? $"{b.Name}：颜色与 HDR 前相同（{a.Describe()}）。"
                        : $"{b.Name} 切回 SDR 后：{string.Join("；", changes)}。HDR 前：{b.Describe()}。");
                else if (rampReset)
                    Log("COLOR", $"{b.Name}：伽马曲线几秒后再次被重置 – {changes[^1].Split('–')[^1].Trim()}。");
            }
        }

        /// <summary>Cancels a pending colour profile reload (e.g. when HDR is switched on again).</summary>
        private void CancelColorProfileReload() => Interlocked.Increment(ref _profileReloadGeneration);

        private static bool RunCalibrationLoader(out string error)
        {
            error = string.Empty;
            var psi = new ProcessStartInfo("schtasks.exe",
                "/run /tn \"\\Microsoft\\Windows\\WindowsColorSystem\\Calibration Loader\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p == null)
            {
                error = "schtasks.exe could not be started";
                return false;
            }

            string stderr = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000))
            {
                error = "schtasks.exe did not finish";
                return false;
            }
            if (p.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr) ? $"schtasks.exe exit code {p.ExitCode}" : stderr.Trim();
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Public API for the UI (thread-safe)
        // ------------------------------------------------------------------
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

        /// <summary>Rows for the Games tab: every library game (real name, source, icon).</summary>
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
                    var procs = game.Processes.ToList();
                    if (procs.Count == 0) continue;

                    rows.Add(new GameRow
                    {
                        Key = game.Id,
                        Name = game.Name,
                        Subtitle = procs.Count > 1 ? $"{game.Source}  ·  {procs[0]}.exe +{procs.Count - 1}" : $"{game.Source}  ·  {procs[0]}.exe",
                        Processes = procs,
                        // library path, or one learned from a running game (e.g. Xbox games)
                        ExePath = game.ExePath ?? procs.Select(p => paths.TryGetValue(p, out var known) ? known : null)
                                                      .FirstOrDefault(p => p != null),
                        // on = the game is watched and its switch is on (turning the switch on starts watching it)
                        Hdr = procs.Any(tracked.Contains) && procs.All(p => IsHdrEnabledFor(hdrModes, p)),
                        Brightness = game.Brightness
                    });
                }

                // Programs added by the user that are not in a library game
                foreach (var proc in Config.HdrGamesAndApps ?? new List<string>())
                {
                    if (covered.Contains(proc)) continue;
                    rows.Add(new GameRow
                    {
                        Key = "proc:" + proc,
                        Name = proc,
                        Subtitle = $"程序  ·  {proc}.exe",
                        Processes = new List<string> { proc },
                        ExePath = paths.TryGetValue(proc, out var path) ? path : null,
                        Hdr = IsHdrEnabledFor(hdrModes, proc),
                        Brightness = brightnessMap.TryGetValue(proc, out var b) ? b : 50
                    });
                }

                return rows.OrderBy(r => r.Name, Cmp).ToList();
            }
        }

        public void SetGameHdr(IEnumerable<string> processes, bool enabled, string displayName)
        {
            var list = processes.Distinct(Cmp).ToList();
            lock (Sync)
            {
                var modes = Config.GameHdrModes;
                var tracked = Config.HdrGamesAndApps;
                bool added = false;

                foreach (string process in list)
                {
                    // the switch state is always stored explicitly: "no entry" must not read back as the opposite
                    modes[process] = enabled;
                    if (enabled && !tracked.Contains(process, Cmp))
                    {
                        tracked.Add(process);
                        added = true;
                    }
                }
                if (added) tracked.Sort(Cmp);

                InvalidateSnapshot();
                Save();
            }
            Log("LIST", enabled ? $"+ '{displayName}' 将使用 HDR" : $"- '{displayName}' 不再使用 HDR");
        }

        /// <summary>Save per-game brightness setting.</summary>
        public void SetGameBrightness(string gameKey, IEnumerable<string> processes, int brightness, string displayName)
        {
            var list = processes.ToList();
            lock (Sync)
            {
                // Update library game brightness
                var games = Config.LibraryGames ?? new List<LibraryGame>();
                bool found = false;
                foreach (var game in games)
                {
                    if (game.Id == gameKey)
                    {
                        game.Brightness = Math.Clamp(brightness, 0, 100);
                        found = true;
                        break;
                    }
                }

                // Update per-game brightness map for user-added programs
                if (!found)
                {
                    var brightnessMap = Config.PerGameBrightness ?? new Dictionary<string, int>();
                    foreach (var proc in list)
                    {
                        brightnessMap[proc] = Math.Clamp(brightness, 0, 100);
                    }
                    Config.PerGameBrightness = brightnessMap;
                }

                Save();
            }
            Log("BRIGHT", $"'{displayName}' 亮度设为 {brightness}");
        }

        // ------------------------------------------------------------------
        // Apps tab (2.6)
        // ------------------------------------------------------------------
        /// <summary>Copies of the Apps tab rules (process name -> rule).</summary>
        public Dictionary<string, AppRule> GetAppRules()
        {
            lock (Sync)
                return Config.AppRules.ToDictionary(kv => kv.Key,
                    kv => new AppRule { Category = kv.Value.Category, Mode = kv.Value.Mode, ExePath = kv.Value.ExePath, AddedByUser = kv.Value.AddedByUser },
                    Cmp);
        }

        /// <summary>
        /// Installed programs found by the Apps tab that are not in the list yet: added with their suggested mode.
        /// A program that is already listed keeps its mode (also "don't touch").
        /// </summary>
        public void AddFoundApps(IEnumerable<(string Process, string Category, string Mode)> found)
        {
            var added = new List<string>();
            lock (Sync)
            {
                foreach (var (process, category, mode) in found)
                {
                    string key = (process ?? string.Empty).Trim().ToLowerInvariant();
                    string? cat = AppCategories.Normalize(category);
                    if (key.Length == 0 || cat == null || Config.AppRules.ContainsKey(key)) continue;
                    Config.AppRules[key] = new AppRule { Category = cat, Mode = AppModes.Normalize(cat, mode) };
                    added.Add(key);
                }
                if (added.Count > 0)
                {
                    InvalidateSnapshot();
                    Save();
                }
            }
            if (added.Count > 0) Log("APPS", $"已找到并添加到程序选项卡：{string.Join("、", added)}。");
        }

        public void SetAppMode(string process, string mode, string displayName)
        {
            string? text = null;
            lock (Sync)
            {
                if (!Config.AppRules.TryGetValue(process, out var rule)) return;
                string normalized = AppModes.Normalize(rule.Category, mode);
                if (normalized == rule.Mode) return;
                rule.Mode = normalized;
                InvalidateSnapshot();
                Save();
                text = DescribeAppMode(rule.Category, normalized);
            }
            Log("APPS", $"'{displayName}'：{text}。");
        }

        /// <summary>A program picked by hand in the Apps tab (Add …). Returns false if the name is not usable.</summary>
        public bool AddApp(string exePath, string category)
        {
            string key = AppCatalog.ProcessOf(exePath);
            string? cat = AppCategories.Normalize(category);
            if (key.Length == 0 || cat == null || key == "explorer" || key == "smarthdrmanager") return false;
            lock (Sync)
            {
                Config.AppRules.TryGetValue(key, out var existing);
                Config.AppRules[key] = new AppRule
                {
                    Category = cat,
                    // already listed in the same group: keep its mode; otherwise the group's "on" mode
                    Mode = existing != null && existing.Category == cat ? existing.Mode : AppModes.Normalize(cat, null),
                    ExePath = exePath,
                    // picked by hand: always listed and removable (a program that is also found automatically comes
                    // back with its suggested mode after "Remove")
                    AddedByUser = true
                };
                InvalidateSnapshot();
                Save();
            }
            Log("APPS", $"+ '{key}' 已添加到程序选项卡（{cat}）。");
            return true;
        }

        /// <summary>Removes a program that was added by hand (programs found automatically can only be set to "off").</summary>
        public void RemoveApp(string process)
        {
            bool removed;
            lock (Sync)
            {
                removed = false;
                if (Config.AppRules.TryGetValue(process, out var rule))
                {
                    if (rule.AddedByUser) removed = Config.AppRules.Remove(process);
                    else if (rule.ExePath != null) { rule.ExePath = null; removed = true; }   // a known program picked by hand
                }
                if (removed)
                {
                    InvalidateSnapshot();
                    Save();
                }
            }
            if (removed) Log("APPS", $"- '{process}' 已从程序选项卡移除。");
        }

        public static string DescribeAppMode(string category, string mode) => (category, mode) switch
        {
            (_, AppModes.Off) => "不干预 HDR",
            (_, AppModes.HdrOnly) => "只有 HDR 视频才切换 HDR",
            _ => "视频按 设置 › 视频 的配置"
        };

        public bool AddGame(string procName, string? exePath = null)
        {
            if (string.IsNullOrWhiteSpace(procName) || procName == "explorer" || procName == "smarthdrmanager")
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

                // Initialize HDR mode for new programs (default: HDR enabled)
                Config.GameHdrModes[procName] = true;

                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    Config.ProgramPaths[procName] = exePath;   // used for the icon in the Games tab
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
                // Remove from HdrGamesAndApps
                foreach (string proc in procList)
                {
                    Config.HdrGamesAndApps.RemoveAll(g => g.Equals(proc, StringComparison.OrdinalIgnoreCase));
                }

                // Remove from ProgramPaths
                foreach (string proc in procList)
                {
                    Config.ProgramPaths.Remove(proc);
                }

                // Remove from GameHdrModes
                foreach (string proc in procList)
                {
                    Config.GameHdrModes.Remove(proc);
                }

                // Remove from LibraryGames if it's a library game
                var libGames = Config.LibraryGames ?? new List<LibraryGame>();
                var gameToRemove = libGames.FirstOrDefault(g => g.Id == key);
                if (gameToRemove != null)
                {
                    libGames.Remove(gameToRemove);
                    Log("LIST", $"'- {gameToRemove.Name}' 已从游戏库移除");
                }

                Config.HdrGamesAndApps.Sort(Cmp);
                InvalidateSnapshot();
                Save();
            }

            Log("LIST", $"- '{procList[0]}' 已移除");
        }

        /// <summary>True while a known HDR game runs (or HDR is still held for one) or any app is full-screen in front.</summary>
        private bool IsGameOrFullscreenActive(out string what)
        {
            string? game = Volatile.Read(ref _runningGame) ?? Volatile.Read(ref _activeHdrGameProcess);
            if (game != null) { what = $"'{game}'"; return true; }
            var status = Status;
            if (status.IsFullscreen)
            {
                what = string.IsNullOrEmpty(status.ActiveProcess) ? "全屏应用" : $"'{status.ActiveProcess}'（全屏）";
                return true;
            }
            what = string.Empty;
            return false;
        }

        public T Read<T>(Func<AppConfig, T> getter)
        {
            lock (Sync) return getter(Config);
        }

        public void Update(Action<AppConfig> change)
        {
            lock (Sync)
            {
                change(Config);
                InvalidateSnapshot();
                Save();
            }
        }

        /// <summary>Re-reads config.json (e.g. after the user edited it in a text editor).</summary>
        public bool ReloadConfig()
        {
            try
            {
                var fresh = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (fresh == null) return false;
                Normalize(fresh);
                lock (Sync)
                {
                    foreach (var prop in typeof(AppConfig).GetProperties())
                        if (prop.CanWrite) prop.SetValue(Config, prop.GetValue(fresh));
                    InvalidateSnapshot();
                }
                Log("CONFIG", "config.json 已重新加载。");
                return true;
            }
            catch (Exception ex)
            {
                Log("ERROR", $"config.json 无法重新加载：{ex.Message}");
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Config load / save (same JSON format as before)
        // ------------------------------------------------------------------
        private AppConfig LoadOrCreateConfig()
        {
            if (!File.Exists(ConfigPath))
                return Normalize(new AppConfig());

            try
            {
                string json = File.ReadAllText(ConfigPath);
                return Normalize(JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig());
            }
            catch (Exception ex)
            {
                try { File.Copy(ConfigPath, ConfigPath + ".bak", true); } catch { }
                Log("ERROR", $"config.json 无法读取（{ex.Message}）– 备份已保存为 config.json.bak，使用默认值。");
                return Normalize(new AppConfig());
            }
        }

        /// <summary>Lists / dictionaries that are missing or null in a hand-edited config.json become empty.</summary>
        private static AppConfig Normalize(AppConfig c)
        {
            c.CustomGameFolders ??= new List<string>();
            c.HdrGamesAndApps ??= new List<string>();
            c.SelectedMonitors ??= new List<string>();
            c.LibraryGames ??= new List<LibraryGame>();

            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in c.ProgramPaths ?? new Dictionary<string, string>())
                if (!string.IsNullOrEmpty(kv.Value)) paths[kv.Key] = kv.Value;
            c.ProgramPaths = paths;

            c.SdrFallbackDelaySeconds = Math.Clamp(c.SdrFallbackDelaySeconds, 0, 30);
            foreach (var g in c.LibraryGames) g.Processes ??= new List<string>();

            // Games tab switches: lower-case keys, no empty entries
            var hdrModes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in c.GameHdrModes ?? new Dictionary<string, bool>())
                if (!string.IsNullOrWhiteSpace(kv.Key)) hdrModes[kv.Key.Trim().ToLowerInvariant()] = kv.Value;
            c.GameHdrModes = hdrModes;

            // Learned launchers: case-insensitive keys, lower-case names, no empty entries
            var launchers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in c.LauncherGames ?? new Dictionary<string, List<string>>())
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                var games = kv.Value.Where(n => !string.IsNullOrWhiteSpace(n) && !n.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                                    .Select(n => n.Trim().ToLowerInvariant()).Distinct().ToList();
                if (games.Count > 0) launchers[kv.Key.Trim().ToLowerInvariant()] = games;
            }
            c.LauncherGames = launchers;

            // AppRules field kept for backward compatibility with old config files
            c.AppRules ??= new Dictionary<string, AppRule>(StringComparer.OrdinalIgnoreCase);

            var quick = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in c.QuickExitCounts ?? new Dictionary<string, int>())
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value > 0) quick[kv.Key.Trim().ToLowerInvariant()] = Math.Min(kv.Value, 99);
            c.QuickExitCounts = quick;
            return c;
        }

        /// <summary>Caller must hold <see cref="Sync"/> (or be in the constructor).</summary>
        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var options = new JsonSerializerOptions { WriteIndented = true };
                string tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Config, options));
                File.Move(tmp, ConfigPath, overwrite: true);   // atomic replace: never leaves a half-written config.json
            }
            catch (Exception ex)
            {
                Log("ERROR", $"config.json 无法保存：{ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Logging (memory ring buffer for the Log tab + %LocalAppData%\SmartHdrManager\log.txt)
        // ------------------------------------------------------------------
        public void Log(string tag, string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {tag,-6} {message}";
            lock (_logLock)
            {
                _log.Add(line);
                if (_log.Count > 500)
                {
                    _log.RemoveRange(0, 100);
                    _logBase += 100;
                }
            }
            WriteFileLog(line);
        }

        private static int _fileLogWrites;
        private const long MaxLogBytes = 2_000_000;

        public static void WriteFileLog(string line)
        {
            try
            {
                lock (FileLogLock)
                {
                    // The app runs for weeks in the tray: keep the log small (old part goes to log.old.txt)
                    if (++_fileLogWrites % 200 == 0 && File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                        File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.txt"), overwrite: true);
                    File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
                }
            }
            catch { }
        }

        /// <summary>Returns log lines added since <paramref name="cursor"/> and advances the cursor.</summary>
        public string[] ReadLog(ref int cursor)
        {
            lock (_logLock)
            {
                int start = Math.Max(cursor - _logBase, 0);
                var lines = _log.Skip(start).ToArray();
                cursor = _logBase + _log.Count;
                return lines;
            }
        }
    }
}
