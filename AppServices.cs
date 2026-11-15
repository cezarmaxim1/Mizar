using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Mizar;

/// <summary>Built-in pages, served from a reserved host by MainForm (never reaches the network).</summary>
static class InternalPages
{
    public const string Base = "https://mizar.internal/";
    public const string History = Base + "history";
    public const string Crashed = Base + "crashed";

    public static bool IsInternal(string? url) =>
        url != null && url.StartsWith(Base, StringComparison.OrdinalIgnoreCase);

    const string Headers =
        "Content-Type: text/html; charset=utf-8\r\n" +
        "Cache-Control: no-store\r\n" +
        "X-Frame-Options: DENY\r\n" +
        "Content-Security-Policy: frame-ancestors 'none'";

    /// <summary>Builds the response for a mizar.internal request (null = not ours).</summary>
    public static (int status, string headers, byte[] body)? Serve(string url, HistoryStore history)
    {
        if (!IsInternal(url)) return null;
        var u = new Uri(url);
        string? html = u.AbsolutePath.TrimEnd('/') switch
        {
            "/history" => history.BuildPage(),
            "/crashed" => CrashedPage(System.Web.HttpUtility.ParseQueryString(u.Query)["u"] ?? ""),
            _ => null
        };
        return html == null
            ? (404, Headers, Encoding.UTF8.GetBytes("<!doctype html><title>Mizar</title>404"))
            : (200, Headers, Encoding.UTF8.GetBytes(html));
    }

    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static string CrashedPage(string target)
    {
        // only offer to retry real web addresses
        bool ok = Uri.TryCreate(target, UriKind.Absolute, out var t) && t.Scheme is "http" or "https";
        var enc = System.Net.WebUtility.HtmlEncode(ok ? target : "");
        var link = ok ? $"<a class=\"btn\" href=\"{enc}\">Încearcă din nou</a><p class=\"url\">{enc}</p>" : "";
        return $$"""
<!doctype html><html lang="ro"><head><meta charset="utf-8"><title>Pagina s-a blocat</title>
<style>
body{margin:0;background:{{Hex(Theme.Strip)}};color:{{Hex(Theme.Text)}};font:15px "Segoe UI",system-ui,sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh}
.box{max-width:560px;padding:32px;text-align:center}
h1{font-size:22px;font-weight:600;margin:0 0 12px}
p{color:{{Hex(Theme.Muted)}};line-height:1.5}
.btn{display:inline-block;margin-top:16px;padding:10px 22px;border-radius:999px;background:{{Hex(Theme.Accent)}};color:#fff;text-decoration:none}
.url{font-size:12.5px;word-break:break-all}
</style></head><body><div class="box">
<h1>Pagina s-a blocat de mai multe ori</h1>
<p>Am oprit reîncărcarea automată, ca să nu se repete la nesfârșit. Poți încerca din nou manual.</p>
{{link}}
</div></body></html>
""";
    }
}

/// <summary>
/// Last-resort error handling: unexpected exceptions are written to errors.log and the browser
/// keeps running instead of closing without a word.
/// </summary>
static class ErrorLog
{
    static readonly object Gate = new();

    public static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Write("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("Fatal", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write("Task", e.Exception); e.SetObserved(); };
    }

    public static void Write(string kind, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                var f = AppPaths.ErrorLog;
                if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024) File.Move(f, f + ".old", true);
                File.AppendAllText(f, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {ex}\n\n");
            }
        }
        catch { }
    }
}

/// <summary>
/// diag.log: one short line per noteworthy event (start, slow or failed page loads, engine process
/// failures, tabs put to sleep, connection repairs), so a problem can be looked up after the fact.
/// Hosts only, never paths, titles or page content. Rotated at 256 KB (one .old kept).
/// </summary>
static class DiagLog
{
    static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                var f = AppPaths.DiagLog;
                if (File.Exists(f) && new FileInfo(f).Length > 256 * 1024) File.Move(f, f + ".old", true);
                File.AppendAllText(f, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
            }
        }
        catch { }
    }
}

/// <summary>
/// Keeps Mizar to one process per data folder. A second launch hands its URL to the running
/// window through a named pipe and exits, so two instances never overwrite each other's files.
/// </summary>
sealed class SingleInstance : IDisposable
{
    readonly Mutex mutex;
    readonly CancellationTokenSource cts = new();

    static readonly string Id = Convert.ToHexString(
        SHA1.HashData(Encoding.UTF8.GetBytes(AppPaths.Root.ToLowerInvariant())))[..12];
    static string MutexName => @"Local\Mizar.Instance." + Id;
    static string PipeName => "Mizar.Open." + Id;

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    SingleInstance(Mutex m) { mutex = m; }

    /// <summary>Returns the guard if this is the first instance, otherwise null.</summary>
    public static SingleInstance? TryAcquire()
    {
        var m = new Mutex(true, MutexName, out bool created);
        if (created) return new SingleInstance(m);
        m.Dispose();
        return null;
    }

    /// <summary>Second instance: pass the URL (may be empty) to the first one. False if it didn't answer.</summary>
    public static bool SendToRunning(string url)
    {
        try
        {
            AllowSetForegroundWindow(-1); // let the running window come to the front
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(url);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return true;
        }
        catch { return false; }
    }

    /// <summary>First instance: call <paramref name="onOpen"/> (on a background thread) for every later launch.</summary>
    public void Listen(Action<string> onOpen)
    {
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, token);
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    if (text.Length <= 8192) onOpen(text);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    ErrorLog.Write("Pipe", ex);
                    try { await Task.Delay(1000, token); } catch { return; }
                }
            }
        });
    }

    public void Dispose()
    {
        cts.Cancel();
        try { mutex.ReleaseMutex(); } catch { }
        mutex.Dispose();
    }
}

/// <summary>
/// Registers Mizar with Windows as a web browser (per user, HKCU) so it can be chosen in
/// Settings > Apps > Default apps. Windows does not let programs set the default themselves.
/// </summary>
static class DefaultBrowser
{
    const string ProgId = "MizarHTML";
    const string ClientKey = @"Software\Clients\StartMenuInternet\Mizar";

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr a, IntPtr b);

    public static bool IsRegistered
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            return (k?.GetValue(null) as string)?.Contains(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    public static void Register()
    {
        var exe = Application.ExecutablePath;
        var open = $"\"{exe}\" \"%1\"";

        using (var cls = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            cls.SetValue(null, "Mizar HTML Document");
            cls.SetValue("FriendlyTypeName", "Mizar HTML Document");
            using (var icon = cls.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"{exe},0");
            using (var cmd = cls.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, open);
        }

        using (var client = Registry.CurrentUser.CreateSubKey(ClientKey))
        {
            client.SetValue(null, "Mizar");
            using (var icon = client.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"{exe},0");
            using (var cmd = client.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, $"\"{exe}\"");
            using var cap = client.CreateSubKey("Capabilities");
            cap.SetValue("ApplicationName", "Mizar");
            cap.SetValue("ApplicationDescription", "Browser minimalist, cu consum mic de memorie.");
            cap.SetValue("ApplicationIcon", $"{exe},0");
            using (var urls = cap.CreateSubKey("URLAssociations"))
            {
                urls.SetValue("http", ProgId);
                urls.SetValue("https", ProgId);
            }
            using (var files = cap.CreateSubKey("FileAssociations"))
            {
                files.SetValue(".htm", ProgId);
                files.SetValue(".html", ProgId);
            }
            using var sm = cap.CreateSubKey("StartMenu");
            sm.SetValue("StartMenuInternet", "Mizar");
        }

        using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            reg.SetValue("Mizar", ClientKey + @"\Capabilities");

        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false);
        Registry.CurrentUser.DeleteSubKeyTree(ClientKey, false);
        using (var reg = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))
            reg?.DeleteValue("Mizar", false);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Opens the Windows page where the user picks the default browser.</summary>
    public static void OpenWindowsSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "ms-settings:defaultapps?registeredAppUser=Mizar") { UseShellExecute = true });
        }
        catch { }
    }
}
