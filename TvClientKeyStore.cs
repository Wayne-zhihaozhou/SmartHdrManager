using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SmartHdrManager
{
    /// <summary>
    /// client-key 持久化：按电视 IP 保存已配对凭证。
    /// 保存后重连走 register(client-key) 免确认，电视不再弹出配对提示；
    /// 文件与 config.json 同目录，随程序设置一起迁移。
    /// </summary>
    public static class TvClientKeyStore
    {
        private const string FileName = "client-key.json";

        private static readonly object Sync = new();
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
                    var directory = Path.GetDirectoryName(KeyFilePath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.WriteAllText(KeyFilePath, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    HdrEngine.WriteFileLog($"[TV] 保存配对凭证失败: {ex.Message}");
                }
            }
        }

        private static Dictionary<string, string> ReadStore()
        {
            try
            {
                if (!File.Exists(KeyFilePath)) return new Dictionary<string, string>();
                var store = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(KeyFilePath));
                return store ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }
    }
}
