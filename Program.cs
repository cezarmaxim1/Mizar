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

        ToolStripManager.Renderer = new ModernRenderer();

        // self-tests run next to the real browser on purpose; everything else is single-instance
        if (args.Contains("--selftest"))
        {
            Application.Run(new MainForm(args));
            return;
        }

        using var guard = SingleInstance.TryAcquire();
        if (guard == null)
        {
            var url = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "";
            if (SingleInstance.SendToRunning(url)) return;
            // the running instance did not answer (hung?): start anyway rather than lock the user out
        }

        var form = new MainForm(args);
        guard?.Listen(url =>
        {
            try { if (form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke(() => form.OpenFromOtherInstance(url)); }
            catch { }
        });
        Application.Run(form);
    }
}
