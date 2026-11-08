using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// Dark, Windows 11 style colour scheme, fonts and helpers shared by all windows and custom controls.
    /// </summary>
    public static class Theme
    {
        // ---- colours ----
        public static readonly Color Back = Color.FromArgb(28, 28, 30);          // window background
        public static readonly Color Surface = Color.FromArgb(40, 40, 43);       // cards
        public static readonly Color SurfaceHover = Color.FromArgb(48, 48, 52);
        public static readonly Color Input = Color.FromArgb(33, 33, 36);         // text boxes, list rows
        public static readonly Color ButtonBack = Color.FromArgb(55, 55, 60);
        public static readonly Color ButtonHover = Color.FromArgb(66, 66, 72);
        public static readonly Color ButtonPressed = Color.FromArgb(78, 78, 85);
        public static readonly Color Border = Color.FromArgb(62, 62, 68);
        public static readonly Color Text = Color.FromArgb(243, 243, 243);
        public static readonly Color Muted = Color.FromArgb(158, 158, 165);
        public static readonly Color Accent = Color.FromArgb(96, 205, 255);      // Windows 11 dark-mode accent
        public static readonly Color AccentHover = Color.FromArgb(128, 215, 255);
        public static readonly Color AccentPressed = Color.FromArgb(70, 176, 224);
        public static readonly Color OnAccent = Color.FromArgb(10, 22, 30);      // text on accent buttons
        public static readonly Color Good = Color.FromArgb(108, 203, 95);        // HDR on
        public static readonly Color Warn = Color.FromArgb(252, 185, 0);         // paused
        public static readonly Color Idle = Color.FromArgb(130, 130, 138);       // HDR off

        // ---- fonts (Windows 11 "Segoe UI Variable" when available, otherwise Segoe UI) ----
        private static readonly HashSet<string> InstalledFamilies = LoadFamilies();

        public static readonly Font Body = new(Pick("Segoe UI Variable Text", "Segoe UI"), 9.75f);
        public static readonly Font Small = new(Pick("Segoe UI Variable Small", "Segoe UI"), 9f);
        public static readonly Font Semibold = SemiboldFont(Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold"), 10f);
        public static readonly Font Title = SemiboldFont(Pick("Segoe UI Variable Display Semibold", "Segoe UI Semibold"), 20f);
        public static readonly Font Section = SemiboldFont(Pick("Segoe UI Variable Display Semibold", "Segoe UI Semibold"), 11.5f);
        public static readonly Font Mono = new(Pick("Cascadia Mono", "Consolas"), 9f);
        public static readonly Font IconFont = new(Pick("Segoe Fluent Icons", "Segoe MDL2 Assets"), 10f);

        // Icon glyphs (same code points in Segoe Fluent Icons and Segoe MDL2 Assets)
        public const string GlyphAdd = "";
        public const string GlyphCancel = "";
        public const string GlyphSearch = "";
        public const string GlyphRefresh = "";
        public const string GlyphPause = "";
        public const string GlyphPlay = "";
        public const string GlyphMinimize = "";
        public const string GlyphDocument = "";
        public const string GlyphFolder = "";
        public const string GlyphSettings = "\uE713";

        private static HashSet<string> LoadFamilies()
        {
            try
            {
                using var fonts = new InstalledFontCollection();
                return new HashSet<string>(fonts.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string Pick(params string[] families)
        {
            foreach (var f in families)
                if (InstalledFamilies.Contains(f)) return f;
            return "Segoe UI";
        }

        private static Font SemiboldFont(string family, float size)
        {
            // "Segoe UI Semibold" / "... Variable ... Semibold" are their own families; plain Segoe UI needs Bold instead
            bool isSemiboldFamily = family.EndsWith("Semibold", StringComparison.OrdinalIgnoreCase);
            return new Font(family, size, isSemiboldFamily ? FontStyle.Regular : FontStyle.Bold);
        }

        public static Color Blend(Color a, Color b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ---- standard controls (used by the small "Add running program" dialog) ----
        public static void ApplyForm(Form form)
        {
            form.BackColor = Back;
            form.ForeColor = Text;
            form.Font = Body;
            Apply(form);

            if (form.IsHandleCreated)
                StyleWindow(form.Handle);
            else
                form.HandleCreated += (_, _) => StyleWindow(form.Handle);
        }

        public static void Apply(Control c)
        {
            switch (c)
            {
                case Button b:
                    StyleButton(b);
                    return;

                case TextBox t:
                    t.BackColor = Input;
                    t.ForeColor = Text;
                    t.BorderStyle = t.Multiline ? BorderStyle.None : BorderStyle.FixedSingle;
                    UseDarkScrollbars(t);
                    return;

                case ListBox l:
                    l.BackColor = Input;
                    l.ForeColor = Text;
                    l.BorderStyle = BorderStyle.None;
                    UseDarkScrollbars(l);
                    return;
            }

            foreach (Control child in c.Controls)
                Apply(child);
        }

        public static void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.BackColor = ButtonBack;
            b.ForeColor = Text;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = ButtonHover;
            b.FlatAppearance.MouseDownBackColor = ButtonPressed;
        }

        // ---- window chrome (DWM) ----
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;   // older Windows 10 builds
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;       // Windows 10 20H1+ / Windows 11
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;      // Windows 11
        private const int DWMWA_BORDER_COLOR = 34;                  // Windows 11
        private const int DWMWA_CAPTION_COLOR = 35;                 // Windows 11
        private const int DWMWCP_ROUND = 2;

        /// <summary>Dark title bar; on Windows 11 also rounded corners and a title bar in the window colour.</summary>
        public static void StyleWindow(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));

                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

                int caption = ToColorRef(Back);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));

                int border = ToColorRef(Border);
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            catch { }
        }

        private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        public static void UseDarkScrollbars(Control c)
        {
            if (c.IsHandleCreated)
                SetDarkExplorerTheme(c);
            c.HandleCreated += (_, _) => SetDarkExplorerTheme(c);
        }

        private static void SetDarkExplorerTheme(Control c)
        {
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }
    }

    public enum IconStyle { Vivid, Gray, Amber }

    /// <summary>
    /// App and tray icons drawn at runtime: a rounded square with a high-dynamic-range gradient and a
    /// half-filled "sun" (left half outlined = dark, right half solid = bright). Vivid = HDR on / app icon,
    /// Gray = SDR, Amber = paused. The same design is in app.ico (the .exe icon).
    /// </summary>
    public static class AppIcons
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

        private static Color[] Palette(IconStyle style) => style switch
        {
            IconStyle.Vivid => new[] { Color.FromArgb(58, 28, 113), Color.FromArgb(215, 109, 119), Color.FromArgb(255, 175, 123) },
            IconStyle.Amber => new[] { Color.FromArgb(122, 66, 0), Color.FromArgb(214, 138, 18), Color.FromArgb(255, 204, 92) },
            _ => new[] { Color.FromArgb(62, 62, 68), Color.FromArgb(98, 98, 106), Color.FromArgb(140, 140, 148) }
        };

        public static Bitmap Render(IconStyle style, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // Background: rounded square with a diagonal gradient
            float pad = size >= 32 ? size * 0.04f : 0.5f;
            var box = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
            Color[] colors = Palette(style);
            using (var path = Theme.RoundRect(box, size * 0.26f))
            using (var bg = new LinearGradientBrush(new RectangleF(0, 0, size, size), colors[0], colors[2], 45f))
            {
                bg.InterpolationColors = new ColorBlend { Colors = colors, Positions = new[] { 0f, 0.5f, 1f } };
                g.FillPath(bg, path);
            }

            // Sun: right half filled (bright), whole circle outlined (the left half reads as "dark")
            // Shifted a little to the left when rays are drawn, so sun + rays are centred as a whole
            float cx = size / 2f - (size >= 24 ? size * 0.07f : 0f);
            float cy = size / 2f;
            float r = size * (size >= 24 ? 0.2f : 0.25f);
            var circle = new RectangleF(cx - r, cy - r, r * 2f, r * 2f);

            using (var white = new SolidBrush(Color.White))
                g.FillPie(white, circle.X, circle.Y, circle.Width, circle.Height, -90f, 180f);

            using (var ring = new Pen(Color.White, Math.Max(1.2f, size * 0.065f)))
                g.DrawEllipse(ring, circle);

            // Rays on the bright side (only where there is room for them)
            if (size >= 24)
            {
                using var ray = new Pen(Color.White, Math.Max(1.5f, size * 0.055f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                foreach (float deg in new[] { -60f, -30f, 0f, 30f, 60f })
                {
                    double a = deg * Math.PI / 180.0;
                    float c = (float)Math.Cos(a);
                    float sn = (float)Math.Sin(a);
                    g.DrawLine(ray, cx + c * r * 1.45f, cy + sn * r * 1.45f, cx + c * r * 1.95f, cy + sn * r * 1.95f);
                }
            }
            return bmp;
        }

        public static Icon Create(IconStyle style, int size)
        {
            using var bmp = Render(style, size);
            IntPtr handle = bmp.GetHicon();
            try
            {
                using var temp = System.Drawing.Icon.FromHandle(handle);
                return (Icon)temp.Clone();   // owned copy, so the GDI handle can be released right away
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
    }

    /// <summary>Dark renderer for the tray icon's right-click menu.</summary>
    public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            using (var brush = new SolidBrush(Theme.ButtonPressed))
                e.Graphics.FillRectangle(brush, r);
            TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, r, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.ButtonHover;
        public override Color MenuItemSelected => Theme.ButtonHover;
        public override Color MenuItemSelectedGradientBegin => Theme.ButtonHover;
        public override Color MenuItemSelectedGradientEnd => Theme.ButtonHover;
        public override Color MenuItemPressedGradientBegin => Theme.ButtonPressed;
        public override Color MenuItemPressedGradientEnd => Theme.ButtonPressed;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color CheckBackground => Theme.ButtonPressed;
        public override Color CheckSelectedBackground => Theme.ButtonPressed;
        public override Color CheckPressedBackground => Theme.ButtonPressed;
    }
}
