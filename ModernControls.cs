using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HdrBrightControl
{
    /// <summary>Panel with rounded corners. BackColor is the card colour; the corners show the parent colour.</summary>
    public class RoundedPanel : Panel
    {
        private int _radius = 10;
        private Color _borderColor = Color.Empty;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }

        /// <summary>Corner radius in 96-DPI pixels (scaled to the display DPI when drawing).</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Radius
        {
            get => _radius;
            set { _radius = value; Invalidate(); }
        }

        protected float ScaledRadius => LogicalToDeviceUnits(_radius);

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color BorderColor
        {
            get => _borderColor;
            set { _borderColor = value; Invalidate(); }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            graphics.Clear(Parent?.BackColor ?? Theme.Back);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), ScaledRadius);
            using (var brush = new SolidBrush(BackColor))
                graphics.FillPath(brush, path);

            if (_borderColor.IsEmpty) return;

            using var pen = new Pen(_borderColor);
            graphics.DrawPath(pen, path);
        }
    }

    /// <summary>Windows 11 style on/off switch with a short animation.</summary>
    public sealed class ToggleSwitch : Control
    {
        private const int AnimationIntervalMs = 15;
        private const float AnimationStep = 0.35f;
        private const float AnimationDoneThreshold = 0.02f;

        private bool _checked;
        private float _onPosition;
        private bool _hover;
        private readonly System.Windows.Forms.Timer _animation = new() { Interval = AnimationIntervalMs };

        public event EventHandler? CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Size = new Size(44, 22);
            Cursor = Cursors.Hand;
            TabStop = true;

            _animation.Tick += (_, _) => Animate();
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                if (IsHandleCreated && Visible)
                {
                    _animation.Start();
                }
                else
                {
                    _onPosition = value ? 1f : 0f;
                    Invalidate();
                }
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void Animate()
        {
            float target = _checked ? 1f : 0f;
            _onPosition += (target - _onPosition) * AnimationStep;
            if (Math.Abs(target - _onPosition) < AnimationDoneThreshold)
            {
                _onPosition = target;
                _animation.Stop();
            }
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            Focus();
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                Checked = !Checked;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            graphics.Clear(BackColor);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            DrawSwitch(graphics, new RectangleF(1, 1, Width - 3, Height - 3), _onPosition, _hover, Enabled);

            if (!Focused || !ShowFocusCues) return;

            using var pen = new Pen(Color.FromArgb(120, Theme.Text)) { DashStyle = DashStyle.Dot };
            using var path = Theme.RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f);
            graphics.DrawPath(pen, path);
        }

        /// <summary>Shared drawing so other lists can draw identical switches inside their rows.</summary>
        public static void DrawSwitch(Graphics graphics, RectangleF track, float position, bool hover, bool enabled)
        {
            float radius = track.Height / 2f;
            Color offTrack = hover ? Theme.ButtonHover : Theme.ButtonBack;
            Color onTrack = hover ? Theme.AccentHover : Theme.Accent;
            Color fill = Theme.Blend(offTrack, onTrack, position);
            if (!enabled) fill = Color.FromArgb(90, fill);

            using (var path = Theme.RoundRect(track, radius))
            {
                using (var brush = new SolidBrush(fill))
                    graphics.FillPath(brush, path);

                if (position < 0.5f)
                {
                    using var pen = new Pen(Theme.Blend(Theme.Muted, onTrack, position * 2f));
                    graphics.DrawPath(pen, path);
                }
            }

            float inset = track.Height * 0.2f;
            float diameter = track.Height - inset * 2f;
            float x = track.X + inset + position * (track.Width - diameter - inset * 2f);
            Color thumb = Theme.Blend(Theme.Muted, Theme.OnAccent, position);
            using var thumbBrush = new SolidBrush(enabled ? thumb : Color.FromArgb(120, thumb));
            graphics.FillEllipse(thumbBrush, x, track.Y + inset, diameter, diameter);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _animation.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Rounded button with an optional icon glyph. Primary = filled with the accent colour.</summary>
    public sealed class ModernButton : Control
    {
        private const int DefaultWidth = 120;
        private const int DefaultHeight = 34;

        private bool _hover;
        private bool _down;
        private bool _primary;
        private string? _glyph;
        private string? _sizingText;

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            Font = Theme.Body;
            Size = new Size(DefaultWidth, DefaultHeight);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Primary
        {
            get => _primary;
            set { _primary = value; Invalidate(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? Glyph
        {
            get => _glyph;
            set { _glyph = value; UpdateAutoSize(); Invalidate(); }
        }

        /// <summary>
        /// The longest text this button can show. AutoSize keeps the button wide enough for it, so the width does not
        /// change when the text does.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SizingText
        {
            get => _sizingText;
            set { _sizingText = value; UpdateAutoSize(); Invalidate(); }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size text = string.IsNullOrEmpty(Text)
                ? Size.Empty
                : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);

            if (!string.IsNullOrEmpty(_sizingText))
            {
                Size sizing = TextRenderer.MeasureText(_sizingText, Font, Size.Empty, TextFormatFlags.NoPadding);
                if (sizing.Width > text.Width) text = new Size(sizing.Width, Math.Max(text.Height, sizing.Height));
            }

            int glyphWidth = string.IsNullOrEmpty(_glyph) ? 0 : Font.Height + (text.Width > 0 ? Font.Height / 3 : 0);
            int width = Math.Max(Font.Height * 2 + 4, text.Width + glyphWidth + Font.Height * 2);
            return new Size(width, Font.Height + 16);
        }

        private void UpdateAutoSize()
        {
            if (AutoSize) Size = GetPreferredSize(Size.Empty);
        }

        protected override void OnTextChanged(EventArgs e) { UpdateAutoSize(); Invalidate(); base.OnTextChanged(e); }
        protected override void OnFontChanged(EventArgs e) { UpdateAutoSize(); base.OnFontChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _down = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        /// <summary>First opaque background colour up the parent chain (BackColor may be transparent).</summary>
        private Color EffectiveBack()
        {
            Control? control = this;
            while (control != null && control.BackColor.A < 255) control = control.Parent;
            return control?.BackColor ?? Theme.Back;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color back = EffectiveBack();

            Color fill, text, border;
            if (_primary)
            {
                fill = _down ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent;
                text = Theme.OnAccent;
                border = fill;
            }
            else
            {
                fill = _down ? Theme.ButtonPressed : _hover ? Theme.ButtonHover : Theme.ButtonBack;
                text = Theme.Text;
                border = Theme.Border;
            }

            if (!Enabled)
            {
                fill = Theme.Blend(fill, back, 0.55f);
                text = Theme.Muted;
                border = Theme.Blend(border, back, 0.55f);
            }

            float radius = Math.Max(4f, Height * 0.18f);
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), radius))
            {
                using (var brush = new SolidBrush(fill))
                    graphics.FillPath(brush, path);
                using (var pen = new Pen(border))
                    graphics.DrawPath(pen, path);
            }

            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            Size textSize = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(graphics, Text, Font, Size.Empty, flags);
            int glyphSize = string.IsNullOrEmpty(_glyph) ? 0 : Font.Height;
            int gap = glyphSize > 0 && textSize.Width > 0 ? Font.Height / 3 : 0;
            int x = (Width - (glyphSize + gap + textSize.Width)) / 2;

            if (glyphSize > 0)
            {
                TextRenderer.DrawText(graphics, _glyph, Theme.IconFont, new Rectangle(x, 0, glyphSize, Height), text,
                    flags | TextFormatFlags.HorizontalCenter);
                x += glyphSize + gap;
            }

            if (textSize.Width > 0)
                TextRenderer.DrawText(graphics, Text, Font, new Rectangle(x, 0, textSize.Width + 2, Height), text, flags);

            if (!Focused || !ShowFocusCues) return;

            using var focusPen = new Pen(Color.FromArgb(150, Theme.Text)) { DashStyle = DashStyle.Dot };
            using var focusPath = Theme.RoundRect(new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f), radius - 2);
            graphics.DrawPath(focusPen, focusPath);
        }
    }

    /// <summary>Segmented tab bar ("pill" style).</summary>
    public sealed class SegmentedTabs : Control
    {
        private const float OuterPaddingFactor = 0.1f;
        private const float SegmentPaddingFactor = 0.9f;
        private const float AccentBarWidthFactor = 0.3f;
        private const float SegmentRadiusFactor = 0.25f;
        private const float SelectedSegmentBottomFactor = 0.12f;

        private readonly List<string> _items = new();
        private int _selected;
        private int _hover = -1;
        private bool _sizing;

        public event EventHandler? SelectedIndexChanged;

        public SegmentedTabs()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.Body;
            Cursor = Cursors.Hand;
            Size = new Size(360, 40);
        }

        public void AddTab(string text)
        {
            _items.Add(text);
            UpdateWidth();
            Invalidate();
        }

        private float OuterPad => Height * OuterPaddingFactor;

        private int SegmentWidth(string text) =>
            TextRenderer.MeasureText(text, Theme.Semibold, Size.Empty, TextFormatFlags.NoPadding).Width +
            (int)(Height * SegmentPaddingFactor);

        /// <summary>The control sizes its width to the tabs (it follows height changes, e.g. from DPI scaling).</summary>
        private void UpdateWidth()
        {
            if (_sizing || _items.Count == 0) return;

            _sizing = true;
            try
            {
                int total = (int)(OuterPad * 2f);
                foreach (string item in _items) total += SegmentWidth(item);
                if (Width != total) Width = total;
            }
            finally { _sizing = false; }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateWidth();
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get => _selected;
            set
            {
                if (value < 0 || value >= _items.Count || value == _selected) return;
                _selected = value;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private RectangleF SegmentRect(int index)
        {
            float pad = OuterPad;
            float x = pad;
            for (int i = 0; i < index; i++) x += SegmentWidth(_items[i]);
            return new RectangleF(x, pad, SegmentWidth(_items[index]), Height - pad * 2f);
        }

        private int HitTest(Point location)
        {
            for (int i = 0; i < _items.Count; i++)
                if (SegmentRect(i).Contains(location)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hovered = HitTest(e.Location);
            if (hovered != _hover) { _hover = hovered; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int index = HitTest(e.Location);
            if (index >= 0) SelectedIndex = index;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            graphics.Clear(BackColor);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using (var track = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Height * SegmentRadiusFactor))
            using (var brush = new SolidBrush(Theme.Surface))
                graphics.FillPath(brush, track);

            for (int i = 0; i < _items.Count; i++)
            {
                RectangleF rect = SegmentRect(i);
                bool selected = i == _selected;

                if (selected || i == _hover)
                {
                    using var path = Theme.RoundRect(rect, rect.Height * SegmentRadiusFactor);
                    using var brush = new SolidBrush(selected ? Theme.ButtonHover : Theme.SurfaceHover);
                    graphics.FillPath(brush, path);
                }

                if (selected)
                {
                    float barWidth = Math.Min(rect.Width * AccentBarWidthFactor, LogicalToDeviceUnits(28));
                    float barHeight = Math.Max(2f, Height * 0.07f);
                    var barRect = new RectangleF(rect.X + (rect.Width - barWidth) / 2f,
                                                 rect.Bottom - barHeight - rect.Height * SelectedSegmentBottomFactor,
                                                 barWidth, barHeight);
                    using var accent = Theme.RoundRect(barRect, barHeight / 2f);
                    using var accentBrush = new SolidBrush(Theme.Accent);
                    graphics.FillPath(accentBrush, accent);
                }

                TextRenderer.DrawText(graphics, _items[i], selected ? Theme.Semibold : Font, Rectangle.Round(rect),
                    selected ? Theme.Text : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }

    /// <summary>Draws one "−"/"+" button of a stepper, shared by the settings row and the games list.</summary>
    internal static class StepperButtons
    {
        private const float CornerFactor = 0.5f;
        private const float BorderWidth = 1f;
        private const int SymbolHalfWidth = 4;
        private const int SymbolWidth = 8;

        public static void Draw(Graphics graphics, Control owner, RectangleF rect, string symbol, bool down, bool hover)
        {
            Color fill = down ? Theme.ButtonPressed : hover ? Theme.ButtonHover : Theme.ButtonBack;

            using (var path = Theme.RoundRect(rect, rect.Height * CornerFactor))
            {
                using (var brush = new SolidBrush(fill))
                    graphics.FillPath(brush, path);
                using (var pen = new Pen(Theme.Border, BorderWidth))
                    graphics.DrawPath(pen, path);
            }

            var center = new RectangleF(
                rect.X + rect.Width / 2f - owner.LogicalToDeviceUnits(SymbolHalfWidth),
                rect.Y + rect.Height / 2f - owner.Font.Height / 2f,
                owner.LogicalToDeviceUnits(SymbolWidth),
                owner.Font.Height);

            TextRenderer.DrawText(graphics, symbol, Theme.Semibold, Rectangle.Round(center), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>
    /// "−  value  +" number control drawn as one control without children, so DPI scaling can never position its parts
    /// twice. Keyboard: Left/Down = one step down, Right/Up = one step up.
    /// </summary>
    public sealed class NumberStepper : Control
    {
        private const int DefaultWidth = 130;
        private const int DefaultHeight = 34;
        private const int FocusInset = 4;
        private const int FocusTop = 2;
        private const int FocusHeightReduction = 5;
        private const int FocusBorderAlpha = 120;

        private enum Part { None, Minus, Plus }

        private int _value;
        private Part _hover = Part.None;
        private Part _pressed = Part.None;

        public event EventHandler? ValueChanged;

        public NumberStepper()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);   // fast repeated clicks all count
            Font = Theme.Body;
            TabStop = true;
            Size = new Size(DefaultWidth, DefaultHeight);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Minimum { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Maximum { get; set; } = BrightnessRange.Max;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Step { get; set; } = BrightnessRange.Step;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _value;
            set
            {
                int clamped = Math.Clamp(value, Minimum, Maximum);
                if (clamped == _value) return;

                _value = clamped;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private Rectangle MinusRect => new(0, 0, Height, Height);

        private Rectangle PlusRect => new(Math.Max(0, Width - Height), 0, Height, Height);

        private Part HitTest(Point location) =>
            MinusRect.Contains(location) ? Part.Minus : PlusRect.Contains(location) ? Part.Plus : Part.None;

        private bool CanChange(Part part) =>
            Enabled && (part == Part.Minus ? _value > Minimum : part == Part.Plus && _value < Maximum);

        private void Change(bool up)
        {
            int delta = Math.Max(1, Step);
            Value = _value + (up ? delta : -delta);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Part hovered = HitTest(e.Location);
            if (hovered != _hover)
            {
                _hover = hovered;
                Cursor = hovered != Part.None && CanChange(hovered) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = Part.None;
            _pressed = Part.None;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Focus();
                _pressed = HitTest(e.Location);
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            Part released = HitTest(e.Location);
            if (e.Button == MouseButtons.Left && released == _pressed && CanChange(released))
                Change(released == Part.Plus);

            _pressed = Part.None;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Left or Keys.Down) { Change(up: false); e.Handled = true; }
            else if (e.KeyCode is Keys.Right or Keys.Up) { Change(up: true); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            graphics.Clear(BackColor);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            StepperButtons.Draw(graphics, this, MinusRect, "\u2212", _pressed == Part.Minus, _hover == Part.Minus);
            StepperButtons.Draw(graphics, this, PlusRect, "+", _pressed == Part.Plus, _hover == Part.Plus);

            var valueRect = new Rectangle(Height, 0, Math.Max(0, Width - Height * 2), Height);
            TextRenderer.DrawText(graphics, _value.ToString(), Theme.Semibold, valueRect,
                Enabled ? Theme.Text : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            if (!Focused || !ShowFocusCues) return;

            int inset = LogicalToDeviceUnits(FocusInset);
            using var pen = new Pen(Color.FromArgb(FocusBorderAlpha, Theme.Text)) { DashStyle = DashStyle.Dot };
            graphics.DrawRectangle(pen, valueRect.X + inset, FocusTop,
                Math.Max(0, valueRect.Width - inset * 2), Height - FocusHeightReduction);
        }
    }
}
