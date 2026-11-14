using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mizar;

/// <summary>
/// Direct Google Drive REST API v3 sync for bookmarks.json (zero background processes, zero idle RAM).
/// Uses OAuth 2.0 + PKCE with drive.file scope so Mizar only sees its own file.
/// Tokens are encrypted at rest with Windows DPAPI (CurrentUser).
/// </summary>
static class DriveSync
{
    const string Scope = "https://www.googleapis.com/auth/drive.file";
    const string FileName = "mizar-bookmarks.json";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    sealed class TokenPayload
    {
        public string RefreshToken { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public long ExpiresAtUtc { get; set; }
    }

    public static bool IsConnected(Settings s) => !string.IsNullOrWhiteSpace(s.DriveTokenEnc);

    // ---------- Windows DPAPI (zero extra NuGet packages) ----------

    [StructLayout(LayoutKind.Sequential)]
    struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr hMem);

    static string Protect(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        var inBlob = new DATA_BLOB { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, inBlob.pbData, bytes.Length);
            if (!CryptProtectData(ref inBlob, "MizarDrive", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var outBlob))
                throw new InvalidOperationException("DPAPI CryptProtectData failed.");
            try
            {
                var res = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, res, 0, res.Length);
                return Convert.ToBase64String(res);
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Marshal.FreeHGlobal(inBlob.pbData); }
    }

    static string? Unprotect(string? enc)
    {
        if (string.IsNullOrWhiteSpace(enc)) return null;
        try
        {
            var bytes = Convert.FromBase64String(enc);
            var inBlob = new DATA_BLOB { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
            try
            {
                Marshal.Copy(bytes, 0, inBlob.pbData, bytes.Length);
                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var outBlob))
                    return null;
                try
                {
                    var res = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, res, 0, res.Length);
                    return Encoding.UTF8.GetString(res);
                }
                finally { LocalFree(outBlob.pbData); }
            }
            finally { Marshal.FreeHGlobal(inBlob.pbData); }
        }
        catch { return null; }
    }

    static TokenPayload? LoadTokens(Settings s)
    {
        var json = Unprotect(s.DriveTokenEnc);
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<TokenPayload>(json); } catch { return null; }
    }

    static void SaveTokens(Settings s, TokenPayload t)
    {
        s.DriveTokenEnc = Protect(JsonSerializer.Serialize(t));
        s.Save();
    }

    public static void Disconnect(Settings s)
    {
        s.DriveTokenEnc = null;
        s.DriveEmail = null;
        s.DriveFileId = null;
        s.DriveLastSyncUtc = 0;
        s.Save();
    }

    // ---------- OAuth 2.0 + PKCE Loopback ----------

    static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static int FindFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public static async Task ConnectAsync(Settings s, Action<string> openUrl, CancellationToken ct = default)
    {
        var clientId = s.DriveClientId?.Trim() ?? "";
        if (clientId.Length == 0)
            throw new InvalidOperationException("Lipsește Client ID-ul Google Drive (Setări API).");

        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64Url(verifierBytes);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        int port = FindFreePort();
        var redirectUri = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var authUrl = "https://accounts.google.com/o/oauth2/v2/auth?" +
            $"client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&response_type=code" +
            $"&scope={Uri.EscapeDataString(Scope)}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            "&code_challenge_method=S256" +
            $"&state={Uri.EscapeDataString(state)}" +
            "&access_type=offline&prompt=consent";

        openUrl(authUrl);

        string? code = null;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
        using (timeoutCts.Token.Register(() => { try { listener.Stop(); } catch { } }))
        {
            while (true)
            {
                var ctx = await listener.GetContextAsync();
                var reqUrl = ctx.Request.Url;
                var qs = System.Web.HttpUtility.ParseQueryString(reqUrl?.Query ?? "");
                if (reqUrl?.AbsolutePath != "/")
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    continue;
                }
                var err = qs["error"];
                var gotState = qs["state"];
                code = qs["code"];
                string html = err == null && gotState == state && !string.IsNullOrEmpty(code)
                    ? "<!doctype html><meta charset='utf-8'><title>Mizar</title><body style='font-family:Segoe UI,sans-serif;background:#131316;color:#eee;display:flex;align-items:center;justify-content:center;height:100vh;margin:0'><div style='text-align:center'><h2>Conectat la Google Drive</h2><p>Poți închide acest tab și reveni la Setări.</p></div></body>"
                    : "<!doctype html><meta charset='utf-8'><title>Mizar</title><body style='font-family:Segoe UI,sans-serif;background:#131316;color:#eee;display:flex;align-items:center;justify-content:center;height:100vh;margin:0'><div style='text-align:center'><h2>Conectare anulată</h2><p>Poți închide acest tab.</p></div></body>";
                var buf = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = buf.Length;
                await ctx.Response.OutputStream.WriteAsync(buf);
                ctx.Response.Close();

                if (err != null) throw new InvalidOperationException("Google OAuth: " + err);
                if (gotState != state || string.IsNullOrEmpty(code)) throw new InvalidOperationException("Răspuns OAuth invalid.");
                break;
            }
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code!,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        };
        if (!string.IsNullOrWhiteSpace(s.DriveClientSecret))
            form["client_secret"] = s.DriveClientSecret.Trim();

        using var res = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Token exchange failed: " + body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString() ?? "";
        var refresh = root.TryGetProperty("refresh_token", out var rf) ? rf.GetString() ?? "" : "";
        int expiresIn = root.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 3600;
        if (string.IsNullOrEmpty(refresh))
            throw new InvalidOperationException("Google nu a returnat refresh_token. Încearcă din nou.");

        SaveTokens(s, new TokenPayload
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn - 60
        });

        try { s.DriveEmail = await FetchEmailAsync(access, ct); s.Save(); } catch { }
    }

    static async Task<string?> EnsureAccessTokenAsync(Settings s, CancellationToken ct)
    {
        var tok = LoadTokens(s);
        if (tok == null || string.IsNullOrEmpty(tok.RefreshToken)) return null;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!string.IsNullOrEmpty(tok.AccessToken) && tok.ExpiresAtUtc > now)
            return tok.AccessToken;

        var clientId = s.DriveClientId?.Trim() ?? "";
        if (clientId.Length == 0) return null;

        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["refresh_token"] = tok.RefreshToken,
            ["grant_type"] = "refresh_token"
        };
        if (!string.IsNullOrWhiteSpace(s.DriveClientSecret))
            form["client_secret"] = s.DriveClientSecret.Trim();

        using var res = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            if (body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
                Disconnect(s);
            return null;
        }
        using var doc = JsonDocument.Parse(body);
        tok.AccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
        int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 3600;
        tok.ExpiresAtUtc = now + expiresIn - 60;
        SaveTokens(s, tok);
        return tok.AccessToken;
    }

    static async Task<string?> FetchEmailAsync(string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("user").GetProperty("emailAddress").GetString();
    }

    // ---------- Drive File Sync ----------

    static async Task<string?> FindFileIdAsync(string token, CancellationToken ct)
    {
        var q = Uri.EscapeDataString($"name='{FileName}' and trashed=false");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files?q={q}&spaces=drive&fields=files(id,modifiedTime)&pageSize=1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var files = doc.RootElement.GetProperty("files");
        return files.GetArrayLength() > 0 ? files[0].GetProperty("id").GetString() : null;
    }

    static async Task<string> CreateFileAsync(string token, string json, CancellationToken ct)
    {
        var boundary = "mizar_" + Guid.NewGuid().ToString("N");
        var meta = JsonSerializer.Serialize(new { name = FileName, mimeType = "application/json" });
        var body = new StringBuilder()
            .Append($"--{boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{meta}\r\n")
            .Append($"--{boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{json}\r\n")
            .Append($"--{boundary}--").ToString();

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(body, Encoding.UTF8);
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/related; boundary={boundary}");
        using var res = await Http.SendAsync(req, ct);
        var txt = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Create Drive file failed: " + txt);
        using var doc = JsonDocument.Parse(txt);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    static async Task<(bool found, string? json)> DownloadAsync(string token, string fileId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?alt=media");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await Http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return (false, null);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Drive download failed: {(int)res.StatusCode}");
        return (true, await res.Content.ReadAsStringAsync(ct));
    }

    static async Task PatchAsync(string token, string fileId, string json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(fileId)}?uploadType=media");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Drive upload failed: {(int)res.StatusCode}");
    }

    public enum SyncResult { NotConnected, UpToDate, Downloaded, Uploaded }

    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<SyncResult> SyncAsync(BookmarkStore local, Settings s, Action<BookmarkStore>? onDownloaded = null, CancellationToken ct = default)
    {
        if (!IsConnected(s)) return SyncResult.NotConnected;
        await Gate.WaitAsync(ct);
        try
        {
            var token = await EnsureAccessTokenAsync(s, ct);
            if (token == null) return SyncResult.NotConnected;

            var fileId = s.DriveFileId;
            if (string.IsNullOrEmpty(fileId))
            {
                fileId = await FindFileIdAsync(token, ct);
                if (!string.IsNullOrEmpty(fileId)) { s.DriveFileId = fileId; s.Save(); }
            }

            if (string.IsNullOrEmpty(fileId))
            {
                if (local.UpdatedAt == 0) local.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                s.DriveFileId = await CreateFileAsync(token, local.ToJson(), ct);
                s.DriveLastSyncUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                s.Save();
                return SyncResult.Uploaded;
            }

            var (found, remoteJson) = await DownloadAsync(token, fileId, ct);
            if (!found)
            {
                s.DriveFileId = await CreateFileAsync(token, local.ToJson(), ct);
                s.DriveLastSyncUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                s.Save();
                return SyncResult.Uploaded;
            }

            BookmarkStore? remote = null;
            if (!string.IsNullOrWhiteSpace(remoteJson))
                try { remote = JsonSerializer.Deserialize<BookmarkStore>(remoteJson); } catch { }

            if (remote != null && remote.UpdatedAt > local.UpdatedAt)
            {
                onDownloaded?.Invoke(remote);
                s.DriveLastSyncUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                s.Save();
                return SyncResult.Downloaded;
            }

            if (remote == null || local.UpdatedAt > remote.UpdatedAt)
            {
                if (local.UpdatedAt == 0) local.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await PatchAsync(token, fileId, local.ToJson(), ct);
                s.DriveLastSyncUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                s.Save();
                return SyncResult.Uploaded;
            }

            s.DriveLastSyncUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            s.Save();
            return SyncResult.UpToDate;
        }
        finally { Gate.Release(); }
    }

    public static async Task UploadAsync(BookmarkStore local, Settings s, CancellationToken ct = default)
    {
        if (!IsConnected(s)) return;
        await Gate.WaitAsync(ct);
        try
        {
            var token = await EnsureAccessTokenAsync(s, ct);
            if (token == null) return;
            if (local.UpdatedAt == 0) local.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var json = local.ToJson();
            if (string.IsNullOrEmpty(s.DriveFileId))
                s.DriveFileId = await FindFileIdAsync(token, ct);
            if (string.IsNullOrEmpty(s.DriveFileId))
                s.DriveFileId = await CreateFileAsync(token, json, ct);
            else
                await PatchAsync(token, s.DriveFileId, json, ct);
            s.DriveLastSyncUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            s.Save();
        }
        finally { Gate.Release(); }
    }
}
