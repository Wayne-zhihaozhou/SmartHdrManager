using System;
using System.Drawing;
using System.Windows.Forms;

namespace HdrBrightControl
{
    /// <summary>
    /// TableLayoutPanel with smooth, animated mouse-wheel scrolling (settings and test pages).
    /// The native scrollbar still works for dragging; the animation picks up from wherever it is.
    /// </summary>
    public sealed class SmoothScrollTable : TableLayoutPanel
    {
        private const double GlideRate = 8.5;
        private const float AnimationTolerance = 0.3f;
        private const float MaxFrameSeconds = 0.05f;
        private const float MinFrameSeconds = 0.001f;
        private const float FullPageWheelFactor = 0.9f;
        private const float MinWheelStep = 60f;
        private const float WheelStepLineFactor = 2.2f;

        private readonly FrameLoop _animation;
        private readonly System.Diagnostics.Stopwatch _frameClock = new();
        private float _current;
        private float _target;

        public SmoothScrollTable()
        {
            DoubleBuffered = true;
            _animation = new FrameLoop(this, Animate);
            Scroll += (_, _) =>
            {
                if (!_animation.IsRunning) _current = _target = -AutoScrollPosition.Y;
            };
        }

        /// <summary>Adds a row of the given height in 96-DPI pixels and puts <paramref name="control"/> in it.</summary>
        public void AddRow(Control control, int height)
        {
            RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            RowCount = RowStyles.Count;
            Controls.Add(control, 0, RowCount - 1);
        }

        /// <summary>
        /// Ends the table with a row that takes the height the content leaves over, so a short page keeps its rows at
        /// their own height instead of stretching the last one. It has to be a real row with a control: TableLayoutPanel
        /// hands the remaining height to the last row of the table and lays out no row without a control at all.
        /// A percentage row cannot be used here: it would make the table always as tall as the visible area.
        /// </summary>
        public void AddFillerRow()
        {
            RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            RowCount = RowStyles.Count;
            Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, RowCount - 1);
        }

        private float MaxScroll => Math.Max(0, DisplayRectangle.Height - ClientSize.Height);

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!VerticalScroll.Visible)
            {
                base.OnMouseWheel(e);
                return;
            }

            if (!_animation.IsRunning) _current = _target = -AutoScrollPosition.Y;

            int lines = SystemInformation.MouseWheelScrollLines;
            float perNotch = lines < 0
                ? ClientSize.Height * FullPageWheelFactor
                : Math.Max(MinWheelStep, lines * Font.Height * WheelStepLineFactor);
            _target = Math.Clamp(_target - e.Delta / 120f * perNotch, 0, MaxScroll);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;

            if (_animation.IsRunning) return;

            _frameClock.Restart();
            _animation.Start();
        }

        private void Animate()
        {
            float dt = Math.Clamp((float)_frameClock.Elapsed.TotalSeconds, MinFrameSeconds, MaxFrameSeconds);
            _frameClock.Restart();

            float k = 1f - (float)Math.Exp(-dt * GlideRate);
            _current += (_target - _current) * k;
            if (Math.Abs(_target - _current) < AnimationTolerance)
            {
                _current = _target;
                _animation.Stop();
            }

            AutoScrollPosition = new Point(0, (int)Math.Round(_current));
            Update();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _animation.Dispose();
            base.Dispose(disposing);
        }
    }
}
