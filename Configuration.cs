using System.Collections.Generic;

namespace SmartHdrManager
{
    /// <summary>Everything that is stored in config.json.</summary>
    public sealed class AppConfig
    {
        /// <summary>Format version of this file: lets a newer version fix default lists in an existing config.json once.</summary>
        public int ConfigVersion { get; set; }

        /// <summary>Active HDR list: only these games switch HDR on automatically.</summary>
        public List<string> HdrGamesAndApps { get; set; } = new();

        /// <summary>SDR list: these games keep HDR off while they run.</summary>
        public List<string> SdrGamesAndApps { get; set; } = new();

        /// <summary>Learned launchers: launcher process -> the game process(es) it started.</summary>
        public Dictionary<string, List<string>> LauncherGames { get; set; } = new();

        /// <summary>Full exe paths of known programs (for their icons in the games list).</summary>
        public Dictionary<string, string> ProgramPaths { get; set; } = new();

        /// <summary>Per-game TV brightness (process name -> 0-100), used for programs added by hand.</summary>
        public Dictionary<string, int> PerGameBrightness { get; set; } = new();

        /// <summary>Per-game HDR switch (process name -> enabled). A process without an entry counts as enabled.</summary>
        public Dictionary<string, bool> GameHdrModes { get; set; } = new();

        /// <summary>Switch HDR on as soon as a listed game process is running (not only when it is in the foreground).</summary>
        public bool EnableHdrOnProcessStart { get; set; } = true;

        /// <summary>How often the decision loop runs, in milliseconds.</summary>
        public int ProcessScanIntervalMs { get; set; } = 100;

        /// <summary>Seconds without HDR content before the desktop goes back to SDR.</summary>
        public int SdrFallbackDelaySeconds { get; set; } = 3;

        /// <summary>TV brightness while no application of either list is running.</summary>
        public int DesktopBrightness { get; set; } = 50;

        public BrightnessConfig? Brightness { get; set; }
    }

    /// <summary>LG webOS TV the app sets brightness on.</summary>
    public sealed class BrightnessConfig
    {
        public const string DefaultTvIp = "192.168.1.4";

        public string TvIp { get; set; } = DefaultTvIp;

        /// <summary>The app may control the TV brightness at all (automatic and manual).</summary>
        public bool Enabled { get; set; } = true;
    }
}
