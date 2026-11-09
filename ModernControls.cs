using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SmartHdrManager
{
    // =========================================================================
    // Small owner-drawn controls for a Windows 11 look. All drawing scales with the control size,
    // so they follow the form's DPI scaling automatically.
    // =========================================================================

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
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), ScaledRadius);
            using (var brush = new SolidBrush(BackColor))
                g.FillPath(brush, path);

            if (!_borderColor.IsEmpty)
            {
                using var pen = new Pen(_borderColor);
                g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>Windows 11 style on/off switch with a short animation.</summary>
    public sealed class ToggleSwitch : Control
    {
        private bool _checked;
        private float _position;   // 0 = off, 1 = on
        private bool _hover;
        private readonly System.Windows.Forms.Timer _animation = new() { Interval = 15 };

        public event EventHandler? CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);   // every click toggles, even quick double-taps
            Size = new Size(44, 22);
            Cursor = Cursors.Hand;
            TabStop = true;

            _animation.Tick += (_, _) =>
            {
                float target = _checked ? 1f : 0f;
                _position += (target - _position) * 0.35f;
                if (Math.Abs(target - _position) < 0.02f)
                {
                    _position = target;
                    _animation.Stop();
                }
                Invalidate();
            };
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
                    _position = value ? 1f : 0f;
                    Invalidate();
                }
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
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
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawSwitch(g, new RectangleF(1, 1, Width - 3, Height - 3), _position, _hover, Enabled);

            if (Focused && ShowFocusCues)
            {
                using var pen = new Pen(Color.FromArgb(120, Theme.Text)) { DashStyle = DashStyle.Dot };
                using var path = Theme.RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f);
                g.DrawPath(pen, path);
            }
        }

        /// <summary>Shared drawing so the games list can draw identical switches inside its rows.</summary>
        public static void DrawSwitch(Graphics g, RectangleF track, float position, bool hover, bool enabled)
        {
            float radius = track.Height / 2f;
            Color offTrack = hover ? Theme.ButtonHover : Theme.ButtonBack;
            Color onTrack = hover ? Theme.AccentHover : Theme.Accent;
            Color fill = Theme.Blend(offTrack, onTrack, position);
            if (!enabled) fill = Color.FromArgb(90, fill);

            using (var path = Theme.RoundRect(track, radius))
            {
                using (var brush = new SolidBrush(fill))
                    g.FillPath(brush, path);

                if (position < 0.5f)
                {
                    using var pen = new Pen(Theme.Blend(Theme.Muted, onTrack, position * 2f));
                    g.DrawPath(pen, path);
                }
            }

            float inset = track.Height * 0.2f;
            float d = track.Height - inset * 2f;
            float x = track.X + inset + position * (track.Width - d - inset * 2f);
            Color thumb = Theme.Blend(Theme.Muted, Theme.OnAccent, position);
            using var thumbBrush = new SolidBrush(enabled ? thumb : Color.FromArgb(120, thumb));
            g.FillEllipse(thumbBrush, x, track.Y + inset, d, d);
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
        private bool _hover;
        private bool _down;
        private bool _primary;
        private string? _glyph;

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);   // quick repeated clicks (e.g. stepper +/-) all count
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            Font = Theme.Body;
            Size = new Size(120, 34);
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
        /// AutoSize: the button is at least as wide as this text needs (the longest text it can show), so it keeps its
        /// width when the text changes. Layout engines only use the preferred size – MinimumSize is not enough.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SizingText
        {
            get => _sizingText;
            set { _sizingText = value; UpdateAutoSize(); Invalidate(); }
        }
        private string? _sizingText;

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size text = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            if (!string.IsNullOrEmpty(_sizingText))
            {
                Size sizing = TextRenderer.MeasureText(_sizingText, Font, Size.Empty, TextFormatFlags.NoPadding);
                if (sizing.Width > text.Width) text = new Size(sizing.Width, Math.Max(text.Height, sizing.Height));
            }
            int glyphWidth = string.IsNullOrEmpty(_glyph) ? 0 : Font.Height + (text.Width > 0 ? Font.Height / 3 : 0);
            int padding = Font.Height;
            int width = Math.Max(Font.Height * 2 + 4, text.Width + glyphWidth + padding * 2);
            int height = Font.Height + 16;
            return new Size(width, height);
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
            Control? c = this;
            while (c != null && c.BackColor.A < 255) c = c.Parent;
            return c?.BackColor ?? Theme.Back;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // The background (BackColor, or the parent when transparent) is already painted by OnPaintBackground
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
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
                    g.FillPath(brush, path);
                using (var pen = new Pen(border))
                    g.DrawPath(pen, path);
            }

            // Icon + text, centred together
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            Size textSize = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags);
            int glyphSize = string.IsNullOrEmpty(_glyph) ? 0 : Font.Height;
            int gap = glyphSize > 0 && textSize.Width > 0 ? Font.Height / 3 : 0;
            int total = glyphSize + gap + textSize.Width;
            int x = (Width - total) / 2;

            if (glyphSize > 0)
            {
                TextRenderer.DrawText(g, _glyph, Theme.IconFont, new Rectangle(x, 0, glyphSize, Height), text,
                    flags | TextFormatFlags.HorizontalCenter);
                x += glyphSize + gap;
            }
            if (textSize.Width > 0)
            {
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, textSize.Width + 2, Height), text, flags);
            }

            if (Focused && ShowFocusCues)
            {
                using var pen = new Pen(Color.FromArgb(150, Theme.Text)) { DashStyle = DashStyle.Dot };
                using var path = Theme.RoundRect(new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f), radius - 2);
                g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>Segmented tab bar ("pill" style).</summary>
    public sealed class SegmentedTabs : Control
    {
        private readonly List<string> _items = new();
        private int _selected;
        private int _hover = -1;

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

        private bool _sizing;

        private float OuterPad => Height * 0.1f;

        // Each segment is as wide as its (semibold) label plus padding, so the labels never touch
        private int SegmentWidth(string text) =>
            TextRenderer.MeasureText(text, Theme.Semibold, Size.Empty, TextFormatFlags.NoPadding).Width + (int)(Height * 0.9f);

        /// <summary>The control sizes its width to the tabs (it follows height changes, e.g. from DPI scaling).</summary>
        private void UpdateWidth()
        {
            if (_sizing || _items.Count == 0) return;
            _sizing = true;
            try
            {
                int total = (int)(OuterPad * 2f);
                foreach (var item in _items) total += SegmentWidth(item);
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

        private int HitTest(Point p)
        {
            for (int i = 0; i < _items.Count; i++)
                if (SegmentRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
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
            int h = HitTest(e.Location);
            if (h >= 0) SelectedIndex = h;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var track = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Height * 0.25f))
            using (var brush = new SolidBrush(Theme.Surface))
                g.FillPath(brush, track);

            for (int i = 0; i < _items.Count; i++)
            {
                RectangleF r = SegmentRect(i);
                bool selected = i == _selected;

                if (selected || i == _hover)
                {
                    using var path = Theme.RoundRect(r, r.Height * 0.25f);
                    using var brush = new SolidBrush(selected ? Theme.ButtonHover : Theme.SurfaceHover);
                    g.FillPath(brush, path);
                }

                if (selected)
                {
                    // small accent bar under the selected label
                    float barW = Math.Min(r.Width * 0.3f, LogicalToDeviceUnits(28));
                    float barH = Math.Max(2f, Height * 0.07f);
                    using var accent = Theme.RoundRect(new RectangleF(r.X + (r.Width - barW) / 2f, r.Bottom - barH - r.Height * 0.12f, barW, barH), barH / 2f);
                    using var accentBrush = new SolidBrush(Theme.Accent);
                    g.FillPath(accentBrush, accent);
                }

                TextRenderer.DrawText(g, _items[i], selected ? Theme.Semibold : Font, Rectangle.Round(r),
                    selected ? Theme.Text : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }

    // =========================================================================
    // Games list: owner-drawn rows with an on/off switch per game
    // =========================================================================
    public sealed class GameItem
    {
        public GameItem(GameRow row)
        {
            Key = row.Key;
            Name = row.Name;
            Subtitle = row.Subtitle;
            Processes = row.Processes;
            ExePath = row.ExePath;
            Hdr = row.Hdr;
            Brightness = row.Brightness;
        }

        public string Key { get; }
        public string Name { get; }
        public string Subtitle { get; }
        public List<string> Processes { get; }
        public string? ExePath { get; }
        public bool Hdr { get; set; }
        public int Brightness { get; set; }
        public override string ToString() => Name;
    }

    /// <summary>
    /// Animation clock synced to the monitor: a background thread waits for each display frame (DwmFlush returns when
    /// the Windows compositor has presented a frame, i.e. at the monitor's refresh rate: 60, 144, 165 Hz ...) and then
    /// runs one animation step on the UI thread. A WinForms Timer only ticks ~64 times per second, unsynchronised,
    /// which looks stuttery on high-refresh monitors. Start/Stop are called on the UI thread only.
    /// </summary>
    internal sealed class FrameLoop : IDisposable
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        private readonly Control _owner;
        private readonly Action _frame;
        private readonly object _gate = new();
        private volatile bool _running;
        private System.Threading.Thread? _thread;

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
                _thread = new System.Threading.Thread(Loop) { IsBackground = true, Name = "FrameLoop" };
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

                if (DwmFlush() != 0) System.Threading.Thread.Sleep(4);   // composition off: fall back to ~250 Hz pacing

                try
                {
                    if (_owner.IsDisposed || !_owner.IsHandleCreated) { _running = false; continue; }
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

    /// <summary>
    /// Games list drawn entirely by this control (no native list box): pixel-precise, animated scrolling for mouse
    /// wheels and touchpads, a slim overlay scrollbar, no flicker (double-buffered), and only visible rows are drawn.
    /// Each row: program icon, name, source line and an HDR switch.
    /// </summary>
    public sealed class GameList : Control
    {
        private List<GameItem> _items = new();
        private float _offset;             // current scroll position in pixels
        private float _target;             // where the scroll animation is heading
        private int _hover = -1;
        private int _selected = -1;
        private int _pressed = -1;
        private bool _pressedOnMinus;
        private bool _pressedOnPlus;
        private bool _minusHover;
        private bool _minusDown;
        private bool _plusHover;
        private bool _plusDown;

        private bool _thumbHover;
        private bool _draggingThumb;
        private int _dragStartY;
        private float _dragStartOffset;

        private readonly FrameLoop _animation;
        private readonly System.Diagnostics.Stopwatch _frameClock = new();

        // Softness of the glide: lower = longer, softer deceleration (1/s)
        private const double GlideRate = 8.5;

        // Brightness buttons: one click changes the value by this many steps
        private const int BrightnessStep = 10;

        private readonly Dictionary<string, Bitmap?> _icons = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _iconsLoading = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<GameItem>? BrightnessToggled;

        // Each row is drawn once into a bitmap and only re-drawn when it changes; scrolling just copies the bitmaps.
        // (Drawing text, antialiased shapes and scaled icons for every row on every frame was too slow for 144 Hz+.)
        private sealed class RowCache
        {
            public Bitmap Bitmap = null!;
            public int Width;
            public int Height;
            public int State;
        }
        private readonly Dictionary<GameItem, RowCache> _rowCache = new();

        public GameList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            _animation = new FrameLoop(this, AnimateScroll);
        }

        // ------------------------------------------------------------------
        // Items / selection
        // ------------------------------------------------------------------
        public IReadOnlyList<GameItem> Items => _items;

        public GameItem? SelectedItem => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

        /// <summary>Replaces the rows, keeping the scroll position and (by key) the selection.</summary>
        public void SetItems(IList<GameItem> items, string? selectedKey)
        {
            ClearRowCache();
            _items = new List<GameItem>(items);
            _selected = selectedKey == null ? -1 : _items.FindIndex(i => i.Key == selectedKey);
            _hover = -1;
            _target = Math.Clamp(_target, 0, MaxOffset);
            _offset = Math.Clamp(_offset, 0, MaxOffset);
            Invalidate();
        }

        private int RowHeight => Font.Height + Theme.Small.Height + 22;
        private int Gap => LogicalToDeviceUnits(4);
        private float ContentHeight => _items.Count * RowHeight + Gap * 2;
        private float MaxOffset => Math.Max(0f, ContentHeight - ClientSize.Height);
        private bool ScrollbarVisible => MaxOffset > 0.5f;
        private int ScrollbarWidth => LogicalToDeviceUnits(10);

        private Rectangle RowRect(int index)
        {
            int right = ClientSize.Width - (ScrollbarVisible ? ScrollbarWidth + LogicalToDeviceUnits(2) : 0);
            int y = (int)Math.Round(Gap + index * RowHeight - _offset);
            return new Rectangle(Gap, y, Math.Max(0, right - Gap * 2), RowHeight);
        }

        private int IndexAt(Point p)
        {
            if (ScrollbarVisible && p.X >= ClientSize.Width - ScrollbarWidth - LogicalToDeviceUnits(2)) return -1;
            float y = p.Y + _offset - Gap;
            if (y < 0) return -1;
            int index = (int)(y / RowHeight);
            return index >= 0 && index < _items.Count ? index : -1;
        }

        private RectangleF BrightnessRect(Rectangle row)
        {
            float h = row.Height * 0.5f;
            float w = LogicalToDeviceUnits(40);
            float textRight = BrightnessTextRect(row).Left;
            float left = textRight - w - LogicalToDeviceUnits(12);
            return new RectangleF(Math.Max(Gap * 2, left), row.Y + (row.Height - h) / 2f, w, h);
        }

        private RectangleF BrightnessTextRect(Rectangle row)
        {
            float h = row.Height * 0.5f;
            float plusLeft = BrightnessPlusRect(row).Left;
            float right = plusLeft - LogicalToDeviceUnits(12);
            float left = right - LogicalToDeviceUnits(80);
            return new RectangleF(Math.Max(Gap * 2, left), row.Y + (row.Height - h) / 2f, right - left, h);
        }

        private RectangleF BrightnessPlusRect(Rectangle row)
        {
            float h = row.Height * 0.5f;
            float w = LogicalToDeviceUnits(40);
            float left = row.Right - Gap - w;
            return new RectangleF(Math.Max(Gap * 2, left), row.Y + (row.Height - h) / 2f, w, h);
        }

        private bool IsOnMinusButton(Point p, int index)
        {
            if (index < 0) return false;
            Rectangle row = RowRect(index);
            RectangleF hit = BrightnessRect(row);
            hit.Inflate(LogicalToDeviceUnits(4), LogicalToDeviceUnits(4));
            return hit.Contains(p);
        }

        private bool IsOnPlusButton(Point p, int index)
        {
            if (index < 0) return false;
            Rectangle row = RowRect(index);
            RectangleF hit = BrightnessPlusRect(row);
            hit.Inflate(LogicalToDeviceUnits(4), LogicalToDeviceUnits(4));
            return hit.Contains(p);
        }

        private void AdjustBrightness(int index, int delta)
        {
            if (index < 0 || index >= _items.Count) return;
            var item = _items[index];
            item.Brightness = Math.Clamp(item.Brightness + delta, 0, 100);
            Invalidate();
            BrightnessToggled?.Invoke(this, item);
        }

        // ------------------------------------------------------------------
        // Smooth scrolling
        // ------------------------------------------------------------------
        private void ScrollTo(float target, bool animate = true)
        {
            _target = Math.Clamp(target, 0, MaxOffset);
            if (!animate)
            {
                _offset = _target;
                _animation.Stop();
                UpdateHoverFromCursor();
                Invalidate();
                return;
            }
            if (!_animation.IsRunning)
            {
                _frameClock.Restart();
                _animation.Start();
            }
        }

        private void AnimateScroll()
        {
            float dt = (float)_frameClock.Elapsed.TotalSeconds;
            _frameClock.Restart();
            dt = Math.Clamp(dt, 0.001f, 0.05f);

            // Exponential ease towards the target: immediate response, long soft landing, frame-rate independent
            float k = 1f - (float)Math.Exp(-dt * GlideRate);
            _offset += (_target - _offset) * k;
            bool finished = Math.Abs(_target - _offset) < 0.3f;
            if (finished)
            {
                _offset = _target;
                _animation.Stop();
            }
            UpdateHoverFromCursor();
            Invalidate();
            Update();   // paint now, inside this display frame
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // Touchpads send many small deltas, mouse wheels steps of 120: both scroll proportionally
            int lines = SystemInformation.MouseWheelScrollLines;
            float perNotch = lines < 0 ? ClientSize.Height * 0.9f : Math.Max(RowHeight * 1.2f, lines * Font.Height * 1.6f);
            ScrollTo(_target - e.Delta / 120f * perNotch);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            base.OnMouseWheel(e);
        }

        private void EnsureVisible(int index)
        {
            if (index < 0) return;
            float top = Gap + index * RowHeight;
            if (top < _target) ScrollTo(top - Gap);
            else if (top + RowHeight > _target + ClientSize.Height) ScrollTo(top + RowHeight - ClientSize.Height + Gap);
        }

        // ------------------------------------------------------------------
        // Overlay scrollbar
        // ------------------------------------------------------------------
        private RectangleF ThumbRect()
        {
            float track = ClientSize.Height - LogicalToDeviceUnits(8);
            float thumbH = Math.Max(LogicalToDeviceUnits(36), track * ClientSize.Height / Math.Max(1f, ContentHeight));
            float y = LogicalToDeviceUnits(4) + (MaxOffset <= 0 ? 0 : (track - thumbH) * (_offset / MaxOffset));
            float w = _thumbHover || _draggingThumb ? ScrollbarWidth - LogicalToDeviceUnits(2) : ScrollbarWidth * 0.5f;
            return new RectangleF(ClientSize.Width - LogicalToDeviceUnits(3) - w, y, w, thumbH);
        }

        private bool OnScrollbar(Point p) => ScrollbarVisible && p.X >= ClientSize.Width - ScrollbarWidth - LogicalToDeviceUnits(4);

        // ------------------------------------------------------------------
        // Mouse & keyboard
        // ------------------------------------------------------------------
        private void UpdateHoverFromCursor()
        {
            Point p = PointToClient(Cursor.Position);
            int h = ClientRectangle.Contains(p) && !_draggingThumb ? IndexAt(p) : -1;
            if (h != _hover) _hover = h;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_draggingThumb)
            {
                float track = ClientSize.Height - LogicalToDeviceUnits(8) - ThumbRect().Height;
                float perPixel = track <= 0 ? 0 : MaxOffset / track;
                ScrollTo(_dragStartOffset + (e.Y - _dragStartY) * perPixel, animate: false);
            }
            else
            {
                bool thumbHover = OnScrollbar(e.Location);
                int h = IndexAt(e.Location);
                bool minusHover = h >= 0 && IsOnMinusButton(e.Location, h);
                bool plusHover = h >= 0 && IsOnPlusButton(e.Location, h);
                if (h != _hover || thumbHover != _thumbHover || minusHover != _minusHover || plusHover != _plusHover)
                {
                    if (minusHover || plusHover)
                    {
                        _hover = -1;
                    }
                    else
                    {
                        _hover = h;
                    }
                    _thumbHover = thumbHover;
                    _minusHover = minusHover;
                    _plusHover = plusHover;
                    Invalidate();
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (!_draggingThumb)
            {
                _hover = -1;
                _thumbHover = false;
                _minusHover = false;
                _plusHover = false;
                _minusDown = false;
                _plusDown = false;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                if (OnScrollbar(e.Location))
                {
                    RectangleF thumb = ThumbRect();
                    if (e.Y >= thumb.Top && e.Y <= thumb.Bottom)
                    {
                        _draggingThumb = true;
                        _dragStartY = e.Y;
                        _dragStartOffset = _offset;
                        Capture = true;
                    }
                    else
                    {
                        // click on the track: jump one page towards the click
                        ScrollTo(_target + (e.Y < thumb.Top ? -1 : 1) * ClientSize.Height * 0.9f);
                    }
                    Invalidate();
                }
                else
                {
                    _pressed = IndexAt(e.Location);
                    _pressedOnMinus = _pressed >= 0 && IsOnMinusButton(e.Location, _pressed);
                    _pressedOnPlus = _pressed >= 0 && IsOnPlusButton(e.Location, _pressed);
                    if (_pressedOnMinus) _minusDown = true;
                    if (_pressedOnPlus) _plusDown = true;
                    if (_pressed >= 0) _selected = _pressed;
                    Invalidate();
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_draggingThumb)
            {
                _draggingThumb = false;
                Capture = false;
                Invalidate();
            }
            else if (e.Button == MouseButtons.Left && PressedRowHit(e.Location))
            {
                if (_pressedOnMinus)
                {
                    AdjustBrightness(_pressed, -BrightnessStep);
                }
                else if (_pressedOnPlus)
                {
                    AdjustBrightness(_pressed, BrightnessStep);
                }
            }
            _pressed = -1;
            _minusDown = false;
            _plusDown = false;
            base.OnMouseUp(e);
        }

        /// <summary>True when a left-button release inside the row pressed earlier should count as a click.</summary>
        private bool PressedRowHit(Point p) => _pressed >= 0 && SameRowBoundary(_pressed, p);

        /// <summary>Ensures the release point falls within the original pressed row's vertical bounds.</summary>
        private bool SameRowBoundary(int index, Point p)
        {
            if (index < 0 || index >= _items.Count) return false;
            Rectangle row = RowRect(index);
            return p.Y >= row.Top && p.Y < row.Bottom;
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Space
            || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int visibleRows = Math.Max(1, ClientSize.Height / RowHeight - 1);
            int next = e.KeyCode switch
            {
                Keys.Up => Math.Max(0, _selected - 1),
                Keys.Down => Math.Min(_items.Count - 1, _selected + 1),
                Keys.PageUp => Math.Max(0, _selected - visibleRows),
                Keys.PageDown => Math.Min(_items.Count - 1, _selected + visibleRows),
                Keys.Home => 0,
                Keys.End => _items.Count - 1,
                _ => int.MinValue
            };

            if (next != int.MinValue && _items.Count > 0)
            {
                _selected = next;
                EnsureVisible(next);
                Invalidate();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _target = Math.Clamp(_target, 0, MaxOffset);
            _offset = Math.Clamp(_offset, 0, MaxOffset);
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        // ------------------------------------------------------------------
        // Painting
        // ------------------------------------------------------------------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_items.Count == 0)
            {
                TextRenderer.DrawText(g, "未找到游戏。请使用「添加程序」或「添加正在运行」。", Font, ClientRectangle, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            int first = Math.Max(0, (int)((_offset - Gap) / RowHeight));
            int last = Math.Min(_items.Count - 1, (int)((_offset + ClientSize.Height) / RowHeight) + 1);

            g.CompositingMode = CompositingMode.SourceCopy;          // rows are opaque: plain copy, fastest path
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            for (int i = first; i <= last; i++)
            {
                Rectangle row = RowRect(i);
                var cache = GetRowBitmap(i, row.Width, row.Height);
                if (cache != null)
                    g.DrawImage(cache.Bitmap, new Rectangle(row.X, row.Y, cache.Width, cache.Height),
                        0, 0, cache.Width, cache.Height, GraphicsUnit.Pixel);
            }
            g.CompositingMode = CompositingMode.SourceOver;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            TrimRowCache(first, last);

            if (ScrollbarVisible)
            {
                RectangleF thumb = ThumbRect();
                Color color = _draggingThumb ? Theme.Text : _thumbHover ? Theme.Muted : Theme.Border;
                using var path = Theme.RoundRect(thumb, thumb.Width / 2f);
                using var brush = new SolidBrush(Color.FromArgb(_thumbHover || _draggingThumb ? 220 : 170, color));
                g.FillPath(brush, path);
            }
        }

        /// <summary>Everything that changes a row's look; the cached bitmap is re-drawn when this changes.</summary>
        private int RowState(int index)
        {
            var item = _items[index];
            int state = 0;
            if (index == _selected) state |= 1;
            if (index == _hover) state |= 2;
            if (index == _selected && Focused && ShowFocusCues) state |= 4;
            if (!string.IsNullOrEmpty(item.ExePath) && _icons.TryGetValue(item.ExePath, out var icon) && icon != null) state |= 16;
            return state;
        }

        private RowCache? GetRowBitmap(int index, int width, int height)
        {
            if (width <= 0 || height <= 0) return null;
            var item = _items[index];
            int state = RowState(index);

            if (_rowCache.TryGetValue(item, out var cache) && cache.Width == width && cache.Height == height && cache.State == state)
                return cache;

            cache?.Bitmap.Dispose();
            // 32bpp RGB (no alpha): GDI text (TextRenderer) renders correctly into it; the row is opaque anyway
            var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                DrawRow(g, index, new Rectangle(0, 0, width, height));
            }

            cache = new RowCache { Bitmap = bmp, Width = width, Height = height, State = RowState(index) };
            _rowCache[item] = cache;
            return cache;
        }

        /// <summary>Keeps bitmaps only for rows near the visible area (memory stays small for long lists).</summary>
        private void TrimRowCache(int first, int last)
        {
            if (_rowCache.Count <= (last - first + 1) + 40) return;
            var keep = new HashSet<GameItem>();
            for (int i = Math.Max(0, first - 20); i <= Math.Min(_items.Count - 1, last + 20); i++) keep.Add(_items[i]);
            foreach (var item in new List<GameItem>(_rowCache.Keys))
            {
                if (keep.Contains(item)) continue;
                _rowCache[item].Bitmap.Dispose();
                _rowCache.Remove(item);
            }
        }

        private void ClearRowCache()
        {
            foreach (var cache in _rowCache.Values) cache.Bitmap.Dispose();
            _rowCache.Clear();
        }

        private void DrawRow(Graphics g, int index, Rectangle row)
        {
            var item = _items[index];
            bool selected = index == _selected;
            bool hover = index == _hover;

            if (selected || hover)
            {
                using var path = Theme.RoundRect(new RectangleF(row.X, row.Y + 1, row.Width, row.Height - 2), row.Height * 0.16f);
                using var brush = new SolidBrush(selected ? Theme.ButtonBack : Theme.SurfaceHover);
                g.FillPath(brush, path);
                if (selected && Focused && ShowFocusCues)
                {
                    using var pen = new Pen(Color.FromArgb(90, Theme.Text));
                    g.DrawPath(pen, path);
                }
            }

            // Icon (exe icon, or a coloured letter)
            float iconSize = row.Height * 0.6f;
            var iconRect = new RectangleF(row.X + row.Height * 0.25f, row.Y + (row.Height - iconSize) / 2f, iconSize, iconSize);
            var icon = GetIcon(item.ExePath);
            if (icon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, iconRect);
            }
            else
            {
                DrawLetterIcon(g, iconRect, item.Name);
            }

            // Name + source line
            int textX = (int)(iconRect.Right + row.Height * 0.25f);
            int textWidth = Math.Max(0, row.Width - textX - Gap * 2);
            int nameHeight = Font.Height;
            int subHeight = Theme.Small.Height;
            int top = row.Y + (row.Height - nameHeight - subHeight) / 2;
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

            TextRenderer.DrawText(g, item.Name, Font, new Rectangle(textX, top, textWidth, nameHeight), Theme.Text, flags);

            TextRenderer.DrawText(g, item.Subtitle, Theme.Small, new Rectangle(textX, top + nameHeight, textWidth, subHeight), Theme.Muted, flags);

            // Brightness buttons: - 亮度值  +
            RectangleF minusRect = BrightnessRect(row);
            RectangleF textRect = BrightnessTextRect(row);
            RectangleF plusRect = BrightnessPlusRect(row);
            string brightText = $"亮度: {item.Brightness}";
            Color brightColor = item.Hdr ? Theme.Text : Theme.Muted;
            TextRenderer.DrawText(g, brightText, Theme.Semibold, Rectangle.Round(textRect), brightColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            
            // Draw minus button
            Color minusFill = _minusDown ? Theme.ButtonPressed : _minusHover ? Theme.ButtonHover : Theme.ButtonBack;
            using var minusPath = Theme.RoundRect(minusRect, minusRect.Height / 2f);
            using var minusBrush = new SolidBrush(minusFill);
            g.FillPath(minusBrush, minusPath);
            using var minusPen = new Pen(Theme.Border, 1f);
            g.DrawPath(minusPen, minusPath);
            var minusCenter = new RectangleF(
                minusRect.X + minusRect.Width / 2f - LogicalToDeviceUnits(4),
                minusRect.Y + minusRect.Height / 2f - Font.Height / 2f,
                LogicalToDeviceUnits(8),
                Font.Height);
            TextRenderer.DrawText(g, "-", Theme.Semibold, Rectangle.Round(minusCenter), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            
            // Draw plus button
            Color plusFill = _plusDown ? Theme.ButtonPressed : _plusHover ? Theme.ButtonHover : Theme.ButtonBack;
            using var plusPath = Theme.RoundRect(plusRect, plusRect.Height / 2f);
            using var plusBrush = new SolidBrush(plusFill);
            g.FillPath(plusBrush, plusPath);
            using var plusPen = new Pen(Theme.Border, 1f);
            g.DrawPath(plusPen, plusPath);
            var plusCenter = new RectangleF(
                plusRect.X + plusRect.Width / 2f - LogicalToDeviceUnits(4),
                plusRect.Y + plusRect.Height / 2f - Font.Height / 2f,
                LogicalToDeviceUnits(8),
                Font.Height);
            TextRenderer.DrawText(g, "+", Theme.Semibold, Rectangle.Round(plusCenter), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        // ------------------------------------------------------------------
        // Icons
        // ------------------------------------------------------------------
        /// <summary>
        /// Program icon from the exe. Loaded in the background the first time a row is shown (a sleeping disk must not
        /// freeze the window); the row shows the letter icon until then and repaints when the icon is ready.
        /// </summary>
        private Bitmap? GetIcon(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_icons.TryGetValue(path, out var cached)) return cached;
            if (!IsHandleCreated || !_iconsLoading.Add(path)) return null;

            string file = path;
            System.Threading.Tasks.Task.Run(() =>
            {
                Bitmap? bmp = null;
                try
                {
                    if (System.IO.File.Exists(file))
                    {
                        using var icon = Icon.ExtractAssociatedIcon(file);
                        if (icon != null) bmp = icon.ToBitmap();
                    }
                }
                catch { }

                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) { bmp?.Dispose(); return; }
                        _icons[file] = bmp;
                        _iconsLoading.Remove(file);
                        Invalidate();
                    }));
                }
                catch
                {
                    bmp?.Dispose();   // the list was closed meanwhile
                }
            });
            return null;
        }

        private static readonly Color[] LetterColors =
        {
            Color.FromArgb(124, 72, 214), Color.FromArgb(215, 96, 132), Color.FromArgb(230, 140, 60),
            Color.FromArgb(60, 150, 210), Color.FromArgb(70, 170, 120), Color.FromArgb(150, 110, 200)
        };

        /// <summary>Fallback when there is no exe icon: rounded square with the first letter of the name.</summary>
        private static void DrawLetterIcon(Graphics g, RectangleF r, string name)
        {
            int hash = 0;
            foreach (char c in name) hash = hash * 31 + c;
            Color color = LetterColors[(hash & 0x7fffffff) % LetterColors.Length];
            using (var path = Theme.RoundRect(r, r.Height * 0.24f))
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, path);

            string letter = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
            TextRenderer.DrawText(g, letter, Theme.Semibold, Rectangle.Round(r), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animation.Dispose();
                ClearRowCache();
                foreach (var bmp in _icons.Values) bmp?.Dispose();
                _icons.Clear();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// TableLayoutPanel with smooth, animated mouse-wheel scrolling (used for the Settings and TV pages).
    /// The native scrollbar still works for dragging; the animation picks up from wherever it is.
    /// </summary>
    public sealed class SmoothScrollTable : TableLayoutPanel
    {
        private readonly FrameLoop _animation;
        private readonly System.Diagnostics.Stopwatch _frameClock = new();
        private const double GlideRate = 8.5;
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
        /// their own height instead of stretching the last one. It has to be a real row with a control, because
        /// TableLayoutPanel hands the remaining height to the last row of the table and lays out no row without a
        /// control at all. A percentage row cannot be used here: it would make the table always as tall as the visible
        /// area, so the page would no longer scroll.
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
            float perNotch = lines < 0 ? ClientSize.Height * 0.9f : Math.Max(60, lines * Font.Height * 2.2f);
            _target = Math.Clamp(_target - e.Delta / 120f * perNotch, 0, MaxScroll);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;

            if (!_animation.IsRunning)
            {
                _frameClock.Restart();
                _animation.Start();
            }
            // no base call: the base class would jump immediately
        }

        private void Animate()
        {
            float dt = Math.Clamp((float)_frameClock.Elapsed.TotalSeconds, 0.001f, 0.05f);
            _frameClock.Restart();
            float k = 1f - (float)Math.Exp(-dt * GlideRate);
            _current += (_target - _current) * k;
            if (Math.Abs(_target - _current) < 0.3f)
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
