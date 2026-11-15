using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Mizar;

/// <summary>Shared palette and drawing helpers (Windows 11 style). Follows the Windows light/dark app setting.</summary>
static class Theme
{
    /// <summary>True when Windows "app mode" is dark. Call <see cref="Refresh"/> to re-read it.</summary>
    public static bool Dark { get; private set; } = ReadSystemDark();

    /// <summary>Re-reads the Windows setting; returns true if it changed.</summary>
    public static bool Refresh()
    {
        bool d = ReadSystemDark();
        if (d == Dark) return false;
        Dark = d;
        return true;
    }

    static bool ReadSystemDark()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);

    public static Color Strip => Dark ? C(19, 19, 22) : C(238, 241, 246);
    public static Color Surface => Dark ? C(19, 19, 22) : Color.White;
    public static Color Menu => Dark ? C(26, 26, 30) : Color.White;
    public static Color HoverFill => Dark ? C(38, 38, 45) : C(232, 235, 241);
    public static Color PressFill => Dark ? C(52, 52, 60) : C(220, 224, 232);
    public static Color TabHover => Dark ? C(27, 27, 32) : C(226, 230, 237);
    public static Color TabActive => Dark ? C(33, 33, 39) : Color.White;
    public static Color CaptionCircle => Dark ? C(31, 31, 37) : C(224, 228, 236);
    public static Color CaptionCircleHover => Dark ? C(48, 48, 56) : C(206, 211, 222);
    public static Color Separator => Dark ? C(48, 48, 56) : C(200, 204, 212);
    public static Color Text => Dark ? C(236, 236, 240) : C(28, 28, 32);
    public static Color Muted => Dark ? C(168, 170, 178) : C(96, 100, 110);
    public static Color Disabled => Dark ? C(98, 98, 106) : C(185, 188, 196);
    public static Color Border => Dark ? C(38, 38, 45) : C(226, 229, 235);
    public static Color Field => Dark ? C(33, 33, 39) : C(240, 242, 246);
    public static Color FieldFocused => Dark ? C(38, 38, 45) : Color.White;
    public static Color FieldBorder => Dark ? C(52, 52, 62) : C(218, 222, 230);
    public static Color Accent => Dark ? C(150, 136, 255) : C(88, 70, 220);
    public static readonly Color CloseRed = Color.FromArgb(196, 43, 28);
    public static Color Warn => Dark ? C(255, 138, 120) : C(196, 43, 28);

    /// <summary>Dark/light title bar, borders and scrollbars for a top-level window.</summary>
    public static void ApplyToWindow(Form f)
    {
        if (!f.IsHandleCreated) return;
        int v = Dark ? 1 : 0;
        try { Native.DwmSetWindowAttribute(f.Handle, 20, ref v, sizeof(int)); } catch { }
    }

    /// <summary>Recolors a dialog and all its children (forms, text boxes, buttons, trees...).</summary>
    public static void ApplyToDialog(Control root)
    {
        void Walk(Control c)
        {
            switch (c)
            {
                case Form f:
                    f.BackColor = Surface; f.ForeColor = Text;
                    break;
                case TextBox t:
                    t.BackColor = Field; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = Field; b.ForeColor = Text;
                    b.FlatAppearance.BorderColor = Border;
                    b.FlatAppearance.MouseOverBackColor = HoverFill;
                    b.FlatAppearance.MouseDownBackColor = PressFill;
                    break;
                case TreeView tv:
                    tv.BackColor = Surface; tv.ForeColor = Text; tv.BorderStyle = BorderStyle.None; tv.LineColor = Muted;
                    if (tv.IsHandleCreated) Native.SetWindowTheme(tv.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
                    else tv.HandleCreated += (_, _) => Native.SetWindowTheme(tv.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
                    break;
                case Label l:
                    l.ForeColor = l.Tag as string == "muted" ? Muted : Text;
                    break;
                default:
                    if (c is not RoundButton) { c.BackColor = Surface; c.ForeColor = Text; }
                    break;
            }
            foreach (Control ch in c.Controls) Walk(ch);
        }
        Walk(root);
        if (root is Form form)
        {
            if (form.IsHandleCreated) ApplyToWindow(form);
            else form.HandleCreated += (_, _) => ApplyToWindow(form);
        }
    }

    public static readonly string IconFont =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Icon fonts are reused: creating a Font on every paint (tabs repaint 20x/s while loading) is wasteful.
    static readonly Dictionary<float, Font> GlyphFonts = new();

    public static void DrawGlyph(Graphics g, string glyph, Rectangle r, Color c, float size)
    {
        if (!GlyphFonts.TryGetValue(size, out var f)) GlyphFonts[size] = f = new Font(IconFont, size);
        TextRenderer.DrawText(g, glyph, f, r, c,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// Layout panel that lets hit-testing fall through to the form where <see cref="PassThrough"/> says so,
/// so the empty tab-strip area works as a real title bar (drag, snap, double-click, top-edge resize).
/// </summary>
public class HitTestPanel : TableLayoutPanel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Point, bool>? PassThrough { get; set; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_NCHITTEST && PassThrough != null)
        {
            var screen = new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
            if (PassThrough(PointToClient(screen)))
            {
                m.Result = (IntPtr)Native.HTTRANSPARENT;
                return;
            }
        }
        base.WndProc(ref m);
    }
}

/// <summary>Flat icon button with rounded hover background.</summary>
public class RoundButton : Control
{
    string glyph = "";
    bool hover, down;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float GlyphSize { get; set; } = 11f;
    /// <summary>Glyph color; null follows the theme text color.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? GlyphColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? HoverBack { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get => glyph; set { glyph = value; Invalidate(); } }

    public RoundButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Default;
        BackColor = Color.Transparent;
    }

    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Enabled && (hover || down))
        {
            float k = DeviceDpi / 96f;
            using var path = Theme.Round(new RectangleF(1, 1, Width - 2, Height - 2), 6 * k);
            using var b = new SolidBrush(down ? Theme.PressFill : HoverBack ?? Theme.HoverFill);
            g.FillPath(b, path);
        }
        Theme.DrawGlyph(g, glyph, ClientRectangle, Enabled ? GlyphColor ?? Theme.Text : Theme.Disabled, GlyphSize);
    }
}

/// <summary>Small rounded text button (e.g. the "120%" zoom indicator).</summary>
public class TextChip : Control
{
    bool hover, down;
    static readonly Font ChipFont = new("Segoe UI Semibold", 9f);

    public TextChip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        int k(int v) => (int)Math.Round(v * DeviceDpi / 96f);
        Width = TextRenderer.MeasureText(Text, ChipFont).Width + k(18);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 3, Width - 2, Height - 6);
        using var path = Theme.Round(r, r.Height / 2);
        using var b = new SolidBrush(down ? Theme.PressFill : hover ? Theme.HoverFill : Theme.Field);
        g.FillPath(b, path);
        TextRenderer.DrawText(g, Text, ChipFont, Rectangle.Round(r), Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Rounded "pill" address field: a borderless TextBox inside a painted panel.</summary>
public class AddressBox : Control
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None, BackColor = Theme.Field, ForeColor = Theme.Text };
    bool focused;

    public AddressBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        Box.Font = new Font("Segoe UI", 10f);
        Controls.Add(Box);
        Box.GotFocus += (_, _) => { focused = true; ApplyTheme(); };
        Box.LostFocus += (_, _) => { focused = false; ApplyTheme(); };
        Cursor = Cursors.IBeam;
        MouseDown += (_, _) => Box.Focus();
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Surface;
        Box.BackColor = focused ? Theme.FieldFocused : Theme.Field;
        Box.ForeColor = Theme.Text;
        Invalidate();
    }

    // ---- security indicator (padlock / "Nesigur") at the left of the field ----

    SecurityLevel security;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SecurityLevel Security
    {
        get => security;
        set { if (security == value) return; security = value; PerformLayout(); Invalidate(); }
    }

    int P(float v) => (int)Math.Round(v * DeviceDpi / 96f);
    static readonly Font ChipFont = new("Segoe UI Semibold", 9f);

    string? ChipText => security switch
    {
        SecurityLevel.Insecure => "Nesigur",
        SecurityLevel.CertError => "Certificat invalid",
        _ => null
    };

    string? ChipGlyph => security switch
    {
        SecurityLevel.Secure => "\uE72E",      // lock
        SecurityLevel.Insecure => "\uE7BA",    // warning
        SecurityLevel.CertError => "\uE7BA",
        SecurityLevel.Internal => "\uE81C",    // history clock
        _ => null
    };

    /// <summary>Area of the indicator (also where hovering shows the tooltip).</summary>
    public Rectangle ChipRect
    {
        get
        {
            if (ChipGlyph == null) return Rectangle.Empty;
            int w = P(30);
            if (ChipText != null) w += TextRenderer.MeasureText(ChipText, ChipFont).Width + P(6);
            return new Rectangle(P(6), P(3), w, Height - P(6));
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = P(16);
        var chip = ChipRect;
        int left = chip.IsEmpty ? pad : chip.Right + P(8);
        Box.Width = Math.Max(10, Width - left - pad);
        Box.Left = left;
        Box.Top = (Height - Box.Height) / 2;
    }

    void PaintChip(Graphics g)
    {
        var glyph = ChipGlyph;
        if (glyph == null) return;
        var chip = ChipRect;
        bool warn = security is SecurityLevel.Insecure or SecurityLevel.CertError;
        var color = warn ? Theme.Warn : Theme.Muted;
        if (warn)
        {
            using var bg = new SolidBrush(Color.FromArgb(Theme.Dark ? 50 : 28, Theme.Warn));
            using var cp = Theme.Round(chip, chip.Height / 2f);
            g.FillPath(bg, cp);
        }
        var iconRect = new Rectangle(chip.Left + P(4), chip.Top, P(22), chip.Height);
        Theme.DrawGlyph(g, glyph, iconRect, color, 9.5f);
        if (ChipText != null)
            TextRenderer.DrawText(g, ChipText, ChipFont, new Rectangle(iconRect.Right, chip.Top, chip.Right - iconRect.Right, chip.Height),
                color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        using var path = Theme.Round(r, r.Height / 2);
        using var b = new SolidBrush(focused ? Theme.FieldFocused : Theme.Field);
        g.FillPath(b, path);
        if (focused)
        {
            using var pen = new Pen(Theme.Accent, Math.Max(1.5f, 2 * DeviceDpi / 96f));
            g.DrawPath(pen, path);
        }
        else if (Theme.Dark)
        {
            using var pen = new Pen(Theme.FieldBorder, 1f);
            g.DrawPath(pen, path);
        }
        PaintChip(g);
    }
}

public enum SecurityLevel { None, Secure, Insecure, CertError, Internal }

/// <summary>Menus with rounded highlights, generous padding and a light border.</summary>
public class ModernRenderer : ToolStripProfessionalRenderer
{
    public bool BarLine { get; set; }

    public ModernRenderer() : base(new Colors()) { RoundedEdges = false; }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Menu;
        public override Color ImageMarginGradientBegin => Theme.Menu;
        public override Color ImageMarginGradientMiddle => Theme.Menu;
        public override Color ImageMarginGradientEnd => Theme.Menu;
        public override Color MenuBorder => Theme.Border;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Menu;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown) { e.Graphics.Clear(Theme.Menu); return; }
        e.Graphics.Clear(Theme.Surface);
        if (BarLine)
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, e.AffectedBounds.Bottom - 1, e.AffectedBounds.Right, e.AffectedBounds.Bottom - 1);
        }
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown) return;
        using var pen = new Pen(Theme.Border);
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
    }

    void Highlight(Graphics g, Rectangle r, bool pressed)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Round(r, 5);
        using var b = new SolidBrush(pressed ? Theme.PressFill : Theme.HoverFill);
        g.FillPath(b, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        Highlight(e.Graphics, new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2), e.Item.Pressed);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed) return;
        Highlight(e.Graphics, new Rectangle(0, 1, e.Item.Width, e.Item.Height - 2), e.Item.Pressed);
    }

    protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed) return;
        Highlight(e.Graphics, new Rectangle(0, 1, e.Item.Width, e.Item.Height - 2), e.Item.Pressed);
    }

    protected override void OnRenderOverflowButtonBackground(ToolStripItemRenderEventArgs e)
    {
        var r = new Rectangle(Point.Empty, e.Item.Size);
        using (var bg = new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(bg, r);
        if (e.Item.Selected || e.Item.Pressed) Highlight(e.Graphics, new Rectangle(0, 1, r.Width, r.Height - 2), e.Item.Pressed);
        Theme.DrawGlyph(e.Graphics, "\uE70D", r, Theme.Muted, 8f); // chevron down
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Border);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Disabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Muted;
        base.OnRenderArrow(e);
    }
}

static class Native
{
    public const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084;
    public const int HTCLIENT = 1, HTCAPTION = 2, HTTRANSPARENT = -1, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct NCCALCSIZE_PARAMS { public RECT r0, r1, r2; public IntPtr lppos; }

    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr h, string? app, string? idList);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    /// <summary>Work area (screen minus taskbar) of the monitor the window is on.</summary>
    public static RECT? WorkArea(IntPtr hwnd)
    {
        var mon = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(mon, ref mi) ? mi.rcWork : null;
    }

    public static void RoundCorners(IntPtr h)
    {
        int pref = 2; // DWMWCP_ROUND
        try { DwmSetWindowAttribute(h, 33, ref pref, sizeof(int)); } catch { }
    }
}
