using System.ComponentModel;
using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.WinForms;

namespace Mizar;

public class BrowserTab
{
    public WebView2 View { get; } = new() { Dock = DockStyle.Fill, Visible = false };
    public string Title { get; set; } = "Tab nou";
    public string Url { get; set; } = "";
    public Image? Favicon { get; private set; }
    bool ownsFavicon;
    public bool IsLoading { get; set; } = true;
    public DateTime LastActive { get; set; } = DateTime.Now;
    public DateTime LoadingStartedUtc { get; set; }
    /// <summary>Requests still loading (tracked only while the tab is in the background, see NetRepair.cs).</summary>
    public Dictionary<string, DateTime> InFlight { get; } = new();
    public bool NetWatch { get; set; }
    /// <summary>CDP event receivers must stay referenced, or their events stop after a GC.</summary>
    public List<object> Receivers { get; } = new();
    /// <summary>"history" while the tab shows a built-in Mizar page; null for normal web pages.</summary>
    public string? Internal => Url.StartsWith(InternalPages.History, StringComparison.OrdinalIgnoreCase) ? "history" : null;
    /// <summary>
    /// Set on a tab restored from the last session that hasn't been opened yet: the page (and its
    /// engine process) is only created when the tab is first shown. Null once loading has started.
    /// </summary>
    public string? PendingUrl { get; set; }
    /// <summary>Recent renderer crashes, to stop reloading a page that keeps crashing.</summary>
    public List<DateTime> Crashes { get; } = new();

    /// <summary>
    /// Sets the tab icon. <paramref name="owned"/> = the image belongs to this tab and is disposed
    /// when replaced or when the tab closes (shared images from the favicon cache are not).
    /// </summary>
    public void SetFavicon(Image? image, bool owned)
    {
        var old = Favicon;
        bool oldOwned = ownsFavicon;
        Favicon = image;
        ownsFavicon = owned && image != null;
        if (oldOwned && old != null && !ReferenceEquals(old, image)) old.Dispose();
    }
    /// <summary>The current page had an HTTPS certificate problem.</summary>
    public bool CertError { get; set; }
    /// <summary>Site whose remembered zoom is applied to this tab ("" for none).</summary>
    public string? ZoomHost { get; set; }
    /// <summary>Zoom Mizar itself last set; a different value in ZoomFactorChanged means the user zoomed.</summary>
    public double ExpectedZoom { get; set; } = 1.0;
    public Microsoft.Web.WebView2.Core.CoreWebView2? Core => View.IsDisposed ? null : View.CoreWebView2;
}

/// <summary>Owner-drawn tab strip that doubles as the window title bar (tabs + window buttons).</summary>
public class TabStrip : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<BrowserTab> Tabs { get; set; } = new();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex { get; set; } = -1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Maximized { get; set; }

    public event Action<int>? TabClicked;
    public event Action<int>? TabCloseClicked;
    public event Action? NewTabClicked;
    public event Action<int, Point>? TabRightClicked;
    public event Action? MinimizeClicked, MaximizeClicked, CloseClicked;
    public event Action<Point>? EmptyAreaPressed;

    // hover targets: tab index >= 0, or special codes below
    const int HPlus = -10, HMin = -11, HMax = -12, HClose = -13;
    int hover = -1;
    bool hoverTabClose;
    int pressed = int.MinValue;
    float spin;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 50 };

    const string GClose = "\uE8BB";

    public TabStrip()
    {
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Strip;
        Font = new Font("Segoe UI", 9f);
        timer.Tick += (_, _) =>
        {
            if (Tabs.Any(t => t.IsLoading)) { spin = (spin + 30) % 360; Invalidate(); }
            else timer.Stop();
        };
    }

    public void Refresh2()
    {
        if (Tabs.Any(t => t.IsLoading) && !timer.Enabled) timer.Start();
        Invalidate();
    }

    float K => DeviceDpi / 96f;
    int P(float v) => (int)Math.Round(v * K);

    int BtnSize => P(26);
    int BtnGap => P(7);
    int ButtonsLeft => Width - P(5) - BtnSize * 3 - BtnGap * 2;
    int TabH => P(28);
    int TabTop => (Height - TabH) / 2;
    int TabGap => P(4);

    Rectangle CaptionRect(int which) =>
        new(ButtonsLeft + which * (BtnSize + BtnGap), (Height - BtnSize) / 2, BtnSize, BtnSize);

    Rectangle CloseHitRect =>
        new(CaptionRect(2).Left - P(5), 0, Width - (CaptionRect(2).Left - P(5)), Height);

    Rectangle CaptionHitRect(int which) => which == 2
        ? CloseHitRect
        : new(CaptionRect(which).Left - P(2), Maximized ? 0 : P(4), BtnSize + P(4), Height - (Maximized ? 0 : P(4)));

    /// <summary>Widest a tab gets (logical px) when there is room; tabs shrink below this as more open.</summary>
    const int MaxTabWidth = 190;

    int TabWidth()
    {
        int n = Math.Max(1, Tabs.Count);
        int avail = ButtonsLeft - P(8) - P(34) - P(36) - (n - 1) * TabGap; // leave room for "+" and a drag area
        return Math.Max(P(44), Math.Min(P(MaxTabWidth), avail / n));
    }

    Rectangle TabRect(int i)
    {
        int w = TabWidth();
        return new Rectangle(P(6) + i * (w + TabGap), TabTop, w, TabH);
    }

    Rectangle TabCloseRect(int i)
    {
        var r = TabRect(i);
        int s = P(18);
        return new Rectangle(r.Right - s - P(6), r.Top + (r.Height - s) / 2, s, s);
    }

    Rectangle PlusRect()
    {
        int x = Tabs.Count == 0 ? P(6) : TabRect(Tabs.Count - 1).Right + P(4);
        int s = P(26);
        return new Rectangle(x, (Height - s) / 2, s, s);
    }

    bool ShowClose(int i) => i == SelectedIndex || TabWidth() > P(96);

    /// <summary>Whether a point is an interactive element (otherwise the window can be dragged from it).</summary>
    public bool IsInteractive(Point p)
    {
        if (CloseHitRect.Contains(p)) return true; // top-right corner is always Close
        if (p.Y < P(4) && !Maximized) return false; // let the top edge resize the window
        if (CaptionHitRect(0).Contains(p) || CaptionHitRect(1).Contains(p)) return true;
        if (PlusRect().Contains(p)) return true;
        return HitTab(p) >= 0;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_NCHITTEST)
        {
            var screen = new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
            if (!IsInteractive(PointToClient(screen)))
            {
                m.Result = (IntPtr)Native.HTTRANSPARENT; // parent form turns this into caption / resize
                return;
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Strip);

        int rad = P(7);
        for (int i = 0; i < Tabs.Count; i++)
        {
            var tab = Tabs[i];
            var r = TabRect(i);
            bool sel = i == SelectedIndex;
            bool hov = !sel && i == hover;
            Color tabBg = sel ? Theme.TabActive : hov ? Theme.TabHover : Theme.Strip;

            if (sel)
            {
                using var shadowPath = Theme.Round(new RectangleF(r.X, r.Y + P(1), r.Width, r.Height), rad);
                using var sb = new SolidBrush(Color.FromArgb(Theme.Dark ? 70 : 28, 0, 0, 0));
                g.FillPath(sb, shadowPath);

                using var path = Theme.Round(r, rad);
                using var b = new SolidBrush(Theme.TabActive);
                g.FillPath(b, path);

                using var pen = new Pen(Color.FromArgb(Theme.Dark ? 28 : 22, Theme.Dark ? Color.White : Color.Black));
                g.DrawPath(pen, path);
            }
            else if (hov)
            {
                using var path = Theme.Round(r, rad);
                using var b = new SolidBrush(Theme.TabHover);
                g.FillPath(b, path);
            }

            int ic = P(15);
            int x = r.Left + P(10);
            int midY = r.Top + r.Height / 2;

            if (tab.IsLoading)
            {
                using var pen = new Pen(Theme.Accent, P(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, x + 1, midY - ic / 2 + 1, ic - 2, ic - 2, spin, 270);
            }
            else if (tab.Favicon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(tab.Favicon, x, midY - ic / 2, ic, ic);
            }
            else
            {
                using var pen = new Pen(Theme.Muted, Math.Max(1, P(1.3f)));
                g.DrawEllipse(pen, x + 1, midY - ic / 2 + 1, ic - 2, ic - 2);
            }

            int textLeft = x + ic + P(7);
            int textRight = ShowClose(i) ? TabCloseRect(i).Left - P(4) : r.Right - P(8);
            int availW = textRight - textLeft;
            if (availW > P(6))
            {
                var textRect = new Rectangle(textLeft, midY - P(10), availW, P(20));
                TextRenderer.DrawText(g, tab.Title, Font, textRect, sel ? Theme.Text : Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

                int fullW = TextRenderer.MeasureText(g, tab.Title, Font, new Size(int.MaxValue, P(20)),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                if (fullW > availW)
                {
                    int fadeW = Math.Min(P(24), availW / 2);
                    if (fadeW > 2)
                    {
                        var fadeRect = new Rectangle(textRight - fadeW, textRect.Top, fadeW, textRect.Height);
                        using var lgb = new LinearGradientBrush(
                            new Rectangle(fadeRect.X - 1, fadeRect.Y, fadeRect.Width + 2, fadeRect.Height),
                            Color.FromArgb(0, tabBg), tabBg, LinearGradientMode.Horizontal);
                        g.FillRectangle(lgb, fadeRect);
                    }
                }
            }

            if (ShowClose(i))
            {
                var cr = TabCloseRect(i);
                if (hover == i && hoverTabClose)
                {
                    using var hb = new SolidBrush(Theme.PressFill);
                    using var cp = Theme.Round(cr, P(5));
                    g.FillPath(hb, cp);
                }
                Theme.DrawGlyph(g, GClose, cr, Theme.Muted, 7f);
            }
        }

        // "+" new tab
        var pr = PlusRect();
        if (hover == HPlus)
        {
            using var hb = new SolidBrush(Theme.HoverFill);
            using var pp = Theme.Round(pr, P(7));
            g.FillPath(hb, pp);
        }
        Theme.DrawGlyph(g, "\uE710", pr, Theme.Text, 10f);

        // window buttons
        DrawCaption(g, 0, HMin);
        DrawCaption(g, 1, HMax);
        DrawCaption(g, 2, HClose);
    }

    void DrawCaption(Graphics g, int which, int code)
    {
        var r = CaptionRect(which);
        bool isClose = code == HClose;
        bool isHov = hover == code;
        bool isDown = isHov && pressed == code;
        if (isClose && isHov)
        {
            var hr = CloseHitRect;
            int rad = P(6);
            using var path = new GraphicsPath();
            path.AddArc(hr.Left, hr.Top, rad * 2, rad * 2, 180, 90);
            path.AddLine(hr.Right, hr.Top, hr.Right, hr.Bottom);
            path.AddArc(hr.Left, hr.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            path.CloseFigure();
            using var b = new SolidBrush(isDown ? Color.FromArgb(160, 36, 24) : Theme.CloseRed);
            g.FillPath(b, path);
        }
        else
        {
            Color fill = !isHov ? Theme.CaptionCircle
                : (isDown ? Theme.PressFill : Theme.CaptionCircleHover);
            using var b = new SolidBrush(fill);
            g.FillEllipse(b, r);
        }
        Color fg = isHov && isClose ? Color.White : Theme.Text;
        float cx = r.X + r.Width / 2f;
        float cy = r.Y + r.Height / 2f;
        float stroke = Math.Max(1.1f, 1.25f * K);
        using var pen = new Pen(fg, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (code == HMin)
        {
            float hs = 4.5f * K;
            g.DrawLine(pen, cx - hs, cy, cx + hs, cy);
        }
        else if (code == HMax && !Maximized)
        {
            float hs = 4.2f * K;
            g.DrawRectangle(pen, cx - hs, cy - hs, hs * 2, hs * 2);
        }
        else if (code == HMax)
        {
            float s = 6.8f * K;
            float off = 2.0f * K;
            float x0 = cx - s / 2f - off / 2f;
            float y0 = cy - s / 2f + off / 2f;
            g.DrawRectangle(pen, x0, y0, s, s);
            g.DrawLines(pen, new[]
            {
                new PointF(x0 + off, y0),
                new PointF(x0 + off, y0 - off),
                new PointF(x0 + off + s, y0 - off),
                new PointF(x0 + off + s, y0 - off + s),
                new PointF(x0 + s, y0 - off + s)
            });
        }
        else
        {
            float hs = 4.0f * K;
            g.DrawLine(pen, cx - hs, cy - hs, cx + hs, cy + hs);
            g.DrawLine(pen, cx + hs, cy - hs, cx - hs, cy + hs);
        }
    }

    int HitTab(Point p)
    {
        for (int i = 0; i < Tabs.Count; i++)
            if (TabRect(i).Contains(p)) return i;
        return -1;
    }

    int HitAny(Point p, out bool onTabClose)
    {
        onTabClose = false;
        if (CloseHitRect.Contains(p)) return HClose;
        if (CaptionHitRect(0).Contains(p)) return HMin;
        if (CaptionHitRect(1).Contains(p)) return HMax;
        if (PlusRect().Contains(p)) return HPlus;
        int i = HitTab(p);
        if (i >= 0) onTabClose = ShowClose(i) && TabCloseRect(i).Contains(p);
        return i;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitAny(e.Location, out bool hc);
        if (h != hover || hc != hoverTabClose) { hover = h; hoverTabClose = hc; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1; hoverTabClose = false; pressed = int.MinValue;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int h = HitAny(e.Location, out bool onClose);
        if (e.Button == MouseButtons.Left)
        {
            if (h <= HMin) { pressed = h; Invalidate(); return; } // caption buttons act on mouse up
            if (h == HPlus) { NewTabClicked?.Invoke(); return; }
            if (h >= 0)
            {
                if (onClose) TabCloseClicked?.Invoke(h);
                else TabClicked?.Invoke(h);
            }
            else if (e.Clicks == 2) MaximizeClicked?.Invoke();
            else EmptyAreaPressed?.Invoke(e.Location); // fallback: let the form move / resize itself
        }
        else if (e.Button == MouseButtons.Middle && h >= 0) TabCloseClicked?.Invoke(h);
        else if (e.Button == MouseButtons.Right && h >= 0) TabRightClicked?.Invoke(h, e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int h = HitAny(e.Location, out _);
        int was = pressed;
        pressed = int.MinValue;
        Invalidate();
        if (e.Button != MouseButtons.Left || h != was) return;
        if (h == HMin) MinimizeClicked?.Invoke();
        else if (h == HMax) MaximizeClicked?.Invoke();
        else if (h == HClose) CloseClicked?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
