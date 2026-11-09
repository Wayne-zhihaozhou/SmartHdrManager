using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// Brightness overlay in the top-right corner of the primary monitor. It runs on its own UI thread with a real
    /// message loop, because WinForms windows and timers do not work on a thread without one (the background thread
    /// that applies the brightness rules has none).
    /// </summary>
    internal sealed class BrightnessOsd
    {
        private const int FadeIntervalMs = 16;
        private const int DisplayMs = 1500;
        private const int Padding = 18;
        private const int Margin = 16;
        private const int BaseDpi = 96;
        private const int ReadyTimeoutMs = 3000;
        private const double FadeStep = 0.06;
        private const double VisibleOpacity = 0.9;
        private const float FontSize = 15f;
        private const string DefaultTitle = "亮度";

        private static readonly object Sync = new();
        private static BrightnessOsd? _shared;

        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;

        private Form? _form;
        private Label? _label;
        private System.Windows.Forms.Timer? _animTimer;
        private System.Windows.Forms.Timer? _dismissTimer;
        private bool _fadingOut;
        private string _lastText = string.Empty;

        public static void Show(int brightness, string? source)
        {
            BrightnessOsd osd;
            lock (Sync) osd = _shared ??= new BrightnessOsd();
            osd.ShowCore($"{source ?? DefaultTitle}: {brightness}");
        }

        private BrightnessOsd()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "BrightnessOsd" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(ReadyTimeoutMs);
        }

        private void Run()
        {
            _form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                TopMost = true,
                BackColor = Color.Black,
                Opacity = 0,
                AutoScaleMode = AutoScaleMode.None
            };

            _label = new Label
            {
                AutoSize = true,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", FontSize, FontStyle.Regular)
            };
            _form.Controls.Add(_label);
            _ = _form.Handle;   // create the window handle on this thread before any caller posts to it

            _animTimer = new System.Windows.Forms.Timer { Interval = FadeIntervalMs };
            _animTimer.Tick += (_, _) => OnAnimationTick();

            _dismissTimer = new System.Windows.Forms.Timer { Interval = DisplayMs };
            _dismissTimer.Tick += (_, _) => StartFadeOut();

            _ready.Set();
            Application.Run(new ApplicationContext());
        }

        private void ShowCore(string text)
        {
            RunOnUiThread(() =>
            {
                if (_form == null || _label == null || _animTimer == null || _dismissTimer == null) return;

                bool sameText = text == _lastText;
                _lastText = text;
                _label.Text = text;

                double scale = Math.Max(1, _form.DeviceDpi) / (double)BaseDpi;
                int padding = Math.Max(1, (int)Math.Round(Padding * scale));
                int margin = Math.Max(1, (int)Math.Round(Margin * scale));

                int width = _label.PreferredWidth + padding * 2;
                int height = _label.PreferredHeight + padding;
                _form.ClientSize = new Size(width, height);
                _label.Location = new Point(padding, Math.Max(0, (height - _label.PreferredHeight) / 2));

                var screen = Screen.PrimaryScreen;
                if (screen != null)
                    _form.Location = new Point(screen.WorkingArea.Right - width - margin, screen.WorkingArea.Top + margin);

                _dismissTimer.Stop();
                _dismissTimer.Start();

                if (sameText && _form.Visible) return;

                _fadingOut = false;
                _animTimer.Stop();
                _form.Opacity = 0;
                _form.Show();
                _form.TopMost = true;
                _animTimer.Start();
            });
        }

        private void RunOnUiThread(Action action)
        {
            var form = _form;
            if (form == null || form.IsDisposed || !form.IsHandleCreated) return;

            try
            {
                if (form.InvokeRequired) form.BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void StartFadeOut()
        {
            if (_form == null || _dismissTimer == null || _animTimer == null) return;

            _dismissTimer.Stop();
            if (!_form.Visible) return;

            _fadingOut = true;
            _animTimer.Start();
        }

        private void OnAnimationTick()
        {
            if (_form == null || _animTimer == null) return;

            if (_fadingOut)
            {
                _form.Opacity = Math.Max(0, _form.Opacity - FadeStep);
                if (_form.Opacity > 0) return;

                _animTimer.Stop();
                _form.Hide();
                return;
            }

            _form.Opacity = Math.Min(VisibleOpacity, _form.Opacity + FadeStep);
            if (_form.Opacity >= VisibleOpacity) _animTimer.Stop();
        }
    }
}
