using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// Animation clock synced to the monitor: a background thread waits for each display frame (DwmFlush returns when
    /// the compositor has presented a frame, i.e. at the monitor's refresh rate) and then runs one animation step on
    /// the UI thread. A WinForms timer only ticks about 64 times per second, unsynchronised, which looks stuttery on
    /// high-refresh monitors. Start/Stop are called on the UI thread only.
    /// </summary>
    internal sealed class FrameLoop : IDisposable
    {
        private const int CompositionOffDelayMs = 4;

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        private readonly Control _owner;
        private readonly Action _frame;
        private readonly object _gate = new();
        private volatile bool _running;
        private Thread? _thread;

        public FrameLoop(Control owner, Action frame)
        {
            _owner = owner;
            _frame = frame;
        }

        public bool IsRunning => _running;

        public void Start()
        {
            lock (_gate)
            {
                _running = true;
                if (_thread != null) return;   // the loop thread is still alive and keeps going

                _thread = new Thread(Loop) { IsBackground = true, Name = "FrameLoop" };
                _thread.Start();
            }
        }

        public void Stop() => _running = false;

        private void Loop()
        {
            while (true)
            {
                lock (_gate)
                {
                    if (!_running)
                    {
                        _thread = null;
                        return;
                    }
                }

                if (DwmFlush() != 0) Thread.Sleep(CompositionOffDelayMs);   // composition off: fall back to ~250 Hz pacing

                try
                {
                    if (_owner.IsDisposed || !_owner.IsHandleCreated)
                    {
                        _running = false;
                        continue;
                    }
                    _owner.Invoke(_frame);   // one step + immediate repaint, then wait for the next display frame
                }
                catch
                {
                    _running = false;        // control closed meanwhile
                }
            }
        }

        public void Dispose() => _running = false;
    }
}
