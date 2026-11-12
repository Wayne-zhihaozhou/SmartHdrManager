using System;
using System.Text.Json;

namespace HdrBrightControl
{
    public sealed partial class HdrEngine
    {
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

        public AppConfig Update(Func<AppConfig, AppConfig> change)
        {
            lock (Sync)
            {
                var result = change(Config);
                Config = result;
                InvalidateSnapshot();
                Save();
                return result;
            }
        }
    }
}
