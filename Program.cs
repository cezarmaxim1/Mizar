namespace Mizar;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ErrorLog.Install();
        ApplicationConfiguration.Initialize();

        // maintenance commands (no window)
        if (args.Contains("--register")) { DefaultBrowser.Register(); return; }
        if (args.Contains("--unregister")) { DefaultBrowser.Unregister(); return; }
        // release tools (release.ps1): exit code 0 = ok
        if (args.Length >= 2 && args[0] is "--release-genkey" or "--release-sign")
        {
            try
            {
                if (args[0] == "--release-genkey") Updater.GenKey(args[1]);
                else if (args.Length >= 4) Updater.Sign(args[1], args[2], args[3]);
                else Environment.Exit(2);
            }
            catch (Exception ex) { File.WriteAllText(args[1] + ".error.txt", ex.ToString()); Environment.Exit(1); }
            return;
        }

        ToolStripManager.Renderer = new ModernRenderer();

        // self-tests run next to the real browser on purpose; everything else is single-instance
        if (args.Contains("--selftest"))
        {
            var test = new MainForm(args);
            Application.Run(test);
            if (args.Contains("--updatetest")) Updater.ApplyStaged();
            return;
        }

        using var guard = SingleInstance.TryAcquire();
        // maintenance commands for the running window: "--fixnet", "--netdiag [report file]"
        int nd = Array.IndexOf(args, "--netdiag");
        string? cmd = args.Contains("--fixnet") ? "mizar-cmd:fixnet"
                    : nd >= 0 ? "mizar-cmd:netdiag|" + (nd + 1 < args.Length ? args[nd + 1] : "") : null;
        if (cmd != null)
        {
            if (guard == null) SingleInstance.SendToRunning(cmd);
            return; // nothing to repair when Mizar isn't running
        }
        if (guard == null)
        {
            var url = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "";
            if (SingleInstance.SendToRunning(url)) return;
            // the running instance did not answer (hung?): start anyway rather than lock the user out
        }

        DiagLog.Write($"start v{Updater.CurrentText}");
        var form = new MainForm(args);
        guard?.Listen(form.EnqueueFromOtherInstance);
        Updater.CleanupOld();
        Application.Run(form);

        // a downloaded update is swapped in once the window has closed
        if (Updater.StagedVersion != null && Updater.ApplyStaged() && form.RestartAfterUpdate)
        {
            guard?.Dispose(); // let the new instance become the main one
            Updater.Restart();
        }
    }
}
