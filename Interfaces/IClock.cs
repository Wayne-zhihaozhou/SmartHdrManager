using System;

namespace HdrBrightControl
{
    public interface IClock
    {
        long NowMs { get; }
    }

    public sealed class SystemClock : IClock
    {
        public long NowMs => Environment.TickCount64;
    }
}
