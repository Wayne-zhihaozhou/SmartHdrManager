using System;
using System.Collections.Generic;

namespace SmartHdrManager
{
    /// <summary>
    /// 桥接 TvCli 与 MainForm 的配置访问。
    /// 从 AppConfig.Brightness 读写，保证序列化一致性。
    /// </summary>
    public static class TvConfig
    {
        private static BrightnessConfig _cfg = new();

        /// <summary>电视 IP 地址（固定配置）。</summary>
        public const string TvIp = "192.168.1.4";

        /// <summary>本机源地址（固定配置）。</summary>
        public const string LocalAddr = "192.168.1.5";

        public static bool Enabled => _cfg.Enabled;

        public static bool AutoSwitchEnabled => _cfg.AutoSwitchEnabled;

        public static List<BrightnessRule> Rules => _cfg.Rules;

        public static void LoadFrom(AppConfig config)
        {
            if (config.Brightness != null)
            {
                _cfg = config.Brightness;
            }
            else
            {
                _cfg = new BrightnessConfig();
                config.Brightness = _cfg;
            }
        }
    }
}
