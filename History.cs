using System.Text.Json;

namespace Mizar;

public class HistoryEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Unix time, milliseconds.</summary>
    public long Time { get; set; }
}

/// <summary>
/// Browsing history kept by Mizar itself (WebView2 has no history UI).
/// Capped at <see cref="Max"/> entries; saved to disk at most every 10 s and on exit.
/// </summary>
public class HistoryStore
{
    const int Max = 5000;
    readonly List<HistoryEntry> items;
    bool dirty;
    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 10_000 };

    static string FilePath => Path.Combine(AppPaths.Root, "history.json");

    public HistoryStore()
    {
        items = Load();
        saveTimer.Tick += (_, _) => Flush();
        saveTimer.Start();
    }

    static List<HistoryEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    /// <summary>When true nothing is written to disk (self-tests against the owner's real data).</summary>
    public bool ReadOnly { get; set; }

    public void Flush()
    {
        if (!dirty || ReadOnly) return;
        dirty = false;
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    static long Now => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    public void Visit(string url, string title)
    {
        if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
              url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return;
        if (InternalPages.IsInternal(url)) return;
        long now = Now;
        // revisiting the same page within 30 minutes just refreshes the entry
        var recent = items.Count > 0 ? items[^1] : null;
        if (recent != null && recent.Url == url && now - recent.Time < 30 * 60 * 1000)
        {
            recent.Time = now;
            if (title.Length > 0) recent.Title = title;
        }
        else items.Add(new HistoryEntry { Url = url, Title = title, Time = now });
        if (items.Count > Max) items.RemoveRange(0, items.Count - Max);
        dirty = true;
    }

    public void UpdateTitle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        for (int i = items.Count - 1; i >= Math.Max(0, items.Count - 30); i--)
        {
            if (items[i].Url != url) continue;
            if (items[i].Title != title) { items[i].Title = title; dirty = true; }
            return;
        }
    }

    /// <summary>Entries from the most recent to the oldest (for address-bar suggestions).</summary>
    public IEnumerable<HistoryEntry> NewestFirst()
    {
        for (int i = items.Count - 1; i >= 0; i--) yield return items[i];
    }

    public int Count => items.Count;

    public void Delete(string url, long time)
    {
        if (items.RemoveAll(e => e.Url == url && e.Time == time) > 0) dirty = true;
    }

    public void Clear()
    {
        items.Clear();
        dirty = true;
        Flush();
    }

    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Self-contained HTML page (newest first), styled with the current theme.</summary>
    public string BuildPage()
    {
        var data = JsonSerializer.Serialize(
            Enumerable.Range(0, items.Count).Select(i => items[items.Count - 1 - i])
                .Select(e => new { u = e.Url, t = e.Title, ts = e.Time }));

        var css = $":root{{--bg:{Hex(Theme.Strip)};--card:{Hex(Theme.Surface)};--text:{Hex(Theme.Text)};" +
                  $"--muted:{Hex(Theme.Muted)};--border:{Hex(Theme.Border)};--hover:{Hex(Theme.HoverFill)};--accent:{Hex(Theme.Accent)}}}";

        return Template.Replace("/*VARS*/", css).Replace("__DATA__", data);
    }

    const string Template = """
<!doctype html><html lang="ro"><head><meta charset="utf-8"><title>Istoric</title>
<style>
/*VARS*/
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);font:14px "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}
.wrap{max-width:880px;margin:0 auto;padding:32px 20px 64px}
h1{font-size:24px;font-weight:600;margin:0 0 20px}
.top{display:flex;gap:10px;align-items:center;margin-bottom:20px}
input{flex:1;padding:11px 18px;border-radius:999px;border:1px solid var(--border);background:var(--card);color:var(--text);font:inherit;outline:none}
input:focus{border-color:var(--accent);box-shadow:0 0 0 1px var(--accent)}
.clear{padding:10px 18px;border-radius:999px;border:1px solid var(--border);background:var(--card);color:var(--text);cursor:pointer;font:inherit}
.clear:hover{background:var(--hover)}
.day{background:var(--card);border-radius:14px;padding:6px 0 8px;margin-bottom:16px;border:1px solid var(--border)}
.day h2{font-size:13px;font-weight:600;color:var(--muted);margin:10px 20px 6px}
.row{display:flex;align-items:center;gap:14px;padding:7px 12px 7px 20px}
.row:hover{background:var(--hover)}
.time{color:var(--muted);font-size:12.5px;width:40px;flex:none;font-variant-numeric:tabular-nums}
.row a{color:var(--text);text-decoration:none;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;flex:1;min-width:0}
.row a:hover{text-decoration:underline}
.host{color:var(--muted);font-size:12.5px;flex:none;max-width:240px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.del{visibility:hidden;border:0;background:transparent;color:var(--muted);cursor:pointer;font-size:17px;line-height:1;border-radius:8px;width:30px;height:30px;flex:none}
.row:hover .del{visibility:visible}
.del:hover{background:var(--border);color:var(--text)}
.empty{color:var(--muted);text-align:center;padding:64px 0}
</style></head><body><div class="wrap">
<h1>Istoric</h1>
<div class="top"><input id="q" placeholder="Caută în istoric" autofocus><button class="clear" id="clr">Șterge tot istoricul</button></div>
<div id="list"></div></div>
<script>
const data = __DATA__;
const list = document.getElementById('list'), q = document.getElementById('q');
const post = m => window.chrome && chrome.webview && chrome.webview.postMessage(m);
function host(u){ try { return new URL(u).hostname.replace(/^www\./,''); } catch { return u; } }
function dayLabel(ts){
  const today = new Date(); today.setHours(0,0,0,0);
  const d = new Date(ts); d.setHours(0,0,0,0);
  const diff = Math.round((today - d) / 864e5);
  if (diff === 0) return 'Azi';
  if (diff === 1) return 'Ieri';
  const s = d.toLocaleDateString('ro-RO', { weekday:'long', day:'numeric', month:'long', year:'numeric' });
  return s.charAt(0).toUpperCase() + s.slice(1);
}
function render(){
  const f = q.value.trim().toLowerCase();
  list.textContent = '';
  let cur = null, box = null, count = 0;
  for (const e of data){
    if (f && !(e.t.toLowerCase().includes(f) || e.u.toLowerCase().includes(f))) continue;
    if (++count > 1500) break;
    const lab = dayLabel(e.ts);
    if (lab !== cur){
      cur = lab; box = document.createElement('div'); box.className = 'day';
      const h = document.createElement('h2'); h.textContent = lab; box.appendChild(h); list.appendChild(box);
    }
    const r = document.createElement('div'); r.className = 'row';
    const tm = document.createElement('span'); tm.className = 'time';
    tm.textContent = new Date(e.ts).toLocaleTimeString('ro-RO', { hour:'2-digit', minute:'2-digit' });
    const a = document.createElement('a'); a.href = e.u; a.textContent = e.t || e.u; a.title = e.u;
    const h = document.createElement('span'); h.className = 'host'; h.textContent = host(e.u);
    const d = document.createElement('button'); d.className = 'del'; d.textContent = '×'; d.title = 'Șterge din istoric';
    d.onclick = () => { post({ cmd:'delete', u:e.u, ts:e.ts }); data.splice(data.indexOf(e), 1); render(); };
    r.append(tm, a, h, d); box.appendChild(r);
  }
  if (!count){
    const p = document.createElement('div'); p.className = 'empty';
    p.textContent = f ? 'Nu am găsit nimic.' : 'Istoricul e gol.'; list.appendChild(p);
  }
}
q.oninput = render;
document.getElementById('clr').onclick = () => {
  if (confirm('Ștergi tot istoricul?')) { post({ cmd:'clear' }); data.length = 0; render(); }
};
render();
</script></body></html>
""";
}
