using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HdrBrightControl
{
    /// <summary>
    /// Persisted client-key per TV IP: after the first pairing, reconnecting with the saved key no longer asks for
    /// confirmation on the TV. The file lives next to config.json, so it travels with the settings.
    /// </summary>
    public static class TvClientKeyStore
    {
        private const string FileName = "client-key.json";

        private static readonly object Sync = new();
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        private static IAppLog? _log;

        public static void Initialize(IAppLog log)
        {
            _log = log;
        }

        private static readonly string KeyFilePath = Path.Combine(
            Path.GetDirectoryName(HdrEngine.ConfigPath) ?? AppContext.BaseDirectory,
            FileName);

        public static string? Load(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;

            lock (Sync)
            {
                var store = ReadStore();
                return store.TryGetValue(host, out var key) && !string.IsNullOrEmpty(key) ? key : null;
            }
        }

        public static void Save(string host, string clientKey)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(clientKey)) return;

            lock (Sync)
            {
                var store = ReadStore();
                store[host] = clientKey;

                try
                {
                    string? directory = Path.GetDirectoryName(KeyFilePath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.WriteAllText(KeyFilePath, JsonSerializer.Serialize(store, WriteOptions));
                }
                catch (Exception ex)
                {
                    _log?.WriteLine($"[TV] 保存配对凭证失败: {ex.Message}");
                }
            }
        }

        private static Dictionary<string, string> ReadStore()
        {
            try
            {
                if (!File.Exists(KeyFilePath)) return new Dictionary<string, string>();
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(KeyFilePath))
                       ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }
    }
}
