using System.Collections.Generic;

namespace HdrBrightControl
{
    /// <summary>Everything that is stored in config.json.</summary>
    public sealed record AppConfig
    {
        /// <summary>Format version of this file: lets a newer version fix default lists in an existing config.json once.</summary>
        public int ConfigVersion { get; init; }

        /// <summary>Active HDR list: only these games switch HDR on automatically.</summary>
        public List<string> HdrGamesAndApps { get; init; } = new();

        /// <summary>SDR list: these games keep HDR off while they run.</summary>
        public List<string> SdrGamesAndApps { get; init; } = new();

        /// <summary>Learned launchers: launcher process -> the game process(es) it started.</summary>
        public Dictionary<string, List<string>> LauncherGames { get; init; } = new();

        /// <summary>Full exe paths of known programs (for their icons in the games list).</summary>
        public Dictionary<string, string> ProgramPaths { get; init; } = new();

        /// <summary>Per-game TV brightness (process name -> 0-100), used for programs added by hand.</summary>
        public Dictionary<string, int> PerGameBrightness { get; init; } = new();

        /// <summary>Per-game HDR switch (process name -> enabled). A process without an entry counts as enabled.</summary>
        public Dictionary<string, bool> GameHdrModes { get; init; } = new();

        /// <summary>Last launch time (Unix milliseconds) for each known program. Used to sort the list with most-recent first.</summary>
        public Dictionary<string, long> LastLaunchedAt { get; init; } = new();

        /// <summary>Switch HDR on as soon as a listed game process is running (not only when it is in the foreground).</summary>
        public bool EnableHdrOnProcessStart { get; init; } = true;

        /// <summary>How often the decision loop runs, in milliseconds.</summary>
        public int ProcessScanIntervalMs { get; init; } = 100;

        /// <summary>Seconds without HDR content before the desktop goes back to SDR.</summary>
        public int SdrFallbackDelaySeconds { get; init; } = 3;

        /// <summary>TV brightness while no application of either list is running.</summary>
        public int DesktopBrightness { get; init; } = (int)BrightnessRange.Default;

        public BrightnessConfig? Brightness { get; init; }
    }

    /// <summary>LG webOS TV the app sets brightness on.</summary>
    public sealed record BrightnessConfig
    {
        public const string DefaultTvIp = "192.168.1.4";

        public string TvIp { get; init; } = DefaultTvIp;

        /// <summary>The app may control the TV brightness at all (automatic and manual).</summary>
        public bool Enabled { get; set; } = true;
    }
}
