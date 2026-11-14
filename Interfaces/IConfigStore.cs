using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace HdrBrightControl
{
    public interface IConfigStore
    {
        AppConfig Load(string configPath, IAppLog? log, string backupPath, string originalPath);
        void Save(string configPath, AppConfig config, IAppLog? log);
        AppConfig Normalize(AppConfig config);
    }

    public sealed class DiskConfigStore : IConfigStore
    {
        public AppConfig Load(string configPath, IAppLog? log, string backupPath, string originalPath)
        {
            if (!System.IO.File.Exists(configPath))
                return Normalize(new AppConfig());

            try
            {
                string json = System.IO.File.ReadAllText(configPath);
                return Normalize(JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig());
            }
            catch (Exception ex)
            {
                try { System.IO.File.Copy(configPath, backupPath, true); } catch { }
                log?.WriteLine($"ERROR  config.json 无法读取（{ex.Message}）– 备份已保存为 {backupPath}，使用默认值。");
                return Normalize(new AppConfig());
            }
        }

        public void Save(string configPath, AppConfig config, IAppLog? log)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(configPath)!);
                string temporaryPath = configPath + ".tmp";
                System.IO.File.WriteAllText(temporaryPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
                System.IO.File.Move(temporaryPath, configPath, overwrite: true);
            }
            catch (Exception ex)
            {
                log?.WriteLine($"ERROR  config.json 无法保存：{ex.Message}");
            }
        }

        public AppConfig Normalize(AppConfig config)
        {
            var hdrGames = config.HdrGamesAndApps ?? new System.Collections.Generic.List<string>();
            var sdrGames = config.SdrGamesAndApps ?? new System.Collections.Generic.List<string>();

            var paths = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var entry in config.ProgramPaths ?? new System.Collections.Generic.Dictionary<string, string>())
                if (!string.IsNullOrEmpty(entry.Value)) paths[entry.Key] = entry.Value;

            var result = config with
            {
                HdrGamesAndApps = hdrGames,
                SdrGamesAndApps = sdrGames,
                ProgramPaths = paths,
                SdrFallbackDelaySeconds = System.Math.Clamp(config.SdrFallbackDelaySeconds, 0, 30),
                DesktopBrightness = System.Math.Clamp(config.DesktopBrightness, 0, 100),
            };

            var hdrModes = new System.Collections.Generic.Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var entry in config.GameHdrModes ?? new System.Collections.Generic.Dictionary<string, bool>())
                if (!string.IsNullOrWhiteSpace(entry.Key)) hdrModes[entry.Key.Trim().ToLowerInvariant()] = entry.Value;
            result = result with { GameHdrModes = hdrModes };

            var brightness = config.Brightness ?? new BrightnessConfig();
            result = result with { Brightness = brightness };

            return result;
        }
    }
}
