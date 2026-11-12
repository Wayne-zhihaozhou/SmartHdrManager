using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HdrBrightControl
{
    public sealed partial class HdrEngine : IAppLog
    {
        private const int LogLinesKept = 500;
        private const int LogLinesDropped = 100;
        private const long MaxLogBytes = 2_000_000;
        private const int LogRotationCheckEvery = 200;

        private static readonly object FileLogLock = new();
        private static int _fileLogWrites;

        private readonly object _logLock = new();
        private readonly List<string> _log = new();
        private int _logBase;

        private void InitializeLogFile()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                    File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.txt"), overwrite: true);
            }
            catch { }
        }

        public void Log(string tag, string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {tag,-6} {message}";
            lock (_logLock)
            {
                _log.Add(line);
                if (_log.Count > LogLinesKept)
                {
                    _log.RemoveRange(0, LogLinesDropped);
                    _logBase += LogLinesDropped;
                }
            }
            WriteFileLog(line);
        }

        public void WriteLine(string line)
        {
            WriteFileLog(line);
        }

        public static void WriteFileLog(string line)
        {
            try
            {
                lock (FileLogLock)
                {
                    if (++_fileLogWrites % LogRotationCheckEvery == 0 && File.Exists(LogPath) &&
                        new FileInfo(LogPath).Length > MaxLogBytes)
                        File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.txt"), overwrite: true);
                    File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
                }
            }
            catch { }
        }

        public string[] ReadLog(ref int cursor)
        {
            lock (_logLock)
            {
                int start = Math.Max(cursor - _logBase, 0);
                var lines = _log.Skip(start).ToArray();
                cursor = _logBase + _log.Count;
                return lines;
            }
        }
    }
}
