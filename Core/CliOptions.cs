using System;

namespace HdrBrightControl
{
    internal sealed record CliOptions(CliCommand Command, bool JustUpdated)
    {
        private const string TogglePauseSwitch = "--toggle-pause";
        private const string UpdatedSwitch = "--updated";

        public static CliOptions Parse(string[] args)
        {
            var command = CliCommand.None;
            bool updated = false;

            foreach (string raw in args)
            {
                string arg = raw.Trim();
                if (arg.Equals(TogglePauseSwitch, StringComparison.OrdinalIgnoreCase)) command = CliCommand.TogglePause;
                else if (arg.Equals(UpdatedSwitch, StringComparison.OrdinalIgnoreCase)) updated = true;
            }

            return new CliOptions(command, updated);
        }
    }
}
