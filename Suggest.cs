using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Mizar;

public record Suggestion(string Title, string Url, bool IsBookmark);

/// <summary>
/// Drop-down under the address bar with matching bookmarks and history.
/// It never takes keyboard focus: the address box keeps it and drives the selection.
/// </summary>
public class SuggestBox : Control
{
    readonly List<Suggestion> items = new();
    int selected = -1, hover = -1;
    Font TitleFont => Theme.UiFont(11f, DeviceDpi);
    Font UrlFont => Theme.UiFont(10f, DeviceDpi);

    public event Action<Suggestion>? Picked;

    public SuggestBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Visible = false;
    }

    int P(float v) => (int)Math.Round(v * DeviceDpi / 96f);
    int RowHeight => P(42);
    int Pad => P(6);

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Count => items.Count;

    public Suggestion? Selected => selected >= 0 && selected < items.Count ? items[selected] : null;

    public void SetItems(IEnumerable<Suggestion> list)
    {
        items.Clear();
        items.AddRange(list);
        selected = hover = -1;
        Height = items.Count * RowHeight + Pad * 2;
        Invalidate();
    }

    /// <summary>Moves the selection with the arrow keys; wraps through "nothing selected".</summary>
    public void Step(int delta)
    {
        if (items.Count == 0) return;
        int n = items.Count + 1;                     // extra slot = no selection (typed text)
        selected = ((selected + 1 + delta) % n + n) % n - 1;
        Invalidate();
    }

    int RowAt(Point p)
    {
        int i = (p.Y - Pad) / RowHeight;
        return p.Y >= Pad && i >= 0 && i < items.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = RowAt(e.Location);
        if (h != hover) { hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover != -1) { hover = -1; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int i = RowAt(e.Location);
        if (e.Button == MouseButtons.Left && i >= 0) Picked?.Invoke(items[i]);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Menu);
        using (var border = new Pen(Theme.Border))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var row = new Rectangle(Pad, Pad + i * RowHeight, Width - Pad * 2, RowHeight);
            if (i == selected || i == hover)
            {
                using var path = Theme.Round(row, P(6));
                using var b = new SolidBrush(i == selected ? Theme.PressFill : Theme.HoverFill);
                g.FillPath(b, path);
            }

            int ic = P(18);
            int x = row.Left + P(12);
            var icon = OperaFavicons.For(it.Url);
            if (icon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, x, row.Top + (row.Height - ic) / 2, ic, ic);
            }
            else Theme.DrawGlyph(g, it.IsBookmark ? "\uE734" : "\uE81C", new Rectangle(x, row.Top, ic, row.Height), Theme.Muted, 10.5f);

            int textLeft = x + ic + P(14);
            int avail = row.Right - P(12) - textLeft;
            if (avail <= 0) continue;

            string title = string.IsNullOrWhiteSpace(it.Title) ? Bookmark.HostOf(it.Url) : it.Title;
            string url = Shorten(it.Url);
            const TextFormatFlags draw = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                                         TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            // measure WITHOUT EndEllipsis: with it (and no size limit) the width comes back as just "r…"
            const TextFormatFlags measure = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int titleW = Math.Min(TextRenderer.MeasureText(g, title, TitleFont, new Size(int.MaxValue, int.MaxValue), measure).Width + P(2),
                                  avail * 6 / 10);
            TextRenderer.DrawText(g, title, TitleFont, new Rectangle(textLeft, row.Top, titleW, row.Height), Theme.Text, draw);
            int urlLeft = textLeft + titleW + P(16);
            bool sameAsTitle = string.Equals(title, url, StringComparison.OrdinalIgnoreCase);
            if (!sameAsTitle && row.Right - P(12) - urlLeft > P(40))
                TextRenderer.DrawText(g, url, UrlFont, new Rectangle(urlLeft, row.Top, row.Right - P(12) - urlLeft, row.Height), Theme.Muted, draw);
        }
    }

    static string Shorten(string url)
    {
        var s = url;
        int i = s.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0) s = s[(i + 3)..];
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        return s.TrimEnd('/');
    }

    /// <summary>
    /// Finds up to <paramref name="max"/> suggestions for the typed text: bookmarks first, then
    /// history (newest first). Sites whose address starts with the text rank above the rest.
    /// </summary>
    public static List<Suggestion> Find(string typed, IEnumerable<Bookmark> bookmarks, IEnumerable<HistoryEntry> historyNewestFirst, int max = 6)
    {
        var q = typed.Trim();
        var result = new List<Suggestion>();
        if (q.Length < 1 || q.Contains("://") || q.Contains(' ') && q.Length > 40) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var starts = new List<Suggestion>();
        var contains = new List<Suggestion>();

        // Short queries match only at the start of the site name or of a word in the title
        // ("re" -> reddit, not facebook.com/?ref=...). From 3 letters on, anywhere matches too.
        bool shortQuery = q.Length < 3;

        static bool WordStart(string text, string q)
        {
            for (int i = text.IndexOf(q, StringComparison.OrdinalIgnoreCase); i >= 0;
                 i = text.IndexOf(q, i + 1, StringComparison.OrdinalIgnoreCase))
                if (i == 0 || !char.IsLetterOrDigit(text[i - 1])) return true;
            return false;
        }

        void Consider(string? url, string title, bool isBookmark)
        {
            if (string.IsNullOrEmpty(url) || InternalPages.IsInternal(url)) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return;
            // skip throw-away addresses (login redirects, tracking links): very long query strings
            if (!isBookmark && parsed.Query.Length > 80) return;
            // pass-through pages ("Se redirecționează", "Redirecting...") are not places anyone goes back to
            if (!isBookmark && (title.StartsWith("Se redirec", StringComparison.OrdinalIgnoreCase) ||
                                title.StartsWith("Redirect", StringComparison.OrdinalIgnoreCase))) return;
            // a "title" that is really an address is useless for matching and display
            if (title.Contains("://") || (title.Contains('/') && !title.Contains(' '))) title = "";
            var shortUrl = Shorten(url);
            var host = Bookmark.HostOf(url);
            bool prefix = shortUrl.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
                          host.StartsWith(q, StringComparison.OrdinalIgnoreCase);
            if (!prefix)
            {
                bool match = shortQuery
                    ? WordStart(title, q) || WordStart(host, q)
                    : shortUrl.Contains(q, StringComparison.OrdinalIgnoreCase) || title.Contains(q, StringComparison.OrdinalIgnoreCase);
                if (!match) return;
            }
            // one row per page: reddit.com and reddit.com/?captcha=1 are the same place
            if (!seen.Add(host + parsed.AbsolutePath.TrimEnd('/'))) return;
            (prefix ? starts : contains).Add(new Suggestion(title, url, isBookmark));
        }

        foreach (var b in bookmarks) Consider(b.Url, b.Name, true);
        foreach (var h in historyNewestFirst)
        {
            Consider(h.Url, h.Title, false);
            if (starts.Count >= max * 3) break; // plenty of good matches already
        }

        result.AddRange(starts.Take(max));
        result.AddRange(contains.Take(max - result.Count));
        return result;
    }
}
