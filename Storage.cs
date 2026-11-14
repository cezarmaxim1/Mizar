using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mizar;

public static class AppPaths
{
    /// <summary>True when MIZAR_DATA_DIR points Mizar at a separate data folder (tests).</summary>
    public static readonly bool IsCustomRoot = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIZAR_DATA_DIR"));

    public static readonly string Root = IsCustomRoot
        ? Path.GetFullPath(Environment.GetEnvironmentVariable("MIZAR_DATA_DIR")!)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mizar");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string BookmarksFile => Path.Combine(Root, "bookmarks.json");
    public static string BookmarksBak => Path.Combine(Root, "bookmarks.bak");
    public static string Backups => Path.Combine(Root, "backups");
    public static string SessionFile => Path.Combine(Root, "session.json");
    public static string ErrorLog => Path.Combine(Root, "errors.log");
    public static string Favicons => Path.Combine(Root, "favicons");
    public static string WebData => Path.Combine(Root, "WebData");

    static AppPaths()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Favicons);
    }
}

public class Settings
{
    public string HomePage { get; set; } = "https://www.google.com";
    public string DownloadFolder { get; set; } = KnownFolders.Downloads;
    public bool AskWhereToSave { get; set; }
    public bool BlockAds { get; set; } = true;
    public bool BlockTrackers { get; set; } = true;
    public bool BlockThirdPartyCookies { get; set; } = true;
    /// <summary>Colour scheme web pages are told to use (CSS prefers-color-scheme): "auto" (follow Windows, default), "light" or "dark".</summary>
    public string WebColorScheme { get; set; } = "auto";
    public bool SavePasswords { get; set; } = true;
    public bool ShowBookmarksBar { get; set; } = true;
    /// <summary>Reopen last session's tabs at startup (otherwise just the home page).</summary>
    public bool RestoreSession { get; set; } = false;
    /// <summary>Wipe the browsing history (Mizar's list and the engine's) every time Mizar closes.</summary>
    public bool ClearHistoryOnExit { get; set; } = true;
    /// <summary>Remembered page zoom per site (host without "www."), e.g. "reddit.com" = 1.25. Sites at 100% aren't stored.</summary>
    public Dictionary<string, double> SiteZoom { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int[]? WindowBounds { get; set; }
    public bool Maximized { get; set; } = true;

    // Google Drive API bookmark sync (optional, zero background RAM when idle)
    public string? DriveClientId { get; set; }
    public string? DriveClientSecret { get; set; }
    public string? DriveTokenEnc { get; set; }
    public string? DriveEmail { get; set; }
    public string? DriveFileId { get; set; }
    public long DriveLastSyncUtc { get; set; }

    // Self-update from GitHub Releases (see Updater.cs)
    public bool AutoUpdate { get; set; } = true;
    public long UpdateLastCheckUtc { get; set; }

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch { }
    }
}

/// <summary>Open tabs, saved every few seconds and on exit, so they come back after a restart or a crash.</summary>
public class SessionData
{
    public List<SessionTab> Tabs { get; set; } = new();
    public int Active { get; set; }

    public static SessionData Load()
    {
        try
        {
            if (File.Exists(AppPaths.SessionFile))
                return JsonSerializer.Deserialize<SessionData>(File.ReadAllText(AppPaths.SessionFile)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            var tmp = AppPaths.SessionFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, AppPaths.SessionFile, true);
        }
        catch { }
    }
}

public class SessionTab
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
}

static class KnownFolders
{
    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    public static string Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
                {
                    var s = Marshal.PtrToStringUni(p)!;
                    Marshal.FreeCoTaskMem(p);
                    return s;
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}

public class Bookmark
{
    public string Name { get; set; } = "";
    public string? Url { get; set; }
    public List<Bookmark>? Children { get; set; }

    [JsonIgnore] public bool IsFolder => Url == null;
    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name : IsFolder ? "Folder" : HostOf(Url!);

    public static string HostOf(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        try
        {
            var h = new Uri(url).Host;
            return h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
        }
        catch { return ""; }
    }
}

public class BookmarkStore
{
    public long UpdatedAt { get; set; }
    public List<Bookmark> Bar { get; set; } = new();
    public List<Bookmark> Other { get; set; } = new();

    public event Action? Changed;

    public string ToJson() => JsonSerializer.Serialize(this, Opts);

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool Exists => File.Exists(AppPaths.BookmarksFile);

    const int KeepDailyBackups = 10;

    static BookmarkStore? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<BookmarkStore>(text);
        }
        catch { return null; }
    }

    static IEnumerable<string> DailyBackups() =>
        Directory.Exists(AppPaths.Backups)
            ? Directory.GetFiles(AppPaths.Backups, "bookmarks-*.json").OrderByDescending(f => f, StringComparer.Ordinal)
            : Enumerable.Empty<string>();

    /// <summary>
    /// Loads bookmarks. If the file is damaged, falls back to bookmarks.bak (the previous save),
    /// then to the newest daily backup, keeps the damaged file aside and repairs the main file.
    /// </summary>
    public static BookmarkStore Load()
    {
        if (!Exists) return new();
        var main = TryRead(AppPaths.BookmarksFile);
        if (main != null) return main;

        try { File.Copy(AppPaths.BookmarksFile, Path.Combine(AppPaths.Root, $"bookmarks.damaged-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true); } catch { }
        foreach (var candidate in new[] { AppPaths.BookmarksBak }.Concat(DailyBackups()))
        {
            var b = TryRead(candidate);
            if (b == null) continue;
            b.Save(backup: false); // repair the main file
            return b;
        }
        return new();
    }

    public void Save() => Save(backup: true);

    void Save(bool backup)
    {
        try
        {
            var tmp = AppPaths.BookmarksFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            if (backup && File.Exists(AppPaths.BookmarksFile) && new FileInfo(AppPaths.BookmarksFile).Length > 2)
            {
                // previous version -> bookmarks.bak; first save of each day also keeps a dated copy
                try
                {
                    File.Copy(AppPaths.BookmarksFile, AppPaths.BookmarksBak, true);
                    Directory.CreateDirectory(AppPaths.Backups);
                    var daily = Path.Combine(AppPaths.Backups, $"bookmarks-{DateTime.Now:yyyy-MM-dd}.json");
                    if (!File.Exists(daily)) File.Copy(AppPaths.BookmarksFile, daily);
                    foreach (var old in DailyBackups().Skip(KeepDailyBackups)) File.Delete(old);
                }
                catch { }
            }
            File.Move(tmp, AppPaths.BookmarksFile, true);
        }
        catch { }
    }

    public void NotifyChanged()
    {
        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Save();
        Changed?.Invoke();
    }

    /// <summary>Applies bookmarks downloaded from Google Drive without re-triggering an upload.</summary>
    public void ReplaceFromSync(BookmarkStore remote)
    {
        Bar = remote.Bar ?? new();
        Other = remote.Other ?? new();
        UpdatedAt = remote.UpdatedAt;
        Save();
        Changed?.Invoke();
    }

    static IEnumerable<Bookmark> Walk(IEnumerable<Bookmark> list)
    {
        foreach (var b in list)
        {
            yield return b;
            if (b.Children != null)
                foreach (var c in Walk(b.Children)) yield return c;
        }
    }

    public IEnumerable<Bookmark> AllUrls() => Walk(Bar).Concat(Walk(Other)).Where(b => !b.IsFolder);

    static string Key(string url) => url.TrimEnd('/');

    public Bookmark? FindByUrl(string? url) =>
        string.IsNullOrEmpty(url) ? null :
        AllUrls().FirstOrDefault(b => string.Equals(Key(b.Url!), Key(url), StringComparison.OrdinalIgnoreCase));

    public bool HasHost(string host) =>
        AllUrls().Any(b => Bookmark.HostOf(b.Url).Equals(host, StringComparison.OrdinalIgnoreCase));

    public List<Bookmark>? ParentOf(Bookmark target)
    {
        List<Bookmark>? Find(List<Bookmark> list)
        {
            if (list.Contains(target)) return list;
            foreach (var b in list)
                if (b.Children != null)
                {
                    var r = Find(b.Children);
                    if (r != null) return r;
                }
            return null;
        }
        return Find(Bar) ?? Find(Other);
    }

    public void Remove(Bookmark b) => ParentOf(b)?.Remove(b);

    public static bool IsDescendant(Bookmark folder, Bookmark candidate) =>
        folder.Children != null && Walk(folder.Children).Contains(candidate);

    public static string? OperaBookmarksPath
    {
        get
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Opera Software", "Opera Stable");
            foreach (var p in new[] { Path.Combine(baseDir, "Default", "Bookmarks"), Path.Combine(baseDir, "Bookmarks") })
                if (File.Exists(p)) return p;
            return null;
        }
    }

    /// <summary>Imports Opera bookmarks, skipping ones already present. Returns how many were added, or -1 if Opera has none.</summary>
    public int ImportFromOpera()
    {
        var path = OperaBookmarksPath;
        if (path == null) return -1;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(fs);
        var roots = doc.RootElement.GetProperty("roots");
        var existing = new HashSet<string>(AllUrls().Select(b => Key(b.Url!)), StringComparer.OrdinalIgnoreCase);
        int added = 0;

        if (roots.TryGetProperty("bookmark_bar", out var bar))
            added += Merge(bar, Bar, existing);

        foreach (var key in new[] { "other", "synced" })
        {
            if (!roots.TryGetProperty(key, out var r)) continue;
            var name = r.TryGetProperty("name", out var n) ? n.GetString() ?? key : key;
            var folder = Other.FirstOrDefault(x => x.IsFolder && x.Name == name);
            bool isNew = folder == null;
            folder ??= new Bookmark { Name = name, Children = new() };
            folder.Children ??= new();
            int c = Merge(r, folder.Children, existing);
            if (c > 0 && isNew) Other.Add(folder);
            added += c;
        }
        return added;
    }

    static int Merge(JsonElement node, List<Bookmark> target, HashSet<string> existing)
    {
        int added = 0;
        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return 0;

        foreach (var c in children.EnumerateArray())
        {
            var type = c.TryGetProperty("type", out var t) ? t.GetString() : null;
            var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

            if (type == "url")
            {
                var url = c.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (url == null || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) continue;
                if (!existing.Add(Key(url))) continue;
                target.Add(new Bookmark { Name = name, Url = url });
                added++;
            }
            else if (type == "folder")
            {
                var f = target.FirstOrDefault(x => x.IsFolder && x.Name == name);
                bool isNew = f == null;
                f ??= new Bookmark { Name = name, Children = new() };
                f.Children ??= new();
                int c2 = Merge(c, f.Children, existing);
                if (isNew && c2 > 0) target.Add(f);
                added += c2;
            }
        }
        return added;
    }
}

/// <summary>Site icons cached on disk, keyed by host (without "www.").</summary>
static class FaviconCache
{
    static readonly Dictionary<string, Image?> Cache = new(StringComparer.OrdinalIgnoreCase);

    static string FileFor(string host)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) host = host.Replace(ch, '_');
        return Path.Combine(AppPaths.Favicons, host + ".png");
    }

    public static bool Has(string host) => File.Exists(FileFor(host));

    public static Image? Get(string host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        if (Cache.TryGetValue(host, out var img)) return img;
        img = null;
        try
        {
            var f = FileFor(host);
            if (File.Exists(f))
            {
                using var tmp = Image.FromFile(f);
                img = new Bitmap(tmp);
            }
        }
        catch { }
        Cache[host] = img;
        return img;
    }

    /// <summary>Saves the icon if none is stored yet. Returns true when a new icon was written.</summary>
    public static bool Save(string host, Image img)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var f = FileFor(host);
        if (File.Exists(f)) return false;
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, 0, 0, 32, 32);
            }
            bmp.Save(f, ImageFormat.Png);
            Cache.Remove(host);
            return true;
        }
        catch { return false; }
    }

    public static Image? Decode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var ic = new Icon(ms, new Size(32, 32));
            return ic.ToBitmap();
        }
        catch { }
        try
        {
            using var ms = new MemoryStream(bytes);
            using var tmp = Image.FromStream(ms);
            return new Bitmap(tmp);
        }
        catch { return null; }
    }
}

static class AdBlocker
{
    // Ad networks only; regular sites (incl. google.com) are never blocked.
    public static readonly string[] Domains =
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.com",
        "googletagservices.com", "pagead2.googlesyndication.com", "adnxs.com", "taboola.com", "outbrain.com",
        "criteo.com", "criteo.net", "amazon-adsystem.com", "pubmatic.com", "rubiconproject.com", "openx.net",
        "adsrvr.org", "moatads.com", "casalemedia.com", "smartadserver.com", "teads.tv", "adform.net",
        "33across.com", "sharethrough.com", "media.net", "zemanta.com", "revcontent.com", "mgid.com",
        "propellerads.com", "popads.net", "exoclick.com", "adsterra.com", "yieldmo.com", "bidswitch.net",
        "contextweb.com", "lijit.com", "sovrn.com", "indexww.com", "adcolony.com", "applovin.com",
        "unityads.unity3d.com", "advertising.com", "adroll.com", "quantserve.com", "scorecardresearch.com",
        "gemius.pl", "adocean.pl", "etarget.ro", "2performant.com", "onetag-sys.com", "seedtag.com",
        "richaudience.com", "improvedigital.com", "360yield.com", "smaato.net", "inmobi.com",
        // display / video ad serving
        "2mdn.net", "serving-sys.com", "sizmek.com", "flashtalking.com", "innovid.com", "adtechus.com",
        "smartclip.net", "stickyadstv.com", "spotxchange.com", "spotx.tv", "tremorhub.com", "lkqd.net",
        "springserve.com", "fwmrm.net", "freewheel.tv", "rhythmone.com", "1rx.io", "vidoomy.com",
        "aniview.com", "connatix.com", "primis.tech", "sekindo.com", "vidazoo.com", "justpremium.com",
        // exchanges / header bidding
        "gumgum.com", "triplelift.com", "3lift.com", "emxdgt.com", "yieldlab.net", "adition.com",
        "adhese.com", "admixer.net", "adkernel.com", "tribalfusion.com", "undertone.com", "sonobi.com",
        "districtm.io", "conversantmedia.com", "dotomi.com", "zedo.com", "adzerk.net", "yieldlove.com",
        "nexx360.io", "adagio.io", "smilewanted.com", "mathtag.com", "rfihub.com", "simpli.fi",
        "adgrx.com", "tidaltv.com", "media6degrees.com", "mookie1.com", "projectagora.net", "httpool.com",
        // native / content recommendation
        "content.ad", "nativo.com", "ntv.io", "plista.com", "ligatus.com", "adnow.com", "adskeeper.co.uk",
        "engageya.com", "strossle.com", "dianomi.com", "adblade.com",
        // publisher ad managers
        "buysellads.com", "servedby-buysellads.com", "carbonads.com", "carbonads.net", "adthrive.com",
        "mediavine.com", "ezoic.net", "ezojs.com", "btloader.com",
        // pop-ups / aggressive networks
        "popcash.net", "juicyads.com", "trafficjunky.net", "trafficstars.com", "adcash.com",
        "hilltopads.net", "clickadu.com", "onclickads.net", "revenuehits.com", "bidvertiser.com",
        "ad-maven.com", "adspyglass.com"
    };

    // Analytics / tracking services (whole domains). Chosen so normal site features keep working.
    public static readonly string[] TrackerDomains =
    {
        "google-analytics.com", "googletagmanager.com", "hotjar.com", "hotjar.io", "clarity.ms",
        "mixpanel.com", "api.segment.io", "cdn.segment.com", "api.amplitude.com", "cdn.amplitude.com",
        "fullstory.com", "mouseflow.com", "crazyegg.com", "heapanalytics.com", "nr-data.net",
        "bat.bing.com", "analytics.tiktok.com", "snap.licdn.com", "px.ads.linkedin.com",
        "analytics.twitter.com", "static.ads-twitter.com", "ct.pinterest.com", "mc.yandex.ru",
        "js.hs-analytics.net", "track.hubspot.com", "doubleverify.com", "adsafeprotected.com",
        "stats.wp.com", "pixel.wp.com", "luckyorange.com", "smartlook.com", "inspectlet.com",
        "matomo.cloud", "chartbeat.com", "chartbeat.net", "parsely.com", "cxense.com",
        // audience data / cross-site identity
        "bluekai.com", "krxd.net", "exelator.com", "eyeota.net", "agkn.com", "crwdcntrl.net",
        "demdex.net", "everesttech.net", "tapad.com", "adsymptotic.com", "zeotap.com", "id5-sync.com",
        "liadm.com", "pippio.com", "rlcdn.com", "turn.com", "imrworldwide.com", "omtrdc.net", "2o7.net",
        "adloox.com", "adlooxtracking.com"
    };

    // Trackers that live on domains we must not block entirely (Facebook Pixel).
    // Login with Facebook, embeds and facebook.com itself keep working.
    public static readonly string[] TrackerPatterns =
    {
        "*://connect.facebook.net/*/fbevents.js*", "*://connect.facebook.net/signals/*",
        "*://www.facebook.com/tr*", "*://facebook.com/tr*"
    };

    public enum Kind { None, Ad, Tracker }

    static readonly HashSet<string> AdSet = new(Domains, StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> TrackerSet = new(TrackerDomains, StringComparer.OrdinalIgnoreCase);

    /// <summary>Blocked-request counters (for diagnostics).</summary>
    public static int BlockedAds, BlockedTrackers;

    /// <summary>
    /// All WebResourceRequested filter patterns (only matching requests reach .NET).
    /// One pattern per domain: "*://*domain/*" covers the bare domain and every subdomain. It also
    /// lets through look-alikes ("notdomain"), which <see cref="Classify"/> then rejects exactly.
    /// </summary>
    public static IEnumerable<string> FilterPatterns()
    {
        foreach (var d in Domains.Concat(TrackerDomains)) yield return $"*://*{d}/*";
        foreach (var p in TrackerPatterns) yield return p;
    }

    public static Kind Classify(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return Kind.None;
        var host = u.Host.ToLowerInvariant();
        var path = u.AbsolutePath;
        if (host == "connect.facebook.net" && (path.EndsWith("/fbevents.js") || path.StartsWith("/signals/"))) return Kind.Tracker;
        if ((host == "www.facebook.com" || host == "facebook.com") && (path == "/tr" || path.StartsWith("/tr/"))) return Kind.Tracker;

        var h = host;
        while (h.Contains('.'))
        {
            if (AdSet.Contains(h)) return Kind.Ad;
            if (TrackerSet.Contains(h)) return Kind.Tracker;
            h = h[(h.IndexOf('.') + 1)..];
        }
        return Kind.None;
    }
}
