using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Mizar;

/// <summary>
/// Reads site icons from Opera's "Favicons" SQLite database, so imported bookmarks get the
/// same icons they had in Opera (many sites have no /favicon.ico, so fetching alone misses them).
/// Icons are stored per bookmark URL (key "u_&lt;hash&gt;") with a per-host fallback.
/// </summary>
static class OperaFavicons
{
    public static string Marker => Path.Combine(AppPaths.Root, "opera-favicons.done");

    static string Norm(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

    public static string UrlKey(string url)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(Norm(url)));
        return "u_" + Convert.ToHexString(hash)[..16];
    }

    /// <summary>Best icon for a bookmark: its own page icon, else its site's icon.</summary>
    public static Image? For(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        return FaviconCache.Get(UrlKey(url)) ?? FaviconCache.Get(Bookmark.HostOf(url));
    }

    static string? DbPath
    {
        get
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Opera Software", "Opera Stable");
            foreach (var p in new[] { Path.Combine(baseDir, "Default", "Favicons"), Path.Combine(baseDir, "Favicons") })
                if (File.Exists(p)) return p;
            return null;
        }
    }

    static string? ChromeDbPath
    {
        get
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "User Data");
            var def = Path.Combine(baseDir, "Default", "Favicons");
            if (File.Exists(def)) return def;
            if (Directory.Exists(baseDir))
            {
                foreach (var d in Directory.GetDirectories(baseDir, "Profile *").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var p = Path.Combine(d, "Favicons");
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }
    }

    /// <summary>Runs off the UI thread. Returns (cache key, PNG bytes) pairs for the given bookmark URLs.</summary>
    public static List<(string key, byte[] png)> Load(IEnumerable<string> bookmarkUrls) => LoadFromDb(DbPath, bookmarkUrls);

    /// <summary>Runs off the UI thread. Reads site icons from Chrome's "Favicons" SQLite database.</summary>
    public static List<(string key, byte[] png)> LoadChrome(IEnumerable<string> bookmarkUrls) => LoadFromDb(ChromeDbPath, bookmarkUrls);

    static List<(string key, byte[] png)> LoadFromDb(string? src, IEnumerable<string> bookmarkUrls)
    {
        var result = new List<(string, byte[])>();
        if (src == null) return result;

        // Opera may have the database open: copy it first (shared read) and query the copy.
        var tmp = Path.Combine(Path.GetTempPath(), $"mizar-favicons-{Guid.NewGuid():N}.db");
        try
        {
            using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = File.Create(tmp))
                input.CopyTo(output);

            var byUrl = new Dictionary<string, (byte[] data, int score)>();
            var byHost = new Dictionary<string, (byte[] data, int score)>(StringComparer.OrdinalIgnoreCase);

            using (var conn = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly;Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT m.page_url, b.image_data, b.width FROM icon_mapping m " +
                                  "JOIN favicon_bitmaps b ON b.icon_id = m.icon_id WHERE b.image_data IS NOT NULL";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var page = r.GetString(0);
                    var data = (byte[])r[1];
                    int w = r.IsDBNull(2) ? 16 : r.GetInt32(2);
                    if (data.Length == 0) continue;
                    int score = Math.Abs(w - 32); // closest to 32px wins

                    var k = Norm(page);
                    if (!byUrl.TryGetValue(k, out var cur) || score < cur.score) byUrl[k] = (data, score);

                    var host = Bookmark.HostOf(page);
                    if (host.Length > 0 && (!byHost.TryGetValue(host, out var ch) || score < ch.score)) byHost[host] = (data, score);
                }
            }

            foreach (var url in bookmarkUrls.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (byUrl.TryGetValue(Norm(url), out var hit))
                    result.Add((UrlKey(url), hit.data));
                else if (byHost.TryGetValue(Bookmark.HostOf(url), out var h))
                    result.Add((UrlKey(url), h.data));
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
        return result;
    }
}
