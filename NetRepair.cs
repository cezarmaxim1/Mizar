using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Mizar;

/// <summary>
/// Network repair and diagnostics.
///
/// Seen on 2026-11-14: Gmail stuck with "Not connected", every request to Google "Pending" for
/// minutes, while the same server answered fine outside Mizar. The engine had ONE connection to
/// Google (HTTP/2, all Google hosts share it) that had silently stopped carrying data, and it kept
/// queueing new requests on it. Closing the engine's connections ("Flush socket pools" in
/// edge://net-internals) makes it open fresh ones.
/// </summary>
public partial class MainForm
{
    DateTime lastFlush = DateTime.MinValue;
    bool flushing;

    // ---------- Don't freeze a tab in the middle of a request ----------
    //
    // All tabs share one HTTP/2 connection per server group (all Google hosts share one). A tab
    // frozen by TrySuspendAsync while a response is still arriving stops reading it; the unread
    // data can fill the connection's window and stall every other tab on it (suspected cause of the
    // Gmail stalls: the owner's running-tracker tab syncs with Google Sheets in the background).
    // So background tabs report their requests through CDP Network events, and a tab is put to sleep
    // only when none is in flight. Active tabs aren't watched (no cost while you use them).

    void WireNetWatch(BrowserTab tab, CoreWebView2 cw)
    {
        static string? Id(CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            try
            {
                using var d = JsonDocument.Parse(e.ParameterObjectAsJson);
                return d.RootElement.GetProperty("requestId").GetString();
            }
            catch { return null; }
        }
        var sent = cw.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        var fin = cw.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
        var fail = cw.GetDevToolsProtocolEventReceiver("Network.loadingFailed");
        tab.Receivers.AddRange(new object[] { sent, fin, fail });
        sent.DevToolsProtocolEventReceived += (_, e) =>
        {
            if (!tab.NetWatch) return;
            try
            {
                using var d = JsonDocument.Parse(e.ParameterObjectAsJson);
                var r = d.RootElement;
                var type = r.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type is "WebSocket" or "EventSource") return;
                var url = r.GetProperty("request").GetProperty("url").GetString();
                if (url == null || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return;
                if (r.GetProperty("requestId").GetString() is { } id) tab.InFlight[id] = DateTime.UtcNow;
            }
            catch { }
        };
        fin.DevToolsProtocolEventReceived += (_, e) => { if (tab.NetWatch && tab.InFlight.Count > 0 && Id(e) is { } id) tab.InFlight.Remove(id); };
        fail.DevToolsProtocolEventReceived += (_, e) => { if (tab.NetWatch && tab.InFlight.Count > 0 && Id(e) is { } id) tab.InFlight.Remove(id); };
    }

    async Task SetNetWatch(BrowserTab tab, bool on)
    {
        var cw = tab.Core;
        if (cw == null || tab.NetWatch == on || (on && !BackgroundNetWatch)) return;
        tab.NetWatch = on;
        tab.InFlight.Clear();
        try
        {
            // with PageAdBlock the domain stays on in every tab (the block list needs it)
            if (!PageAdBlock) await cw.CallDevToolsProtocolMethodAsync(on ? "Network.enable" : "Network.disable", "{}");
        }
        catch (Exception ex) { ErrorLog.Write("NetWatch", ex); }
    }

    /// <summary>
    /// Closes all of the engine's open network connections, the way edge://net-internals'
    /// "Flush socket pools" does. Uses a temporary invisible WebView that is closed right after.
    /// Returns "ok" or what went wrong.
    /// </summary>
    async Task<string> FlushSockets()
    {
        if (envTask == null) return "no engine";
        if (flushing) return "busy";
        flushing = true;
        CoreWebView2Controller? ctl = null;
        try
        {
            var env = await envTask;
            ctl = await env.CreateCoreWebView2ControllerAsync(Handle);
            ctl.IsVisible = false;
            var cw = ctl.CoreWebView2;
            var nav = new TaskCompletionSource<bool>();
            cw.NavigationCompleted += (_, e) => nav.TrySetResult(e.IsSuccess);
            cw.Navigate("edge://net-internals/#sockets");
            if (await Task.WhenAny(nav.Task, Task.Delay(8000)) != nav.Task) return "timeout";
            if (!nav.Task.Result) return "page blocked";
            for (int i = 0; i < 15; i++)
            {
                var r = await cw.ExecuteScriptAsync(
                    "(() => { const b = document.getElementById('sockets-view-flush-button'); if (!b) return 'missing'; b.click(); return 'ok'; })()");
                if (r == "\"ok\"") { lastFlush = DateTime.Now; return "ok"; }
                await Task.Delay(300);
            }
            return "button missing";
        }
        catch (Exception ex) { ErrorLog.Write("FlushSockets", ex); return "error: " + ex.Message; }
        finally
        {
            flushing = false;
            try { ctl?.Close(); } catch { }
        }
    }

    /// <summary>
    /// Menu "Repară conexiunea": notes the page's state in diag.log, closes stale connections,
    /// removes the site's service worker (the real cause of the 2026-11-15 Gmail stall: a wedged
    /// worker kept every image/font fetch pending, and neither F5 nor a socket flush touched it),
    /// then reloads the active page. Cookies and logins are not touched.
    /// </summary>
    async Task RepairConnection()
    {
        var cw = active?.Core;
        var host = cw == null ? "?" : Bookmark.HostOf(cw.Source);
        var before = cw == null ? "" : await PageProbe(cw);
        if (cw != null && before.Contains("sw=activated")) await BlackBox(cw, host);
        var flush = await FlushSockets();
        var sw = cw == null ? "no page" : await ResetServiceWorkers(cw);
        DiagLog.Write($"repair {DH(host)}: before [{before}] flush={flush} sw={sw}");
        if (cw != null && (flush == "ok" || sw.StartsWith("unregister")))
        {
            try { cw.Reload(); } catch { }
            _ = LogAfterRepair(cw, host);
        }
        else if (!IsSelfTest)
            MessageBox.Show(this, $"Conexiunea nu a putut fi resetată ({flush}; {sw}).", "Mizar",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Stops the engine's running service workers and removes the registrations (and their Cache
    /// Storage) of the page's origin. The site recreates them on the next load.
    /// </summary>
    static async Task<string> ResetServiceWorkers(CoreWebView2 cw)
    {
        if (!Uri.TryCreate(cw.Source, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
            return "not a web page";
        // 1) unregister from the page, like edge://serviceworker-internals -> Unregister (fixed it by
        //    hand on 2026-11-15); enough for a worker stuck "starting" (--stucktest);
        // 2) then remove the site's workers outright (Storage.clearDataForOrigin, service_workers only):
        //    a running worker keeps controlling its open page after a plain unregister (--healtest).
        // No ServiceWorker.stopAllWorkers and no cache_storage any more: on 2026-11-16 and 2026-11-17 the
        // whole browser process died around a reset that included them (unproven which step; in tests
        // only attaching to the stuck worker crashed anything, and the black box no longer does that).
        try
        {
            var page = await Eval(cw, UnregisterJs);
            var args = JsonSerializer.Serialize(new { origin = u.GetLeftPart(UriPartial.Authority), storageTypes = "service_workers" });
            var cleared = await Cdp(cw, "Storage.clearDataForOrigin", args, 5000);
            return $"unregister {page}, removed {(cleared == null ? "TIMEOUT" : cleared.Contains("error") ? "error" : "ok")}";
        }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    const string UnregisterJs = "(async()=>{" + ProbeHelpers +
        "return await T(()=>navigator.serviceWorker.getRegistrations().then(rs=>Promise.all(rs.map(x=>x.unregister()))).then(a=>a.filter(Boolean).length))})()";

    /// <summary>
    /// What the page looks like right now, without touching the network: images still loading or
    /// broken, how many resources it loaded, and whether a service worker controls the page.
    /// </summary>
    static async Task<string> PageProbe(CoreWebView2 cw)
    {
        const string js = "(() => { try { const im=[...document.images]; const res=performance.getEntriesByType('resource'); " +
            "const c=navigator.serviceWorker && navigator.serviceWorker.controller; " +
            "return 'ready=' + document.readyState + ' img=' + im.length + ' imgPending=' + im.filter(i=>!i.complete).length + " +
            "' imgBroken=' + im.filter(i=>i.complete && i.naturalWidth===0).length + " +
            "' pendingSameSite=' + im.filter(i=>!i.complete && i.src.startsWith(location.origin)).length + " +
            "' pendingCrossSite=' + im.filter(i=>!i.complete && /^https?:/.test(i.src) && !i.src.startsWith(location.origin)).length + " +
            "' res=' + res.length + ' resCrossSite=' + res.filter(r=>!r.name.startsWith(location.origin)).length + " +
            "' fonts=' + (document.fonts ? document.fonts.status : '?') + ' sw=' + (c ? c.state : 'none'); } catch(e) { return 'probe error ' + e.message; } })()";
        try
        {
            var t = cw.ExecuteScriptAsync(js);
            if (await Task.WhenAny(t, Task.Delay(3000)) != t) return "probe timeout (page not responding)";
            return JsonSerializer.Deserialize<string>(t.Result) ?? "";
        }
        catch (Exception ex) { return "probe error " + ex.Message; }
    }

    async Task LogAfterRepair(CoreWebView2 cw, string host)
    {
        try
        {
            await Task.Delay(10000);
            if (IsDisposed) return;
            DiagLog.Write($"repair {DH(host)}: after 10 s [{await PageProbe(cw)}]");
        }
        catch { }
    }

    /// <summary>
    /// Logging only (no automatic repair: that could cut a download). Once per navigation, when the
    /// active tab has been loading for 20 s, notes its state in diag.log.
    /// </summary>
    async void LogSlowLoad(BrowserTab? tab)
    {
        if (tab?.Core == null || !tab.IsLoading || tab.SlowLogged) return;
        if ((DateTime.Now - tab.LoadStarted).TotalSeconds < SlowLoadSeconds) return;
        tab.SlowLogged = true;
        try
        {
            var cw = tab.Core;
            var host = Bookmark.HostOf(tab.Url);
            var probe = await PageProbe(cw);
            DiagLog.Write($"slow load {DH(host)}: {SlowLoadSeconds} s [{probe}] tabs={tabs.Count} asleep={tabs.Count(t => t.Core?.IsSuspended == true)}");
            if (WedgedWorker(probe)) await HealWedgedWorker(tab, cw, host);
        }
        catch { }
    }

    int SlowLoadSeconds => startArgs.Contains("--healtest") ? 6 : 20;
    readonly Dictionary<string, DateTime> lastHeal = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The 2026-11-14/15/16 Gmail stall, as the page probe sees it: a service worker controls the page
    /// and (nearly) none of its images has loaded. Seen as img=99/99 and img=132/132 pending, sw=activated.
    /// </summary>
    static bool WedgedWorker(string probe)
    {
        static int Num(string p, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(p, @"\b" + key + @"=(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }
        int img = Num(probe, "img"), pending = Num(probe, "imgPending");
        return probe.Contains("sw=activated") && img >= 5 && pending >= img * 0.8;
    }

    /// <summary>
    /// Automatic version of what fixed it by hand: remove the site's service worker and reload that one
    /// page. No socket flush, so downloads and other tabs are untouched. At most once per 5 minutes per
    /// site; the page may still show its own "Reload site?" prompt (Gmail does).
    /// </summary>
    async Task HealWedgedWorker(BrowserTab tab, CoreWebView2 cw, string host)
    {
        if (lastHeal.TryGetValue(host, out var t) && DateTime.Now - t < TimeSpan.FromMinutes(5))
        {
            DiagLog.Write($"heal {DH(host)}: skipped (healed {(DateTime.Now - t).TotalSeconds:0} s ago)");
            return;
        }
        lastHeal[host] = DateTime.Now;
        await BlackBox(cw, host); // evidence first: after the reset there is nothing left to look at
        var sw = await ResetServiceWorkers(cw);
        DiagLog.Write($"heal {DH(host)}: service worker reset={sw}, reloading");
        if (sw.StartsWith("error") || sw == "not a web page") return;
        try { cw.Reload(); } catch { }
        _ = LogAfterRepair(cw, host);
    }


    // ---------- Black box: what is the stuck worker doing? ----------

    /// <summary>CDP call that gives up after <paramref name="ms"/> (a wedged target may never answer).</summary>
    static async Task<string?> Cdp(CoreWebView2 cw, string method, string args, int ms = 4000, string? session = null)
    {
        try
        {
            var t = session == null ? cw.CallDevToolsProtocolMethodAsync(method, args)
                                    : cw.CallDevToolsProtocolMethodForSessionAsync(session, method, args);
            return await Task.WhenAny(t, Task.Delay(ms)) == t ? t.Result : null;
        }
        catch (Exception ex) { return "{\"error\":" + JsonSerializer.Serialize(ex.Message) + "}"; }
    }

    /// <summary>Runs an async JS expression in a page or worker and returns its string result, "TIMEOUT" or the error.</summary>
    static async Task<string> Eval(CoreWebView2 cw, string expr, string? session = null)
    {
        var r = await Cdp(cw, "Runtime.evaluate", JsonSerializer.Serialize(new { expression = expr, awaitPromise = true, returnByValue = true }), 9000, session);
        if (r == null) return "NO ANSWER (JS thread busy or blocked)";
        try
        {
            using var d = JsonDocument.Parse(r);
            if (d.RootElement.TryGetProperty("error", out var e)) return "error " + e;
            if (d.RootElement.TryGetProperty("exceptionDetails", out var x)) return "exception " + (x.TryGetProperty("text", out var tx) ? tx.GetString() : "");
            return d.RootElement.GetProperty("result").TryGetProperty("value", out var v) ? v.ToString() : "?";
        }
        catch { return "unparsable"; }
    }

    // each step: "ok:<value>", "err:<name>" or "TIMEOUT" after 3 s; never touches page content
    const string ProbeHelpers =
        "const T=(f,ms)=>{let p;try{p=f()}catch(e){return Promise.resolve('throw:'+e.name)}" +
        "return Promise.race([Promise.resolve(p).then(v=>'ok:'+v,e=>'err:'+(e&&e.name)),new Promise(r=>setTimeout(()=>r('TIMEOUT'),ms||3000))])};";

    // runs every step of a probe at once (each with its own 3 s limit), keeping the keys in order
    const string RunAll = "const o=Object.fromEntries(Object.keys(s).map(k=>[k,'']));" +
        "await Promise.all(Object.entries(s).map(async([k,f])=>{o[k]=await T(f)}));";

    const string PageBox = "(async()=>{" + ProbeHelpers +
        "const pi=[...document.images].find(i=>!i.complete&&/^https?:/.test(i.src));" +
        "const s={" +
        "fetchViaWorker:()=>fetch(location.origin+'/favicon.ico?mz='+Date.now(),{cache:'no-store'}).then(r=>r.status)," +
        "stuckImageRefetch:()=>pi?fetch(pi.src,{mode:'no-cors'}).then(r=>r.type):'none'," +
        "caches:()=>caches.keys().then(k=>k.length)," +
        "cacheOpen:()=>caches.open('mizar-probe').then(()=>caches.delete('mizar-probe'))," +
        "idb:()=>indexedDB.databases().then(d=>d.length)," +
        "storage:()=>navigator.storage.estimate().then(e=>Math.round(e.usage/1048576)+'MB')," +
        "regs:()=>navigator.serviceWorker.getRegistrations().then(r=>r.map(x=>(x.active?'A:'+x.active.state:'')+(x.waiting?' W':'')+(x.installing?' I':'')).join('|'))};" +
        RunAll +
        "const c=navigator.serviceWorker.controller;o.controller=c?c.state:'none';" +
        "return JSON.stringify(o)})()";

    const string PageFetchOnly = "(async()=>{" + ProbeHelpers +
        "const a=await T(()=>fetch(location.origin+'/favicon.ico?mz='+Date.now(),{cache:'no-store'}).then(r=>r.status));" +
        "const pi=[...document.images].find(i=>!i.complete&&/^https?:/.test(i.src));" +
        "const b=pi?await T(()=>fetch(pi.src,{mode:'no-cors'}).then(r=>r.type)):'none';" +
        "return 'favicon='+a+' stuckImage='+b})()";

    /// <summary>
    /// Recorded in diag.log just before a stuck page is repaired, so every stall leaves evidence of
    /// WHERE it hangs: (1) the page's own view: does a fetch through the worker answer, do Cache Storage
    /// and IndexedDB answer; (2) the same fetch with the worker bypassed: if that one works, the network
    /// is fine and the worker is the blocker; (3) the engine's view of the worker (status, running
    /// status, reported errors); (4) whether the worker exists as a debug target (it is never attached:
    /// that crashed a stuck worker's page in tests). Hosts and states only; about 10 s when things hang.
    /// </summary>
    async Task BlackBox(CoreWebView2 cw, string host)
    {
        try
        {
            DiagLog.Write($"blackbox {DH(host)} page: {await Eval(cw, PageBox)}");

            await Cdp(cw, "Network.enable", NetEnableArgs);
            await Cdp(cw, "Network.setBypassServiceWorker", "{\"bypass\":true}");
            DiagLog.Write($"blackbox {DH(host)} fetch bypassing worker: {await Eval(cw, PageFetchOnly)}");
            await Cdp(cw, "Network.setBypassServiceWorker", "{\"bypass\":false}");
            if (active?.Core == cw && !PageAdBlock) await Cdp(cw, "Network.disable", "{}"); // background tabs keep their watch

            // the engine's view: versions and errors arrive as events once the domain is enabled
            var versions = new List<string>(); var errors = new List<string>();
            var rv = cw.GetDevToolsProtocolEventReceiver("ServiceWorker.workerVersionUpdated");
            var re = cw.GetDevToolsProtocolEventReceiver("ServiceWorker.workerErrorReported");
            void OnV(object? s, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
            {
                try
                {
                    using var d = JsonDocument.Parse(e.ParameterObjectAsJson);
                    foreach (var v in d.RootElement.GetProperty("versions").EnumerateArray())
                    {
                        var url = v.TryGetProperty("scriptURL", out var u) ? u.GetString() ?? "" : "";
                        if (!url.Contains(host, StringComparison.OrdinalIgnoreCase)) continue;
                        string G(string k) => v.TryGetProperty(k, out var p) ? p.ToString() : "?";
                        versions.Add($"status={G("status")} running={G("runningStatus")} clients={(v.TryGetProperty("controlledClients", out var cc) ? cc.GetArrayLength() : 0)}");
                    }
                }
                catch { }
            }
            void OnE(object? s, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
            {
                try
                {
                    using var d = JsonDocument.Parse(e.ParameterObjectAsJson);
                    var m = d.RootElement.GetProperty("errorMessage").GetProperty("errorMessage").GetString() ?? "";
                    errors.Add(m.Length > 160 ? m[..160] : m);
                }
                catch { }
            }
            rv.DevToolsProtocolEventReceived += OnV; re.DevToolsProtocolEventReceived += OnE;
            await Cdp(cw, "ServiceWorker.enable", "{}");
            await Task.Delay(800);
            await Cdp(cw, "ServiceWorker.disable", "{}");
            rv.DevToolsProtocolEventReceived -= OnV; re.DevToolsProtocolEventReceived -= OnE;
            DiagLog.Write($"blackbox {DH(host)} engine: {(versions.Count == 0 ? "no worker version reported" : string.Join(" ; ", versions.Distinct()))}" +
                          (errors.Count > 0 ? " errors: " + string.Join(" | ", errors.Distinct().Take(5)) : ""));

            // the worker as a debug target
            var targets = await Cdp(cw, "Target.getTargets", "{}");
            string? targetId = null;
            if (targets != null)
            {
                using var d = JsonDocument.Parse(targets);
                if (d.RootElement.TryGetProperty("targetInfos", out var infos))
                    foreach (var t in infos.EnumerateArray())
                        if (t.GetProperty("type").GetString() == "service_worker" &&
                            (t.GetProperty("url").GetString() ?? "").Contains("://" + host, StringComparison.OrdinalIgnoreCase))
                        { targetId = t.GetProperty("targetId").GetString(); break; }
            }
            if (targetId == null) { DiagLog.Write($"blackbox {DH(host)} worker: no worker target (not running)"); return; }
            // Not attaching to it any more: on 2026-11-17 a test worker stuck "starting" crashed its
            // page's process (0xC0000005) every time the black box attached to it and evaluated code there,
            // and the owner's engine died 3 s after the same step. Its presence and type are enough.
            DiagLog.Write($"blackbox {DH(host)} worker: target present (not attached)");
        }
        catch (Exception ex) { DiagLog.Write($"blackbox {DH(host)}: failed {ex.Message}"); }
    }

    /// <summary>After the laptop wakes up, old connections are often dead: start with fresh ones.</summary>
    void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        DiagLog.Write("power resume");
        try { BeginInvoke(() => { RecalcFrame(); EnsureClientOnScreen("power resume"); }); } catch { }
        try
        {
            BeginInvoke(async () =>
            {
                await Task.Delay(3000);
                if (!flushing && (DateTime.Now - lastFlush).TotalSeconds >= 10)
                    await FlushSockets();
            });
        }
        catch { }
    }

    /// <summary>
    /// Commands sent by a second launch through the pipe ("mizar-cmd:&lt;name&gt;|&lt;arg&gt;").
    /// "netdiag|&lt;file&gt;": diagnose the active tab, flush, diagnose again, write the report.
    /// "fixnet": same as the menu's "Repară conexiunea".
    /// </summary>
    async Task RunCommand(string cmd)
    {
        var parts = cmd.Split('|', 2);
        switch (parts[0])
        {
            case "fixnet": await RepairConnection(); break;
            case "netdiag": await RunNetDiag(parts.Length > 1 && parts[1].Length > 0 ? parts[1] : Path.Combine(AppPaths.Root, "netdiag.txt")); break;
            case "reopen-close": SaveReopenOnce(); Close(); break;
        }
    }

    /// <summary>
    /// Reloads the active tab with CDP Network events on and reports, after 15 s, how many requests
    /// finished, failed or are still pending (host + resource type only, never paths or content).
    /// Then flushes the connections and does it again, so the report shows whether that fixed it.
    /// </summary>
    async Task RunNetDiag(string outFile)
    {
        var sb = new StringBuilder();
        var cw = active?.Core;
        sb.AppendLine($"Mizar netdiag {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (cw == null) { sb.AppendLine("no active page"); WriteReport(outFile, sb); return; }
        sb.AppendLine($"active host: {Bookmark.HostOf(cw.Source)}, tabs: {tabs.Count}, suspended: {tabs.Count(t => t.Core?.IsSuspended == true)}");

        var pending = new Dictionary<string, (string host, string type, DateTime at)>();
        int finished = 0;
        var failed = new List<string>();
        void Sent(object? s, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            using var d = JsonDocument.Parse(e.ParameterObjectAsJson);
            var r = d.RootElement;
            var id = r.GetProperty("requestId").GetString() ?? "";
            var url = r.GetProperty("request").GetProperty("url").GetString() ?? "";
            if (!url.StartsWith("http")) return;
            var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";
            var type = r.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            pending[id] = (host, type, DateTime.Now);
        }
        void Finished(object? s, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            using var d = JsonDocument.Parse(e.ParameterObjectAsJson);
            if (pending.Remove(d.RootElement.GetProperty("requestId").GetString() ?? "")) finished++;
        }
        void Failed(object? s, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            using var d = JsonDocument.Parse(e.ParameterObjectAsJson);
            var r = d.RootElement;
            var id = r.GetProperty("requestId").GetString() ?? "";
            var reason = r.TryGetProperty("errorText", out var et) ? et.GetString() ?? "" : "";
            if (pending.Remove(id, out var p)) failed.Add($"{p.host} [{p.type}] {reason}");
        }

        var rSent = cw.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        var rFin = cw.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
        var rFail = cw.GetDevToolsProtocolEventReceiver("Network.loadingFailed");
        rSent.DevToolsProtocolEventReceived += Sent;
        rFin.DevToolsProtocolEventReceived += Finished;
        rFail.DevToolsProtocolEventReceived += Failed;
        try
        {
            await cw.CallDevToolsProtocolMethodAsync("Network.enable", NetEnableArgs);

            async Task Phase(string name)
            {
                sb.AppendLine($"page before reload: {await PageProbe(cw)}");
                pending.Clear(); failed.Clear(); finished = 0;
                cw.Reload();
                await Task.Delay(15000);
                sb.AppendLine($"--- {name}: finished={finished} failed={failed.Count} pending={pending.Count}");
                foreach (var g in pending.Values.GroupBy(p => $"{p.host} [{p.type}]").OrderByDescending(g => g.Count()))
                    sb.AppendLine($"  pending {g.Count()}x {g.Key}, oldest {(DateTime.Now - g.Min(p => p.at)).TotalSeconds:0} s");
                foreach (var f in failed.Take(30)) sb.AppendLine("  failed " + f);
            }

            await Phase("before flush");
            sb.AppendLine("flush: " + await FlushSockets());
            await Phase("after flush");
            sb.AppendLine("service workers: " + await ResetServiceWorkers(cw));
            await Phase("after service worker reset");
            if (!PageAdBlock) try { await cw.CallDevToolsProtocolMethodAsync("Network.disable", "{}"); } catch { }
        }
        catch (Exception ex) { sb.AppendLine("error: " + ex.Message); }
        finally
        {
            rSent.DevToolsProtocolEventReceived -= Sent;
            rFin.DevToolsProtocolEventReceived -= Finished;
            rFail.DevToolsProtocolEventReceived -= Failed;
        }
        WriteReport(outFile, sb);
        DiagLog.Write("netdiag: " + string.Join(" | ", sb.ToString().Split('\n').Where(l => l.StartsWith("---")).Select(l => l.Trim())));
    }

    static void WriteReport(string file, StringBuilder sb)
    {
        try { File.WriteAllText(file, sb.ToString(), Encoding.UTF8); }
        catch (Exception ex) { ErrorLog.Write("netdiag", ex); }
    }
}
