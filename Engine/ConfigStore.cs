using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HdrBrightControl
{
    public sealed partial class HdrEngine
    {
        private const int CurrentConfigVersion = 5;

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

        private static AppConfig Normalize(AppConfig config)
        {
            var hdrGames = config.HdrGamesAndApps ?? new List<string>();
            var sdrGames = config.SdrGamesAndApps ?? new List<string>();

            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in config.ProgramPaths ?? new Dictionary<string, string>())
                if (!string.IsNullOrEmpty(entry.Value)) paths[entry.Key] = entry.Value;

            var result = config with
            {
                HdrGamesAndApps = hdrGames,
                SdrGamesAndApps = sdrGames,
                ProgramPaths = paths,
                SdrFallbackDelaySeconds = Math.Clamp(config.SdrFallbackDelaySeconds, 0, 30),
                DesktopBrightness = Math.Clamp(config.DesktopBrightness, 0, 100),
            };

            var hdrModes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in config.GameHdrModes ?? new Dictionary<string, bool>())
                if (!string.IsNullOrWhiteSpace(entry.Key)) hdrModes[entry.Key.Trim().ToLowerInvariant()] = entry.Value;
            result = result with { GameHdrModes = hdrModes };

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
            result = result with { LauncherGames = launchers };

            var lastLaunched = config.LastLaunchedAt ?? new Dictionary<string, long>();
            result = result with { LastLaunchedAt = lastLaunched };

            var brightness = config.Brightness ?? new BrightnessConfig();
            result = result with { Brightness = brightness };

            return result;
        }

        private AppConfig UpgradeConfig(AppConfig config)
        {
            if (config.ConfigVersion >= CurrentConfigVersion) return config;

            if (config.ConfigVersion < 2)
            {
                foreach (string example in new[] { "cyberpunk2077", "eldenring", "alanwake2" })
                {
                    if (config.HdrGamesAndApps.RemoveAll(n => string.Equals(n, example, StringComparison.OrdinalIgnoreCase)) > 0)
                        Log("CONFIG", $"已从 HDR 游戏中移除示例条目 '{example}'（本机未安装）。");
                }
            }

            return config with { ConfigVersion = CurrentConfigVersion };
        }

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

        private void ApplyConfigValues(AppConfig source)
        {
            foreach (var property in typeof(AppConfig).GetProperties())
                if (property.CanWrite) property.SetValue(Config, property.GetValue(source));
        }

        public void ResetSettings()
        {
            lock (Sync)
            {
                var fresh = Config with
                {
                    HdrGamesAndApps = Config.HdrGamesAndApps,
                    SdrGamesAndApps = Config.SdrGamesAndApps,
                    ProgramPaths = Config.ProgramPaths,
                    LauncherGames = Config.LauncherGames,
                    LastLaunchedAt = Config.LastLaunchedAt
                };
                Normalize(fresh);

                lock (Sync)
                {
                    ApplyConfigValues(fresh);
                    InvalidateSnapshot();
                    Save();
                }
            }
            Log("CONFIG", "设置已恢复默认值（游戏列表已保留）。");
        }

        public bool ReloadConfig()
        {
            try
            {
                var fresh = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (fresh == null) return false;
                Normalize(fresh);

                lock (Sync)
                {
                    ApplyConfigValues(fresh);
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
    }
}
