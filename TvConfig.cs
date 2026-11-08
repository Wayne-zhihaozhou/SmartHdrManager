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

        public static string TvIp
        {
            get => _cfg.TvIp;
            set => _cfg.TvIp = value;
        }

        public static string LocalAddr
        {
            get => _cfg.LocalAddr;
            set => _cfg.LocalAddr = value;
        }

        public static bool Enabled
        {
            get => _cfg.Enabled;
            set => _cfg.Enabled = value;
        }

        public static bool AutoSwitchEnabled
        {
            get => _cfg.AutoSwitchEnabled;
            set => _cfg.AutoSwitchEnabled = value;
        }

        public static List<BrightnessRule> Rules
        {
            get => _cfg.Rules;
            set => _cfg.Rules = value;
        }

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

        public static void SaveTo(AppConfig config)
        {
            if (config.Brightness == null) config.Brightness = _cfg;
            // _cfg is already the same reference, no need to copy
        }
    }
}
