using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mizar;

/// <summary>
/// Self-update from GitHub Releases (github.com/cezarmaxim1/Mizar, public).
///
/// - Checked at most once every 30 days (one small HTTPS request, 1 minute after start), or on demand
///   from Settings. Nothing runs in the background between checks.
/// - A release carries Mizar-win-x64.zip and Mizar-win-x64.zip.sig. The .sig is an ECDSA P-256 signature
///   of "&lt;version&gt;\n&lt;sha256 of the zip&gt;", made with a private key that lives only on the owner's PC
///   (%USERPROFILE%\.mizar-release\signing-key.pem, never in the repo). A zip whose signature doesn't match
///   the public key below is thrown away, so a hacked GitHub account can't push a modified Mizar, and an old
///   signed zip can't be passed off as a newer version.
/// - A verified update is unpacked to %LOCALAPPDATA%\Mizar\update\staged and swapped in when Mizar closes
///   (running files are renamed to *.old, which Windows allows; they're deleted at the next start).
///   User data lives elsewhere and is never touched.
/// </summary>
static class Updater
{
    public const string Repo = "cezarmaxim1/Mizar";
    public const string AssetName = "Mizar-win-x64.zip";
    public static readonly TimeSpan Interval = TimeSpan.FromDays(30);

    /// <summary>ECDSA P-256 public key (SubjectPublicKeyInfo, base64). Pair of the owner's release key.</summary>
    const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE3sh1zoJxjYaC1mx8qH7RNAcEh3WKjCUGRw+kduVUTEUVyHBtz3NMabpKoWJykC3/kdnrbOpG7nU442DBkDiyeQ==";

    public static Version Current => Norm(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0));
    public static string CurrentText => Text(Current);

    static string InstallDir => AppContext.BaseDirectory;
    static string Dir => Path.Combine(AppPaths.Root, "update");
    static string Staged => Path.Combine(Dir, "staged");
    static string StagedVersionFile => Path.Combine(Dir, "staged.version");

    /// <summary>Only an installed copy updates itself, never a development build (bin\).</summary>
    public static bool CanUpdate =>
        !InstallDir.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

    static string LatestUrl =>
        Environment.GetEnvironmentVariable("MIZAR_UPDATE_URL") is { Length: > 0 } u ? u // tests
        : $"https://api.github.com/repos/{Repo}/releases/latest";

    public static string? StagedVersion =>
        File.Exists(StagedVersionFile) && File.Exists(Path.Combine(Staged, "Mizar.exe"))
            ? File.ReadAllText(StagedVersionFile).Trim() : null;

    static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
    static string Text(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    public record Result(bool Ready, string Version, string Message);

    /// <summary>
    /// Asks GitHub for the latest release. If it is newer, downloads it, checks the signature and
    /// unpacks it, ready to be swapped in when Mizar closes. Throws on network errors.
    /// </summary>
    public static async Task<Result> CheckAndStage()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mizar-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestUrl));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed))
            return new(false, tag, $"Versiune necunoscută pe GitHub: {tag}");
        var latest = Norm(parsed);
        var latestText = Text(latest);
        if (latest <= Current) return new(false, latestText, $"Ai ultima versiune ({CurrentText}).");
        if (StagedVersion == latestText) return new(true, latestText, $"Versiunea {latestText} e descărcată.");

        string? zipUrl = null, sigUrl = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            var url = a.GetProperty("browser_download_url").GetString();
            if (name == AssetName) zipUrl = url;
            else if (name == AssetName + ".sig") sigUrl = url;
        }
        if (zipUrl == null || sigUrl == null)
            return new(false, latestText, $"Versiunea {latestText} nu are fișierele de instalare.");

        var sig = Convert.FromBase64String((await http.GetStringAsync(sigUrl)).Trim());
        var zip = await http.GetByteArrayAsync(zipUrl);
        if (!Verify(zip, latestText, sig))
            return new(false, latestText, $"Versiunea {latestText} NU are o semnătură validă și a fost ignorată.");

        // unpack next to the data folder; the swap happens when Mizar closes
        if (Directory.Exists(Staged)) Directory.Delete(Staged, true);
        Directory.CreateDirectory(Staged);
        using (var ms = new MemoryStream(zip)) ZipFile.ExtractToDirectory(ms, Staged);
        var exe = Path.Combine(Staged, "Mizar.exe");
        var dll = Path.Combine(Staged, "Mizar.dll");
        if (!File.Exists(exe) || !File.Exists(dll))
        {
            Directory.Delete(Staged, true);
            return new(false, latestText, "Pachetul de actualizare e incomplet.");
        }
        File.WriteAllText(StagedVersionFile, latestText);
        return new(true, latestText, $"Versiunea {latestText} e gata de instalare.");
    }

    static byte[] Payload(byte[] zip, string version) =>
        Encoding.UTF8.GetBytes(version + "\n" + Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant());

    static bool Verify(byte[] zip, string version, byte[] sig)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
            return ec.VerifyData(Payload(zip, version), sig, HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    /// <summary>
    /// Swaps the staged files in. Called after the window has closed. Running files are renamed to
    /// *.old first (allowed by Windows); on any error everything is put back.
    /// </summary>
    public static bool ApplyStaged()
    {
        if (StagedVersion == null || !CanUpdate) return false;
        var moved = new List<string>();
        try
        {
            foreach (var src in Directory.EnumerateFiles(Staged, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(InstallDir, Path.GetRelativePath(Staged, src));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest))
                {
                    File.Move(dest, dest + ".old", true);
                    moved.Add(dest);
                }
                File.Copy(src, dest, true);
            }
            Directory.Delete(Staged, true);
            File.Delete(StagedVersionFile);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Update apply", ex);
            foreach (var dest in moved)
                try { File.Move(dest + ".old", dest, true); } catch { }
            try { Directory.Delete(Staged, true); File.Delete(StagedVersionFile); } catch { }
            return false;
        }
    }

    /// <summary>Removes the *.old files left by the last update (they were in use until now).</summary>
    public static void CleanupOld()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(InstallDir, "*.old", SearchOption.AllDirectories))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    public static void Restart()
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(InstallDir, "Mizar.exe")) { UseShellExecute = true }); }
        catch (Exception ex) { ErrorLog.Write("Update restart", ex); }
    }

    // ---------- Release tools (used by release.ps1 on the owner's PC) ----------

    /// <summary>mizar.exe --release-genkey &lt;keyfile&gt;: new private key (PEM) plus &lt;keyfile&gt;.pub (base64).</summary>
    public static void GenKey(string keyFile)
    {
        if (File.Exists(keyFile)) throw new IOException("Key already exists: " + keyFile);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyFile))!);
        File.WriteAllText(keyFile, ec.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(keyFile + ".pub", Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>mizar.exe --release-sign &lt;zip&gt; &lt;version&gt; &lt;keyfile&gt;: writes &lt;zip&gt;.sig.</summary>
    public static void Sign(string zipFile, string version, string keyFile)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(File.ReadAllText(keyFile));
        var zip = File.ReadAllBytes(zipFile);
        var sig = ec.SignData(Payload(zip, version), HashAlgorithmName.SHA256);
        if (!Verify(zip, version, sig)) throw new InvalidOperationException("Key doesn't match the public key built into Mizar.");
        File.WriteAllText(zipFile + ".sig", Convert.ToBase64String(sig));
    }
}

public partial class MainForm
{
    /// <summary>Set when the user chose "restart now": Program relaunches Mizar after the swap.</summary>
    public bool RestartAfterUpdate { get; private set; }
    bool updateBusy;

    /// <summary>At most once every 30 days, a minute after start: one request to GitHub.</summary>
    async Task AutoCheckForUpdates()
    {
        if (!settings.AutoUpdate || !Updater.CanUpdate) return;
        var last = DateTimeOffset.FromUnixTimeSeconds(settings.UpdateLastCheckUtc);
        if (DateTimeOffset.UtcNow - last < Updater.Interval) return;
        await Task.Delay(TimeSpan.FromMinutes(1));
        await CheckForUpdates(manual: false);
    }

    /// <summary>Settings → "Verifică acum", or the monthly check. Manual checks always report back.</summary>
    public async Task CheckForUpdates(bool manual)
    {
        if (updateBusy) return;
        if (!Updater.CanUpdate)
        {
            if (manual) MessageBox.Show(this, "Aceasta e o versiune de dezvoltare (bin\\); actualizările se aplică doar copiei instalate.", "Mizar");
            return;
        }
        updateBusy = true;
        try
        {
            var r = await Updater.CheckAndStage();
            settings.UpdateLastCheckUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!IsSelfTest) settings.Save();
            if (!r.Ready)
            {
                if (manual) MessageBox.Show(this, r.Message, "Mizar");
                return;
            }
            var answer = MessageBox.Show(this,
                $"Versiunea {r.Version} a lui Mizar e descărcată (ai {Updater.CurrentText}).\n\n" +
                "Repornesc acum? Tab-urile deschise se redeschid automat.\nDacă alegi Nu, se instalează data viitoare când închizi Mizar.",
                "Actualizare Mizar", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes)
            {
                SaveReopenOnce();
                RestartAfterUpdate = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Update check", ex);
            if (manual) MessageBox.Show(this, "Nu am putut verifica actualizările: " + ex.Message, "Mizar",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { updateBusy = false; }
    }
}
