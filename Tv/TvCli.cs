using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HdrBrightControl
{
    /// <summary>
    /// LG webOS TV brightness control (pure C#, no Node.js).
    /// A direct setSystemSettings call is refused by the ACG, so an alert is created and the luna write is attached to
    /// its button / close callback; closing the alert makes the TV apply the write itself with its own privileges.
    /// Nothing has to be confirmed on the TV and no dialog is left behind.
    /// </summary>
    public static class TvCli
    {
        private static TvBrightnessService? _service;

        public static void Initialize(TvBrightnessService service)
        {
            _service = service;
        }

        /// <summary>
        /// Remembers where the TV is. Nothing is connected here: the connection is made the first time brightness is
        /// actually used, so the app does not touch the network for users who never adjust the TV.
        /// </summary>
        public static void Configure(string tvIp)
        {
            _service?.Configure(tvIp);
        }

        /// <summary>Sets the brightness (0-100) and reads it back to verify.</summary>
        public static async Task<TvResult> SetBrightnessAsync(int value)
        {
            return await _service!.SetBrightnessAsync(value).ConfigureAwait(false);
        }

        /// <summary>Reads the brightness the TV reports right now, connecting first if needed.</summary>
        public static async Task<TvResult> ReadCurrentBrightnessAsync()
        {
            return await _service!.ReadCurrentBrightnessAsync().ConfigureAwait(false);
        }

        /// <summary>Tests the connection and reports the current brightness (also saves the pairing key).</summary>
        public static async Task<TvResult> TestConnectionAsync()
        {
            return await _service!.TestConnectionAsync().ConfigureAwait(false);
        }

        /// <summary>Forces a new pairing: saved credentials are ignored, so the TV asks again.</summary>
        public static async Task<TvResult> PairAsync()
        {
            return await _service!.PairAsync().ConfigureAwait(false);
        }

        public static void Shutdown()
        {
            _service?.Shutdown();
        }
    }

    /// <summary>
    /// Outcome of one TV command: the reason so callers can show it instead of a bare failure, plus the brightness the
    /// TV reported for commands that read it.
    /// </summary>
    public readonly struct TvResult
    {
        public bool Success { get; }

        public string Detail { get; }

        public int? Brightness { get; }

        public TvResult(bool success, string detail, int? brightness = null)
        {
            Success = success;
            Detail = detail;
            Brightness = brightness;
        }
    }
}
