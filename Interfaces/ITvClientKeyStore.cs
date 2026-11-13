using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HdrBrightControl
{
    public interface ITvClientKeyStore
    {
        string? Load(string host);
        void Save(string host, string clientKey);
    }

    public sealed class DiskTvClientKeyStore : ITvClientKeyStore
    {
        private const string FileName = "client-key.json";
        private readonly object _sync = new();
        private readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };
        private readonly string _keyFilePath;

        public DiskTvClientKeyStore(string configDir)
        {
            _keyFilePath = Path.Combine(configDir, FileName);
        }

        public string? Load(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;

            lock (_sync)
            {
                var store = ReadStore();
                return store.TryGetValue(host, out var key) && !string.IsNullOrEmpty(key) ? key : null;
            }
        }

        public void Save(string host, string clientKey)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(clientKey)) return;

            lock (_sync)
            {
                var store = ReadStore();
                store[host] = clientKey;

                try
                {
                    string? directory = Path.GetDirectoryName(_keyFilePath);
                    if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
                    System.IO.File.WriteAllText(_keyFilePath, JsonSerializer.Serialize(store, _writeOptions));
                }
                catch { }
            }
        }

        private Dictionary<string, string> ReadStore()
        {
            try
            {
                if (!System.IO.File.Exists(_keyFilePath)) return new Dictionary<string, string>();
                return JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(_keyFilePath))
                       ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }
    }
}
