using System.IO.Compression;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Mizar;

/// <summary>
/// YouTube ad blocking built from uBlock Origin's own code and filter lists, without the extension.
/// <list type="bullet">
/// <item>Once a day: the latest uBO release (only its js/ and lib/ folders) and uBO's filter lists
/// are downloaded into &lt;data&gt;\ytblock\work.</item>
/// <item>A hidden WebView runs YouTubeBuild.js on them: uBO's parser and scriptlet maker keep only
/// the YouTube rules and produce one script (scriptlets + hiding CSS).</item>
/// <item>That script is added to every tab with AddScriptToExecuteOnDocumentCreated and does
/// nothing outside YouTube. Nothing is routed through Mizar, so pages like Gmail are untouched.</item>
/// </list>
/// </summary>
public static class YouTubeBlock
{
    const string Host = "ytbuild.mizar.internal";
    const string UboRepo = "gorhill/uBlock";
    static readonly (string file, string[] urls)[] Lists =
    {
        ("filters.txt", Cdn("filters/filters.min.txt")),
        ("quick-fixes.txt", Cdn("filters/quick-fixes.min.txt")),
        ("unbreak.txt", Cdn("filters/unbreak.min.txt")),
        ("easylist.txt", new[] { "https://ublockorigin.github.io/uAssets/thirdparties/easylist.txt",
            "https://cdn.jsdelivr.net/gh/uBlockOrigin/uAssetsCDN@main/thirdparties/easylist.txt",
            "https://ublockorigin.pages.dev/thirdparties/easylist.txt" }),
    };
    static string[] Cdn(string path) => new[]
    {
        "https://ublockorigin.github.io/uAssetsCDN/" + path,
        "https://ublockorigin.pages.dev/" + path,
        "https://cdn.jsdelivr.net/gh/uBlockOrigin/uAssetsCDN@main/" + path,
    };

    /// <summary>Lists change several times a week; check about once a day.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(20);

    static string Dir => Path.Combine(AppPaths.Root, "ytblock");
    static string Work => Path.Combine(Dir, "work");
    static string PayloadFile => Path.Combine(Dir, "youtube.js");
    static string StateFile => Path.Combine(Dir, "state.json");

    sealed class State
    {
        public string Ubo { get; set; } = "";
        public long CheckedUtc { get; set; }
        public string Stats { get; set; } = "";
    }

    static State LoadState()
    {
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new(); }
        catch { return new(); }
    }

    /// <summary>The script added to every tab, or null before the first successful build.</summary>
    public static string? Payload { get; private set; }

    static YouTubeBlock() => Load();

    public static void Load()
    {
        try { if (File.Exists(PayloadFile)) Payload = File.ReadAllText(PayloadFile); }
        catch (Exception ex) { ErrorLog.Write("YouTubeBlock load", ex); }
    }

    public static bool Due() =>
        DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(LoadState().CheckedUtc) >= Interval;

    static bool busy;

    /// <summary>Downloads, rebuilds and stores the script. Returns a short status for diag.log.</summary>
    public static async Task<string> UpdateAsync(CoreWebView2Environment env, IntPtr hwnd)
    {
        if (busy) return "busy";
        busy = true;
        var state = LoadState();
        try
        {
            Directory.CreateDirectory(Path.Combine(Work, "lists"));
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mizar");

            // 1. uBO's code: only when GitHub has a newer release (or none is unpacked yet)
            var tag = await LatestUboTag(http);
            var uboDir = Path.Combine(Work, "ubo");
            if (tag != null && (tag != state.Ubo || !Directory.Exists(uboDir)))
            {
                await DownloadUbo(http, tag, uboDir);
                state.Ubo = tag;
            }
            if (!Directory.Exists(uboDir)) return "no uBO code";

            // 2. filter lists (a failed download keeps the previous copy)
            int listsOk = 0;
            foreach (var (file, urls) in Lists)
                if (await DownloadList(http, urls, Path.Combine(Work, "lists", file))) listsOk++;

            // 3. build in a hidden WebView with uBO's own parser and scriptlet maker
            await File.WriteAllTextAsync(Path.Combine(Work, "build.js"), ReadResource("YouTubeBuild.js"));
            await File.WriteAllTextAsync(Path.Combine(Work, "build.html"),
                "<!doctype html><meta charset=utf-8><script type=module src=build.js></script>");
            var json = await RunBuild(env, hwnd);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var err)) return "build error: " + FirstLine(err.GetString());
            var main = r.GetProperty("main").GetString() ?? "";
            var isolated = r.GetProperty("isolated").GetString() ?? "";
            var css = r.GetProperty("css").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            var stats = r.GetProperty("stats");
            int scriptlets = stats.GetProperty("scriptlets").GetInt32();
            if (scriptlets == 0 || main.Length < 1000) return $"build too small: scriptlets={scriptlets}";

            var payload = Wrap(main + "\n" + isolated, css);
            await File.WriteAllTextAsync(PayloadFile, payload);
            Payload = payload;
            state.CheckedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            state.Stats = $"ubo={state.Ubo} lists={listsOk}/{Lists.Length} scriptlets={scriptlets} css={css.Count} " +
                          $"skippedCss={stats.GetProperty("skippedCosmetic").GetInt32()} size={payload.Length / 1024}KB";
            return state.Stats;
        }
        catch (Exception ex) { ErrorLog.Write("YouTubeBlock update", ex); return "error: " + ex.Message; }
        finally
        {
            try { File.WriteAllText(StateFile, JsonSerializer.Serialize(state)); } catch { }
            busy = false;
        }
    }

    /// <summary>Runs only on YouTube hosts; hiding rules go in a constructed stylesheet (not subject to the page's CSP).</summary>
    static string Wrap(string code, List<string> css) =>
        "(function mizarYouTubeBlock() {\n" +
        "if (!/(^|\\.)(youtube|youtube-nocookie)\\.com$/.test(location.hostname)) return;\n" +
        "try { const s = new CSSStyleSheet(); s.replaceSync(" + JsonSerializer.Serialize(string.Join("\n", css)) + ");\n" +
        "  document.adoptedStyleSheets = [...document.adoptedStyleSheets, s]; } catch { }\n" +
        code + "\n})();";

    static async Task<string?> LatestUboTag(HttpClient http)
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{UboRepo}/releases/latest"));
            return doc.RootElement.GetProperty("tag_name").GetString();
        }
        catch (Exception ex) { ErrorLog.Write("YouTubeBlock tag", ex); return null; }
    }

    static async Task DownloadUbo(HttpClient http, string tag, string uboDir)
    {
        var url = $"https://github.com/{UboRepo}/releases/download/{tag}/uBlock0_{tag}.chromium.zip";
        var bytes = await http.GetByteArrayAsync(url);
        var tmp = uboDir + ".new";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            foreach (var e in zip.Entries)
            {
                // keep only uBlock0.chromium/js/... and uBlock0.chromium/lib/...
                var parts = e.FullName.Split('/', 2);
                if (parts.Length < 2 || !(parts[1].StartsWith("js/") || parts[1].StartsWith("lib/")) || e.Name.Length == 0) continue;
                var dest = Path.GetFullPath(Path.Combine(tmp, parts[1]));
                if (!dest.StartsWith(Path.GetFullPath(tmp) + Path.DirectorySeparatorChar)) continue; // no path escapes
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
            }
        }
        if (!File.Exists(Path.Combine(tmp, "js", "make-scriptlets.js"))) throw new InvalidDataException("unexpected uBO zip layout");
        if (Directory.Exists(uboDir)) Directory.Delete(uboDir, true);
        Directory.Move(tmp, uboDir);
    }

    static async Task<bool> DownloadList(HttpClient http, string[] urls, string dest)
    {
        foreach (var url in urls)
        {
            try
            {
                var s = await http.GetStringAsync(url);
                if (s.Length < 1000) continue;
                await File.WriteAllTextAsync(dest + ".tmp", s);
                File.Move(dest + ".tmp", dest, true);
                return true;
            }
            catch { }
        }
        return false;
    }

    static async Task<string> RunBuild(CoreWebView2Environment env, IntPtr hwnd)
    {
        CoreWebView2Controller? ctl = null;
        try
        {
            ctl = await env.CreateCoreWebView2ControllerAsync(hwnd);
            ctl.IsVisible = false;
            var cw = ctl.CoreWebView2;
            cw.SetVirtualHostNameToFolderMapping(Host, Work, CoreWebView2HostResourceAccessKind.Deny);
            cw.Navigate($"https://{Host}/build.html");
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                var r = await cw.ExecuteScriptAsync("window.__ytResult ? JSON.stringify(window.__ytResult) : ''");
                var s = JsonSerializer.Deserialize<string>(r);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return "{\"error\":\"timeout\"}";
        }
        finally { try { ctl?.Close(); } catch { } }
    }

    static string ReadResource(string name)
    {
        using var s = typeof(YouTubeBlock).Assembly.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    static string FirstLine(string? s) => (s ?? "").Split('\n')[0];
}
