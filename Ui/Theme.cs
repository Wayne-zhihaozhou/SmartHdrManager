using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HdrBrightControl
{
    /// <summary>Dark, Windows 11 style colours, fonts and helpers shared by all windows and custom controls.</summary>
    public static class Theme
    {
        public static readonly Color Back = Color.FromArgb(28, 28, 30);
        public static readonly Color Surface = Color.FromArgb(40, 40, 43);
        public static readonly Color SurfaceHover = Color.FromArgb(48, 48, 52);
        public static readonly Color Input = Color.FromArgb(33, 33, 36);
        public static readonly Color ButtonBack = Color.FromArgb(55, 55, 60);
        public static readonly Color ButtonHover = Color.FromArgb(66, 66, 72);
        public static readonly Color ButtonPressed = Color.FromArgb(78, 78, 85);
        public static readonly Color Border = Color.FromArgb(62, 62, 68);
        public static readonly Color Text = Color.FromArgb(243, 243, 243);
        public static readonly Color Muted = Color.FromArgb(158, 158, 165);
        public static readonly Color Accent = Color.FromArgb(96, 205, 255);
        public static readonly Color AccentHover = Color.FromArgb(128, 215, 255);
        public static readonly Color AccentPressed = Color.FromArgb(70, 176, 224);
        public static readonly Color OnAccent = Color.FromArgb(10, 22, 30);

        private static readonly HashSet<string> InstalledFamilies = LoadFamilies();

        public static readonly Font Body = new(Pick("Segoe UI Variable Text", "Segoe UI"), 9.75f);
        public static readonly Font Small = new(Pick("Segoe UI Variable Small", "Segoe UI"), 9f);
        public static readonly Font Semibold = SemiboldFont(Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold"), 10f);
        public static readonly Font Section = SemiboldFont(Pick("Segoe UI Variable Display Semibold", "Segoe UI Semibold"), 11.5f);
        public static readonly Font Mono = new(Pick("Cascadia Mono", "Consolas"), 9f);
        public static readonly Font IconFont = new(Pick("Segoe Fluent Icons", "Segoe MDL2 Assets"), 10f);

        public const string GlyphAdd = "\uE710";
        public const string GlyphSearch = "\uE721";
        public const string GlyphRefresh = "\uE72C";
        public const string GlyphDocument = "\uE8A5";
        public const string GlyphFolder = "\uE8B7";
        public const string GlyphLink = "\uE71B";
        public const string GlyphDelete = "\uE74D";

        private static HashSet<string> LoadFamilies()
        {
            try
            {
                using var fonts = new InstalledFontCollection();
                return new HashSet<string>(fonts.Families.Select(family => family.Name), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string Pick(params string[] families)
        {
            foreach (string family in families)
                if (InstalledFamilies.Contains(family)) return family;
            return "Segoe UI";
        }

        /// <summary>
        /// "Segoe UI Semibold" / "... Variable ... Semibold" are their own families; plain Segoe UI needs Bold instead.
        /// </summary>
        private static Font SemiboldFont(string family, float size)
        {
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

        public static GraphicsPath RoundRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
            if (diameter <= 0.5f)
            {
                path.AddRectangle(rect);
                return path;
            }

            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

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

        public static void Apply(Control control)
        {
            switch (control)
            {
                case Button button:
                    StyleButton(button);
                    return;

                case TextBox textBox:
                    textBox.BackColor = Input;
                    textBox.ForeColor = Text;
                    textBox.BorderStyle = textBox.Multiline ? BorderStyle.None : BorderStyle.FixedSingle;
                    UseDarkScrollbars(textBox);
                    return;

                case ListBox listBox:
                    listBox.BackColor = Input;
                    listBox.ForeColor = Text;
                    listBox.BorderStyle = BorderStyle.None;
                    UseDarkScrollbars(listBox);
                    return;
            }

            foreach (Control child in control.Controls)
                Apply(child);
        }

        public static void StyleButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.BackColor = ButtonBack;
            button.ForeColor = Text;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = ButtonHover;
            button.FlatAppearance.MouseDownBackColor = ButtonPressed;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

        private const int DwmUseImmersiveDarkModeOld = 19;
        private const int DwmUseImmersiveDarkMode = 20;
        private const int DwmWindowCornerPreference = 33;
        private const int DwmBorderColor = 34;
        private const int DwmCaptionColor = 35;
        private const int DwmWindowCornerRound = 2;

        /// <summary>Dark title bar; on Windows 11 also rounded corners and a title bar in the window colour.</summary>
        public static void StyleWindow(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeOld, ref on, sizeof(int));

                int round = DwmWindowCornerRound;
                DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref round, sizeof(int));

                int caption = ToColorRef(Back);
                DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));

                int border = ToColorRef(Border);
                DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
            }
            catch { }
        }

        private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

        public static void UseDarkScrollbars(Control control)
        {
            if (control.IsHandleCreated)
                SetDarkExplorerTheme(control);
            control.HandleCreated += (_, _) => SetDarkExplorerTheme(control);
        }

        private static void SetDarkExplorerTheme(Control control)
        {
            try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); } catch { }
        }
    }

    public enum IconStyle { Vivid, Gray, Amber }

    /// <summary>
    /// App and tray icons drawn at runtime: a rounded square with a high-dynamic-range gradient and a half-filled
    /// "sun" (left half outlined = dark, right half solid = bright). Vivid = HDR on / app icon, Gray = SDR,
    /// Amber = paused. The same design is in app.ico (the .exe icon).
    /// </summary>
    public static class AppIcons
    {
        private const float CornerFactor = 0.26f;
        private const float RayInnerFactor = 1.45f;
        private const float RayOuterFactor = 1.95f;
        private const int LargeIconSize = 24;

        private static readonly float[] RayAngles = { -60f, -30f, 0f, 30f, 60f };

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private static Color[] Palette(IconStyle style) => style switch
        {
            IconStyle.Vivid => new[] { Color.FromArgb(58, 28, 113), Color.FromArgb(215, 109, 119), Color.FromArgb(255, 175, 123) },
            IconStyle.Amber => new[] { Color.FromArgb(122, 66, 0), Color.FromArgb(214, 138, 18), Color.FromArgb(255, 204, 92) },
            _ => new[] { Color.FromArgb(62, 62, 68), Color.FromArgb(98, 98, 106), Color.FromArgb(140, 140, 148) }
        };

        public static Bitmap Render(IconStyle style, int size)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            float padding = size >= 32 ? size * 0.04f : 0.5f;
            var box = new RectangleF(padding, padding, size - 2 * padding, size - 2 * padding);
            Color[] colors = Palette(style);

            using (var path = Theme.RoundRect(box, size * CornerFactor))
            using (var background = new LinearGradientBrush(new RectangleF(0, 0, size, size), colors[0], colors[2], 45f))
            {
                background.InterpolationColors = new ColorBlend { Colors = colors, Positions = new[] { 0f, 0.5f, 1f } };
                graphics.FillPath(background, path);
            }

            float centerX = size / 2f - (size >= LargeIconSize ? size * 0.07f : 0f);
            float centerY = size / 2f;
            float radius = size * (size >= LargeIconSize ? 0.2f : 0.25f);
            var sun = new RectangleF(centerX - radius, centerY - radius, radius * 2f, radius * 2f);

            using (var white = new SolidBrush(Color.White))
                graphics.FillPie(white, sun.X, sun.Y, sun.Width, sun.Height, -90f, 180f);

            using (var ring = new Pen(Color.White, Math.Max(1.2f, size * 0.065f)))
                graphics.DrawEllipse(ring, sun);

            if (size >= LargeIconSize)
            {
                using var ray = new Pen(Color.White, Math.Max(1.5f, size * 0.055f))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };

                foreach (float degrees in RayAngles)
                {
                    double angle = degrees * Math.PI / 180.0;
                    float cos = (float)Math.Cos(angle);
                    float sin = (float)Math.Sin(angle);
                    graphics.DrawLine(ray,
                        centerX + cos * radius * RayInnerFactor, centerY + sin * radius * RayInnerFactor,
                        centerX + cos * radius * RayOuterFactor, centerY + sin * radius * RayOuterFactor);
                }
            }
            return bitmap;
        }

        public static Icon Create(IconStyle style, int size)
        {
            using var bitmap = Render(style, size);
            IntPtr handle = bitmap.GetHicon();
            try
            {
                using var temporary = Icon.FromHandle(handle);
                return (Icon)temporary.Clone();
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
            Rectangle bounds = e.ImageRectangle;
            using (var brush = new SolidBrush(Theme.ButtonPressed))
                e.Graphics.FillRectangle(brush, bounds);

            TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, bounds, Theme.Text,
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
