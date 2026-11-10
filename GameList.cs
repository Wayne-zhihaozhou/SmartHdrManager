using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HdrBrightControl
{
    public sealed class GameItem
    {
        public GameItem(GameRow row)
        {
            Key = row.Key;
            Name = row.Name;
            Subtitle = row.Subtitle;
            Processes = row.Processes;
            ExePath = row.ExePath;
            Tracked = row.Tracked;
            Brightness = row.Brightness;
        }

        public string Key { get; }
        public string Name { get; }
        public string Subtitle { get; }
        public List<string> Processes { get; }
        public string? ExePath { get; }
        public bool Tracked { get; set; }
        public int Brightness { get; set; }
    }

    /// <summary>
    /// Games list drawn entirely by this control (no native list box): pixel-precise, animated scrolling for mouse
    /// wheels and touchpads, a slim overlay scrollbar, no flicker (double-buffered), and only visible rows are drawn.
    /// </summary>
    public sealed class GameList : Control
    {
        private const double GlideRate = 8.5;
        private const int BrightnessStep = 10;
        private const int RowTextPadding = 22;
        private const int RowGapPx = 4;
        private const int ScrollbarWidthPx = 10;
        private const int ScrollbarMinThumbPx = 36;
        private const int BrightnessButtonWidthPx = 40;
        private const int BrightnessTextWidthPx = 80;
        private const int HitInflationPx = 4;
        private const int MaxCachedRows = 40;
        private const float ScrollAnimationTolerance = 0.3f;

        private List<GameItem> _items = new();
        private float _offset;
        private float _target;
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

        private readonly Dictionary<string, Bitmap?> _icons = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _iconsLoading = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<GameItem>? BrightnessToggled;

        /// <summary>Raised when the left mouse button is double-clicked on a row (the brightness steppers excluded).</summary>
        public event EventHandler<GameItem>? ItemActivated;

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
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardDoubleClick, true);
            TabStop = true;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            _animation = new FrameLoop(this, AnimateScroll);
        }

        public GameItem? SelectedItem => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

        /// <summary>Replaces the rows, keeping the scroll position and (by key) the selection.</summary>
        public void SetItems(IList<GameItem> items, string? selectedKey)
        {
            ClearRowCache();
            _items = new List<GameItem>(items);
            _selected = selectedKey == null ? -1 : _items.FindIndex(item => item.Key == selectedKey);
            _hover = -1;
            _target = Math.Clamp(_target, 0, MaxOffset);
            _offset = Math.Clamp(_offset, 0, MaxOffset);
            Invalidate();
        }

        private int RowHeight => Font.Height + Theme.Small.Height + RowTextPadding;
        private int Gap => LogicalToDeviceUnits(RowGapPx);
        private float ContentHeight => _items.Count * RowHeight + Gap * 2;
        private float MaxOffset => Math.Max(0f, ContentHeight - ClientSize.Height);
        private bool ScrollbarVisible => MaxOffset > 0.5f;
        private int ScrollbarWidth => LogicalToDeviceUnits(ScrollbarWidthPx);

        private Rectangle RowRect(int index)
        {
            int right = ClientSize.Width - (ScrollbarVisible ? ScrollbarWidth + LogicalToDeviceUnits(2) : 0);
            int y = (int)Math.Round(Gap + index * RowHeight - _offset);
            return new Rectangle(Gap, y, Math.Max(0, right - Gap * 2), RowHeight);
        }

        private int IndexAt(Point location)
        {
            if (ScrollbarVisible && location.X >= ClientSize.Width - ScrollbarWidth - LogicalToDeviceUnits(2)) return -1;

            float y = location.Y + _offset - Gap;
            if (y < 0) return -1;

            int index = (int)(y / RowHeight);
            return index >= 0 && index < _items.Count ? index : -1;
        }

        private RectangleF BrightnessMinusRect(Rectangle row)
        {
            float height = row.Height * 0.5f;
            float width = LogicalToDeviceUnits(BrightnessButtonWidthPx);
            float textRight = BrightnessTextRect(row).Left;
            float left = textRight - width - LogicalToDeviceUnits(12);
            return new RectangleF(Math.Max(Gap * 2, left), row.Y + (row.Height - height) / 2f, width, height);
        }

        private RectangleF BrightnessTextRect(Rectangle row)
        {
            float height = row.Height * 0.5f;
            float plusLeft = BrightnessPlusRect(row).Left;
            float right = plusLeft - LogicalToDeviceUnits(12);
            float left = right - LogicalToDeviceUnits(BrightnessTextWidthPx);
            return new RectangleF(Math.Max(Gap * 2, left), row.Y + (row.Height - height) / 2f, right - left, height);
        }

        private RectangleF BrightnessPlusRect(Rectangle row)
        {
            float height = row.Height * 0.5f;
            float width = LogicalToDeviceUnits(BrightnessButtonWidthPx);
            float left = row.Right - Gap - width;
            return new RectangleF(Math.Max(Gap * 2, left), row.Y + (row.Height - height) / 2f, width, height);
        }

        private bool IsOnMinusButton(Point location, int index)
        {
            if (index < 0) return false;
            RectangleF hit = BrightnessMinusRect(RowRect(index));
            hit.Inflate(LogicalToDeviceUnits(HitInflationPx), LogicalToDeviceUnits(HitInflationPx));
            return hit.Contains(location);
        }

        private bool IsOnPlusButton(Point location, int index)
        {
            if (index < 0) return false;
            RectangleF hit = BrightnessPlusRect(RowRect(index));
            hit.Inflate(LogicalToDeviceUnits(HitInflationPx), LogicalToDeviceUnits(HitInflationPx));
            return hit.Contains(location);
        }

        private void AdjustBrightness(int index, int delta)
        {
            if (index < 0 || index >= _items.Count) return;

            var item = _items[index];
            item.Brightness = Math.Clamp(item.Brightness + delta, 0, 100);
            Invalidate();
            BrightnessToggled?.Invoke(this, item);
        }

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

            if (_animation.IsRunning) return;

            _frameClock.Restart();
            _animation.Start();
        }

        private void AnimateScroll()
        {
            float dt = Math.Clamp((float)_frameClock.Elapsed.TotalSeconds, 0.001f, 0.05f);
            _frameClock.Restart();

            float k = 1f - (float)Math.Exp(-dt * GlideRate);
            _offset += (_target - _offset) * k;
            if (Math.Abs(_target - _offset) < ScrollAnimationTolerance)
            {
                _offset = _target;
                _animation.Stop();
            }

            UpdateHoverFromCursor();
            Invalidate();
            Update();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
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

        private RectangleF ThumbRect()
        {
            float track = ClientSize.Height - LogicalToDeviceUnits(8);
            float thumbHeight = Math.Max(LogicalToDeviceUnits(ScrollbarMinThumbPx),
                                         track * ClientSize.Height / Math.Max(1f, ContentHeight));
            float y = LogicalToDeviceUnits(4) + (MaxOffset <= 0 ? 0 : (track - thumbHeight) * (_offset / MaxOffset));
            float width = _thumbHover || _draggingThumb ? ScrollbarWidth - LogicalToDeviceUnits(2) : ScrollbarWidth * 0.5f;
            return new RectangleF(ClientSize.Width - LogicalToDeviceUnits(3) - width, y, width, thumbHeight);
        }

        private bool OnScrollbar(Point location) =>
            ScrollbarVisible && location.X >= ClientSize.Width - ScrollbarWidth - LogicalToDeviceUnits(4);

        private void UpdateHoverFromCursor()
        {
            Point position = PointToClient(Cursor.Position);
            int hovered = ClientRectangle.Contains(position) && !_draggingThumb ? IndexAt(position) : -1;
            if (hovered != _hover) _hover = hovered;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_draggingThumb)
            {
                float track = ClientSize.Height - LogicalToDeviceUnits(8) - ThumbRect().Height;
                float perPixel = track <= 0 ? 0 : MaxOffset / track;
                ScrollTo(_dragStartOffset + (e.Y - _dragStartY) * perPixel, animate: false);
                base.OnMouseMove(e);
                return;
            }

            bool thumbHover = OnScrollbar(e.Location);
            int hovered = IndexAt(e.Location);
            bool minusHover = hovered >= 0 && IsOnMinusButton(e.Location, hovered);
            bool plusHover = hovered >= 0 && IsOnPlusButton(e.Location, hovered);

            if (hovered != _hover || thumbHover != _thumbHover || minusHover != _minusHover || plusHover != _plusHover)
            {
                _hover = minusHover || plusHover ? -1 : hovered;
                _thumbHover = thumbHover;
                _minusHover = minusHover;
                _plusHover = plusHover;
                Invalidate();
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
            if (e.Button != MouseButtons.Left)
            {
                base.OnMouseDown(e);
                return;
            }

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
                if (_pressedOnMinus) AdjustBrightness(_pressed, -BrightnessStep);
                else if (_pressedOnPlus) AdjustBrightness(_pressed, BrightnessStep);
            }

            _pressed = -1;
            _minusDown = false;
            _plusDown = false;
            base.OnMouseUp(e);
        }

        private bool PressedRowHit(Point location) => _pressed >= 0 && SameRowBoundary(_pressed, location);

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int index = IndexAt(e.Location);
                if (index >= 0 && !IsOnMinusButton(e.Location, index) && !IsOnPlusButton(e.Location, index))
                {
                    _selected = index;
                    Invalidate();
                    ItemActivated?.Invoke(this, _items[index]);
                }
            }
            base.OnMouseDoubleClick(e);
        }

        private bool SameRowBoundary(int index, Point location)
        {
            if (index < 0 || index >= _items.Count) return false;

            Rectangle row = RowRect(index);
            return location.Y >= row.Top && location.Y < row.Bottom;
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Space ||
            base.IsInputKey(keyData);

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

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            graphics.Clear(BackColor);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            if (_items.Count == 0)
            {
                TextRenderer.DrawText(graphics, "未找到游戏。请使用「添加程序」或「添加正在运行」。", Font, ClientRectangle,
                    Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            int first = Math.Max(0, (int)((_offset - Gap) / RowHeight));
            int last = Math.Min(_items.Count - 1, (int)((_offset + ClientSize.Height) / RowHeight) + 1);

            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            for (int i = first; i <= last; i++)
            {
                Rectangle row = RowRect(i);
                var cache = GetRowBitmap(i, row.Width, row.Height);
                if (cache != null)
                    graphics.DrawImage(cache.Bitmap, new Rectangle(row.X, row.Y, cache.Width, cache.Height),
                        0, 0, cache.Width, cache.Height, GraphicsUnit.Pixel);
            }
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.PixelOffsetMode = PixelOffsetMode.Default;
            TrimRowCache(first, last);

            if (!ScrollbarVisible) return;

            RectangleF thumb = ThumbRect();
            Color color = _draggingThumb ? Theme.Text : _thumbHover ? Theme.Muted : Theme.Border;
            using var thumbPath = Theme.RoundRect(thumb, thumb.Width / 2f);
            using var thumbBrush = new SolidBrush(Color.FromArgb(_thumbHover || _draggingThumb ? 220 : 170, color));
            graphics.FillPath(thumbBrush, thumbPath);
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

            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(BackColor);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                DrawRow(graphics, index, new Rectangle(0, 0, width, height));
            }

            cache = new RowCache { Bitmap = bitmap, Width = width, Height = height, State = state };
            _rowCache[item] = cache;
            return cache;
        }

        /// <summary>Keeps bitmaps only for rows near the visible area (memory stays small for long lists).</summary>
        private void TrimRowCache(int first, int last)
        {
            if (_rowCache.Count <= (last - first + 1) + MaxCachedRows) return;

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

        private void DrawRow(Graphics graphics, int index, Rectangle row)
        {
            var item = _items[index];
            bool selected = index == _selected;
            bool hover = index == _hover;

            if (selected || hover)
            {
                using var path = Theme.RoundRect(new RectangleF(row.X, row.Y + 1, row.Width, row.Height - 2), row.Height * 0.16f);
                using var brush = new SolidBrush(selected ? Theme.ButtonBack : Theme.SurfaceHover);
                graphics.FillPath(brush, path);

                if (selected && Focused && ShowFocusCues)
                {
                    using var pen = new Pen(Color.FromArgb(90, Theme.Text));
                    graphics.DrawPath(pen, path);
                }
            }

            float iconSize = row.Height * 0.6f;
            var iconRect = new RectangleF(row.X + row.Height * 0.25f, row.Y + (row.Height - iconSize) / 2f, iconSize, iconSize);
            var icon = GetIcon(item.ExePath);
            if (icon != null)
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(icon, iconRect);
            }
            else
            {
                DrawLetterIcon(graphics, iconRect, item.Name);
            }

            int textX = (int)(iconRect.Right + row.Height * 0.25f);
            int textWidth = Math.Max(0, row.Width - textX - Gap * 2);
            int nameHeight = Font.Height;
            int subHeight = Theme.Small.Height;
            int top = row.Y + (row.Height - nameHeight - subHeight) / 2;
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                                         TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

            TextRenderer.DrawText(graphics, item.Name, Font, new Rectangle(textX, top, textWidth, nameHeight), Theme.Text, flags);
            TextRenderer.DrawText(graphics, item.Subtitle, Theme.Small, new Rectangle(textX, top + nameHeight, textWidth, subHeight),
                Theme.Muted, flags);

            DrawBrightnessButtons(graphics, item, row);
        }

        private void DrawBrightnessButtons(Graphics graphics, GameItem item, Rectangle row)
        {
            RectangleF minusRect = BrightnessMinusRect(row);
            RectangleF textRect = BrightnessTextRect(row);
            RectangleF plusRect = BrightnessPlusRect(row);

            TextRenderer.DrawText(graphics, $"亮度: {item.Brightness}", Theme.Semibold, Rectangle.Round(textRect),
                item.Tracked ? Theme.Text : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            StepperButtons.Draw(graphics, this, minusRect, "\u2212", _minusDown, _minusHover);
            StepperButtons.Draw(graphics, this, plusRect, "+", _plusDown, _plusHover);
        }

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
                Bitmap? bitmap = null;
                try
                {
                    if (System.IO.File.Exists(file))
                    {
                        using var icon = Icon.ExtractAssociatedIcon(file);
                        if (icon != null) bitmap = icon.ToBitmap();
                    }
                }
                catch { }

                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) { bitmap?.Dispose(); return; }
                        _icons[file] = bitmap;
                        _iconsLoading.Remove(file);
                        Invalidate();
                    }));
                }
                catch
                {
                    bitmap?.Dispose();
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
        private static void DrawLetterIcon(Graphics graphics, RectangleF rect, string name)
        {
            int hash = 0;
            foreach (char c in name) hash = hash * 31 + c;

            Color color = LetterColors[(hash & 0x7fffffff) % LetterColors.Length];
            using (var path = Theme.RoundRect(rect, rect.Height * 0.24f))
            using (var brush = new SolidBrush(color))
                graphics.FillPath(brush, path);

            string letter = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
            TextRenderer.DrawText(graphics, letter, Theme.Semibold, Rectangle.Round(rect), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animation.Dispose();
                ClearRowCache();
                foreach (var bitmap in _icons.Values) bitmap?.Dispose();
                _icons.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
