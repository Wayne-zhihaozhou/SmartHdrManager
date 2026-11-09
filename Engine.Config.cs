using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SmartHdrManager
{
    public sealed partial class HdrEngine
    {
        private const int CurrentConfigVersion = 5;
        private const int LogLinesKept = 500;
        private const int LogLinesDropped = 100;
        private const long MaxLogBytes = 2_000_000;
        private const int LogRotationCheckEvery = 200;
        private const int MaxQuickExitCount = 99;

        private static readonly object FileLogLock = new();
        private static int _fileLogWrites;

        private readonly object _logLock = new();
        private readonly List<string> _log = new();
        private int _logBase;

        private void InitializeLogFile()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                    File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.txt"), overwrite: true);
            }
            catch { }
        }

        /// <summary>Settings › Reset to defaults. Kept: the game lists, found games, program locations, extra folders.</summary>
        public void ResetSettings()
        {
            lock (Sync)
            {
                var fresh = new AppConfig
                {
                    ConfigVersion = Config.ConfigVersion,
                    HdrGamesAndApps = Config.HdrGamesAndApps,
                    LibraryGames = Config.LibraryGames,
                    ProgramPaths = Config.ProgramPaths,
                    CustomGameFolders = Config.CustomGameFolders,
                    LauncherGames = Config.LauncherGames,
                    QuickExitCounts = Config.QuickExitCounts
                };
                Normalize(fresh);

                foreach (var property in typeof(AppConfig).GetProperties())
                    if (property.CanWrite) property.SetValue(Config, property.GetValue(fresh));

                InvalidateSnapshot();
                Save();
            }
            Log("CONFIG", "设置已恢复默认值（游戏列表已保留）。");
        }

        private void UpgradeConfig(AppConfig config)
        {
            if (config.ConfigVersion >= CurrentConfigVersion) return;

            if (config.ConfigVersion < 2)
            {
                var installed = new HashSet<string>(config.ProgramPaths.Keys, Cmp);
                foreach (var game in config.LibraryGames)
                    foreach (string process in game.Processes)
                        installed.Add(process);

                foreach (string example in new[] { "cyberpunk2077", "eldenring", "alanwake2" })
                {
                    if (installed.Contains(example)) continue;
                    if (config.HdrGamesAndApps.RemoveAll(n => string.Equals(n, example, StringComparison.OrdinalIgnoreCase)) > 0)
                        Log("CONFIG", $"已从 HDR 游戏中移除示例条目 '{example}'（本机未安装）。");
                }
            }

            config.ConfigVersion = CurrentConfigVersion;
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
                try { File.Move(LegacyConfigPath, LegacyConfigPath + ".old", overwrite: true); } catch { }
            }
            catch (Exception ex)
            {
                Log("ERROR", $"旧设置无法迁移（{ex.Message}）。");
            }
        }

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
        private static AppConfig Normalize(AppConfig config)
        {
            config.CustomGameFolders ??= new List<string>();
            config.HdrGamesAndApps ??= new List<string>();
            config.LibraryGames ??= new List<LibraryGame>();

            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in config.ProgramPaths ?? new Dictionary<string, string>())
                if (!string.IsNullOrEmpty(entry.Value)) paths[entry.Key] = entry.Value;
            config.ProgramPaths = paths;

            config.SdrFallbackDelaySeconds = Math.Clamp(config.SdrFallbackDelaySeconds, 0, 30);
            foreach (var game in config.LibraryGames) game.Processes ??= new List<string>();

            var hdrModes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in config.GameHdrModes ?? new Dictionary<string, bool>())
                if (!string.IsNullOrWhiteSpace(entry.Key)) hdrModes[entry.Key.Trim().ToLowerInvariant()] = entry.Value;
            config.GameHdrModes = hdrModes;

            var launchers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in config.LauncherGames ?? new Dictionary<string, List<string>>())
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value == null) continue;
                var games = entry.Value
                    .Where(n => !string.IsNullOrWhiteSpace(n) && !n.Equals(entry.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.Trim().ToLowerInvariant())
                    .Distinct()
                    .ToList();
                if (games.Count > 0) launchers[entry.Key.Trim().ToLowerInvariant()] = games;
            }
            config.LauncherGames = launchers;

            var quickExits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in config.QuickExitCounts ?? new Dictionary<string, int>())
                if (!string.IsNullOrWhiteSpace(entry.Key) && entry.Value > 0)
                    quickExits[entry.Key.Trim().ToLowerInvariant()] = Math.Min(entry.Value, MaxQuickExitCount);
            config.QuickExitCounts = quickExits;

            config.Brightness ??= new BrightnessConfig();
            config.Brightness.Rules ??= new List<BrightnessRule>();
            return config;
        }

        /// <summary>Caller must hold <see cref="Sync"/> (or be in the constructor).</summary>
        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                string temporaryPath = ConfigPath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, ConfigPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log("ERROR", $"config.json 无法保存：{ex.Message}");
            }
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
                    foreach (var property in typeof(AppConfig).GetProperties())
                        if (property.CanWrite) property.SetValue(Config, property.GetValue(fresh));
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

        public void Log(string tag, string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {tag,-6} {message}";
            lock (_logLock)
            {
                _log.Add(line);
                if (_log.Count > LogLinesKept)
                {
                    _log.RemoveRange(0, LogLinesDropped);
                    _logBase += LogLinesDropped;
                }
            }
            WriteFileLog(line);
        }

        public static void WriteFileLog(string line)
        {
            try
            {
                lock (FileLogLock)
                {
                    if (++_fileLogWrites % LogRotationCheckEvery == 0 && File.Exists(LogPath) &&
                        new FileInfo(LogPath).Length > MaxLogBytes)
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
