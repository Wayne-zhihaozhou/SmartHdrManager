using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HdrBrightControl
{
    public sealed class EngineStatus
    {
        public bool HdrOn { get; init; }
        public bool Paused { get; init; }
        public string? BrightnessOwner { get; init; }
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
    public sealed partial class HdrEngine : IAppLog
    {
        /// <summary>Old location next to the exe. Not writable when the app is installed in "Program Files".</summary>
        public static readonly string LegacyConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");

        /// <summary>A file named "portable.txt" next to the exe keeps the settings next to the exe.</summary>
        public static readonly bool IsPortable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

        public static readonly string ConfigPath = IsPortable
            ? LegacyConfigPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HdrBrightControl", "config.json");

        public static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HdrBrightControl", "log.txt");

        private const int ProcessScanPeriodMs = 1000;
        private const int SwitchSpacingMs = 1500;
        private const int FailuresBeforeCooldown = 3;
        private const int FailureCooldownMs = 30000;
        private const int DisplayEventGraceMs = 5000;
        private const int ExternalChangeGraceMs = 8000;
        private const int ExternalHoldCheckMs = 2000;
        private const int BrightnessSettleAfterHdrMs = 2500;
        private const int BrightnessVerifyAfterHdrMs = 6000;
        private const int BrightnessRetryDelayMs = 2000;
        private const int BrightnessMaxAttempts = 5;
        private const long NeverHappenedMs = long.MinValue / 2;
        private const int DefaultEngineWaitMs = 100;

        /// <summary>Process name of this app, lower case: its own window is not content that decides anything.</summary>
        public static readonly string SelfProcessName = ResolveSelfProcessName();

        private static string ResolveSelfProcessName()
        {
            string? exe = Environment.ProcessPath;
            string name = string.IsNullOrEmpty(exe) ? "HdrBrightControl" : Path.GetFileNameWithoutExtension(exe);
            return name.ToLowerInvariant();
        }

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
            public readonly ProgramMatcher Programs;
            public readonly bool EnableOnProcessStart;
            public readonly bool BrightnessEnabled;
            public readonly int FallbackSeconds;
            public readonly int TickMs;
            public readonly int DesktopBrightness;
            public readonly Dictionary<string, int> Brightness;

            public Snapshot(AppConfig c)
            {
                Programs = new ProgramMatcher(BuildListedPrograms(c));
                EnableOnProcessStart = c.EnableHdrOnProcessStart;
                BrightnessEnabled = c.Brightness?.Enabled ?? true;
                FallbackSeconds = Math.Max(0, c.SdrFallbackDelaySeconds);
                TickMs = Math.Clamp(c.ProcessScanIntervalMs, 50, 1000);
                DesktopBrightness = Math.Clamp(c.DesktopBrightness, (int)BrightnessRange.Min, (int)BrightnessRange.Max);
                Brightness = BuildBrightness(c);
            }

            /// <summary>The programs of both lists; a process on both lists follows the HDR rule.</summary>
            private static List<ListedProgram> BuildListedPrograms(AppConfig c)
            {
                var paths = c.ProgramPaths ?? new Dictionary<string, string>(Cmp);
                var hdr = new List<string>(c.HdrGamesAndApps ?? new List<string>());
                hdr.RemoveAll(process => !IsHdrEnabledFor(c.GameHdrModes, process));

                var programs = new List<ListedProgram>(hdr.Count);
                foreach (string process in hdr)
                    programs.Add(Listed(process, isHdr: true, paths));

                foreach (string process in c.SdrGamesAndApps ?? new List<string>())
                    if (!programs.Exists(entry => string.Equals(entry.Entry, process, StringComparison.OrdinalIgnoreCase)))
                        programs.Add(Listed(process, isHdr: false, paths));

                return programs;
            }

            private static ListedProgram Listed(string process, bool isHdr, Dictionary<string, string> paths) => new()
            {
                Entry = process,
                IsHdr = isHdr,
                ExePath = paths.TryGetValue(process, out var path) ? path : null
            };

            /// <summary>TV brightness per list entry: per-program config value.</summary>
            private static Dictionary<string, int> BuildBrightness(AppConfig c)
            {
                var brightness = new Dictionary<string, int>(Cmp);

                foreach (var entry in c.PerGameBrightness ?? new Dictionary<string, int>())
                    if (!brightness.ContainsKey(entry.Key))
                        brightness[entry.Key] = Math.Clamp(entry.Value, (int)BrightnessRange.Min, (int)BrightnessRange.Max);

                return brightness;
            }

            /// <summary>Brightness of one list entry, or the desktop value.</summary>
            public int BrightnessFor(ListedProgram? program) =>
                program != null && Brightness.TryGetValue(program.Entry, out int value) ? value : DesktopBrightness;
        }

        /// <summary>The games list switch of one process: on unless the config disables it.</summary>
        private static bool IsHdrEnabledFor(Dictionary<string, bool>? modes, string process) =>
            modes == null || !modes.TryGetValue(process, out bool enabled) || enabled;

        private volatile Snapshot? _snapshot;
        private Snapshot? _lastSnapshot;

        private bool _hdrAny;
        private bool _verifyOn;
        private int _forceHdrQuery = 1;
        private bool _firstHdrQueryDone;
        private bool _apiLogged;
        private bool _noHdrLogged;

        private string? _runningGame;
        private string? _runningSdrApp;
        private ListedProgram? _contentProgram;
        private string _contentProcess = string.Empty;
        private int _contentProcessId;
        private readonly HashSet<string> _identityLogged = new(Cmp);

        private BrightnessState _brightness = new();

        private long _hdrChangedMs = NeverHappenedMs;
        private long _nextProcessScanMs;
        private string? _activeHdrGameProcess;

        private HdrState _hdr = new();
        private bool _externalEnded;
        private string _externalEndedWhat = string.Empty;

        private string _foregroundProcess = string.Empty;
        private bool _foregroundFullscreen;
        private string? _externalHoldProcess;
        private long _nextExternalCheckMs;

        private DecisionState _decision = new();

        private static long Now => Environment.TickCount64;

        private readonly IHdrSwitch _hdrSwitch;
        private readonly IClock _clock;
        private readonly IConfigStore _configStore;

        public HdrEngine()
        {
            _clock = new SystemClock();
            _hdrSwitch = new WindowsHdrSwitch();
            _configStore = new DiskConfigStore();
            InitializeLogFile();
            MigrateLegacyConfig();
            Config = _configStore.Load(ConfigPath, this, ConfigPath + ".bak", LegacyConfigPath);
            UpgradeConfig(Config);
            Save();
        }

        public void Start()
        {
            Log("INFO", "HDR亮控 已启动 – 监控中。");
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
                if (!_hdrAny || !_hdr.OnByApp) return;

                string? busy = _runningGame != null ? $"'{_runningGame}' 仍在运行"
                    : _externalHoldProcess != null ? $"'{_externalHoldProcess}' 仍在运行"
                    : null;
                if (busy != null)
                {
                    Log("SDR", $"程序已关闭 – 因为 {busy}，HDR 保持开启。");
                    return;
                }

                Log("SDR", "程序已关闭 – 切回 SDR，HDR 是本程序开启的。");
                var result = _hdrSwitch.Set(false);
                if (!result.Ok) Log("ERROR", result.Error);
            }
            catch (Exception ex)
            {
                Log("ERROR", $"退出时无法切回 SDR：{ex.Message}");
            }
        }

        /// <summary>Forces the HDR state to be re-read on the next tick (display change, resume from sleep).</summary>
        public void InvalidateHdrState(bool resumed = false)
        {
            if (resumed) Interlocked.Exchange(ref _hdr.DisplayEventMs, _clock.NowMs);
            Interlocked.Exchange(ref _forceHdrQuery, 1);
            _wake.Set();
        }

        private void Run()
        {
            while (!_stop)
            {
                int waitMs = DefaultEngineWaitMs;
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
            long now = _clock.NowMs;

            if (!ReferenceEquals(snapshot, _lastSnapshot))
            {
                _lastSnapshot = snapshot;
                _nextProcessScanMs = 0;
            }

            var (procName, _, isFullscreen, procId) = WindowDetector.GetActiveWindowInfo();
            _foregroundProcess = procName;
            _foregroundFullscreen = isFullscreen;

            UpdateForegroundProgram(snapshot, procName, procId);

            RefreshHdrState(now);

            if (now >= _nextProcessScanMs)
            {
                ScanRunningPrograms(snapshot);
                _nextProcessScanMs = now + ProcessScanPeriodMs;
            }

            if (!Paused)
                Decide(snapshot, now);

            // Brightness follows its own switch, not the HDR control: pausing HDR must not freeze the TV brightness
            UpdateAppBrightness(snapshot);

            Status = new EngineStatus
            {
                HdrOn = _hdrAny,
                Paused = Paused,
                BrightnessOwner = _brightness.Owner
            };
        }

        /// <summary>
        /// The program that owns HDR and the TV brightness right now. This app's own window does not count as content:
        /// opening the settings must not hand the screen to the desktop brightness and back, so the last real
        /// foreground program keeps deciding until another one takes over.
        /// </summary>
        private void UpdateForegroundProgram(Snapshot snapshot, string processName, int processId)
        {
            if (!string.IsNullOrEmpty(processName) && !IsSelf(processName))
            {
                _contentProcess = processName;
                _contentProcessId = processId;
            }

            var match = snapshot.Programs.MatchProcess(_contentProcess, _contentProcessId);
            _contentProgram = match.Program;

            if (match.Kind is ProgramMatchKind.ExePath or ProgramMatchKind.Product)
                LogIdentityMatch(_contentProcess, match);
        }

        private static bool IsSelf(string processName) =>
            processName.Equals(SelfProcessName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Logs once per program that a list entry was recognised by its exe, so a report shows why an app that runs a
        /// sibling exe (PotPlayerMini64.exe for an entry called potplayer64) is treated as that entry.
        /// </summary>
        private void LogIdentityMatch(string processName, ProgramMatch match)
        {
            string entry = match.Program?.Entry ?? string.Empty;
            string key = string.Concat(processName, "|", entry);
            lock (_identityLogged)
                if (!_identityLogged.Add(key)) return;

            string how = match.Kind == ProgramMatchKind.ExePath ? "同一个 exe" : "同目录、同产品名";
            Log("LIST", $"'{processName}' 被识别为列表中的 '{entry}'（{how}）– 按该条目处理。");
        }

        private void RefreshHdrState(long now)
        {
            if (Interlocked.Exchange(ref _forceHdrQuery, 0) != 1)
                return;

            if (_hdrSwitch.TryQuery(out bool supported, out bool any))
            {
                if (!_apiLogged && _hdrSwitch.ApiDescription != "未知")
                {
                    _apiLogged = true;
                    string apiName = _hdrSwitch.ApiDescription switch
                    {
                        "modern HDR API (Windows 11 24H2 或更新版本)" => "modern HDR API (Windows 11 24H2 或更新版本)",
                        "classic 高级颜色 API" => "classic 高级颜色 API",
                        _ => _hdrSwitch.ApiDescription
                    };
                    Log("INFO", $"显示控制：{apiName} – {WindowsVersion.Describe()}。");
                }

                if (!supported)
                {
                    if (!_noHdrLogged)
                    {
                        _noHdrLogged = true;
                        Log("INFO", "未检测到支持 HDR 的显示器。");
                    }
                }
                else if (!_firstHdrQueryDone && any)
                    Log("HDR", "HDR 已开启 – 保持现状（本程序只切回自己开启的 HDR）。");

                bool towardOwn = _hdr.ExpectAny.HasValue && any == _hdr.ExpectAny.Value;
                if (towardOwn) _hdr.SwitchConfirmed = true;

                if (_firstHdrQueryDone && any != _hdrAny && !towardOwn &&
                    (_hdr.SwitchConfirmed || now - _decision.LastAttemptMs > ExternalChangeGraceMs))
                    HandleExternalChange(any, now);

                if (!any) _externalHoldProcess = null;

                _firstHdrQueryDone = true;
                if (_hdrAny != any) _hdrChangedMs = now;
                _hdrAny = any;

                if (_verifyOn)
                {
                    _verifyOn = false;
                    if (!any)
                    {
                        Log("ERROR", "HDR 已切换开启，但显示器未报告 HDR 处于活动状态。");
                        RegisterFailure(now);
                    }
                    else
                    {
                        _decision.ConsecutiveFailures = 0;
                    }
                }
            }
        }

        /// <summary>
        /// HDR changed although this app did not switch it: a game, Win+Alt+B, Windows Settings or another tool did it.
        /// Logged with the window in front, so reports show which games switch HDR themselves. Right after an own
        /// switch, Windows may still report the old state for a moment: that is not a change by someone else.
        /// </summary>
        private void HandleExternalChange(bool any, long now)
        {
            string front = string.IsNullOrEmpty(_foregroundProcess) ? ProcessNames.Unknown : _foregroundProcess;
            Log("INFO", $"Windows HDR 在未经本程序的情况下变为 {(any ? "开启" : "关闭")} – 由游戏、Win+Alt+B 或 Windows 设置触发（前台窗口：{front}）。");

            _hdr.OnByApp = false;
            _hdr.ExpectAny = any;
            _hdr.SwitchConfirmed = true;

            if (any && _foregroundFullscreen && !IsSelf(front) &&
                front is not (ProcessNames.Unknown or ProcessNames.Explorer))
            {
                _externalHoldProcess = front;
                _externalEnded = false;
                Log("HDR", $"'{front}' 自行开启了 HDR – 其运行期间保持 HDR 开启。");
                return;
            }

            if (Paused) return;

            bool displayEvent = now - Interlocked.Read(ref _hdr.DisplayEventMs) < DisplayEventGraceMs;
            if (!any && displayEvent) return;

            _hdr.HandOffDemand = _hdr.LastDemand;
            if (any)
                Log("HDR", "HDR 由您或其他程序开启 – 保持开启。本程序只切回自己开启的 HDR。");
            else if (_hdr.LastDemand != "none")
                Log("HDR", $"HDR 由您或其他程序关闭 – 在 {DescribeDemand(_hdr.LastDemand)} 结束前（或下一个游戏 / 视频启动前）不再开启。");
        }

        private void Decide(Snapshot snapshot, long now)
        {
            bool shouldEnableHdr = EvaluateHdrRequirement(_contentProgram, out string reason);
            bool canAttempt = now - _decision.LastAttemptMs >= SwitchSpacingMs && now >= _decision.CooldownUntilMs;

            if (TryForceSdr(shouldEnableHdr, canAttempt)) return;

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
            else if (_contentProgram is { IsHdr: true } hdrInFront) demand = "game:" + hdrInFront.Entry;
            else if (_externalHoldProcess != null) demand = "ext:" + _externalHoldProcess;
            else demand = "focus:" + _contentProcess;

            if (HandedOff(demand))
            {
                _decision.SdrFallbackAtMs = -1;
                _externalEnded = false;
                if (_runningGame == null) _activeHdrGameProcess = null;
                return;
            }

            bool owns = _hdr.OnByApp;

            if (shouldEnableHdr)
            {
                _decision.SdrFallbackAtMs = -1;
                _externalEnded = false;
                if (!_hdrAny && canAttempt)
                {
                    string? game = _runningGame ?? (_contentProgram is { IsHdr: true } inFront ? inFront.Entry : null);
                    TurnHdrOn(reason);
                }
                return;
            }

            if (!_hdrAny)
            {
                _activeHdrGameProcess = null;
                _decision.SdrFallbackAtMs = -1;
                _externalEnded = false;
                return;
            }

            if (_activeHdrGameProcess != null && _runningGame == null)
            {
                if (now >= _decision.CooldownUntilMs)
                {
                    string closedGame = _activeHdrGameProcess;
                    _activeHdrGameProcess = null;
                    _decision.SdrFallbackAtMs = -1;
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
                    _decision.SdrFallbackAtMs = -1;
                    TurnHdrOff($"'{_externalEndedWhat}' 已关闭（它曾自行开启 HDR）");
                }
                return;
            }

            if (!owns)
            {
                _decision.SdrFallbackAtMs = -1;
                return;
            }

            if (_decision.SdrFallbackAtMs < 0)
            {
                _decision.SdrFallbackAtMs = now + snapshot.FallbackSeconds * 1000L;
            }
            else if (now >= _decision.SdrFallbackAtMs && canAttempt)
            {
                _decision.SdrFallbackAtMs = -1;
                UndoOwnHdr("无 HDR 内容处于活动状态（桌面模式）");
            }
        }

        /// <summary>
        /// An application on the SDR list wants HDR off. The HDR list wins whenever one of its applications is active,
        /// so this only steps in while nothing from the HDR list is. Returns true when the tick was handled here.
        /// </summary>
        private bool TryForceSdr(bool hdrAppInFront, bool canAttempt)
        {
            if (hdrAppInFront || _runningGame != null) return false;

            string? sdrApp = _runningSdrApp;
            if (sdrApp == null && _contentProgram is { IsHdr: false } sdrInFront) sdrApp = sdrInFront.Entry;
            if (sdrApp == null) return false;

            _activeHdrGameProcess = null;
            _decision.SdrFallbackAtMs = -1;
            _externalEnded = false;

            if (_hdrAny && canAttempt)
                TurnHdrOff($"'{sdrApp}' 在 SDR 列表中 – HDR 保持关闭");

            return true;
        }

        /// <summary>
        /// Applies the brightness of the program that owns the screen right now. A running HDR game keeps its
        /// brightness even when the user switches away; otherwise the program in front decides, and the desktop
        /// brightness applies while a program that is on neither list is in front. Only a change is written to the TV,
        /// so editing a value in the settings window reaches the TV at once instead of only after an app switch.
        /// An HDR change makes the TV switch picture mode, and each mode carries its own brightness: the value is then
        /// written again, once the TV had time for the switch, and a write the TV did not take is retried instead of
        /// being remembered as done.
        /// </summary>
        private void UpdateAppBrightness(Snapshot snapshot)
        {
            if (!snapshot.BrightnessEnabled)
            {
                _brightness.Owner = null;
                _brightness.Started = false;
                _brightness.Pending = false;
                _brightness.HdrKnown = false;
                _brightness.VerifyAtMs = 0;
                return;
            }

            long now = _clock.NowMs;
            var owner = ResolveBrightnessOwner(snapshot);
            string? app = owner?.Entry;
            int brightness = snapshot.BrightnessFor(owner);

            bool firstCheck = !_brightness.Started;
            _brightness.Started = true;

            bool targetChanged = firstCheck || brightness != _brightness.Value ||
                                 !string.Equals(app, _brightness.Owner, StringComparison.OrdinalIgnoreCase);

            if (targetChanged)
            {
                _brightness.Owner = app;
                _brightness.Value = brightness;

                // Nothing listed is running at start: leave the TV as the user left it
                if (firstCheck && app == null) return;

                ScheduleBrightnessWrite(now);
            }

            if (_brightness.HdrKnown && _brightness.AppliedOnHdr != _hdrAny)
            {
                ScheduleBrightnessWrite(now);
                ScheduleBrightnessVerify(now);
            }

            if (_brightness.Applying || _brightness.Verifying) return;

            if (_brightness.Pending && now >= _brightness.NextAttemptMs &&
                now - _hdrChangedMs >= BrightnessSettleAfterHdrMs)
            {
                _brightness.Pending = false;
                _brightness.Applying = true;
                _brightness.AppliedOnHdr = _hdrAny;
                _brightness.HdrKnown = true;
                ScheduleBrightnessVerify(now);
                StartBrightnessWrite(brightness, app);
                return;
            }

            if (_brightness.VerifyAtMs > 0 && now >= _brightness.VerifyAtMs && !_brightness.Pending &&
                _brightness.HdrKnown && _brightness.AppliedOnHdr == _hdrAny)
            {
                _brightness.VerifyAtMs = 0;
                _brightness.Verifying = true;
                _ = Task.Run(() => VerifyBrightnessAsync(brightness, app));
            }
        }

        /// <summary>
        /// A write made close to an HDR change needs one more read afterwards: the TV may switch picture mode after
        /// the write and restore the brightness it has stored for that mode.
        /// </summary>
        private void ScheduleBrightnessVerify(long now)
        {
            if (now - _hdrChangedMs >= BrightnessVerifyAfterHdrMs) return;
            _brightness.VerifyAtMs = Math.Max(_brightness.VerifyAtMs, _hdrChangedMs + BrightnessVerifyAfterHdrMs);
        }

        /// <summary>A write is needed: the retries start over, and an HDR switch may still reset the value.</summary>
        private void ScheduleBrightnessWrite(long now)
        {
            _brightness.Pending = true;
            _brightness.Attempts = 0;
            _brightness.NextAttemptMs = now;
            _brightness.HdrKnown = false;
        }

        private void StartBrightnessWrite(int brightness, string? app)
        {
            Log("BRIGHT", $"{(app == null ? "桌面" : $"'{app}'")} – 亮度 {brightness}");
            _ = Task.Run(() => WriteBrightnessAsync(brightness, app));
        }

        private async Task WriteBrightnessAsync(int brightness, string? app)
        {
            TvResult result = await RunTvCommandAsync(() => TvCli.SetBrightnessAsync(brightness)).ConfigureAwait(false);
            _brightness.Applying = false;
            if (!IsSameTarget(brightness, app)) return;

            if (result.Success)
            {
                _brightness.Attempts = 0;
                return;
            }

            _brightness.Attempts++;
            if (_brightness.Attempts >= BrightnessMaxAttempts)
            {
                Log("ERROR", $"亮度 {brightness} 连续 {BrightnessMaxAttempts} 次未被电视接受（{result.Detail}）– 本轮不再重试，下次切换时再设置。");
                return;
            }

            _brightness.Pending = true;
            _brightness.NextAttemptMs = _clock.NowMs + (long)BrightnessRetryDelayMs * _brightness.Attempts;
            Log("ERROR", $"设置亮度 {brightness} 失败：{result.Detail} – {BrightnessRetryDelayMs * _brightness.Attempts / 1000} 秒后重试" +
                         $"（{_brightness.Attempts}/{BrightnessMaxAttempts}）。");
        }

        /// <summary>
        /// The TV switched picture mode after the write and may have restored its own brightness in the new mode.
        /// Reading once more after the switch rewrites the wanted value instead of leaving the wrong one on screen.
        /// </summary>
        private async Task VerifyBrightnessAsync(int brightness, string? app)
        {
            TvResult result = await RunTvCommandAsync(() => TvCli.ReadCurrentBrightnessAsync()).ConfigureAwait(false);
            _brightness.Verifying = false;
            if (!result.Success || result.Brightness == brightness) return;
            if (!IsSameTarget(brightness, app)) return;

            Log("BRIGHT", $"电视在新图像模式下用了自己的亮度 {result.Brightness}（期望 {brightness}）– 重新写入。");
            ScheduleBrightnessWrite(_clock.NowMs);
        }

        /// <summary>A TV command never throws at the caller: a failure is reported as a result.</summary>
        private static async Task<TvResult> RunTvCommandAsync(Func<Task<TvResult>> command)
        {
            try
            {
                return await command().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new TvResult(false, ex.Message);
            }
        }

        /// <summary>The value was not replaced by another program or slider while the TV command was running.</summary>
        private bool IsSameTarget(int brightness, string? app) =>
            brightness == _brightness.Value &&
            string.Equals(app, _brightness.Owner, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The program whose brightness belongs on screen: the running HDR game (it keeps its brightness even when the
        /// user switches away), otherwise the listed program in front, otherwise null for the desktop brightness. A
        /// program from either list that keeps running in the background does not hold the brightness – switching to
        /// it is what applies it.
        /// </summary>
        private ListedProgram? ResolveBrightnessOwner(Snapshot snapshot)
        {
            if (_runningGame == null) return _contentProgram;

            // A game the launcher follow just started is only in the config, not yet in this snapshot
            return snapshot.Programs.Match(_runningGame) ?? new ListedProgram { Entry = _runningGame, IsHdr = true };
        }

        /// <summary>
        /// HDR was changed by something else (you, a game, another program): the app keeps its hands off until what
        /// wants HDR changes – a new game or video starts, or the current one ends. Returns true while handed off.
        /// </summary>
        private bool HandedOff(string demand)
        {
            _hdr.LastDemand = demand;
            if (_hdr.HandOffDemand == null) return false;
            if (string.Equals(demand, _hdr.HandOffDemand, StringComparison.OrdinalIgnoreCase)) return true;
            _hdr.HandOffDemand = null;
            Log("HDR", $"自动切换重新接管（{(demand == "none" ? "您的列表中已无活动项目" : DescribeDemand(demand))}）。");
            return false;
        }

        /// <summary>"game:witcher3" -> "'witcher3'" – for the log.</summary>
        private static string DescribeDemand(string demand)
        {
            int colon = demand.IndexOf(':');
            string kind = colon >= 0 ? demand[..colon] : demand;
            string what = colon >= 0 && colon + 1 < demand.Length
                ? demand[(colon + 1)..]
                : string.Empty;
            return kind switch
            {
                "game" or "ext" or "focus" => $"'{what}'",
                "follow" => $"由 '{what}' 启动的游戏",
                _ => "此项目"
            };
        }

        /// <summary>
        /// The reason this app switched HDR on has ended: back to SDR.
        /// </summary>
        private void UndoOwnHdr(string reason) => TurnHdrOff(reason);

        private void TurnHdrOn(string reason)
        {
            long now = _clock.NowMs;
            _decision.LastAttemptMs = now;
            Log("HDR", $"开启 HDR – {reason}");

            var result = _hdrSwitch.Set(true);
            if (result.Ok)
            {
                _hdr.OnByApp = true;
                _hdrAny = true;
                _hdrChangedMs = now;
                _verifyOn = true;
                _hdr.ExpectAny = true;
                _hdr.SwitchConfirmed = false;
            }
            else
            {
                Log("ERROR", result.Error);
            }
            AfterSwitch(result.Ok, now);
        }

        private void TurnHdrOff(string reason)
        {
            long now = _clock.NowMs;
            _decision.LastAttemptMs = now;
            Log("SDR", $"切换到 SDR – {reason}");

            var result = _hdrSwitch.Set(false);
            if (result.Ok)
            {
                _hdr.OnByApp = false;
                _hdr.ExpectAny = false;
                _hdr.SwitchConfirmed = false;
                _hdrAny = false;
                _hdrChangedMs = now;
                _verifyOn = false;
            }
            else
            {
                Log("ERROR", result.Error);
            }
            AfterSwitch(result.Ok, now);
        }

        private void AfterSwitch(bool ok, long now)
        {
            if (ok)
            {
                if (!_verifyOn) _decision.ConsecutiveFailures = 0;
                return;
            }

            RegisterFailure(now);
        }

        private void RegisterFailure(long now)
        {
            if (++_decision.ConsecutiveFailures >= FailuresBeforeCooldown)
            {
                _decision.ConsecutiveFailures = 0;
                _decision.CooldownUntilMs = now + FailureCooldownMs;
                Log("ERROR", $"HDR 切换连续失败 {FailuresBeforeCooldown} 次 – 暂停自动尝试 {FailureCooldownMs / 1000} 秒。");
            }
        }

        private static bool EvaluateHdrRequirement(ListedProgram? front, out string reason)
        {
            reason = string.Empty;
            if (front is not { IsHdr: true }) return false;

            reason = $"HDR 游戏 / 应用在前台（{front.Entry}）";
            return true;
        }

        #region State Classes

        private sealed class BrightnessState
        {
            public string? Owner;
            public int Value;
            public bool Started;
            public bool Pending;
            public bool Applying;
            public bool Verifying;
            public bool HdrKnown;
            public bool AppliedOnHdr;
            public int Attempts;
            public long NextAttemptMs;
            public long VerifyAtMs;
        }

        private sealed class HdrState
        {
            public bool OnByApp;
            public string? HandOffDemand;
            public string LastDemand = string.Empty;
            public bool? ExpectAny;
            public bool SwitchConfirmed;
            public long DisplayEventMs;
        }

        private sealed class DecisionState
        {
            public long SdrFallbackAtMs = -1;
            public long LastAttemptMs = NeverHappenedMs;
            public long CooldownUntilMs;
            public int ConsecutiveFailures;
        }

        #endregion
    }
}
