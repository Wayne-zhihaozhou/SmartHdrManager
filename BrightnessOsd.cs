using System;
using System.Drawing;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// 屏幕右上角 OSD 提示：亮度调节时显示。
    /// 使用 TopMost + Transparent + 淡入淡出动画。
    /// </summary>
    public static class BrightnessOsd
    {
        private static Form? _form;
        private static Panel? _panel;
        private static Label? _label;
        private static Timer? _fadeTimer;
        private static Timer? _dismissTimer;
        private static bool _isVisible;
        private static int _lastBrightness;

        private const int FADE_INTERVAL = 16;   // ~60fps
        private const int FADE_STEP = 25;        // alpha step per frame
        private const int MAX_ALPHA = 230;       // max opacity (not fully opaque)
        private const int MIN_ALPHA = 0;

        public static void Show(int brightness, string? title = null)
        {
            if (brightness == _lastBrightness) return;
            _lastBrightness = brightness;

            if (_form == null || _form.IsDisposed)
            {
                CreateForm();
            }

            if (_isVisible)
            {
                // Reset fade-in
                if (_fadeTimer != null) { _fadeTimer.Stop(); _fadeTimer.Dispose(); }
                if (_dismissTimer != null) { _dismissTimer.Stop(); _dismissTimer.Dispose(); }
                _form!.Opacity = 0;
                _isVisible = false;
            }

            var settings = TvConfig.Osd;
            _label!.Text = $"{title ?? settings.Title}: {brightness}";
            _label.Font = new Font("Segoe UI", Math.Max(10, settings.FontSize * 2f / 3f), FontStyle.Regular);
            _panel!.BackColor = settings.BackgroundColor;
            _label.ForeColor = settings.ForegroundColor;

            // Position: top-right of primary monitor
            var screen = Screen.PrimaryScreen;
            if (screen != null)
            {
                int dpiScale = (int)(screen.Bounds.Width * 96.0 / screen.Bounds.Width);
                if (dpiScale == 0) dpiScale = 96; // fallback
                int margin = dpiScale / 72 * settings.Margin;
                int padding = dpiScale / 72 * settings.PaddingX;
                int height = dpiScale / 72 * 50;
                int width = _label.PreferredSize.Width + padding * 2 + dpiScale / 72 * 60; // bar width

                _panel.Size = new Size(width, height);
                _label.SetBounds(padding, (height - _label.PreferredHeight) / 2,
                    width - padding * 2 - dpiScale / 72 * 60, _label.Height);

                _form.Location = new Point(
                    screen.WorkingArea.Right - width - margin,
                    screen.WorkingArea.Top + margin);
            }

            _form!.TopMost = true;
            _form.Show();
            _form.BringToFront();

            // Fade in
            _fadeTimer = new Timer { Interval = FADE_INTERVAL };
            _fadeTimer.Tick += (_, _) => FadeIn();
            _fadeTimer.Start();

            // Auto dismiss
            _dismissTimer = new Timer { Interval = settings.DisplayMs };
            _dismissTimer.Tick += (_, _) => FadeOut();
            _dismissTimer.Start();
        }

        private static void CreateForm()
        {
            _form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                BackColor = Color.Black,
                Opacity = 0,
                ShowInTaskbar = false,
                TopMost = true,
            };
            _form.VisibleChanged += (s, e) =>
            {
                // Hide immediately when not visible (e.g., minimized)
                var form = s as Form;
                if (!_isVisible && form != null && !form.Visible)
                {
                    form.Hide();
                }
            };

            _panel = new Panel
            {
                BackColor = Color.Black,
                Size = new Size(200, 40),
                Dock = DockStyle.Fill,
            };

            _label = new Label
            {
                Font = new Font("Segoe UI", 14, FontStyle.Regular),
                ForeColor = Color.White,
                Text = "亮度: 0",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = true,
            };

            _panel.Controls.Add(_label);
            _form.Controls.Add(_panel);

            // Rounded corner effect via region (simple approach)
            _form.Region = CreateRoundedRegion(_form, 12);
        }

        private static Region CreateRoundedRegion(Form form, int radius)
        {
            try
            {
                var g = Graphics.FromHwnd(form.Handle);
                var path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
                path.AddArc(form.Width - radius * 2, 0, radius * 2, radius * 2, 270, 90);
                path.AddArc(form.Width - radius * 2, form.Height - radius * 2, radius * 2, radius * 2, 0, 90);
                path.AddArc(0, form.Height - radius * 2, radius * 2, radius * 2, 90, 90);
                path.CloseFigure();
                var region = new Region(path);
                path.Dispose();
                g.Dispose();
                return region;
            }
            catch
            {
                return null!;
            }
        }

        private static void FadeIn()
        {
            if (_form == null || _panel == null) return;
            _form.Opacity += 0.05;
            if (_form.Opacity >= 1.0)
            {
                _form.Opacity = 1.0;
                _form.Opacity = MAX_ALPHA / 255.0;
                _isVisible = true;
                _fadeTimer!.Stop();
                _fadeTimer.Dispose();
                _fadeTimer = null;
            }
            // Update panel background alpha
            _panel.BackColor = Color.FromArgb((int)_form.Opacity * 255, TvConfig.Osd.BackgroundColor);
        }

        private static void FadeOut()
        {
            if (_form == null) return;
            _form.Opacity -= 0.05;
            if (_form.Opacity <= 0.01)
            {
                _form.Opacity = 0;
                _isVisible = false;
                _dismissTimer!.Stop();
                _dismissTimer.Dispose();
                _dismissTimer = null;
                _form.Hide();
            }
            _panel!.BackColor = Color.FromArgb((int)(_form.Opacity * 255), TvConfig.Osd.BackgroundColor);
        }

        public static void ForceHide()
        {
            if (_fadeTimer != null) { _fadeTimer.Stop(); _fadeTimer.Dispose(); _fadeTimer = null; }
            if (_dismissTimer != null) { _dismissTimer.Stop(); _dismissTimer.Dispose(); _dismissTimer = null; }
            if (_form != null && !_form.IsDisposed)
            {
                _form.Opacity = 0;
                _isVisible = false;
                _form.Hide();
            }
        }

        public static void Shutdown()
        {
            ForceHide();
            _lastBrightness = 0;
        }
    }
}
