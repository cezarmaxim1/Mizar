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
        if (cw == null || tab.NetWatch == on) return;
        tab.NetWatch = on;
        tab.InFlight.Clear();
        try
        {
            await cw.CallDevToolsProtocolMethodAsync(on ? "Network.enable" : "Network.disable", "{}");
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

    /// <summary>Menu "Repară conexiunea": close stale connections, then reload the active page.</summary>
    async Task RepairConnection()
    {
        var r = await FlushSockets();
        if (r == "ok") active?.Core?.Reload();
        else MessageBox.Show(this, "Conexiunea nu a putut fi resetată (" + r + ").", "Mizar",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>After the laptop wakes up, old connections are often dead: start with fresh ones.</summary>
    void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
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
            await cw.CallDevToolsProtocolMethodAsync("Network.enable", "{}");

            async Task Phase(string name)
            {
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
            try { await cw.CallDevToolsProtocolMethodAsync("Network.disable", "{}"); } catch { }
        }
        catch (Exception ex) { sb.AppendLine("error: " + ex.Message); }
        finally
        {
            rSent.DevToolsProtocolEventReceived -= Sent;
            rFin.DevToolsProtocolEventReceived -= Finished;
            rFail.DevToolsProtocolEventReceived -= Failed;
        }
        WriteReport(outFile, sb);
    }

    static void WriteReport(string file, StringBuilder sb)
    {
        try { File.WriteAllText(file, sb.ToString(), Encoding.UTF8); }
        catch (Exception ex) { ErrorLog.Write("netdiag", ex); }
    }
}
