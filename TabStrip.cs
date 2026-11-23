using System.ComponentModel;
using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.WinForms;

namespace Mizar;

public class BrowserTab
{
    public WebView2 View { get; private set; } = new() { Dock = DockStyle.Fill, Visible = false };

    /// <summary>
    /// After the browser process died, a WebView2 control can't be started again: the tab gets a new
    /// one (returned is the old one, for the caller to remove and dispose). Per-page state is reset.
    /// </summary>
    public WebView2 ReplaceView()
    {
        var old = View;
        View = new() { Dock = DockStyle.Fill, Visible = false };
        InFlight.Clear(); NetWatch = false; Receivers.Clear(); Crashes.Clear();
        CertError = false; ZoomHost = null; ExpectedZoom = 1.0; SlowLogged = false;
        return old;
    }
    public string Title { get; set; } = "Tab nou";
    public string Url { get; set; } = "";
    public Image? Favicon { get; private set; }
    bool ownsFavicon;
    public bool IsLoading { get; set; } = true;
    public DateTime LastActive { get; set; } = DateTime.Now;
    /// <summary>Requests still loading (tracked only while the tab is in the background, see NetRepair.cs).</summary>
    public Dictionary<string, DateTime> InFlight { get; } = new();
    public bool NetWatch { get; set; }
    /// <summary>When the current navigation started (diag.log "slow load" line; logging only).</summary>
    public DateTime LoadStarted { get; set; }
    public bool SlowLogged { get; set; }
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
    public event Action? TabMoved;
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

    // tab drag-to-reorder + smooth slide animation (animTimer stops as soon as all tabs settle)
    BrowserTab? dragTab;
    bool dragActive;
    int dragStartX, dragOffsetX, dragX;
    readonly Dictionary<BrowserTab, float> slideOffset = new();
    readonly System.Windows.Forms.Timer animTimer = new() { Interval = 16 };

    const string GClose = "\uE8BB";

    public TabStrip()
    {
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Strip;
        Font = Theme.UiFont(9f, DeviceDpi);
        timer.Tick += (_, _) =>
        {
            if (Tabs.Any(t => t.IsLoading)) { spin = (spin + 30) % 360; Invalidate(); }
            else timer.Stop();
        };
        animTimer.Tick += (_, _) =>
        {
            if (slideOffset.Count == 0) { animTimer.Stop(); return; }
            List<BrowserTab>? done = null;
            foreach (var key in slideOffset.Keys.ToList())
            {
                float next = slideOffset[key] * 0.68f;
                if (Math.Abs(next) < 0.5f || !Tabs.Contains(key)) (done ??= new()).Add(key);
                else slideOffset[key] = next;
            }
            if (done != null) foreach (var k in done) slideOffset.Remove(k);
            if (slideOffset.Count == 0) animTimer.Stop();
            Invalidate();
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

    Rectangle TabCloseRectFor(Rectangle r)
    {
        int s = P(18);
        return new Rectangle(r.Right - s - P(6), r.Top + (r.Height - s) / 2, s, s);
    }

    Rectangle TabCloseRect(int i) => TabCloseRectFor(TabRect(i));

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
        int dragIdx = dragActive && dragTab != null ? Tabs.IndexOf(dragTab) : -1;
        for (int i = 0; i < Tabs.Count; i++)
        {
            if (i == dragIdx) continue;
            var r = TabRect(i);
            if (slideOffset.TryGetValue(Tabs[i], out float dx)) r.X += (int)Math.Round(dx);
            DrawTab(g, i, r, rad);
        }
        if (dragIdx >= 0)
        {
            var r = TabRect(dragIdx);
            r.X = dragX;
            DrawTab(g, dragIdx, r, rad);
        }

        // "+" new tab
        var pr = PlusRect();
        if (!dragActive && hover == HPlus)
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

    void DrawTab(Graphics g, int i, Rectangle r, int rad)
    {
        var tab = Tabs[i];
        bool sel = i == SelectedIndex;
        bool hov = !dragActive && !sel && i == hover;
        Color tabBg = sel ? Theme.TabActive : hov ? Theme.TabHover : Theme.Strip;

        if (sel)
        {
            // fill only, no outline or shadow (owner's choice, 2026-11-17)
            using var path = Theme.Round(r, rad);
            using var b = new SolidBrush(Theme.TabActive);
            g.FillPath(b, path);
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

        var cr = TabCloseRectFor(r);
        int textLeft = x + ic + P(7);
        int textRight = ShowClose(i) ? cr.Left - P(4) : r.Right - P(8);
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
            if (!dragActive && hover == i && hoverTabClose)
            {
                using var hb = new SolidBrush(Theme.PressFill);
                using var cp = Theme.Round(cr, P(5));
                g.FillPath(hb, cp);
            }
            Theme.DrawGlyph(g, GClose, cr, Theme.Muted, 7f);
        }
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

    void FinishDrag()
    {
        if (dragActive && dragTab != null)
        {
            int idx = Tabs.IndexOf(dragTab);
            if (idx >= 0)
            {
                float dx = dragX - TabRect(idx).Left;
                if (Math.Abs(dx) >= 0.5f)
                {
                    slideOffset[dragTab] = dx;
                    if (!animTimer.Enabled) animTimer.Start();
                }
            }
        }
        dragActive = false;
        dragTab = null;
        if (Capture) Capture = false;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragTab != null)
        {
            if ((e.Button & MouseButtons.Left) == 0 || Tabs.Count <= 1 || !Tabs.Contains(dragTab))
            {
                FinishDrag();
                return;
            }
            if (!dragActive && Math.Abs(e.X - dragStartX) >= P(5))
            {
                dragActive = true;
                Capture = true;
            }
            if (dragActive)
            {
                int w = TabWidth();
                int step = w + TabGap;
                int maxLeft = TabRect(Tabs.Count - 1).Left;
                dragX = Math.Clamp(e.X - dragOffsetX, P(6), maxLeft);
                int curIdx = Tabs.IndexOf(dragTab);
                int targetIdx = Math.Clamp((dragX + w / 2 - P(6)) / Math.Max(1, step), 0, Tabs.Count - 1);
                if (curIdx >= 0 && targetIdx != curIdx)
                {
                    if (targetIdx > curIdx)
                    {
                        for (int i = curIdx + 1; i <= targetIdx; i++)
                            slideOffset[Tabs[i]] = slideOffset.GetValueOrDefault(Tabs[i]) + step;
                    }
                    else
                    {
                        for (int i = targetIdx; i < curIdx; i++)
                            slideOffset[Tabs[i]] = slideOffset.GetValueOrDefault(Tabs[i]) - step;
                    }
                    Tabs.RemoveAt(curIdx);
                    Tabs.Insert(targetIdx, dragTab);
                    SelectedIndex = targetIdx;
                    if (!animTimer.Enabled) animTimer.Start();
                    TabMoved?.Invoke();
                }
                Invalidate();
                return;
            }
        }
        int h = HitAny(e.Location, out bool hc);
        if (h != hover || hc != hoverTabClose) { hover = h; hoverTabClose = hc; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (dragActive) return;
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
                else
                {
                    dragTab = Tabs[h];
                    dragStartX = e.X;
                    dragOffsetX = e.X - TabRect(h).Left;
                    dragX = TabRect(h).Left;
                    dragActive = false;
                    TabClicked?.Invoke(h);
                }
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
        if (dragTab != null)
        {
            bool wasDrag = dragActive;
            FinishDrag();
            if (wasDrag) return;
        }
        int h = HitAny(e.Location, out _);
        int was = pressed;
        pressed = int.MinValue;
        Invalidate();
        if (e.Button != MouseButtons.Left || h != was) return;
        if (h == HMin) MinimizeClicked?.Invoke();
        else if (h == HMax) MaximizeClicked?.Invoke();
        else if (h == HClose) CloseClicked?.Invoke();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (dragActive) FinishDrag();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); animTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
