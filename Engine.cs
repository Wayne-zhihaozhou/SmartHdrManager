using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SmartHdrManager
{
    public sealed class EngineStatus
    {
        public bool HdrOn { get; init; }
        public bool Paused { get; init; }
        public string ActiveProcess { get; init; } = string.Empty;
        public bool IsFullscreen { get; init; }
        public string? RunningGame { get; init; }
        public string? RunningSdrApp { get; init; }
        public string? FollowingLauncher { get; init; }
    }

    /// <summary>One row of the games list: a library game (several processes possible) or a single program.</summary>
    public sealed class GameRow
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
        public List<string> Processes { get; init; } = new();
        public string? ExePath { get; init; }

        /// <summary>The application is on the list this row belongs to (HDR or SDR).</summary>
        public bool Tracked { get; init; }

        public int Brightness { get; init; } = 50;
    }

    /// <summary>
    /// The HDR / SDR decision loop. Runs on its own background thread and owns every display switch.
    /// The UI only changes the configuration, which it always does under <see cref="Sync"/>.
    /// </summary>
    public sealed partial class HdrEngine
    {
        /// <summary>Old location next to the exe. Not writable when the app is installed in "Program Files".</summary>
        public static readonly string LegacyConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");

        /// <summary>A file named "portable.txt" next to the exe keeps the settings next to the exe.</summary>
        public static readonly bool IsPortable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

        public static readonly string ConfigPath = IsPortable
            ? LegacyConfigPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartHdrManager", "config.json");

        public static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartHdrManager", "log.txt");

        private const int ProcessScanPeriodMs = 1000;
        private const int HdrRequeryIntervalMs = 2000;
        private const int SwitchSpacingMs = 1500;
        private const int FailuresBeforeCooldown = 3;
        private const int FailureCooldownMs = 30000;
        private const int DisplayEventGraceMs = 5000;
        private const int LauncherShortRunMs = 15000;
        private const int LauncherFollowMs = 45000;
        private const int ExternalChangeGraceMs = 8000;
        private const int ExternalHoldCheckMs = 2000;
        private const int BrightnessMin = 0;
        private const int BrightnessMax = 100;

        private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

        public AppConfig Config { get; private set; }

        public object Sync { get; } = new();

        public volatile bool Paused;

        public EngineStatus Status { get; private set; } = new();

        private Thread? _thread;
        private volatile bool _stop;
        private readonly AutoResetEvent _wake = new(false);

        private sealed class Snapshot
        {
            public readonly HashSet<string> HdrGames;
            public readonly HashSet<string> SdrGames;
            public readonly HashSet<string> AllGames;
            public readonly bool EnableOnProcessStart;
            public readonly bool BrightnessEnabled;
            public readonly int FallbackSeconds;
            public readonly int TickMs;
            public readonly int DesktopBrightness;
            public readonly Dictionary<string, int> Brightness;
            public readonly Dictionary<string, (string[] Folders, string[] Exes)> GameFolders;

            public Snapshot(AppConfig c)
            {
                HdrGames = new HashSet<string>(c.HdrGamesAndApps ?? new List<string>(), Cmp);
                HdrGames.RemoveWhere(process => !IsHdrEnabledFor(c.GameHdrModes, process));

                SdrGames = new HashSet<string>(c.SdrGamesAndApps ?? new List<string>(), Cmp);
                SdrGames.ExceptWith(HdrGames);   // a process on both lists follows the HDR rule

                AllGames = new HashSet<string>(HdrGames, Cmp);
                AllGames.UnionWith(SdrGames);

                EnableOnProcessStart = c.EnableHdrOnProcessStart;
                BrightnessEnabled = c.Brightness?.Enabled ?? true;
                FallbackSeconds = Math.Max(0, c.SdrFallbackDelaySeconds);
                TickMs = Math.Clamp(c.ProcessScanIntervalMs, 50, 1000);
                DesktopBrightness = Math.Clamp(c.DesktopBrightness, BrightnessMin, BrightnessMax);
                Brightness = BuildBrightness(c);
                GameFolders = BuildGameFolders(c, AllGames);
            }

            /// <summary>TV brightness per process: per-game config value.</summary>
            private static Dictionary<string, int> BuildBrightness(AppConfig c)
            {
                var brightness = new Dictionary<string, int>(Cmp);

                foreach (var entry in c.PerGameBrightness ?? new Dictionary<string, int>())
                    if (!brightness.ContainsKey(entry.Key))
                        brightness[entry.Key] = Math.Clamp(entry.Value, BrightnessMin, BrightnessMax);

                return brightness;
            }

            /// <summary>Brightness of one process, or the desktop value.</summary>
            public int BrightnessFor(string? process) =>
                process != null && Brightness.TryGetValue(process, out int value) ? value : DesktopBrightness;

            /// <summary>
            /// Game install folders and known exes, by process name. No library games are scanned anymore;
            /// only explicitly added programs are matched (by name, not by folder).
            /// </summary>
            private static Dictionary<string, (string[] Folders, string[] Exes)> BuildGameFolders(AppConfig c, HashSet<string> trackedGames)
            {
                return new Dictionary<string, (string[] Folders, string[] Exes)>(Cmp);
            }

        }

        /// <summary>The games list switch of one process: on unless the config disables it.</summary>
        private static bool IsHdrEnabledFor(Dictionary<string, bool>? modes, string process) =>
            modes == null || !modes.TryGetValue(process, out bool enabled) || enabled;

        private volatile Snapshot? _snapshot;
        private Snapshot? _lastSnapshot;

        private bool _hdrAny;
        private bool _verifyOn;
        private long _nextHdrQueryMs;
        private int _forceHdrQuery = 1;
        private bool _firstHdrQueryDone;
        private bool _apiLogged;

        private string? _runningGame;
        private string? _runningSdrApp;
        private string? _brightnessApp;
        private bool _brightnessApplied;
        private long _nextProcessScanMs;
        private string? _activeHdrGameProcess;

        private string? _seenGame;
        private long _seenGameSinceMs;
        private bool _launcherTrackingStarted;
        private string? _followLauncher;
        private long _followUntilMs;
        private bool _followHoldsHdr;
        private DateTime _followNotBefore;
        private int _followGeneration;
        private volatile HashSet<string>? _followNames;

        private bool _hdrOnByApp;

        private string? _handOffDemand;
        private string _lastDemand = "none";
        private bool? _expectAny;
        private bool _switchConfirmed = true;
        private long _displayEventMs = long.MinValue / 2;
        private bool _externalEnded;
        private string _externalEndedWhat = string.Empty;

        private string? _hdrOnForGame;
        private long _hdrOnForGameAtMs;

        private string _foregroundProcess = string.Empty;
        private bool _foregroundFullscreen;
        private string? _externalHoldProcess;
        private long _nextExternalCheckMs;

        private long _sdrFallbackAtMs = -1;
        private long _lastAttemptMs = long.MinValue / 2;
        private long _cooldownUntilMs;
        private int _consecutiveFailures;

        private static long Now => Environment.TickCount64;

        public HdrEngine()
        {
            InitializeLogFile();
            MigrateLegacyConfig();
            Config = LoadOrCreateConfig();
            UpgradeConfig(Config);
            Save();
        }

        public void Start()
        {
            Log("INFO", "Smart HDR Manager 已启动 – 监控中。");
            ApplyBrightnessControlSettings();

            _thread = new Thread(Run) { IsBackground = true, Name = "HdrEngine" };
            _thread.Start();
        }

        /// <summary>Connects or disconnects the TV after the brightness control switch was changed.</summary>
        public void ApplyBrightnessControlSettings()
        {
            var brightness = Read(config => config.Brightness);
            if (brightness == null || !brightness.Enabled)
            {
                TvCli.Shutdown();
                return;
            }

            TvCli.Configure(brightness.TvIp);
        }

        public void Stop()
        {
            _stop = true;
            _wake.Set();

            bool finished = true;
            try { finished = _thread == null || _thread.Join(2000); } catch { }
            if (finished)
            {
                TvCli.Shutdown();
                RestoreSdrOnExit();
            }
            lock (Sync) Save();
        }

        /// <summary>
        /// Exit: if this app switched HDR on, go back to SDR. HDR that was on before the app started or was switched by
        /// something else is left alone, and so is HDR for a game that is still running.
        /// </summary>
        private void RestoreSdrOnExit()
        {
            try
            {
                if (!_hdrAny || !_hdrOnByApp) return;

                string? busy = _runningGame != null ? $"'{_runningGame}' 仍在运行"
                    : _externalHoldProcess != null ? $"'{_externalHoldProcess}' 仍在运行"
                    : null;
                if (busy != null)
                {
                    Log("SDR", $"程序已关闭 – 因为 {busy}，HDR 保持开启。");
                    return;
                }

                Log("SDR", "程序已关闭 – 切回 SDR，HDR 是本程序开启的。");
                if (!HdrController.SetHdrState(false)) Log("ERROR", HdrController.LastError);
            }
            catch (Exception ex)
            {
                Log("ERROR", $"退出时无法切回 SDR：{ex.Message}");
            }
        }

        /// <summary>Forces the HDR state to be re-read on the next tick (display change, resume from sleep).</summary>
        public void InvalidateHdrState(bool resumed = false)
        {
            if (resumed) Interlocked.Exchange(ref _displayEventMs, Now);
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
                    var snapshot = GetSnapshot();
                    waitMs = snapshot.TickMs;
                    Tick(snapshot);
                }
                catch (Exception ex)
                {
                    Log("ERROR", $"引擎错误：{ex.Message}");
                    waitMs = 1000;
                }

                _wake.WaitOne(waitMs);
            }
        }

        private Snapshot GetSnapshot()
        {
            var snapshot = _snapshot;
            if (snapshot != null) return snapshot;
            lock (Sync)
            {
                snapshot = _snapshot ??= new Snapshot(Config);
                return snapshot;
            }
        }

        /// <summary>Call after every configuration change (while holding <see cref="Sync"/>).</summary>
        private void InvalidateSnapshot()
        {
            _snapshot = null;
            _wake.Set();
        }

        private void Tick(Snapshot snapshot)
        {
            long now = Now;

            if (!ReferenceEquals(snapshot, _lastSnapshot))
            {
                _lastSnapshot = snapshot;
                _nextProcessScanMs = 0;
            }

            var (procName, _, isFullscreen, procId) = WindowDetector.GetActiveWindowInfo();
            _foregroundProcess = procName;
            _foregroundFullscreen = isFullscreen;

            bool foregroundListed = snapshot.AllGames.Contains(procName) && snapshot.GameFolders.ContainsKey(procName);
            _foregroundElsewhere = foregroundListed && !IsGameLocation(snapshot, procName, procId);

            RefreshHdrState(now);

            if (now >= _nextProcessScanMs)
            {
                _runningGame = FindRunningApp(snapshot, snapshot.HdrGames, out string? runningPath);
                _runningSdrApp = FindRunningApp(snapshot, snapshot.SdrGames, out _);
                _nextProcessScanMs = now + ProcessScanPeriodMs;
                if (_runningGame != null && runningPath != null) RememberProgramPath(_runningGame, runningPath);
                TrackLauncherFollow(snapshot, now);
            }

            if (!Paused)
                Decide(snapshot, now, procName);

            // Brightness follows its own switch, not the HDR control: pausing HDR must not freeze the TV brightness
            UpdateAppBrightness(snapshot, procName);

            Status = new EngineStatus
            {
                HdrOn = _hdrAny,
                Paused = Paused,
                ActiveProcess = procName,
                IsFullscreen = isFullscreen,
                RunningGame = _runningGame,
                RunningSdrApp = _runningSdrApp,
                FollowingLauncher = _followLauncher
            };
        }

        private void RefreshHdrState(long now)
        {
            bool forced = Interlocked.Exchange(ref _forceHdrQuery, 0) == 1;
            if (!forced && now < _nextHdrQueryMs)
                return;

            if (HdrController.TryQueryHdrState(out bool any))
            {
                if (!_apiLogged && HdrController.HdrApiName != "not determined yet")
                {
                    _apiLogged = true;
                    Log("INFO", $"显示控制：{HdrController.HdrApiName} – {WindowsVersion.Describe()}。");
                }

                if (!_firstHdrQueryDone && any)
                    Log("HDR", "HDR 已开启 – 保持现状（本程序只切回自己开启的 HDR）。");

                bool towardOwn = _expectAny.HasValue && any == _expectAny.Value;
                if (towardOwn) _switchConfirmed = true;

                if (_firstHdrQueryDone && any != _hdrAny && !towardOwn &&
                    (_switchConfirmed || now - _lastAttemptMs > ExternalChangeGraceMs))
                    HandleExternalChange(any, now);

                if (!any) _externalHoldProcess = null;

                _firstHdrQueryDone = true;
                _hdrAny = any;

                if (_verifyOn && !forced)
                {
                    _verifyOn = false;
                    if (!any)
                    {
                        Log("ERROR", "HDR 已切换开启，但显示器未报告 HDR 处于活动状态。");
                        RegisterFailure(now);
                    }
                    else
                    {
                        _consecutiveFailures = 0;
                    }
                }
            }
            _nextHdrQueryMs = now + HdrRequeryIntervalMs;
        }

        /// <summary>
        /// HDR changed although this app did not switch it: a game, Win+Alt+B, Windows Settings or another tool did it.
        /// Logged with the window in front, so reports show which games switch HDR themselves. Right after an own
        /// switch, Windows may still report the old state for a moment: that is not a change by someone else.
        /// </summary>
        private void HandleExternalChange(bool any, long now)
        {
            string front = string.IsNullOrEmpty(_foregroundProcess) ? "unknown" : _foregroundProcess;
            Log("INFO", $"Windows HDR 在未经本程序的情况下变为 {(any ? "开启" : "关闭")} – 由游戏、Win+Alt+B 或 Windows 设置触发（前台窗口：{front}）。");

            _hdrOnByApp = false;
            _hdrOnForGame = null;
            _expectAny = any;
            _switchConfirmed = true;

            if (any && _foregroundFullscreen && front is not ("unknown" or "explorer" or "smarthdrmanager"))
            {
                _externalHoldProcess = front;
                _externalEnded = false;
                Log("HDR", $"'{front}' 自行开启了 HDR – 其运行期间保持 HDR 开启。");
                return;
            }

            if (Paused) return;

            bool displayEvent = now - Interlocked.Read(ref _displayEventMs) < DisplayEventGraceMs;
            if (!any && displayEvent) return;

            _handOffDemand = _lastDemand;
            if (any)
                Log("HDR", "HDR 由您或其他程序开启 – 保持开启。本程序只切回自己开启的 HDR。");
            else if (_lastDemand != "none")
                Log("HDR", $"HDR 由您或其他程序关闭 – 在 {DescribeDemand(_lastDemand)} 结束前（或下一个游戏 / 视频启动前）不再开启。");
        }

        private void Decide(Snapshot snapshot, long now, string procName)
        {
            bool shouldEnableHdr = EvaluateHdrRequirement(snapshot, procName, _foregroundElsewhere, out string reason);
            bool canAttempt = now - _lastAttemptMs >= SwitchSpacingMs && now >= _cooldownUntilMs;

            if (TryForceSdr(snapshot, procName, shouldEnableHdr, canAttempt)) return;

            if (_externalHoldProcess != null)
            {
                if (now >= _nextExternalCheckMs)
                {
                    _nextExternalCheckMs = now + ExternalHoldCheckMs;
                    if (!ProcessNames.IsRunning(_externalHoldProcess))
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

            if (_followLauncher != null && _followHoldsHdr && !shouldEnableHdr)
            {
                shouldEnableHdr = true;
                reason = $"等待 '{_followLauncher}' 启动的游戏";
            }

            if (!shouldEnableHdr && snapshot.EnableOnProcessStart && _runningGame != null)
            {
                shouldEnableHdr = true;
                reason = $"HDR 游戏已启动 / 正在运行（{_runningGame}）";
            }

            if (_runningGame != null)
                _activeHdrGameProcess = _runningGame;

            string demand;
            if (!shouldEnableHdr) demand = "none";
            else if (_runningGame != null) demand = "game:" + _runningGame;
            else if (snapshot.HdrGames.Contains(procName) && !_foregroundElsewhere) demand = "game:" + procName;
            else if (_externalHoldProcess != null) demand = "ext:" + _externalHoldProcess;
            else if (_followLauncher != null && _followHoldsHdr) demand = "follow:" + _followLauncher;
            else demand = "focus:" + procName;

            if (HandedOff(demand))
            {
                _sdrFallbackAtMs = -1;
                _externalEnded = false;
                if (_runningGame == null) _activeHdrGameProcess = null;
                return;
            }

            bool owns = _hdrOnByApp;

            if (shouldEnableHdr)
            {
                _sdrFallbackAtMs = -1;
                _externalEnded = false;
                if (!_hdrAny && canAttempt)
                {
                    string? game = _runningGame ?? (snapshot.HdrGames.Contains(procName) && !_foregroundElsewhere ? procName : null);
                    TurnHdrOn(reason);
                    if (_hdrAny && game != null)
                    {
                        _hdrOnForGame = game;
                        _hdrOnForGameAtMs = now;
                    }
                }
                return;
            }

            if (!_hdrAny)
            {
                _activeHdrGameProcess = null;
                _sdrFallbackAtMs = -1;
                _externalEnded = false;
                return;
            }

            if (_activeHdrGameProcess != null && _runningGame == null)
            {
                if (now >= _cooldownUntilMs)
                {
                    string closedGame = _activeHdrGameProcess;
                    _activeHdrGameProcess = null;
                    _sdrFallbackAtMs = -1;
                    if (owns) UndoOwnHdr($"HDR game closed ({closedGame})");
                    else Log("HDR", $"'{closedGame}' 已关闭 – HDR 保持开启（不是本程序开启的）。");
                }
                return;
            }

            if (_externalEnded)
            {
                if (canAttempt)
                {
                    _externalEnded = false;
                    _sdrFallbackAtMs = -1;
                    TurnHdrOff($"'{_externalEndedWhat}' 已关闭（它曾自行开启 HDR）");
                }
                return;
            }

            if (!owns)
            {
                _sdrFallbackAtMs = -1;
                return;
            }

            if (_sdrFallbackAtMs < 0)
            {
                _sdrFallbackAtMs = now + snapshot.FallbackSeconds * 1000L;
            }
            else if (now >= _sdrFallbackAtMs && canAttempt)
            {
                _sdrFallbackAtMs = -1;
                UndoOwnHdr("无 HDR 内容处于活动状态（桌面模式）");
            }
        }

        /// <summary>
        /// An application on the SDR list wants HDR off. The HDR list wins whenever one of its applications is active,
        /// so this only steps in while nothing from the HDR list is. Returns true when the tick was handled here.
        /// </summary>
        private bool TryForceSdr(Snapshot snapshot, string procName, bool hdrAppInFront, bool canAttempt)
        {
            if (hdrAppInFront || _runningGame != null) return false;

            string? sdrApp = _runningSdrApp;
            if (sdrApp == null && snapshot.SdrGames.Contains(procName) && !_foregroundElsewhere) sdrApp = procName;
            if (sdrApp == null) return false;

            _activeHdrGameProcess = null;
            _sdrFallbackAtMs = -1;
            _externalEnded = false;

            if (_hdrAny && canAttempt)
                TurnHdrOff($"'{sdrApp}' 在 SDR 列表中 – HDR 保持关闭");

            return true;
        }

        /// <summary>
        /// Applies the brightness of the application that owns the screen right now. A running HDR game keeps its
        /// brightness even when the user switches away; otherwise the program in front decides, and the desktop
        /// brightness applies while a program that is on neither list is in front. Only a change is written to the TV.
        /// </summary>
        private void UpdateAppBrightness(Snapshot snapshot, string procName)
        {
            if (!snapshot.BrightnessEnabled)
            {
                _brightnessApplied = false;
                _brightnessApp = null;
                return;
            }

            string? active = ResolveBrightnessApp(snapshot, procName);

            if (_brightnessApplied && string.Equals(active, _brightnessApp, StringComparison.OrdinalIgnoreCase)) return;

            bool firstCheck = !_brightnessApplied;
            _brightnessApplied = true;
            _brightnessApp = active;

            // Nothing listed is running at start: leave the TV as the user left it
            if (firstCheck && active == null) return;

            ApplyBrightness(snapshot.BrightnessFor(active), active);
        }

        /// <summary>
        /// The application whose brightness belongs on screen: the running HDR game, otherwise the listed program in
        /// front (HDR or SDR list), otherwise null for the desktop brightness. A program from either list that keeps
        /// running in the background does not hold the brightness – switching to it is what applies it.
        /// </summary>
        private string? ResolveBrightnessApp(Snapshot snapshot, string procName)
        {
            if (_runningGame != null) return _runningGame;
            if (string.IsNullOrEmpty(procName) || _foregroundElsewhere) return null;
            if (snapshot.HdrGames.Contains(procName) || snapshot.SdrGames.Contains(procName)) return procName;
            return null;
        }

        private void ApplyBrightness(int brightness, string? app)
        {
            Log("BRIGHT", $"{(app == null ? "桌面" : $"'{app}'")} – 亮度 {brightness}");
            _ = Task.Run(async () =>
            {
                try { await TvCli.SetBrightnessAsync(brightness).ConfigureAwait(false); }
                catch (Exception ex) { Log("ERROR", $"设置亮度失败：{ex.Message}"); }
            });
        }

        /// <summary>
        /// HDR was changed by something else (you, a game, another program): the app keeps its hands off until what
        /// wants HDR changes – a new game or video starts, or the current one ends. Returns true while handed off.
        /// </summary>
        private bool HandedOff(string demand)
        {
            _lastDemand = demand;
            if (_handOffDemand == null) return false;
            if (string.Equals(demand, _handOffDemand, StringComparison.OrdinalIgnoreCase)) return true;
            _handOffDemand = null;
            Log("HDR", $"自动切换重新接管（{(demand == "none" ? "您的列表中已无活动项目" : DescribeDemand(demand))}）。");
            return false;
        }

        /// <summary>"game:witcher3" -> "'witcher3'" – for the log.</summary>
        private static string DescribeDemand(string demand)
        {
            int colon = demand.IndexOf(':');
            string kind = colon < 0 ? demand : demand.Substring(0, colon);
            string what = colon < 0 ? string.Empty : demand.Substring(colon + 1);
            return kind switch
            {
                "game" or "ext" or "focus" => $"'{what}'",
                "follow" => $"由 '{what}' 启动的游戏",
                "sdr" => $"'{what}'（SDR 程序）",
                _ => "此项目"
            };
        }

        /// <summary>
        /// The reason this app switched HDR on has ended: back to SDR.
        /// </summary>
        private void UndoOwnHdr(string reason) => TurnHdrOff(reason);

        private void TurnHdrOn(string reason)
        {
            long now = Now;
            _lastAttemptMs = now;
            Log("HDR", $"开启 HDR – {reason}");

            bool ok = HdrController.SetHdrState(true);
            if (ok)
            {
                _hdrOnByApp = true;
                _hdrAny = true;
                _verifyOn = true;
                _expectAny = true;
                _switchConfirmed = false;
            }
            else
            {
                Log("ERROR", HdrController.LastError);
            }
            AfterSwitch(ok, now);
        }

        private void TurnHdrOff(string reason)
        {
            long now = Now;
            _lastAttemptMs = now;
            Log("SDR", $"切换到 SDR – {reason}");

            bool ok = HdrController.SetHdrState(false);
            if (ok)
            {
                _hdrOnByApp = false;
                _expectAny = false;
                _switchConfirmed = false;
                _hdrAny = false;
                _verifyOn = false;
            }
            else
            {
                Log("ERROR", HdrController.LastError);
            }
            AfterSwitch(ok, now);
        }

        private void AfterSwitch(bool ok, long now)
        {
            _nextHdrQueryMs = now + SwitchSpacingMs;

            if (ok)
            {
                if (!_verifyOn) _consecutiveFailures = 0;
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

        private static bool EvaluateHdrRequirement(Snapshot snapshot, string procName, bool otherProgram, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrEmpty(procName)) return false;
            if (!snapshot.HdrGames.Contains(procName) || otherProgram) return false;

            reason = $"HDR 游戏 / 应用在前台（{procName}）";
            return true;
        }
    }
}
