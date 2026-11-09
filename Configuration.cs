using System.Collections.Generic;

namespace SmartHdrManager
{
    /// <summary>Everything that is stored in config.json.</summary>
    public sealed class AppConfig
    {
        /// <summary>Format version of this file: lets a newer version fix default lists in an existing config.json once.</summary>
        public int ConfigVersion { get; set; }

        /// <summary>Extra folders to search for games (the common library folders are found automatically).</summary>
        public List<string> CustomGameFolders { get; set; } = new();

        /// <summary>Active HDR list: only these games switch HDR on automatically.</summary>
        public List<string> HdrGamesAndApps { get; set; } = new();

        /// <summary>Games found in the Steam / Epic / GOG / Xbox / … libraries.</summary>
        public List<LibraryGame> LibraryGames { get; set; } = new();

        /// <summary>Learned launchers: launcher process -> the game process(es) it started.</summary>
        public Dictionary<string, List<string>> LauncherGames { get; set; } = new();

        /// <summary>Games that closed within seconds after HDR was switched on for them (possible crash).</summary>
        public Dictionary<string, int> QuickExitCounts { get; set; } = new();

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

        public BrightnessConfig? Brightness { get; set; }
    }

    /// <summary>LG webOS TV the app sets brightness on.</summary>
    public sealed class BrightnessConfig
    {
        public const string DefaultTvIp = "192.168.1.4";
        public const string DefaultLocalAddr = "192.168.1.5";

        public string TvIp { get; set; } = DefaultTvIp;
        public string LocalAddr { get; set; } = DefaultLocalAddr;
    }

    public sealed class LibraryGame
    {
        /// <summary>e.g. "steam:1091500".</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Display name, e.g. "Cyberpunk 2077".</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Steam / Epic / GOG / Xbox / Ubisoft / …</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Process names (exe without ".exe", lower case).</summary>
        public List<string> Processes { get; set; } = new();

        /// <summary>Main exe, used for the icon.</summary>
        public string? ExePath { get; set; }

        public int Brightness { get; set; } = 50;
    }
}
