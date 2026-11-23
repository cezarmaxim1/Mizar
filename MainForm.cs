using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace Mizar;

public partial class MainForm : Form
{
    readonly Settings settings = Settings.Load();
    readonly BookmarkStore bookmarks = BookmarkStore.Load();
    readonly bool firstRun = !BookmarkStore.Exists;
    /// <summary>Hidden tabs get MemoryUsageTargetLevel.Low (ruled out for the Gmail stall on 2026-11-19).</summary>
    const bool LowMemoryForHiddenTabs = true;

    // These three were ruled out for the Gmail stall on 2026-11-20 (stalled with all three off).
    /// <summary>At start, also clear the engine's own browsing/download history (Mizar's history.json is cleared regardless).</summary>
    const bool ClearEngineHistoryAtStart = true;
    /// <summary>Background tabs turn on CDP Network to track requests in flight, so they aren't put to sleep mid-request.</summary>
    const bool BackgroundNetWatch = true;
    /// <summary>Engine flag --enable-features=HttpsUpgrades (HTTPS-First).</summary>
    const bool HttpsUpgrades = true;

    // Gmail experiment (STATUS.md): after ~84 h clean with both off (2026-11-20 08:17 -> 2026-11-23 20:48),
    // turned TabSleeping back on first; AdFilter stays off for the next 3-4 days to isolate them.
    /// <summary>Background tabs are suspended (TrySuspendAsync) after SleepAfter.</summary>
    const bool TabSleeping = true;
    /// <summary>Ad and tracker requests are filtered (AddWebResourceRequestedFilter per AdBlocker domain).</summary>
    const bool AdFilter = false;

    Task<CoreWebView2Environment>? envTask;
    /// <summary>Bumped when the engine is restarted, so late events from the dead engine are ignored.</summary>
    int engineGen;
    readonly List<DateTime> engineRestarts = new();
    readonly List<BrowserTab> tabs = new();
    BrowserTab? active;
    readonly Stack<string> closedTabs = new();
    readonly HistoryStore history = new();
    readonly ToolTip tips = new();
    readonly string[] startArgs;

    readonly HitTestPanel root = new();
    readonly TabStrip tabStrip = new();
    readonly TableLayoutPanel nav = new();
    readonly AddressBox addressBox = new();
    TextBox address => addressBox.Box;
    RoundButton btnBack = null!, btnFwd = null!, btnReload = null!, btnHome = null!, btnStar = null!, btnMenu = null!;
    readonly System.Windows.Forms.Timer sleepTimer = new() { Interval = 60_000 };
    static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(5);
    readonly BookmarkBar bar = new();
    readonly Panel content = new();
    readonly ContextMenuStrip menu = new();
    ToolStripMenuItem miShowBar = null!;
    bool fullscreen;
    FormWindowState prevState;
    bool selectAllOnClick;
    Image? globe, folderIcon;
    readonly SuggestBox suggest = new();
    readonly System.Windows.Forms.Timer sessionTimer = new() { Interval = 5000 };
    bool sessionDirty;
    FormWindowState lastWindowState = FormWindowState.Normal;
    readonly Queue<string> pendingPipeUrls = new();

    const string GBack = "\uE72B", GFwd = "\uE72A", GReload = "\uE72C", GStop = "\uE711", GHome = "\uE80F",
        GStar = "\uE734", GStarFull = "\uE735", GMore = "\uE712";

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    public MainForm(string[] args)
    {
        startArgs = args;
        history.ReadOnly = IsSelfTest && !AppPaths.IsCustomRoot; // tests on real data must not write history
        thirdPartyBlockedAtStart = settings.BlockThirdPartyCookies;
        Text = "Mizar";
        BackColor = Color.White;
        Theme.Dpi = appliedDpi = DeviceDpi;
        Font = Theme.UiFont(9f, DeviceDpi);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        MinimumSize = new Size(S(520), S(360));
        Size = new Size(S(1280), S(820));
        StartPosition = FormStartPosition.CenterScreen;
        RestoreWindow();
        lastWindowState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;

        globe = Icons.Globe(S(16));
        folderIcon = Icons.Folder(S(16));

        // Tab strip
        tabStrip.Tabs = tabs;
        tabStrip.Dock = DockStyle.Fill;
        tabStrip.Height = S(36);
        tabStrip.Margin = Padding.Empty;
        tabStrip.TabClicked += i => Activate(tabs[i]);
        tabStrip.TabCloseClicked += i => CloseTab(tabs[i]);
        tabStrip.TabMoved += () => sessionDirty = true;
        tabStrip.NewTabClicked += () => _ = NewTabFocused();
        tabStrip.TabRightClicked += ShowTabMenu;
        tabStrip.MinimizeClicked += () => WindowState = FormWindowState.Minimized;
        tabStrip.MaximizeClicked += () => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        tabStrip.CloseClicked += Close;
        tabStrip.EmptyAreaPressed += p =>
        {
            bool top = p.Y < S(4) && WindowState != FormWindowState.Maximized;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)(top ? Native.HTTOP : Native.HTCAPTION), IntPtr.Zero);
        };
        Resize += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized) lastWindowState = WindowState;
            bool max = WindowState == FormWindowState.Maximized;
            if (tabStrip.Maximized != max) { tabStrip.Maximized = max; tabStrip.Invalidate(); }
        };
        sleepTimer.Tick += async (_, _) => await SleepInactiveTabs();
        sleepTimer.Start();
        zoomPoll.Tick += (_, _) => { CheckZoom(active); LogSlowLoad(active); };
        zoomPoll.Start();

        BuildNav();
        BuildMenu();
        BuildBookmarkBar();

        content.Dock = DockStyle.Fill;
        content.Margin = Padding.Empty;
        content.BackColor = Color.White;

        root.Dock = DockStyle.Fill;
        // top-edge and empty tab-strip area fall through to the form (caption / resize)
        root.PassThrough = p => tabStrip.Visible && p.Y < tabStrip.Bottom &&
                                !tabStrip.IsInteractive(new Point(p.X - tabStrip.Left, p.Y - tabStrip.Top));
        root.ColumnCount = 1;
        // without an explicit 100% column the column only ever grows (stuck at the maximized width)
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowCount = 4;
        root.Margin = root.Padding = Padding.Empty;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(tabStrip, 0, 0);
        root.Controls.Add(nav, 0, 1);
        root.Controls.Add(bar, 0, 2);
        root.Controls.Add(content, 0, 3);
        Controls.Add(root);

        // suggestions drop-down floats above everything (a sibling of the layout panel)
        suggest.Picked += s => { HideSuggestions(); Navigate(s.Url); active?.View.Focus(); };
        Controls.Add(suggest);
        suggest.BringToFront();
        Resize += (_, _) => HideSuggestions();
        Deactivate += (_, _) => HideSuggestions();

        sessionTimer.Tick += (_, _) => SaveSession();
        sessionTimer.Start();
        driveUploadTimer.Tick += OnDriveUploadTick;

        bookmarks.Changed += () => { RebuildBookmarkBar(); if (!applyingRemoteSync && !IsSelfTest && DriveSync.IsConnected(settings)) ScheduleDriveUpload(); };
        if (firstRun)
        {
            try { bookmarks.ImportFromOpera(); } catch { }
            bookmarks.Save();
        }
        ApplyTheme();

        Shown += async (_, _) =>
        {
            ApplyDpi();
            var url = startArgs.FirstOrDefault(a => !a.StartsWith('-'));
            if (IsSelfTest)
            {
                var first = await CreateTab(url ?? settings.HomePage);
                int st = Array.IndexOf(startArgs, "--selftest");
                if (st + 2 < startArgs.Length && first?.Core != null) await RunSelfTest(first.Core, startArgs[st + 2]);
                return;
            }

            if (startArgs.Contains("--throw-test")) // diagnostics: prove an unexpected error is logged, not fatal
                BeginInvoke(() => throw new InvalidOperationException("Mizar --throw-test"));

            // last session's tabs come back as placeholders; each loads only when first shown (saves RAM)
            int restoreActive = -1;
            SessionData? restoreData = null;
            if (File.Exists(AppPaths.ReopenOnceFile))
            {
                restoreData = SessionData.Load(AppPaths.ReopenOnceFile);
                try { File.Delete(AppPaths.ReopenOnceFile); } catch { }
            }
            else if (settings.RestoreSession)
            {
                restoreData = SessionData.Load();
            }
            if (restoreData != null)
            {
                foreach (var t in restoreData.Tabs)
                    if (!string.IsNullOrWhiteSpace(t.Url)) AddLazyTab(t.Url, t.Title);
                if (tabs.Count > 0) restoreActive = Math.Clamp(restoreData.Active, 0, tabs.Count - 1);
            }

            if (url != null) await CreateTab(url);
            else if (restoreActive >= 0) Activate(tabs[restoreActive]);
            else await CreateTab(settings.HomePage);

            List<string> queued;
            lock (pendingPipeUrls)
            {
                sessionReady = true;
                queued = new List<string>(pendingPipeUrls);
                pendingPipeUrls.Clear();
            }
            foreach (var queuedUrl in queued) OpenFromOtherInstance(queuedUrl);

            // History is wiped on close. Also here, in case the last run didn't close cleanly,
            // and for the engine's own history (visited-link colours), which needs an async call.
            await LogGoogleCookies("start");
            if (settings.ClearHistoryOnExit)
            {
                if (ClearEngineHistoryAtStart)
                    await ClearData(CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory);
                else history.Clear();
                await LogGoogleCookies("after history wipe");
            }

            if (!File.Exists(OperaFavicons.Marker)) await ImportOperaFavicons();
            if (firstRun) _ = PrefetchFavicons();
            if (DriveSync.IsConnected(settings)) _ = SyncBookmarksAsync(silent: true);
            _ = AutoCheckForUpdates();
        };
        FormClosing += async (_, e) =>
        {
            if (cookiesLoggedAtClose) return;
            cookiesLoggedAtClose = true;
            e.Cancel = true;
            await LogGoogleCookies("close");
            BeginInvoke(Close);
        };
        // self-tests must never touch the owner's settings, history or session
        FormClosing += (_, e) =>
        {
            if (e.Cancel || IsSelfTest) return;
            SaveWindow();
            if (settings.ClearHistoryOnExit) history.Clear(); else history.Flush();
            sessionDirty = true;
            SaveSession();
        };

        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        FormClosed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            sleepTimer.Stop(); sleepTimer.Dispose();
            zoomPoll.Stop(); zoomPoll.Dispose();
            sessionTimer.Stop(); sessionTimer.Dispose();
            driveUploadTimer.Stop(); driveUploadTimer.Dispose();
            history.Dispose();
            menu.Dispose();
            tips.Dispose();
            globe?.Dispose();
            folderIcon?.Dispose();
        };
    }

    // ---------- Theme (follows Windows light/dark) ----------

    void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() => { if (Theme.Refresh()) ApplyTheme(); });
    }

    void ApplyTheme()
    {
        BackColor = Theme.Strip;
        tabStrip.BackColor = Theme.Strip;
        root.BackColor = nav.BackColor = bar.BackColor = content.BackColor = Theme.Surface;
        addressBox.ApplyTheme();
        Theme.ApplyToWindow(this);
        if (btnStar != null) UpdateStar();
        RebuildBookmarkBar();
        tabStrip.Invalidate();
        nav.Invalidate(true);
        bar.Invalidate();
    }

    async Task ImportOperaFavicons()
    {
        var urls = bookmarks.AllUrls().Select(b => b.Url!).ToList();
        List<(string key, byte[] png)> found;
        try { found = await Task.Run(() => OperaFavicons.Load(urls)); }
        catch { return; }
        int saved = 0;
        foreach (var (key, png) in found)
        {
            using var img = FaviconCache.Decode(png);
            if (img != null && FaviconCache.Save(key, img)) saved++;
        }
        try { File.WriteAllText(OperaFavicons.Marker, DateTime.Now.ToString("s")); } catch { }
        if (saved > 0) RebuildBookmarkBar();
    }

    // ---------- Layout ----------

    RoundButton IconButton(string glyph, string tip, Action click)
    {
        var b = new RoundButton
        {
            Glyph = glyph,
            GlyphSize = 11f,
            Size = new Size(S(32), S(30)),
            Margin = new Padding(S(1), 0, S(1), 0),
            Anchor = AnchorStyles.None,
        };
        tips.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    ContextMenuStrip NewMenu()
    {
        var cm = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = menu.Font,
            Renderer = new ModernRenderer(),
            Padding = new Padding(0, S(4), 0, S(4))
        };
        cm.Closed += (_, _) => { if (!cm.IsDisposed) BeginInvoke(() => { if (!cm.IsDisposed) cm.Dispose(); }); };
        return cm;
    }

    // ---------- Custom title bar (tabs live in the caption area) ----------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle);
        Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0020 | 0x0002 | 0x0001 | 0x0004); // FRAMECHANGED|NOMOVE|NOSIZE|NOZORDER
    }

    Native.RECT? testWrongWorkArea;
    int appliedDpi;

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (IsHandleCreated) BeginInvoke(ApplyDpi); // after WinForms has finished its own rescaling
    }

    /// <summary>
    /// Re-applies every size and font for the monitor the window is on now. The owner's two monitors
    /// are 100% and 150%; before this, sizes were fixed at the scaling Mizar started with, so on the
    /// other monitor the bars were 1.5x too tall (or too small) and the text didn't match them.
    /// </summary>
    void ApplyDpi()
    {
        if (IsDisposed || DeviceDpi == appliedDpi) return;
        DiagLog.Write($"dpi {appliedDpi} -> {DeviceDpi}");
        Theme.Dpi = appliedDpi = DeviceDpi;
        SuspendLayout();
        Font = Theme.UiFont(9f, DeviceDpi);
        MinimumSize = new Size(S(520), S(360));
        tabStrip.Font = Theme.UiFont(9f, DeviceDpi);
        tabStrip.Height = S(36);
        nav.Height = S(38);
        nav.Padding = new Padding(S(8), S(3), S(8), S(3));
        foreach (var b in nav.Controls.OfType<RoundButton>())
        {
            b.Size = new Size(S(32), S(30));
            b.Margin = new Padding(S(1), 0, S(1), 0);
        }
        addressBox.Box.Font = Theme.UiFont(10f, DeviceDpi);
        addressBox.Height = S(30);
        addressBox.Margin = new Padding(S(8), 0, S(6), 0);
        addressBox.PerformLayout();
        zoomChip.Height = S(30);
        zoomChip.Margin = new Padding(0, 0, S(2), 0);
        zoomChip.FitWidth();
        bar.Font = Theme.UiFont(9f, DeviceDpi);
        bar.Padding = new Padding(S(8), 0, S(8), S(2));
        bar.ImageScalingSize = new Size(S(16), S(16));
        menu.Font = Theme.UiFont(9.5f, DeviceDpi);
        menu.Padding = new Padding(0, S(4), 0, S(4));
        RebuildBookmarkBar();
        HideSuggestions();
        ResumeLayout(true);
        Invalidate(true);
    }

    void RecalcFrame() =>
        Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0020 | 0x0002 | 0x0001 | 0x0004 | 0x0010); // FRAMECHANGED, nothing else

    /// <summary>
    /// The client area must lie inside the window. On 2026-11-16, after the displays changed while
    /// Mizar was maximized, the window was on one monitor and its whole content on the other, so the
    /// window looked alive but ignored every click. Checked when the displays change, when the laptop
    /// wakes and whenever the window is activated; a mismatch is logged and repaired.
    /// </summary>
    bool EnsureClientOnScreen(string why)
    {
        if (!IsHandleCreated || IsDisposed || WindowState == FormWindowState.Minimized) return true;
        if (!Native.GetWindowRect(Handle, out var w)) return true;
        var c = PointToScreen(Point.Empty);
        bool ok = c.X >= w.Left - 2 && c.X <= w.Right && c.Y >= w.Top - 2 && c.Y <= w.Bottom;
        if (ok) return true;
        DiagLog.Write($"client area off-window ({why}): window {w.Left},{w.Top} client {c.X},{c.Y} -> recalculated");
        RecalcFrame();
        return false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_NCCALCSIZE && m.WParam != IntPtr.Zero && FormBorderStyle != FormBorderStyle.None)
        {
            var before = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.NCCALCSIZE_PARAMS>(m.LParam);
            int top = before.r0.Top;
            base.WndProc(ref m);
            var after = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.NCCALCSIZE_PARAMS>(m.LParam);
            // Remove the caption. When maximized, Windows places the window partly off-screen,
            // so pin the client area exactly to the monitor's work area (nothing gets cut off).
            // The monitor is taken from the NEW window rectangle, not from the window: when Windows
            // moves a maximized window to another monitor (a display was unplugged or went to sleep),
            // the window still "is" on the old one while this message is handled. Using the old
            // monitor left the whole client area on the other screen: the window looked fine but
            // every click fell on an empty frame (2026-11-16).
            if (Native.IsZoomed(Handle) && Native.WorkArea(before.r0) is { } work)
            {
                after.r0 = testWrongWorkArea ?? work;
            }
            else after.r0.Top = top;
            System.Runtime.InteropServices.Marshal.StructureToPtr(after, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == Native.WM_DISPLAYCHANGE || (m.Msg == Native.WM_SETTINGCHANGE && (int)m.WParam == Native.SPI_SETWORKAREA))
        {
            // monitors or the taskbar changed: have the client area recalculated once things settle
            base.WndProc(ref m);
            DiagLog.Write(m.Msg == Native.WM_DISPLAYCHANGE ? "display change" : "work area change");
            if (IsHandleCreated) BeginInvoke(() => { RecalcFrame(); EnsureClientOnScreen("display change"); });
            return;
        }
        if (m.Msg == Native.WM_ACTIVATE && (int)m.WParam != 0)
        {
            // self-heal: the user clicked a window whose content sits somewhere else
            base.WndProc(ref m);
            if (IsHandleCreated) BeginInvoke(() => EnsureClientOnScreen("activate"));
            return;
        }
        if (m.Msg == Native.WM_NCHITTEST && FormBorderStyle != FormBorderStyle.None)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)Native.HTCLIENT)
            {
                var screen = new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
                var p = PointToClient(screen);
                if (tabStrip.Visible && p.Y < tabStrip.Bottom && !tabStrip.IsInteractive(tabStrip.PointToClient(screen)))
                {
                    int edge = S(4);
                    if (WindowState != FormWindowState.Maximized && p.Y < edge)
                        m.Result = (IntPtr)(p.X < edge * 2 ? Native.HTTOPLEFT : p.X > ClientSize.Width - edge * 2 ? Native.HTTOPRIGHT : Native.HTTOP);
                    else
                        m.Result = (IntPtr)Native.HTCAPTION;
                }
            }
            return;
        }
        base.WndProc(ref m);
    }

    // ---------- Memory: put background tabs to sleep ----------

    async Task SleepInactiveTabs()
    {
        if (!TabSleeping) return;
        foreach (var t in tabs.ToList())
        {
            var cw = t.Core;
            if (t == active || cw == null || cw.IsSuspended) continue;
            if (DateTime.Now - t.LastActive < SleepAfter) continue;
            if (cw.IsDocumentPlayingAudio) continue;
            // never freeze a page in the middle of a request (see NetRepair.cs): start watching
            // the tab's requests if we weren't yet, and wait while any of them is still loading
            if (BackgroundNetWatch && !t.NetWatch) { await SetNetWatch(t, true); continue; }
            var now = DateTime.UtcNow;
            foreach (var kv in t.InFlight.Where(kv => (now - kv.Value).TotalSeconds > 60).ToList())
                t.InFlight.Remove(kv.Key);
            if (t.InFlight.Count > 0) continue;
            try { if (await cw.TrySuspendAsync()) DiagLog.Write("sleep " + Bookmark.HostOf(t.Url)); } catch { }
        }
    }

    void BuildNav()
    {
        btnBack = IconButton(GBack, "Înapoi (Alt+←)", () => { active?.View.Focus(); active?.Core?.GoBack(); });
        btnFwd = IconButton(GFwd, "Înainte (Alt+→)", () => { active?.View.Focus(); active?.Core?.GoForward(); });
        btnReload = IconButton(GReload, "Reîncarcă (F5)", () =>
        {
            if (active?.Core == null) return;
            active.View.Focus();
            if (active.IsLoading) active.Core.Stop(); else active.Core.Reload();
        });
        btnHome = IconButton(GHome, "Pagina de pornire", () => Navigate(settings.HomePage));
        btnStar = IconButton(GStar, "Adaugă la bookmark-uri (Ctrl+D)", ToggleBookmark);
        btnMenu = IconButton(GMore, "Meniu", () => menu.Show(btnMenu, new Point(btnMenu.Width - menu.Width, btnMenu.Height)));

        addressBox.Height = S(30);
        addressBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        addressBox.Margin = new Padding(S(8), 0, S(6), 0);
        address.KeyDown += (_, e) =>
        {
            if ((e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) && suggest.Visible)
            {
                e.SuppressKeyPress = true;
                suggest.Step(e.KeyCode == Keys.Down ? 1 : -1);
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                var pick = suggest.Visible ? suggest.Selected : null;
                HideSuggestions();
                Navigate(pick?.Url ?? address.Text);
                active?.View.Focus();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                if (suggest.Visible) { HideSuggestions(); return; }
                UpdateAddress(force: true);
                active?.View.Focus();
            }
        };
        // only text the user types (address box focused) brings up suggestions
        address.TextChanged += (_, _) => { if (address.Focused && !settingAddress) UpdateSuggestions(); };
        address.Enter += (_, _) => { selectAllOnClick = true; BeginInvoke(address.SelectAll); };
        address.MouseUp += (_, _) => { if (selectAllOnClick) { address.SelectAll(); selectAllOnClick = false; } };
        address.Leave += (_, _) => { selectAllOnClick = false; HideSuggestions(); UpdateAddress(force: true); };

        nav.Dock = DockStyle.Fill;
        nav.Height = S(38);
        nav.Margin = Padding.Empty;
        nav.Padding = new Padding(S(8), S(3), S(8), S(3));
        nav.BackColor = Theme.Surface;
        nav.RowCount = 1;
        zoomChip.Height = S(30);
        zoomChip.Anchor = AnchorStyles.None;
        zoomChip.Margin = new Padding(0, 0, S(2), 0);
        zoomChip.Visible = false;
        zoomChip.Click += (_, _) => ShowZoomMenu();

        nav.ColumnCount = 8;
        for (int i = 0; i < 8; i++)
            nav.ColumnStyles.Add(i == 4 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
        nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        nav.Controls.Add(btnBack, 0, 0);
        nav.Controls.Add(btnFwd, 1, 0);
        nav.Controls.Add(btnReload, 2, 0);
        nav.Controls.Add(btnHome, 3, 0);
        nav.Controls.Add(addressBox, 4, 0);
        nav.Controls.Add(zoomChip, 5, 0);
        nav.Controls.Add(btnStar, 6, 0);
        nav.Controls.Add(btnMenu, 7, 0);
    }

    // ---------- Zoom (remembered per site) ----------

    readonly TextChip zoomChip = new();

    /// <summary>
    /// ZoomFactorChanged is not reliable for every zoom path (it never fired in tests), so also check
    /// the active tab once a second. One in-process property read: no measurable CPU/RAM cost.
    /// </summary>
    readonly System.Windows.Forms.Timer zoomPoll = new() { Interval = 1000 };

    void CheckZoom(BrowserTab? tab)
    {
        if (tab == null || tab.View.IsDisposed || tab.Core == null) return;
        if (Math.Abs(tab.View.ZoomFactor - tab.ExpectedZoom) > 0.001) OnZoomChanged(tab);
    }
    static readonly double[] ZoomLevels = { 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0 };

    /// <summary>On entering a different site, apply that site's remembered zoom (or 100%).</summary>
    void ApplySiteZoom(BrowserTab tab)
    {
        var host = InternalPages.IsInternal(tab.Url) ? "" : Bookmark.HostOf(tab.Url);
        if (host == tab.ZoomHost) return;
        tab.ZoomHost = host;
        double z = host.Length > 0 && settings.SiteZoom.TryGetValue(host, out var v) ? v : 1.0;
        tab.ExpectedZoom = z;
        if (Math.Abs(tab.View.ZoomFactor - z) > 0.001) tab.View.ZoomFactor = z;
        if (tab == active) UpdateZoomChip();
    }

    /// <summary>The user zoomed (Ctrl+wheel, Ctrl +/-/0, menu): remember it for this site.</summary>
    void OnZoomChanged(BrowserTab tab)
    {
        double z = tab.View.ZoomFactor;
        if (Math.Abs(z - tab.ExpectedZoom) > 0.001)
        {
            tab.ExpectedZoom = z;
            var host = tab.ZoomHost ?? "";
            if (host.Length > 0)
            {
                if (Math.Abs(z - 1.0) < 0.004) settings.SiteZoom.Remove(host);
                else settings.SiteZoom[host] = Math.Round(z, 3);
                if (!IsSelfTest) settings.Save();
                // other open tabs on the same site follow
                foreach (var t in tabs)
                    if (t != tab && t.ZoomHost == host && Math.Abs(t.View.ZoomFactor - z) > 0.001)
                    {
                        t.ExpectedZoom = z;
                        t.View.ZoomFactor = z;
                    }
            }
        }
        if (tab == active) UpdateZoomChip();
    }

    void UpdateZoomChip()
    {
        double z = active?.View.ZoomFactor ?? 1.0;
        bool show = active != null && !InternalPages.IsInternal(active.Url) && Math.Abs(z - 1.0) > 0.004;
        if (show)
        {
            zoomChip.Text = $"{Math.Round(z * 100)}%";
            var host = active!.ZoomHost;
            tips.SetToolTip(zoomChip, string.IsNullOrEmpty(host)
                ? "Zoom — clic pentru opțiuni"
                : $"Zoom pentru {host} (ținut minte) — clic pentru opțiuni");
        }
        if (zoomChip.Visible != show) zoomChip.Visible = show;
    }

    void ShowZoomMenu()
    {
        var cm = NewMenu();
        cm.Items.Add("Mărește        Ctrl +", null, (_, _) => Zoom(1));
        cm.Items.Add("Micșorează     Ctrl −", null, (_, _) => Zoom(-1));
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add("Revino la 100%   Ctrl 0", null, (_, _) => { if (active != null) SetUserZoom(active, 1.0); });
        cm.Show(zoomChip, new Point(0, zoomChip.Height));
    }

    void BuildMenu()
    {
        menu.Font = Theme.UiFont(9.5f, DeviceDpi);
        menu.ShowImageMargin = false;
        menu.Renderer = new ModernRenderer();
        menu.Padding = new Padding(0, S(4), 0, S(4));

        ToolStripMenuItem Item(string text, string keys, Action a)
        {
            var mi = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys };
            mi.Click += (_, _) => a();
            return mi;
        }

        miShowBar = Item("Afișează bara de bookmark-uri", "Ctrl+Shift+B", ToggleBar);
        miShowBar.CheckOnClick = false;
        var bm = new ToolStripMenuItem("Bookmark-uri");
        bm.DropDownItems.Add(Item("Adaugă pagina curentă", "Ctrl+D", ToggleBookmark));
        bm.DropDownItems.Add(Item("Gestionează bookmark-urile", "Ctrl+Shift+O", OpenBookmarkManager));
        bm.DropDownItems.Add(miShowBar);
        bm.DropDownItems.Add(new ToolStripSeparator());
        bm.DropDownItems.Add(Item("Importă din Opera", "", ImportOpera));

        var zoom = new ToolStripMenuItem("Zoom");
        zoom.DropDownItems.Add(Item("Mărește", "Ctrl+Plus", () => Zoom(1)));
        zoom.DropDownItems.Add(Item("Micșorează", "Ctrl+Minus", () => Zoom(-1)));
        zoom.DropDownItems.Add(Item("100%", "Ctrl+0", () => { if (active != null) SetUserZoom(active, 1.0); }));

        menu.Items.Add(Item("Tab nou", "Ctrl+T", () => _ = NewTabFocused()));
        menu.Items.Add(Item("Redeschide tabul închis", "Ctrl+Shift+T", ReopenClosed));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(bm);
        menu.Items.Add(Item("Istoric", "Ctrl+H", () => _ = OpenHistory()));
        menu.Items.Add(Item("Descărcări", "Ctrl+J", () => active?.Core?.OpenDefaultDownloadDialog()));
        menu.Items.Add(Item("Deschide folderul de descărcări", "", OpenDownloadsFolder));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(zoom);
        menu.Items.Add(Item("Printează", "Ctrl+P", () => active?.Core?.ShowPrintUI()));
        menu.Items.Add(Item("Ecran complet", "F11", () => SetFullscreen(!fullscreen)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Repară conexiunea", "", () => _ = RepairConnection()));
        menu.Items.Add(Item("Setări", "", OpenSettings));
        menu.Items.Add(Item("Ieșire", "", Close));
        menu.Opening += (_, _) => miShowBar.Checked = settings.ShowBookmarksBar;
    }

    // ---------- Session (open tabs survive a restart or a crash) ----------

    bool sessionReady; // don't overwrite the saved session before it has been restored

    SessionData BuildSessionData()
    {
        var data = new SessionData();
        foreach (var t in tabs)
        {
            if (string.IsNullOrWhiteSpace(t.Url) || t.Url == "about:blank" || InternalPages.IsInternal(t.Url)) continue;
            if (t == active) data.Active = data.Tabs.Count;
            data.Tabs.Add(new SessionTab { Url = t.Url, Title = t.Title });
        }
        return data;
    }

    void SaveReopenOnce()
    {
        if (IsSelfTest) return;
        BuildSessionData().Save(AppPaths.ReopenOnceFile);
    }

    void SaveSession()
    {
        if (!sessionDirty || !sessionReady || IsSelfTest) return;
        sessionDirty = false;
        if (!settings.RestoreSession)
        {
            // nothing to reopen, so keep no list of open pages on disk either
            try { File.Delete(AppPaths.SessionFile); } catch { }
            return;
        }
        BuildSessionData().Save();
    }

    /// <summary>A second Mizar launch (e.g. a link clicked in another app) lands here (thread-safe).</summary>
    public void EnqueueFromOtherInstance(string url)
    {
        try
        {
            lock (pendingPipeUrls)
            {
                if (!IsHandleCreated || !sessionReady)
                {
                    pendingPipeUrls.Enqueue(url);
                    return;
                }
            }
            if (!IsDisposed) BeginInvoke(() => OpenFromOtherInstance(url));
        }
        catch { }
    }

    public void OpenFromOtherInstance(string url)
    {
        // maintenance commands from "Mizar.exe --netdiag/--fixnet": no window activation, no tab
        if (url.StartsWith("mizar-cmd:")) { _ = RunCommand(url["mizar-cmd:".Length..]); return; }
        if (WindowState == FormWindowState.Minimized)
            WindowState = lastWindowState == FormWindowState.Minimized ? FormWindowState.Normal : lastWindowState;
        Activate();
        Native.SetForegroundWindow(Handle);
        if (!string.IsNullOrWhiteSpace(url)) _ = CreateTab(url);
    }

    // ---------- Address-bar suggestions ----------

    void UpdateSuggestions()
    {
        var list = SuggestBox.Find(address.Text, bookmarks.AllUrls(), history.NewestFirst());
        if (list.Count == 0) { HideSuggestions(); return; }
        suggest.SetItems(list);
        var p = PointToClient(addressBox.PointToScreen(new Point(0, addressBox.Height + S(2))));
        suggest.Location = p;
        suggest.Width = addressBox.Width;
        if (!suggest.Visible) { suggest.Visible = true; suggest.BringToFront(); }
    }

    void HideSuggestions()
    {
        if (suggest.Visible) suggest.Visible = false;
    }

    void BuildBookmarkBar()
    {
        bar.Dock = DockStyle.Fill;
        bar.GripStyle = ToolStripGripStyle.Hidden;
        bar.Renderer = new ModernRenderer { BarLine = true };
        bar.BackColor = Theme.Surface;
        bar.Padding = new Padding(S(8), 0, S(8), S(2));
        bar.Margin = Padding.Empty;
        bar.ImageScalingSize = new Size(S(16), S(16));
        bar.Font = Theme.UiFont(9f, DeviceDpi);
        bar.CanOverflow = true;
        bar.ShowItemToolTips = true;
        bar.Visible = settings.ShowBookmarksBar;
        bar.Reordered += (dragged, dropIdx) =>
        {
            int fromIdx = bookmarks.Bar.IndexOf(dragged);
            if (fromIdx < 0 || dropIdx == fromIdx || dropIdx == fromIdx + 1) return;
            int toIdx = dropIdx > fromIdx ? dropIdx - 1 : dropIdx;
            bookmarks.Bar.RemoveAt(fromIdx);
            bookmarks.Bar.Insert(toIdx, dragged);
            bookmarks.NotifyChanged();
        };
        bar.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || bar.GetItemAt(e.Location) != null) return;
            var cm = NewMenu();
            cm.Items.Add("Adaugă pagina curentă", null, (_, _) => ToggleBookmark());
            cm.Items.Add("Gestionează bookmark-urile", null, (_, _) => OpenBookmarkManager());
            cm.Items.Add("Importă din Opera", null, (_, _) => ImportOpera());
            cm.Show(bar, e.Location);
        };
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    Image? IconFor(Bookmark b) => b.IsFolder ? null : OperaFavicons.For(b.Url) ?? globe;

    void RebuildBookmarkBar()
    {
        bar.CancelDrag();
        bar.SuspendLayout();
        var old = bar.Items.Cast<ToolStripItem>().ToList();
        bar.Items.Clear();
        foreach (var it in old) it.Dispose();

        foreach (var b in bookmarks.Bar) bar.Items.Add(MakeBarItem(b));

        if (bookmarks.Other.Count > 0)
        {
            var other = new ToolStripDropDownButton("Alte bookmark-uri", folderIcon) { Alignment = ToolStripItemAlignment.Right };
            FillMenu(other.DropDownItems, bookmarks.Other);
            bar.Items.Add(other);
        }
        if (bookmarks.Bar.Count == 0)
            bar.Items.Add(new ToolStripLabel("Apasă pe ☆ ca să adaugi pagini aici") { ForeColor = Color.Gray });

        bar.ResumeLayout();
        UpdateStar();
    }

    ToolStripItem MakeBarItem(Bookmark b)
    {
        var item = MakeBarItemCore(b);
        bar.HookOverflowDrag(item);
        return item;
    }

    ToolStripItem MakeBarItemCore(Bookmark b)
    {
        if (b.IsFolder)
        {
            var dd = new ToolStripDropDownButton(Trunc(b.DisplayName, 24), folderIcon) { Tag = b };
            FillMenu(dd.DropDownItems, b.Children ??= new());
            dd.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowBookmarkMenu(b); };
            return dd;
        }
        // like Opera: bookmarks without a name show only their icon
        bool iconOnly = string.IsNullOrWhiteSpace(b.Name);
        var btn = new ToolStripButton(iconOnly ? "" : Trunc(b.Name, 24), IconFor(b))
        {
            Tag = b,
            ToolTipText = (iconOnly ? Bookmark.HostOf(b.Url) : b.Name) + "\n" + b.Url,
            DisplayStyle = iconOnly ? ToolStripItemDisplayStyle.Image : ToolStripItemDisplayStyle.ImageAndText,
            Padding = iconOnly ? new Padding(S(3), S(2), S(3), S(2)) : new Padding(S(2), S(2), S(3), S(2)),
            Margin = new Padding(0, 0, S(2), 0)
        };
        btn.MouseUp += (_, e) =>
        {
            if (bar.JustDragged || bar.OverflowDragging) return;
            if (e.Button == MouseButtons.Left)
            {
                if (ModifierKeys.HasFlag(Keys.Control)) _ = CreateTab(b.Url, false);
                else Navigate(b.Url!);
            }
            else if (e.Button == MouseButtons.Middle) _ = CreateTab(b.Url, false);
            else if (e.Button == MouseButtons.Right) ShowBookmarkMenu(b);
        };
        return btn;
    }

    /// <summary>Bookmark bar ToolStrip with smooth left/right drag-to-reorder without ToolStrip stealing capture.</summary>
    sealed class BookmarkBar : ToolStrip
    {
        public event Action<Bookmark, int>? Reordered;
        ToolStripItem? dragItem;
        Point dragStart;
        bool dragActive;
        int dragDropIndex = -1, dragInsertX = -1;

        int S(int v) => (int)Math.Round(v * DeviceDpi / 96f);

        public void CancelDrag()
        {
            bool was = dragActive || dragInsertX >= 0;
            dragItem = null;
            dragActive = false;
            dragDropIndex = -1;
            dragInsertX = -1;
            if (Capture) Capture = false;
            if (Cursor != Cursors.Default) Cursor = Cursors.Default;
            if (was) Invalidate();
        }

        int ItemIndexOf(Bookmark target)
        {
            int idx = 0;
            foreach (ToolStripItem it in Items)
            {
                if (it.Tag is not Bookmark b) continue;
                if (ReferenceEquals(b, target)) return idx;
                idx++;
            }
            return -1;
        }

        /// <summary>True right after a drag ended, so the release doesn't also open the bookmark.</summary>
        public bool JustDragged { get; private set; }

        void EndDragSoon()
        {
            JustDragged = true;
            BeginInvoke(() => JustDragged = false);
        }

        // ---- dragging from the overflow list (the » drop-down) ----
        // The drop-down swallows MouseDown/MouseMove over its items, so the drag starts from the item's
        // own MouseDown and then follows the mouse with a short timer until the button is released.
        ToolStripItem? odItem;
        Point odStart;
        bool odActive;
        int odInsertY = -1;
        readonly System.Windows.Forms.Timer odTimer = new() { Interval = 15 };
        /// <summary>Mouse position and left button; replaced by the self-test.</summary>
        public Func<Point> MousePos = () => Control.MousePosition;
        public Func<bool> LeftDown = () => (Control.MouseButtons & MouseButtons.Left) != 0;
        /// <summary>A drag from the overflow list is under way: its items must not open on mouse-up.</summary>
        public bool OverflowDragging => odActive;

        public BookmarkBar()
        {
            odTimer.Tick += (_, _) => OverflowDragTick();
            OverflowButton.DropDown.Paint += (_, e) =>
            {
                if (!odActive || odInsertY < 0) return;
                int h = Math.Max(2, S(2));
                using var b = new SolidBrush(Theme.Accent);
                e.Graphics.FillRectangle(b, S(6), odInsertY - h / 2, OverflowButton.DropDown.Width - S(12), h);
            };
        }

        /// <summary>Called for every bookmark item: a left press on it while it sits in the overflow list may start a drag.</summary>
        public void HookOverflowDrag(ToolStripItem it)
        {
            it.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left || it.Placement != ToolStripItemPlacement.Overflow || it.Tag is not Bookmark) return;
                odItem = it;
                odStart = MousePos();
                odActive = false;
                odTimer.Start();
            };
        }

        public void OverflowDragTick()
        {
            var dd = OverflowButton.DropDown;
            if (odItem == null) { odTimer.Stop(); return; }
            var screen = MousePos();
            if (!LeftDown())
            {
                var dragged = odActive ? odItem.Tag as Bookmark : null;
                int dropIdx = dragDropIndex;
                EndOverflowDrag(dd);
                if (dragged != null)
                {
                    EndDragSoon();
                    dd.Close();
                    if (dropIdx >= 0) Reordered?.Invoke(dragged, dropIdx);
                }
                return;
            }
            if (!odActive && (Math.Abs(screen.X - odStart.X) >= S(6) || Math.Abs(screen.Y - odStart.Y) >= S(6)))
            {
                odActive = true;
                dragItem = odItem;
                dragActive = true;
                Cursor.Current = Cursors.SizeAll;
            }
            if (!odActive) return;
            var onBar = PointToClient(screen);
            if (ClientRectangle.Contains(onBar))
            {
                SetOverflowInsert(dd, -1);
                UpdateDropTarget(onBar.X); // the bar's own insertion line
            }
            else
            {
                if (dragInsertX >= 0) { dragInsertX = -1; Invalidate(); }
                UpdateOverflowTarget(dd, dd.PointToClient(screen).Y);
            }
        }

        void EndOverflowDrag(ToolStripDropDown dd)
        {
            odTimer.Stop();
            odItem = null;
            odActive = false;
            SetOverflowInsert(dd, -1);
            Cursor.Current = Cursors.Default;
            CancelDrag();
        }

        void SetOverflowInsert(ToolStripDropDown dd, int y)
        {
            if (odInsertY == y) return;
            odInsertY = y;
            dd.Invalidate();
        }

        /// <summary>Drop position inside the overflow list, as an index over all bookmark items.</summary>
        void UpdateOverflowTarget(ToolStripDropDown dd, int y)
        {
            if (dragItem?.Tag is not Bookmark dragged) return;
            int fromIdx = ItemIndexOf(dragged);
            int idx = 0, dropIdx = -1, insertY = -1;
            ToolStripItem? last = null;
            foreach (ToolStripItem it in Items)
            {
                if (it.Tag is not Bookmark) continue;
                if (it.Placement == ToolStripItemPlacement.Overflow)
                {
                    last = it;
                    if (dropIdx < 0 && y < it.Bounds.Top + it.Bounds.Height / 2) { dropIdx = idx; insertY = it.Bounds.Top; }
                }
                idx++;
            }
            if (dropIdx < 0 && last != null) { dropIdx = ItemIndexOf((Bookmark)last.Tag!) + 1; insertY = last.Bounds.Bottom; }
            if (dropIdx == fromIdx || dropIdx == fromIdx + 1) insertY = -1;
            dragDropIndex = dropIdx;
            SetOverflowInsert(dd, insertY);
        }

        void UpdateDropTarget(int x)
        {
            if (dragItem?.Tag is not Bookmark dragged) return;
            int fromIdx = ItemIndexOf(dragged);
            if (fromIdx < 0) return;

            int idx = 0, dropIdx = -1, insertX = -1;
            ToolStripItem? lastMain = null;
            foreach (ToolStripItem it in Items)
            {
                if (it.Tag is not Bookmark || it.Placement != ToolStripItemPlacement.Main) continue;
                lastMain = it;
                if (dropIdx < 0 && x < it.Bounds.Left + it.Bounds.Width / 2)
                {
                    dropIdx = idx;
                    insertX = it.Bounds.Left - S(1);
                }
                idx++;
            }
            if (dropIdx < 0 && lastMain != null)
            {
                dropIdx = idx;
                insertX = lastMain.Bounds.Right + S(1);
            }
            if (dropIdx == fromIdx || dropIdx == fromIdx + 1) insertX = -1;
            if (dropIdx != dragDropIndex || insertX != dragInsertX)
            {
                dragDropIndex = dropIdx;
                dragInsertX = insertX;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs mea)
        {
            if (mea.Button == MouseButtons.Left)
            {
                var it = GetItemAt(mea.Location);
                if (it?.Tag is Bookmark && it.Placement == ToolStripItemPlacement.Main)
                {
                    dragItem = it;
                    dragStart = mea.Location;
                    dragActive = false;
                    dragDropIndex = -1;
                    dragInsertX = -1;
                }
                else dragItem = null;
            }
            base.OnMouseDown(mea);
        }

        protected override void OnMouseMove(MouseEventArgs mea)
        {
            if (dragItem != null)
            {
                if ((mea.Button & MouseButtons.Left) == 0) { CancelDrag(); base.OnMouseMove(mea); return; }
                if (!dragActive && (Math.Abs(mea.X - dragStart.X) >= S(6) || Math.Abs(mea.Y - dragStart.Y) >= S(6)))
                {
                    dragActive = true;
                    if (dragItem is ToolStripDropDownButton dd) dd.HideDropDown();
                    Capture = true;
                    Cursor = Cursors.SizeWE;
                }
                if (dragActive)
                {
                    UpdateDropTarget(mea.X);
                    return; // prevent ToolStrip from toggling capture, hover or tooltips while dragging
                }
            }
            base.OnMouseMove(mea);
        }

        protected override void OnMouseUp(MouseEventArgs mea)
        {
            if (dragActive)
            {
                var dragged = dragItem?.Tag as Bookmark;
                int dropIdx = dragDropIndex;
                dragActive = false;
                CancelDrag();
                EndDragSoon();
                base.OnMouseLeave(EventArgs.Empty);
                if (dragged != null && dropIdx >= 0) Reordered?.Invoke(dragged, dropIdx);
                return;
            }
            dragItem = null;
            base.OnMouseUp(mea);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (dragActive) CancelDrag();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!dragActive || dragInsertX < 0) return;
            int w = Math.Max(2, S(2));
            int padY = S(4);
            using var b = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(b, dragInsertX - w / 2, padY, w, Math.Max(4, Height - padY * 2 - 1));
        }
    }

    void FillMenu(ToolStripItemCollection items, List<Bookmark> list)
    {
        if (list.Count == 0) { items.Add(new ToolStripMenuItem("(gol)") { Enabled = false }); return; }
        foreach (var b in list)
        {
            if (b.IsFolder)
            {
                var sub = new ToolStripMenuItem(Trunc(b.DisplayName, 50), folderIcon);
                FillMenu(sub.DropDownItems, b.Children ??= new());
                items.Add(sub);
            }
            else
            {
                var mi = new ToolStripMenuItem(Trunc(b.DisplayName, 60), IconFor(b)) { ToolTipText = b.Url };
                mi.MouseUp += (_, e) =>
                {
                    if (e.Button == MouseButtons.Middle || ModifierKeys.HasFlag(Keys.Control)) _ = CreateTab(b.Url, false);
                    else if (e.Button == MouseButtons.Left) Navigate(b.Url!);
                };
                items.Add(mi);
            }
        }
    }

    void ShowBookmarkMenu(Bookmark b)
    {
        var cm = NewMenu();
        if (!b.IsFolder)
        {
            cm.Items.Add("Deschide", null, (_, _) => Navigate(b.Url!));
            cm.Items.Add("Deschide în tab nou", null, (_, _) => _ = CreateTab(b.Url));
            cm.Items.Add(new ToolStripSeparator());
        }
        cm.Items.Add("Editează...", null, (_, _) =>
        {
            using var d = new EditDialog(b.IsFolder ? "Editează folderul" : "Editează bookmark-ul", b.Name, b.Url);
            if (d.ShowDialog(this) != DialogResult.OK) return;
            b.Name = d.NameBox.Text.Trim();
            if (!b.IsFolder && d.UrlBox.Text.Trim().Length > 0) b.Url = Normalize(d.UrlBox.Text);
            bookmarks.NotifyChanged();
        });
        cm.Items.Add("Șterge", null, (_, _) => { bookmarks.Remove(b); bookmarks.NotifyChanged(); });
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add("Gestionează bookmark-urile", null, (_, _) => OpenBookmarkManager());
        cm.Show(Cursor.Position);
    }

    // ---------- Tabs ----------

    async Task NewTabFocused()
    {
        // a new tab is an empty page (owner's choice), not the home page
        await CreateTab("about:blank");
        address.Focus();
        address.SelectAll();
    }

    async Task<BrowserTab?> CreateTab(string? url, bool activate = true,
        CoreWebView2NewWindowRequestedEventArgs? nw = null, CoreWebView2Deferral? deferral = null, int insertAt = -1)
    {
        var tab = new BrowserTab { Title = url == "about:blank" ? "Tab nou" : "Se încarcă…" };
        tab.View.DefaultBackgroundColor = Theme.Surface;
        if (insertAt >= 0 && insertAt <= tabs.Count) tabs.Insert(insertAt, tab); else tabs.Add(tab);
        content.Controls.Add(tab.View);
        if (activate || active == null) Activate(tab); else SyncStrip();
        return await InitTab(tab, url, nw, deferral) ? tab : null;
    }

    /// <summary>
    /// A tab restored from the last session: only a title and an icon until it is first shown,
    /// so a long session doesn't start a page process per tab at startup.
    /// </summary>
    void AddLazyTab(string url, string title)
    {
        var tab = new BrowserTab
        {
            Title = string.IsNullOrWhiteSpace(title) ? Bookmark.HostOf(url) : title,
            Url = url,
            PendingUrl = url,
            IsLoading = false
        };
        tab.SetFavicon(OperaFavicons.For(url), owned: false); // shared cache image
        tab.View.DefaultBackgroundColor = Theme.Surface;
        tabs.Add(tab);
        content.Controls.Add(tab.View);
        SyncStrip();
    }

    /// <summary>
    /// The whole browser process died (seen on 2026-11-17, exit 0xC0000005): every tab was blank and
    /// nothing worked until Mizar was restarted. Start a new engine and give every tab a fresh view;
    /// tabs come back lazily (like a restored session), the active one right away.
    /// </summary>
    void RecoverEngine()
    {
        if (IsDisposed) return;
        var now = DateTime.Now;
        engineRestarts.RemoveAll(t => now - t > TimeSpan.FromMinutes(10));
        engineRestarts.Add(now);
        if (engineRestarts.Count > 3)
        {
            DiagLog.Write("engine died again: not restarting it (more than 3 times in 10 minutes)");
            MessageBox.Show(this, "Motorul browserului s-a oprit de mai multe ori în ultimele minute.\nÎnchide Mizar și deschide-l din nou.",
                "Mizar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DiagLog.Write($"engine restart tabs={tabs.Count}");
        envTask = null;
        var keep = active;
        content.SuspendLayout();
        foreach (var t in tabs)
        {
            var old = t.ReplaceView();
            t.View.DefaultBackgroundColor = Theme.Surface;
            content.Controls.Remove(old);
            content.Controls.Add(t.View);
            try { old.Dispose(); } catch (Exception ex) { ErrorLog.Write("Dispose dead view", ex); }
            t.PendingUrl ??= string.IsNullOrWhiteSpace(t.Url) ? "about:blank" : t.Url;
            t.IsLoading = false;
        }
        content.ResumeLayout();
        active = null;
        if (keep != null && tabs.Contains(keep)) Activate(keep);
        else if (tabs.Count > 0) Activate(tabs[0]);
        UpdateNav();
    }

    bool cookiesLoggedAtClose;

    /// <summary>
    /// Since 2026-11-16 the owner is signed out of Google at every restart (other sites stay signed
    /// in). One diag.log line per call: the Google cookies the engine has for Gmail and the sign-in
    /// page, as domain/name and s (session) or expiry date. Never values. Compare "close" with the
    /// next "start" to see whether they expire by design or get removed.
    /// </summary>
    async Task LogGoogleCookies(string when)
    {
        try
        {
            var cw = active?.Core ?? tabs.Select(t => t.Core).FirstOrDefault(c => c != null);
            if (cw == null) { DiagLog.Write($"cookies {when} google: no engine"); return; }
            var seen = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var url in new[] { "https://mail.google.com/mail/", "https://accounts.google.com/" })
            {
                var t = cw.CookieManager.GetCookiesAsync(url);
                if (await Task.WhenAny(t, Task.Delay(3000)) != t) { DiagLog.Write($"cookies {when} google: TIMEOUT"); return; }
                foreach (var c in t.Result)
                    seen.Add($"{c.Domain}{c.Path.TrimEnd('/')}/{c.Name}:{(c.IsSession ? "s" : c.Expires.ToString("yyyy-MM-dd"))}");
            }
            DiagLog.Write($"cookies {when} google: {seen.Count} [{string.Join(' ', seen)}]");
        }
        catch (Exception ex) { DiagLog.Write($"cookies {when} google: failed {ex.Message}"); }
    }

    /// <summary>Starts the engine for a tab and loads its first page. False if the tab is gone or the engine failed.</summary>
    async Task<bool> InitTab(BrowserTab tab, string? url, CoreWebView2NewWindowRequestedEventArgs? nw = null, CoreWebView2Deferral? deferral = null)
    {
        try
        {
            envTask ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebData, EngineOptions());
            var env = await envTask;
            await tab.View.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            deferral?.Complete();
            ErrorLog.Write("Engine", ex);
            if (!tab.View.IsDisposed)
                MessageBox.Show(this, "Nu am putut porni motorul WebView2:\n" + ex.Message, "Mizar", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        if (tab.View.IsDisposed || !tabs.Contains(tab)) { deferral?.Complete(); return false; }

        var cw = tab.View.CoreWebView2;
        Wire(tab, cw);
        if (tab != active && LowMemoryForHiddenTabs) cw.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;

        if (nw != null)
        {
            nw.NewWindow = cw;
            nw.Handled = true;
            deferral?.Complete();
        }
        else cw.Navigate(Normalize(url ?? settings.HomePage));
        return true;
    }

    void Wire(BrowserTab tab, CoreWebView2 cw)
    {
        ApplySettings(cw);
        cw.Settings.IsStatusBarEnabled = true;
        cw.Settings.AreDevToolsEnabled = true;
        cw.Settings.AreHostObjectsAllowed = false;

        cw.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = cw.Source == "about:blank" ? "Tab nou"
                : string.IsNullOrWhiteSpace(cw.DocumentTitle) ? Bookmark.HostOf(cw.Source) : cw.DocumentTitle;
            if (!InternalPages.IsInternal(cw.Source)) history.UpdateTitle(cw.Source, cw.DocumentTitle);
            TabChanged(tab);
        };
        cw.SourceChanged += (_, _) => { tab.Url = cw.Source; ApplySiteZoom(tab); TabChanged(tab); };
        tab.View.ZoomFactorChanged += (_, _) => OnZoomChanged(tab);
        cw.NavigationStarting += (_, e) =>
        {
            tab.CertError = false;
            CheckZoom(tab); // record a zoom change made just before leaving the page
            tab.IsLoading = true;
            tab.LoadStarted = DateTime.Now;
            tab.SlowLogged = false;
            TabChanged(tab);
        };
        cw.NavigationCompleted += (_, e) =>
        {
            tab.IsLoading = false;
            if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                DiagLog.Write($"nav failed {Bookmark.HostOf(cw.Source)} {e.WebErrorStatus} http={e.HttpStatusCode}");
            if (e.IsSuccess && !IsSelfTest) history.Visit(cw.Source, cw.DocumentTitle);
            TabChanged(tab);
        };
        // Keyboard shortcuts pressed while a web page has focus are caught in the page
        // (WebView2 swallows some, e.g. Ctrl+H) and forwarded here.
        _ = cw.AddScriptToExecuteOnDocumentCreatedAsync(ShortcutScript);
        cw.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
                var m = doc.RootElement;
                if (m.ValueKind == System.Text.Json.JsonValueKind.Object && m.TryGetProperty("mizarKey", out var key))
                {
                    if (tab == active) RunShortcut(key.GetString() ?? "");
                    return;
                }
                // only our own history page (top-level document on mizar.internal) may send commands
                if (!e.Source.StartsWith(InternalPages.History, StringComparison.OrdinalIgnoreCase)) return;
                var cmd = m.GetProperty("cmd").GetString();
                if (cmd == "delete") history.Delete(m.GetProperty("u").GetString() ?? "", m.GetProperty("ts").GetInt64());
                else if (cmd == "clear") { history.Clear(); _ = ClearData(CoreWebView2BrowsingDataKinds.BrowsingHistory); }
            }
            catch (Exception ex) { ErrorLog.Write("WebMessage", ex); }
        };
        cw.HistoryChanged += (_, _) => { if (tab == active) UpdateNav(); };
        WireNetWatch(tab, cw);
        cw.FaviconChanged += async (_, _) => await LoadFavicon(tab, cw);
        cw.NewWindowRequested += (_, e) =>
        {
            var d = e.GetDeferral();
            _ = CreateTab(null, true, e, d, tabs.IndexOf(tab) + 1);
        };
        // A new tab starts with the theme colour (no white flash in dark mode), but once a page
        // loads the canvas must be white like in every browser: many sites (e.g. altex.ro) never set
        // a background and rely on the default white, and look broken on a dark canvas.
        cw.ContentLoading += (_, _) =>
        {
            if (cw.Source == "about:blank") return; // empty new tab keeps the theme colour
            if (tab.View.DefaultBackgroundColor != Color.White) tab.View.DefaultBackgroundColor = Color.White;
        };
        cw.DownloadStarting += OnDownloadStarting;
        // the engine still blocks the page with its own error screen; we only flag it in the address bar
        cw.ServerCertificateErrorDetected += (_, _) => { tab.CertError = true; TabChanged(tab); };
        cw.ContainsFullScreenElementChanged += (_, _) => { if (tab == active) SetFullscreen(cw.ContainsFullScreenElement); };
        cw.WindowCloseRequested += (_, _) => BeginInvoke(() => CloseTab(tab));
        int gen = engineGen;
        cw.ProcessFailed += (_, e) =>
        {
            DiagLog.Write($"process failed {e.ProcessFailedKind} reason={e.Reason} exit={e.ExitCode} host={Bookmark.HostOf(tab.Url)}");
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                // every tab reports it: only the first report of this engine restarts it
                if (gen == engineGen) { engineGen++; BeginInvoke(RecoverEngine); }
                return;
            }
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited) return;
            BeginInvoke(() =>
            {
                // reload after a crash, but not forever: 3 crashes within a minute -> stop and explain
                var now = DateTime.Now;
                tab.Crashes.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
                tab.Crashes.Add(now);
                try
                {
                    if (tab.Crashes.Count >= 3)
                    {
                        var target = InternalPages.IsInternal(tab.Url) ? "" : tab.Url;
                        cw.Navigate(InternalPages.Crashed + "?u=" + Uri.EscapeDataString(target));
                    }
                    else cw.Reload();
                }
                catch (Exception ex) { ErrorLog.Write("Crash reload", ex); }
            });
        };

        // built-in pages (history, crash notice) are served locally and never reach the network
        cw.AddWebResourceRequestedFilter(InternalPages.Base + "*", CoreWebView2WebResourceContext.All);
        if (AdFilter) foreach (var pattern in AdBlocker.FilterPatterns())
            // Document only (pages and their frames), NOT service/shared workers: a worker filter makes
            // the engine route worker requests through this window, and Gmail's service worker was found
            // wedged on 2026-11-15 with all its image/font fetches pending (see NetRepair.cs).
            cw.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.Document);
        cw.WebResourceRequested += (_, e) =>
        {
            var served = InternalPages.Serve(e.Request.Uri, history);
            if (served is { } page)
            {
                if (page.status == 200) history.Flush();
                e.Response = cw.Environment.CreateWebResourceResponse(new MemoryStream(page.body), page.status,
                    page.status == 200 ? "OK" : "Not Found", page.headers);
                return;
            }
            var kind = AdBlocker.Classify(e.Request.Uri);
            bool block = kind == AdBlocker.Kind.Ad ? settings.BlockAds : kind == AdBlocker.Kind.Tracker && settings.BlockTrackers;
            if (!block) return;
            if (kind == AdBlocker.Kind.Ad) AdBlocker.BlockedAds++; else AdBlocker.BlockedTrackers++;
            e.Response = cw.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
    }

    /// <summary>
    /// Tells web pages which colour scheme to use (CSS prefers-color-scheme): follow Windows (default),
    /// always light (like Opera) or always dark. Profile-wide, applies live.
    /// "--scheme light|dark|auto" overrides it (self-tests).
    /// </summary>
    void ApplyColorScheme(CoreWebView2 cw)
    {
        int i = Array.IndexOf(startArgs, "--scheme");
        var s = i >= 0 && i + 1 < startArgs.Length ? startArgs[i + 1] : settings.WebColorScheme;
        var want = s switch
        {
            "dark" => CoreWebView2PreferredColorScheme.Dark,
            "light" => CoreWebView2PreferredColorScheme.Light,
            _ => CoreWebView2PreferredColorScheme.Auto
        };
        try { if (cw.Profile.PreferredColorScheme != want) cw.Profile.PreferredColorScheme = want; } catch { }
    }

    void ApplySettings(CoreWebView2 cw)
    {
        ApplyColorScheme(cw);
        cw.Settings.IsPasswordAutosaveEnabled = settings.SavePasswords;
        cw.Settings.IsGeneralAutofillEnabled = settings.SavePasswords;
    }

    /// <summary>
    /// Engine start-up arguments. Third-party cookies are blocked with Chromium's
    /// "--test-third-party-cookie-phaseout" switch (verified on whatismybrowser.com;
    /// the DevTools Network.setCookieControls route had no effect in WebView2).
    /// Arguments are fixed when the engine starts, so changing the setting needs a restart.
    /// </summary>
    CoreWebView2EnvironmentOptions EngineOptions()
    {
        var args = new List<string>();
        if (HttpsUpgrades) args.Add("--enable-features=HttpsUpgrades");
        if (settings.BlockThirdPartyCookies) args.Add("--test-third-party-cookie-phaseout");
        DiagLog.Write($"engine options: 3pcBlock={settings.BlockThirdPartyCookies} trackingPrevention=off hiddenLowMemory={On(LowMemoryForHiddenTabs)} clearEngineHistory={On(ClearEngineHistoryAtStart)} netWatch={On(BackgroundNetWatch)} httpsUpgrades={On(HttpsUpgrades)} tabSleeping={On(TabSleeping)} adFilter={On(AdFilter)}");
        static string On(bool b) => b ? "on" : "off";
        return new CoreWebView2EnvironmentOptions(string.Join(' ', args))
        {
            // Edge's own tracking prevention is off: Mizar has its own blocker (AdBlocker), and Edge's
            // classifies every cross-site request in the browser process. On 2026-11-15 Gmail's
            // cross-site requests (gstatic.com) never reached the network stack while same-site ones
            // did (NetLog), so anything that gates cross-site requests is a suspect. See known issue 0.
            EnableTrackingPrevention = false
        };
    }

    readonly bool thirdPartyBlockedAtStart;

    /// <summary>Diagnostics: mizar.exe --selftest &lt;url&gt; &lt;outfile&gt; loads a page, dumps results, exits.</summary>
    async Task RunSelfTest(CoreWebView2 cw, string outFile)
    {
        // --netlog: record failed requests (host + type + reason only, never paths or page content)
        var netFails = new List<string>();
        if (startArgs.Contains("--netlog"))
        {
            var types = new Dictionary<string, string>();
            var hosts = new Dictionary<string, string>();
            cw.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent").DevToolsProtocolEventReceived += (_, e) =>
            {
                using var d = System.Text.Json.JsonDocument.Parse(e.ParameterObjectAsJson);
                var id = d.RootElement.GetProperty("requestId").GetString() ?? "";
                var url = d.RootElement.GetProperty("request").GetProperty("url").GetString() ?? "";
                hosts[id] = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url[..Math.Min(30, url.Length)];
                types[id] = d.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            };
            cw.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += (_, e) =>
            {
                using var d = System.Text.Json.JsonDocument.Parse(e.ParameterObjectAsJson);
                var r = d.RootElement;
                var id = r.GetProperty("requestId").GetString() ?? "";
                string reason = r.TryGetProperty("blockedReason", out var b) ? "blocked:" + b.GetString()
                              : r.TryGetProperty("corsErrorStatus", out var c) ? "cors:" + c.GetProperty("corsError").GetString()
                              : r.TryGetProperty("errorText", out var et) ? et.GetString() ?? "" : "";
                lock (netFails) netFails.Add($"{(hosts.TryGetValue(id, out var h) ? h : "?")} [{(types.TryGetValue(id, out var ty) ? ty : r.TryGetProperty("type", out var t2) ? t2.GetString() : "")}] {reason}");
            };
            await cw.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            cw.Reload();
        }
        var done = new TaskCompletionSource();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult();
        cw.NavigationCompleted += Handler;
        await Task.WhenAny(done.Task, Task.Delay(30000));
        await Task.Delay(8000);
        string keys = "";
        if (startArgs.Contains("--monitortest"))
        {
            // put the content on another monitor on purpose, then check that the self-heal brings it back
            // sizes must follow each monitor's scaling (the owner's two monitors are 100% and 150%)
            foreach (var s in Screen.AllScreens)
            {
                WindowState = FormWindowState.Normal;
                Bounds = new Rectangle(s.WorkingArea.X + 100, s.WorkingArea.Y + 100, 1300, 800);
                WindowState = FormWindowState.Maximized;
                await Task.Delay(1500);
                keys += $"monitor: dpi={DeviceDpi} expect strip={S(36)} nav={S(38)} addr={S(30)} | actual strip={tabStrip.Height} nav={nav.Height} addr={addressBox.Height} bar={bar.Height} | font px: form={Font.Height} tabs={tabStrip.Font.Height} address={addressBox.Box.Font.Height} bar={bar.Font.Height} expect={Theme.UiFont(9f, DeviceDpi).Height}\n";
            }
            var other = Screen.AllScreens.FirstOrDefault(s => !s.Bounds.Contains(PointToScreen(Point.Empty)));
            if (other == null) keys += "monitor: needs two monitors\n";
            else
            {
                var wa = other.WorkingArea;
                testWrongWorkArea = new Native.RECT { Left = wa.Left, Top = wa.Top, Right = wa.Right, Bottom = wa.Bottom };
                RecalcFrame();
                await Task.Delay(500);
                testWrongWorkArea = null;
                var broken = PointToScreen(Point.Empty);
                bool wasOk = EnsureClientOnScreen("test");
                await Task.Delay(500);
                var healed = PointToScreen(Point.Empty);
                keys += $"monitor: broken client at {broken.X},{broken.Y} detected={!wasOk} | healed client at {healed.X},{healed.Y} ok={EnsureClientOnScreen("test2")}\n";
            }
        }
        if (startArgs.Contains("--healtest"))
        {
            // the page registers a worker that never answers image requests (a wedged worker);
            // the slow-load check must notice it, remove the worker and reload, and the images must load
            const string img = "(() => { const i=[...document.images]; return 'img=' + i.length + ' pending=' + i.filter(x=>!x.complete).length + ' sw=' + (navigator.serviceWorker.controller ? 'yes' : 'no'); })()";
            await cw.ExecuteScriptAsync("navigator.serviceWorker.register('/stuck-sw.js')");
            await Task.Delay(2000);
            cw.Reload();   // now controlled: every image hangs
            await Task.Delay(4000);
            keys += $"heal: stuck -> {System.Text.Json.JsonSerializer.Deserialize<string>(await cw.ExecuteScriptAsync(img))}\n";
            await Task.Delay(40000); // slow-load check (6 s in this test) + black box + heal + reload
            keys += $"heal: after -> {System.Text.Json.JsonSerializer.Deserialize<string>(await cw.ExecuteScriptAsync(img))}\n";
        }
        if (startArgs.Contains("--settingstest"))
        {
            // opens Settings as if the screen were 700 px tall (a small laptop) and as tall as it is,
            // and saves a picture of each: every section and the Save/Cancel buttons must be reachable
            foreach (var h in new[] { 700, 0 })
            {
                SettingsForm.TestScreenHeight = h;
                BeginInvoke(async () =>
                {
                    await Task.Delay(1500);
                    var f = Application.OpenForms.OfType<SettingsForm>().FirstOrDefault();
                    if (f == null) { keys += "settings: not open\n"; return; }
                    var scroll = f.Controls.OfType<Panel>().First(p => p.AutoScroll);
                    var save = f.Controls.OfType<FlowLayoutPanel>().First();
                    keys += $"settings h={(h == 0 ? "real" : h.ToString())}: client={f.ClientSize} window={f.Bounds} scrollbar={scroll.VerticalScroll.Visible} content={scroll.DisplayRectangle.Height} buttonsVisible={f.ClientRectangle.Contains(save.Bounds)}\n";
                    using var bmp = new Bitmap(f.Width, f.Height);
                    f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
                    bmp.Save($"{outFile}.settings-{(h == 0 ? "real" : h.ToString())}.png");
                    f.Close();
                });
                OpenSettings();
            }
            SettingsForm.TestScreenHeight = 0;
        }
        if (startArgs.Contains("--bardragtest"))
        {
            // needs enough bookmarks to overflow the bar: drag the last hidden one onto the bar's
            // first place, then the first hidden one to the end of the hidden list
            // drives the overflow drag the way the timer sees the mouse: press on a hidden item,
            // move, release over the target
            string Order() => string.Join(",", bookmarks.Bar.Select(b => b.Name));
            var dd = bar.OverflowButton.DropDown;
            var mouse = Point.Empty; bool down = false;
            bar.MousePos = () => mouse; bar.LeftDown = () => down;
            List<ToolStripItem> Hidden() => bar.Items.Cast<ToolStripItem>().Where(i => i.Tag is Bookmark && i.Placement == ToolStripItemPlacement.Overflow).ToList();
            async Task Drag(ToolStripItem src, Point to)
            {
                mouse = dd.PointToScreen(new Point(src.Bounds.X + 10, src.Bounds.Y + src.Bounds.Height / 2));
                down = true;
                typeof(ToolStripItem).GetMethod("HandleMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(src, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 10, 5, 0) });
                bar.OverflowDragTick();
                mouse = new Point(mouse.X, mouse.Y + 20); bar.OverflowDragTick();                mouse = to; bar.OverflowDragTick();
                down = false; bar.OverflowDragTick();
                await Task.Delay(800);
            }
            keys += $"bardrag: before -> {Order()} | hidden={Hidden().Count}\n";
            bar.OverflowButton.ShowDropDown(); await Task.Delay(800);
            var firstOnBar = bar.Items.Cast<ToolStripItem>().First(i => i.Tag is Bookmark && i.Placement == ToolStripItemPlacement.Main);
            await Drag(Hidden()[^1], bar.PointToScreen(new Point(firstOnBar.Bounds.Left + 2, firstOnBar.Bounds.Top + 4)));
            keys += $"bardrag: hidden last -> bar first -> {Order()} | tab url={Bookmark.HostOf(cw.Source)}\n";
            bar.OverflowButton.ShowDropDown(); await Task.Delay(800);
            var h = Hidden();
            await Drag(h[0], dd.PointToScreen(new Point(h[^1].Bounds.X + 10, h[^1].Bounds.Bottom - 2)));
            keys += $"bardrag: hidden first -> hidden end -> {Order()}\n";
            bar.MousePos = () => Control.MousePosition; bar.LeftDown = () => (Control.MouseButtons & MouseButtons.Left) != 0;
        }
        if (startArgs.Contains("--focustest"))
        {
            // switching back to a tab must not put a keyboard focus ring on the page's first link
            const string probe = "(() => { const a = document.activeElement; return (a ? a.tagName : 'none') + ' focusVisible=' + (a && a !== document.body ? a.matches(':focus-visible') : false); })()";
            var first = active!;
            Activate(); first.View.Focus(); // the real case: Mizar is the foreground window and the page has focus
            await Task.Delay(500);
            keys += $"focus: foreground={ActiveForm == this} firstHasFocus={first.View.ContainsFocus}\n";
            await CreateTab(cw.Source);      // a second page with links, which takes the focus
            await Task.Delay(2500);
            keys += $"focus: second has focus={active!.View.ContainsFocus}\n";
            Activate(first);
            await Task.Delay(1500);
            keys += $"focus: after switch back -> {System.Text.Json.JsonSerializer.Deserialize<string>(await cw.ExecuteScriptAsync(probe))}\n";
        }
        if (startArgs.Contains("--downloadtest"))
        {
            // a page with <a id=l href="x.pdf" download>: a link download must still follow the settings
            await cw.ExecuteScriptAsync("document.getElementById('l').click()");
            await Task.Delay(5000);
            var dir = Directory.Exists(settings.DownloadFolder) ? settings.DownloadFolder : KnownFolders.Downloads;
            keys += $"download: files in folder -> {string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName))}\n";
        }
        if (startArgs.Contains("--cookietest"))
        {
            await LogGoogleCookies("test");
            keys += "cookies: logged to diag.log\n";
        }
        if (startArgs.Contains("--stucktest"))
        {
            // a worker whose script never finishes when it is started again: the "running=starting"
            // state Gmail's worker was in on 2026-11-17. The reset must not take the engine down.
            // --oldreset runs the reset used until then (stopAllWorkers + clearDataForOrigin) instead.
            const string regs = "navigator.serviceWorker.getRegistrations().then(r => r.length)";
            await cw.ExecuteScriptAsync("navigator.serviceWorker.register('/loop-sw.js')");
            await Task.Delay(3000);
            cw.Reload();
            await Task.Delay(3000);
            await Cdp(cw, "ServiceWorker.enable", "{}");
            await Cdp(cw, "ServiceWorker.stopAllWorkers", "{}"); // healthy worker: stop it, so the next request restarts it
            await Cdp(cw, "ServiceWorker.disable", "{}");
            await Task.Delay(2000);
            await cw.ExecuteScriptAsync("fetch('/i0.svg?x=' + Date.now())"); // starts the worker: its script loops
            await Task.Delay(3000);
            var pid = cw.BrowserProcessId;
            if (!startArgs.Contains("--nobox")) await BlackBox(cw, Bookmark.HostOf(cw.Source));
            string how;
            if (startArgs.Contains("--oldreset"))
            {
                how = "old";
                await Cdp(cw, "ServiceWorker.enable", "{}");
                await Cdp(cw, "ServiceWorker.stopAllWorkers", "{}");
                await Cdp(cw, "ServiceWorker.disable", "{}");
                var u = new Uri(cw.Source);
                await Cdp(cw, "Storage.clearDataForOrigin", System.Text.Json.JsonSerializer.Serialize(new { origin = u.GetLeftPart(UriPartial.Authority), storageTypes = "service_workers,cache_storage" }));
            }
            else how = "new: " + await ResetServiceWorkers(cw);
            await Task.Delay(4000);
            bool alive; try { alive = !Process.GetProcessById((int)pid).HasExited; } catch { alive = false; }
            keys += $"stuck: reset {how} -> browser process alive={alive} restarts={engineRestarts.Count}\n";
            if (alive)
            {
                cw.Reload();
                await Task.Delay(4000);
                keys += $"stuck: after reload regs={await Eval(cw, regs)} controlled={await cw.ExecuteScriptAsync("!!navigator.serviceWorker.controller")}\n";
            }
        }
        if (startArgs.Contains("--enginetest"))
        {
            // kill the whole browser process (what happened on 2026-11-17); Mizar must start a new engine
            var pid = (int)cw.BrowserProcessId;
            keys += $"engine: killing browser process {pid}\n";
            try { Process.GetProcessById(pid).Kill(); } catch (Exception ex) { keys += "engine: kill failed " + ex.Message + "\n"; }
            await Task.Delay(12000);
            var c2 = active?.Core;
            keys += $"engine: after -> restarts={engineRestarts.Count} core={(c2 != null)} newPid={(c2 != null ? c2.BrowserProcessId : 0)} source={Bookmark.HostOf(c2?.Source ?? "")} pending={tabs.Count(t => t.PendingUrl != null)} of {tabs.Count}\n";
            if (c2 != null)
            {
                keys += $"engine: title -> {System.Text.Json.JsonSerializer.Deserialize<string>(await c2.ExecuteScriptAsync("document.title"))}\n";
                cw = c2;
            }
        }
        if (startArgs.Contains("--repairtest"))
        {
            // the page gets a service worker; "Repară conexiunea" must remove it and reload the page
            const string reg = "navigator.serviceWorker.register('/sw.js').then(() => document.title = 'sw-ok').catch(e => document.title = 'sw-fail ' + e)";
            const string count = "navigator.serviceWorker.getRegistrations().then(r => document.title = 'regs=' + r.length)";
            await cw.ExecuteScriptAsync(reg);
            await Task.Delay(3000);
            keys += $"repair: register -> '{cw.DocumentTitle}'\n";
            await cw.ExecuteScriptAsync(count); await Task.Delay(1000);
            keys += $"repair: before -> '{cw.DocumentTitle}'\n";
            await RepairConnection();
            await Task.Delay(5000);
            await cw.ExecuteScriptAsync(count); await Task.Delay(1000);
            keys += $"repair: after -> '{cw.DocumentTitle}' source={Bookmark.HostOf(cw.Source)}\n";
        }
        if (startArgs.Contains("--flushtest"))
        {
            var sw = Stopwatch.StartNew();
            var r = await FlushSockets();
            keys += $"flush: {r} in {sw.ElapsedMilliseconds} ms\n";
            cw.Reload();
            await Task.Delay(6000);
            keys += $"after flush reload: source={Bookmark.HostOf(cw.Source)} loading={active?.IsLoading}\n";
        }
        if (startArgs.Contains("--zoom") && active != null)
        {
            // simulate the user zooming to 125%, then leave the site and come back
            var tab = active;
            var start = cw.Source;
            var host = tab.ZoomHost;
            // zoom changed by something other than Mizar's own controls (like Ctrl+wheel would):
            // set the engine value directly and let the poll pick it up
            tab.View.ZoomFactor = 1.25;
            await Task.Delay(2500);
            string chip1 = zoomChip.Visible ? zoomChip.Text : "hidden";
            bool saved = settings.SiteZoom.TryGetValue(host ?? "", out var savedZ);
            cw.Navigate("https://example.com/");
            await Task.Delay(5000);
            double other = tab.View.ZoomFactor;
            string chip2 = zoomChip.Visible ? zoomChip.Text : "hidden";
            cw.Navigate(start);
            await Task.Delay(6000);
            double back = tab.View.ZoomFactor;
            string chip3 = zoomChip.Visible ? zoomChip.Text : "hidden";
            keys += $"zoom: host={host} saved={saved}:{savedZ} chip={chip1} | other site={other} chip={chip2} | back={back} chip={chip3}\n";
        }
        if (startArgs.Contains("--newtab"))
        {
            await NewTabFocused();
            await Task.Delay(15000); // time for an outside screenshot of the real window
            var t = active!;
            keys += $"newtab: tabs={tabs.Count} url='{t.Url}' title='{t.Title}' loading={t.IsLoading} core={(t.Core != null)} source='{t.Core?.Source}' address='{address.Text}' focused={address.Focused}\n";
            if (t.Core != null)
            {
                using var shot = File.Create(outFile + ".newtab.png");
                await t.Core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot);
            }
        }
        if (startArgs.Contains("--updatetest"))
        {
            // MIZAR_UPDATE_URL points at a local fake of the GitHub API; the swap happens after exit (Program)
            try
            {
                var r = await Updater.CheckAndStage();
                keys += $"update: current={Updater.CurrentText} canUpdate={Updater.CanUpdate} ready={r.Ready} version={r.Version} staged={Updater.StagedVersion} msg={r.Message}\n";
            }
            catch (Exception ex) { keys += "update: ERROR " + ex.Message + "\n"; }
        }
        if (startArgs.Contains("--sleepguard") && active != null)
        {
            // a background tab with a request in flight must not be put to sleep; once it finishes, it may
            var first = active;
            var core = first.Core!;
            await NewTabFocused();               // first tab goes to the background (NetWatch on)
            await Task.Delay(1500);
            first.LastActive = DateTime.Now - SleepAfter - TimeSpan.FromMinutes(1);
            // slow request (server answers after ~12 s), started from the background tab
            await core.ExecuteScriptAsync("const t0=performance.now(); fetch('https://postman-echo.com/delay/10?x=' + Date.now(), {cache:'no-store'}).then(r => r.status).then(s => document.title = 'done ' + s + ' ' + Math.round(performance.now()-t0) + 'ms').catch(e => document.title = 'fail ' + e)");
            await Task.Delay(2000);
            int inflight = first.InFlight.Count;
            await SleepInactiveTabs();
            bool sleptWhileBusy = core.IsSuspended;
            await Task.Delay(14000);             // the request finishes
            int after = first.InFlight.Count;
            await SleepInactiveTabs();
            bool sleptWhenIdle = core.IsSuspended;
            keys += $"sleepguard: watch={first.NetWatch} inflight={inflight} sleptWhileBusy={sleptWhileBusy} | after: inflight={after} sleptWhenIdle={sleptWhenIdle} title='{first.Title}'\n";
            Activate(first);
        }
        if (startArgs.Contains("--sleeptest"))
        {
            // background the tab, let Mizar put it to sleep, bring it back: images and network must still work
            async Task<string> Eval(CoreWebView2 c, string js)
            {
                try { return System.Text.Json.JsonSerializer.Deserialize<string>(await c.ExecuteScriptAsync(js)) ?? ""; }
                catch (Exception ex) { return "ERR " + ex.Message; }
            }
            // ExecuteScriptAsync does not await promises, so the probe is synchronous (sync XHR for the network check)
            const string probe = "(() => { const imgs=[...document.images]; const broken=imgs.filter(i=>i.complete && i.naturalWidth===0).length; " +
                                 "const pending=imgs.filter(i=>!i.complete).length; let net='?'; " +
                                 "try { const x=new XMLHttpRequest(); x.open('GET', location.origin + '/favicon.ico?t=' + Date.now(), false); x.send(); net=x.status; } catch(e) { net='ERR ' + e.message; } " +
                                 "return 'images=' + imgs.length + ' broken=' + broken + ' pending=' + pending + ' net=' + net + ' visible=' + document.visibilityState; })()";
            var first = tabs[0];
            string before = await Eval(cw, probe);
            await CreateTab("https://example.com/");               // first tab goes to the background (Low memory)
            await Task.Delay(3000);
            bool suspended = false;
            try { suspended = await cw.TrySuspendAsync(); } catch { }
            await Task.Delay(int.TryParse(Environment.GetEnvironmentVariable("MIZAR_SLEEP_SECONDS"), out var ss) ? ss * 1000 : 15000);
            Activate(first);
            await Task.Delay(1500);
            string after1 = await Eval(cw, probe);
            await Task.Delay(5000);
            string after6 = await Eval(cw, probe);
            keys += $"sleeptest: suspended={suspended}\n  before:   {before}\n  after 1s: {after1}\n  after 6s: {after6}\n";
            if (!startArgs.Contains("--noshot"))
                using (var shot = File.Create(outFile + ".after.png"))
                    await Task.WhenAny(cw.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot), Task.Delay(5000));
        }
        if (startArgs.Contains("--histback"))
        {
            // history page -> follow a link -> Back: the page must come back and still delete entries
            async Task<string> Eval(string js)
            {
                try { return System.Text.Json.JsonSerializer.Deserialize<string>(await cw.ExecuteScriptAsync(js)) ?? ""; }
                catch (Exception ex) { return "ERR " + ex.Message; }
            }
            int before = history.Count;
            string rows1 = await Eval("String(document.querySelectorAll('.row').length)");
            cw.Navigate("https://example.com/");
            await Task.Delay(4000);
            cw.GoBack();
            await Task.Delay(4000);
            string rows2 = await Eval("String(document.querySelectorAll('.row').length)");
            await Eval("(document.querySelector('.del')?.click(), 'clicked')");
            await Task.Delay(1500);
            keys += $"histback: rows before={rows1} after back={rows2} url={cw.Source} entries {before} -> {history.Count}\n";
        }
        if (startArgs.Contains("--crash"))
        {
            // crash the page repeatedly: it must stop reloading after 3 crashes in a minute
            for (int i = 0; i < 4; i++)
            {
                if (InternalPages.IsInternal(cw.Source)) break;
                try { await cw.CallDevToolsProtocolMethodAsync("Page.crash", "{}"); } catch { }
                await Task.Delay(4000);
            }
            await Task.Delay(2000);
            keys += $"crash: final url={cw.Source} crashes={active?.Crashes.Count}\n";
        }
        if (startArgs.Contains("--suggest"))
        {
            int qi = Array.IndexOf(startArgs, "--query");
            address.Focus();
            address.Text = qi >= 0 && qi + 1 < startArgs.Length ? startArgs[qi + 1] : "wiki";
            await Task.Delay(300);
            var sel1 = suggest.Visible ? suggest.Count : 0;
            if (suggest.Visible)
            {
                using var bmp = new Bitmap(suggest.Width, suggest.Height);
                suggest.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(outFile + ".suggest.png");
            }
            suggest.Step(1);
            var sw = Stopwatch.StartNew();
            foreach (var q in new[] { "ex", "exa", "examp", "zzzz", "pagina", "wiki" })
                for (int i = 0; i < 50; i++) SuggestBox.Find(q, bookmarks.AllUrls(), history.NewestFirst());
            double perKey = sw.Elapsed.TotalMilliseconds / 300;
            keys += $"suggest: visible={suggest.Visible} count={sel1} first={suggest.Selected?.Url} historyEntries={history.Count} msPerKeystroke={perKey:F2}\n";
            HideSuggestions();
        }
        if (startArgs.Contains("--keys"))
        {
            // press Ctrl+H with the web page focused, exactly like the user would
            Activate(tabs[0]);
            // a real key press delivered straight into the page (no risk of typing into other windows)
            const string down = "{\"type\":\"keyDown\",\"modifiers\":2,\"key\":\"h\",\"code\":\"KeyH\",\"windowsVirtualKeyCode\":72}";
            const string up = "{\"type\":\"keyUp\",\"modifiers\":2,\"key\":\"h\",\"code\":\"KeyH\",\"windowsVirtualKeyCode\":72}";
            await cw.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", down);
            await cw.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", up);
            await Task.Delay(3000);
            keys = $"historyOpenedByCtrlH={tabs.Any(t => t.Internal == "history")}\n";
            if (active?.Core != null)
            {
                await Task.Delay(1500);
                using var fs = File.Create(outFile + ".png");
                await active.Core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            }
        }
        string text;
        try { text = System.Text.Json.JsonSerializer.Deserialize<string>(await cw.ExecuteScriptAsync("document.body ? document.body.innerText.slice(0, 4000) : ''")) ?? ""; }
        catch (Exception ex) { text = "script failed: " + ex.Message; }
        text = keys + text;
        if (startArgs.Contains("--netlog"))
        {
            List<string> fails; lock (netFails) fails = netFails.ToList();
            var summary = fails.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => $"  {g.Count()}x {g.Key}");
            text = $"netlog: {fails.Count} failed requests\n{string.Join("\n", summary)}\n" +
                   (startArgs.Contains("--notext") ? "" : text);
        }
        File.WriteAllText(outFile,
            $"url={cw.Source}\nscheme={cw.Profile.PreferredColorScheme}\nblock3pc={settings.BlockThirdPartyCookies}\nblockedAds={AdBlocker.BlockedAds}\nblockedTrackers={AdBlocker.BlockedTrackers}\n---\n{text}");
        // --noshot: private pages (e.g. the owner's mail) are never captured
        if (!startArgs.Contains("--noshot")) try
        {
            // a hidden tab never renders a frame, so its capture can wait forever: give up after 5 s
            using var shot = File.Create(outFile + ".page.png");
            await Task.WhenAny(cw.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot), Task.Delay(5000));
        }
        catch { }
        Close();
    }

    async Task LoadFavicon(BrowserTab tab, CoreWebView2 cw)
    {
        try
        {
            using var stream = await cw.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (tab.View.IsDisposed) return;
            if (stream == null || stream.Length == 0) { tab.SetFavicon(null, false); TabChanged(tab); return; }
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            Image img;
            using (var tmp = Image.FromStream(ms)) img = new Bitmap(tmp);
            if (tab.View.IsDisposed) { img.Dispose(); return; }
            tab.SetFavicon(img, owned: true); // the previous icon is released here
            TabChanged(tab);

            var host = Bookmark.HostOf(cw.Source);
            bool updated = host.Length > 0 && FaviconCache.Save(host, img, overwrite: true);
            var urlKey = OperaFavicons.UrlKey(cw.Source);
            if (FaviconCache.Has(urlKey) && FaviconCache.Save(urlKey, img, overwrite: true)) updated = true;
            if (updated && bookmarks.HasHost(host))
                RebuildBookmarkBar();
        }
        catch { }
    }

    async Task PrefetchFavicons()
    {
        var hosts = bookmarks.AllUrls().Select(b => Bookmark.HostOf(b.Url))
            .Where(h => h.Length > 0 && !FaviconCache.Has(h))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hosts.Count == 0) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        int got = 0;
        await Task.WhenAll(hosts.Select(async h =>
        {
            foreach (var u in new[] { $"https://{h}/favicon.ico", $"https://www.{h}/favicon.ico" })
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync(u);
                    var img = FaviconCache.Decode(bytes);
                    if (img == null) continue;
                    if (FaviconCache.Save(h, img)) got++;
                    img.Dispose();
                    return;
                }
                catch { }
            }
        }));
        if (got > 0) RebuildBookmarkBar();
    }

    void Activate(BrowserTab tab)
    {
        if (active == tab) { SyncStrip(); return; }
        var prev = active;
        CheckZoom(prev);
        active = tab;
        sessionDirty = true;
        HideSuggestions();
        tab.View.Visible = true;
        tab.View.BringToFront();
        if (tab.PendingUrl is { } pending)
        {
            // restored tab opened for the first time: start its page now
            tab.PendingUrl = null;
            tab.IsLoading = true;
            _ = InitTab(tab, pending);
        }
        if (tab.Core != null) tab.Core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        _ = SetNetWatch(tab, false);
        if (prev != null && !prev.View.IsDisposed)
        {
            // Give the new tab the focus BEFORE hiding the old one. Hiding the focused WebView makes
            // WinForms pick the next control with a directed Select, which the WinForms WebView2 turns
            // into MoveFocus(Next): the page then focuses its first link ("Skip to main content")
            // with a visible focus ring (owner's report, 2026-11-20).
            if (prev.View.ContainsFocus)
            {
                if (tab.Url != "about:blank") tab.View.Focus(); else address.Focus();
            }
            prev.View.Visible = false;
            prev.LastActive = DateTime.Now;
            if (prev.Core != null && LowMemoryForHiddenTabs) prev.Core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            _ = SetNetWatch(prev, true);
        }
        SyncStrip();
        UpdateAddress(force: true);
        UpdateNav();
        UpdateTitle();
        if (tab.Url != "about:blank") tab.View.Focus();
    }

    void SyncStrip()
    {
        tabStrip.SelectedIndex = active == null ? -1 : tabs.IndexOf(active);
        tabStrip.Refresh2();
    }

    void CloseTab(BrowserTab tab)
    {
        int i = tabs.IndexOf(tab);
        if (i < 0) return;
        if (!string.IsNullOrEmpty(tab.Url) && tab.Url != "about:blank") closedTabs.Push(tab.Url);
        tabs.RemoveAt(i);
        content.Controls.Remove(tab.View);
        tab.View.Dispose();
        tab.SetFavicon(null, false); // release the tab's own icon
        tab.Receivers.Clear();
        tab.InFlight.Clear();
        sessionDirty = true;

        if (tabs.Count == 0) { Close(); return; }
        if (active == tab)
        {
            active = null;
            Activate(tabs[Math.Min(i, tabs.Count - 1)]);
        }
        else SyncStrip();
    }

    void ReopenClosed()
    {
        if (closedTabs.Count > 0) _ = CreateTab(closedTabs.Pop());
    }

    void Cycle(int d)
    {
        if (tabs.Count < 2 || active == null) return;
        int i = (tabs.IndexOf(active) + d + tabs.Count) % tabs.Count;
        Activate(tabs[i]);
    }

    void ShowTabMenu(int i, Point p)
    {
        var tab = tabs[i];
        var cm = NewMenu();
        cm.Items.Add("Tab nou", null, (_, _) => _ = NewTabFocused());
        cm.Items.Add("Reîncarcă", null, (_, _) => tab.Core?.Reload());
        cm.Items.Add("Duplică", null, (_, _) => _ = CreateTab(tab.Url, true, insertAt: tabs.IndexOf(tab) + 1));
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add("Închide", null, (_, _) => CloseTab(tab));
        cm.Items.Add("Închide celelalte tab-uri", null, (_, _) =>
        {
            foreach (var t in tabs.Where(t => t != tab).ToList()) CloseTab(t);
        });
        cm.Items.Add("Redeschide tabul închis", null, (_, _) => ReopenClosed()).Enabled = closedTabs.Count > 0;
        cm.Show(tabStrip, p);
    }

    void TabChanged(BrowserTab tab)
    {
        if (IsDisposed) return;
        sessionDirty = true;
        tabStrip.Refresh2();
        if (tab != active) return;
        UpdateAddress();
        UpdateNav();
        UpdateTitle();
    }

    void UpdateTitle() => Text = active == null ? "Mizar" : $"{active.Title} - Mizar";

    bool settingAddress;

    void UpdateAddress(bool force = false)
    {
        UpdateSecurity();
        if (address.Focused && !force) return;
        string next = active?.Internal == "history" ? "Istoric"
            : (active?.Url ?? "") is var u && u == "about:blank" ? "" : u;
        if (address.Text == next) return;
        settingAddress = true;
        try { address.Text = next; }
        finally { settingAddress = false; }
    }

    /// <summary>Padlock / "Nesigur" indicator for the active tab.</summary>
    void UpdateSecurity()
    {
        var u = active?.Url ?? "";
        SecurityLevel level;
        string tip;
        if (InternalPages.IsInternal(u)) { level = SecurityLevel.Internal; tip = "Pagină Mizar"; }
        else if (active?.CertError == true)
        {
            level = SecurityLevel.CertError;
            tip = "Certificatul de securitate al site-ului nu e valid.\nConexiunea poate fi interceptată. Nu introduce parole sau date de card.";
        }
        else if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            level = SecurityLevel.Secure;
            tip = "Conexiune securizată (HTTPS)\nDatele trimise către " + Bookmark.HostOf(u) + " sunt criptate.";
        }
        else if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            level = SecurityLevel.Insecure;
            tip = "Conexiune nesecurizată (HTTP)\nCe trimiți pe acest site poate fi văzut de alții. Nu introduce parole sau date de card.";
        }
        else { level = SecurityLevel.None; tip = ""; }
        if (addressBox.Security != level) addressBox.Security = level;
        tips.SetToolTip(addressBox, tip);
    }

    const string ShortcutScript = """
(() => {
  if (window.top !== window) return;
  addEventListener('keydown', e => {
    if (!e.ctrlKey || e.altKey || e.metaKey) return;
    const k = (e.key || '').toLowerCase();
    let c = null;
    if (e.shiftKey) c = { t: 'reopen', b: 'bar', o: 'bookmarks', tab: 'prevtab' }[k];
    else if (/^[1-9]$/.test(k)) c = 'tab' + k;
    else c = { h: 'history', t: 'newtab', w: 'close', d: 'bookmark', l: 'address', j: 'downloads', tab: 'nexttab' }[k];
    if (!c) return;
    e.preventDefault(); e.stopImmediatePropagation();
    window.chrome && chrome.webview && chrome.webview.postMessage({ mizarKey: c });
  }, true);
})();
""";

    void RunShortcut(string c)
    {
        switch (c)
        {
            case "history": _ = OpenHistory(); break;
            case "newtab": _ = NewTabFocused(); break;
            case "close": if (active != null) CloseTab(active); break;
            case "reopen": ReopenClosed(); break;
            case "bookmark": ToggleBookmark(); break;
            case "bar": ToggleBar(); break;
            case "bookmarks": OpenBookmarkManager(); break;
            case "address": address.Focus(); address.SelectAll(); break;
            case "downloads": active?.Core?.OpenDefaultDownloadDialog(); break;
            case "nexttab": Cycle(1); break;
            case "prevtab": Cycle(-1); break;
            default:
                if (c.StartsWith("tab") && int.TryParse(c[3..], out int n) && tabs.Count > 0)
                    Activate(tabs[n == 9 ? tabs.Count - 1 : Math.Min(n - 1, tabs.Count - 1)]);
                break;
        }
    }

    /// <summary>
    /// Ctrl+H: shows the history page (https://mizar.internal/history, served locally), reusing an
    /// open history tab if there is one. Being a real address, Back/Forward/Reload all work on it.
    /// </summary>
    async Task OpenHistory()
    {
        history.Flush();
        var tab = tabs.FirstOrDefault(t => t.Internal == "history");
        if (tab == null) { await CreateTab(InternalPages.History); return; }
        bool wasPending = tab.PendingUrl != null;
        Activate(tab);
        if (!wasPending) tab.Core?.Reload(); // fresh list
    }

    void UpdateNav()
    {
        var cw = active?.Core;
        btnBack.Enabled = cw?.CanGoBack ?? false;
        btnFwd.Enabled = cw?.CanGoForward ?? false;
        bool loading = active?.IsLoading ?? false;
        btnReload.Glyph = loading ? GStop : GReload;
        tips.SetToolTip(btnReload, loading ? "Oprește" : "Reîncarcă (F5)");
        UpdateStar();
        UpdateZoomChip();
    }

    void UpdateStar()
    {
        if (btnStar == null) return;
        bool on = bookmarks.FindByUrl(active?.Url) != null;
        btnStar.Glyph = on ? GStarFull : GStar;
        btnStar.GlyphColor = on ? Theme.Accent : null;
        btnStar.Invalidate();
        tips.SetToolTip(btnStar, on ? "Elimină din bookmark-uri (Ctrl+D)" : "Adaugă la bookmark-uri (Ctrl+D)");
    }

    // ---------- Navigation ----------

    public static string Normalize(string input)
    {
        var s = input.Trim();
        if (s.Length == 0) return "about:blank";
        if (Uri.TryCreate(s, UriKind.Absolute, out var u) &&
            u.Scheme is "http" or "https" or "file" or "about" or "edge" or "data" or "view-source" or "mailto")
            return s;
        if (!s.Contains(' '))
        {
            if (s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || s.StartsWith("127.0.0.1"))
                return "http://" + s;
            if (s.Contains('.') && Uri.TryCreate("https://" + s, UriKind.Absolute, out var u2) &&
                u2.Host.Contains('.') && !u2.Host.EndsWith('.'))
                return "https://" + s;
        }
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(s);
    }

    void Navigate(string input)
    {
        if (active?.Core == null) return;
        HideSuggestions();
        active.View.Focus();
        var target = Normalize(input);
        try { active.Core.Navigate(target); }
        catch { active.Core.Navigate("https://www.google.com/search?q=" + Uri.EscapeDataString(input)); }
    }

    /// <summary>Steps through the standard zoom levels (like Chrome/Edge): dir = +1 bigger, -1 smaller.</summary>
    void Zoom(int dir)
    {
        if (active == null) return;
        double z = active.View.ZoomFactor;
        double next = dir > 0
            ? ZoomLevels.FirstOrDefault(l => l > z + 0.001, ZoomLevels[^1])
            : ZoomLevels.LastOrDefault(l => l < z - 0.001, ZoomLevels[0]);
        SetUserZoom(active, next);
    }

    /// <summary>Zoom chosen through Mizar's own controls. Setting ZoomFactor from code does not raise
    /// ZoomFactorChanged, so record it explicitly.</summary>
    void SetUserZoom(BrowserTab tab, double z)
    {
        tab.View.ZoomFactor = z;
        OnZoomChanged(tab);
    }

    // ---------- Bookmarks ----------

    void ToggleBookmark()
    {
        if (active == null || string.IsNullOrEmpty(active.Url) || active.Url == "about:blank") return;
        var existing = bookmarks.FindByUrl(active.Url);
        if (existing != null) bookmarks.Remove(existing);
        else
        {
            var name = active.Title;
            if (name.Length > 60) name = name[..60];
            bookmarks.Bar.Add(new Bookmark { Name = name, Url = active.Url });
            if (active.Favicon != null)
            {
                FaviconCache.Save(Bookmark.HostOf(active.Url), active.Favicon, overwrite: true);
                FaviconCache.Save(OperaFavicons.UrlKey(active.Url), active.Favicon, overwrite: true);
            }
        }
        bookmarks.NotifyChanged();
    }

    void ToggleBar()
    {
        settings.ShowBookmarksBar = !settings.ShowBookmarksBar;
        settings.Save();
        if (!fullscreen) bar.Visible = settings.ShowBookmarksBar;
    }

    void OpenBookmarkManager()
    {
        using var f = new BookmarkManagerForm(bookmarks, url => _ = CreateTab(url));
        f.ShowDialog(this);
    }

    void ImportOpera()
    {
        int n;
        try { n = bookmarks.ImportFromOpera(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Nu am putut citi bookmark-urile din Opera:\n" + ex.Message, "Mizar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (n < 0) { MessageBox.Show(this, "Nu am găsit Opera pe acest calculator.", "Mizar"); return; }
        bookmarks.NotifyChanged();
        MessageBox.Show(this, n == 0 ? "Toate bookmark-urile din Opera sunt deja aici." : $"Am importat {n} bookmark-uri din Opera.", "Mizar");
        if (n > 0) _ = ImportOperaFavicons().ContinueWith(_ => BeginInvoke(() => _ = PrefetchFavicons()));
    }

    // ---------- Downloads ----------

    /// <summary>The tab shows this very PDF, so the download is the viewer's own Save.</summary>
    static bool IsPdfViewerSave(CoreWebView2 src, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var op = e.DownloadOperation;
            bool pdf = string.Equals(op.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)
                       || e.ResultFilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            return pdf && Uri.TryCreate(op.Uri, UriKind.Absolute, out var a) && Uri.TryCreate(src.Source, UriKind.Absolute, out var b)
                   && Uri.Compare(a, b, UriComponents.HttpRequestUrl, UriFormat.Unescaped, StringComparison.Ordinal) == 0;
        }
        catch { return false; }
    }

    void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var name = Path.GetFileName(e.ResultFilePath);
        if (string.IsNullOrEmpty(name)) name = "descarcare";
        var folder = Directory.Exists(settings.DownloadFolder) ? settings.DownloadFolder : KnownFolders.Downloads;

        // Saving from the PDF viewer's own toolbar (or Ctrl+S on a PDF): the engine has already shown
        // its Save As dialog, and ResultFilePath is where the owner chose. Asking again was the
        // "asks twice" bug of 2026-11-18. A link to a PDF is not affected: its Uri isn't the page's.
        bool viewerSave = sender is CoreWebView2 src && IsPdfViewerSave(src, e);
        DiagLog.Write($"download {Bookmark.HostOf(e.DownloadOperation.Uri)} mime={e.DownloadOperation.MimeType} pdfViewerSave={viewerSave} ask={settings.AskWhereToSave}");
        if (viewerSave) return;

        if (!settings.AskWhereToSave)
        {
            try { Directory.CreateDirectory(folder); } catch { }
            e.ResultFilePath = UniquePath(folder, name);
            return;
        }

        var deferral = e.GetDeferral();
        BeginInvoke(() =>
        {
            try
            {
                using var d = new SaveFileDialog { FileName = name, InitialDirectory = folder, OverwritePrompt = true };
                var ext = Path.GetExtension(name);
                d.Filter = ext.Length > 0 ? $"Fișier {ext}|*{ext}|Toate fișierele|*.*" : "Toate fișierele|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) e.ResultFilePath = d.FileName;
                else e.Cancel = true;
            }
            finally { deferral.Complete(); }
        });
    }

    static string UniquePath(string folder, string name)
    {
        var p = Path.Combine(folder, name);
        if (!File.Exists(p)) return p;
        var b = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            p = Path.Combine(folder, $"{b} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    void OpenDownloadsFolder()
    {
        var folder = Directory.Exists(settings.DownloadFolder) ? settings.DownloadFolder : KnownFolders.Downloads;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); } catch { }
    }

    // ---------- Settings ----------

    bool applyingRemoteSync;
    readonly System.Windows.Forms.Timer driveUploadTimer = new() { Interval = 2000 };

    void ScheduleDriveUpload()
    {
        driveUploadTimer.Stop();
        driveUploadTimer.Start();
    }

    async void OnDriveUploadTick(object? sender, EventArgs e)
    {
        driveUploadTimer.Stop();
        try { await DriveSync.UploadAsync(bookmarks, settings); }
        catch (Exception ex) { ErrorLog.Write("DriveUpload", ex); }
    }

    async Task SyncBookmarksAsync(bool silent)
    {
        try
        {
            var res = await DriveSync.SyncAsync(bookmarks, settings, remote =>
            {
                if (IsDisposed) return;
                BeginInvoke(() =>
                {
                    applyingRemoteSync = true;
                    try { bookmarks.ReplaceFromSync(remote); }
                    finally { applyingRemoteSync = false; }
                    _ = PrefetchFavicons();
                });
            });
            if (!silent)
            {
                var msg = res switch
                {
                    DriveSync.SyncResult.Downloaded => "Bookmark-urile au fost actualizate din Google Drive.",
                    DriveSync.SyncResult.Uploaded => "Bookmark-urile au fost trimise în Google Drive.",
                    DriveSync.SyncResult.UpToDate => "Bookmark-urile sunt deja la zi.",
                    _ => "Google Drive nu este conectat."
                };
                MessageBox.Show(this, msg, "Mizar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("DriveSync", ex);
            if (!silent) MessageBox.Show(this, "Eroare la sincronizarea Google Drive:\n" + ex.Message, "Mizar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    async Task ConnectDriveAsync()
    {
        try
        {
            await DriveSync.ConnectAsync(settings, url => _ = CreateTab(url));
            await SyncBookmarksAsync(silent: false);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("DriveConnect", ex);
            MessageBox.Show(this, "Conectarea la Google Drive a eșuat:\n" + ex.Message, "Mizar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void OpenSettings()
    {
        using var f = new SettingsForm(settings, ClearData, ImportOpera, OpenBookmarkManager, MakeDefaultBrowser,
            ConnectDriveAsync, () => SyncBookmarksAsync(silent: false), () => DriveSync.Disconnect(settings), url => _ = CreateTab(url),
            () => CheckForUpdates(manual: true));
        if (f.ShowDialog(this) != DialogResult.OK) return;
        foreach (var t in tabs)
            if (t.Core != null) ApplySettings(t.Core);
        if (!fullscreen) bar.Visible = settings.ShowBookmarksBar;
        if (settings.BlockThirdPartyCookies != thirdPartyBlockedAtStart)
            MessageBox.Show(this, "Schimbarea pentru cookie-urile third-party se aplică după ce redeschizi Mizar.", "Mizar",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Registers Mizar as a browser with Windows, then opens the Windows page where the user picks it.
    /// (Windows only lets the user change the default browser, not programs.)
    /// </summary>
    void MakeDefaultBrowser()
    {
        try { DefaultBrowser.Register(); }
        catch (Exception ex)
        {
            ErrorLog.Write("Register", ex);
            MessageBox.Show(this, "Nu am putut înregistra Mizar în Windows:\n" + ex.Message, "Mizar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        MessageBox.Show(this,
            "Mizar e înregistrat ca browser.\n\nSe deschide acum pagina din Windows: apasă „Setați valoarea implicită” " +
            "(sau alege Mizar la HTTP și HTTPS).", "Mizar", MessageBoxButtons.OK, MessageBoxIcon.Information);
        DefaultBrowser.OpenWindowsSettings();
    }

    async Task ClearData(CoreWebView2BrowsingDataKinds kinds)
    {
        var cw = tabs.Select(t => t.Core).FirstOrDefault(c => c != null);
        if (cw == null) return;
        if (kinds.HasFlag(CoreWebView2BrowsingDataKinds.BrowsingHistory)) history.Clear();
        try { await cw.Profile.ClearBrowsingDataAsync(kinds); } catch { }
    }

    // ---------- Window ----------

    void SetFullscreen(bool on)
    {
        if (on == fullscreen) return;
        fullscreen = on;
        SuspendLayout();
        if (on)
        {
            prevState = WindowState;
            tabStrip.Visible = nav.Visible = bar.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
            WindowState = prevState;
            tabStrip.Visible = nav.Visible = true;
            bar.Visible = settings.ShowBookmarksBar;
        }
        ResumeLayout();
    }

    void RestoreWindow()
    {
        var b = settings.WindowBounds;
        if (b is { Length: 4 })
        {
            var r = new Rectangle(b[0], b[1], b[2], b[3]);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)) && r.Width > 300 && r.Height > 200)
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = r;
            }
        }
        if (settings.Maximized) WindowState = FormWindowState.Maximized;
    }

    bool IsSelfTest => startArgs.Contains("--selftest");

    void SaveWindow()
    {
        if (fullscreen) return;
        var effectiveState = WindowState == FormWindowState.Minimized ? lastWindowState : WindowState;
        var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (r.Width > 300 && r.Height > 200)
            settings.WindowBounds = new[] { r.X, r.Y, r.Width, r.Height };
        settings.Maximized = effectiveState == FormWindowState.Maximized;
        settings.Save();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.T: _ = NewTabFocused(); return true;
            case Keys.Control | Keys.W:
            case Keys.Control | Keys.F4: if (active != null) CloseTab(active); return true;
            case Keys.Control | Keys.Shift | Keys.T: ReopenClosed(); return true;
            case Keys.Control | Keys.L:
            case Keys.Alt | Keys.D:
            case Keys.F6: address.Focus(); address.SelectAll(); return true;
            case Keys.Control | Keys.Tab:
            case Keys.Control | Keys.PageDown: Cycle(1); return true;
            case Keys.Control | Keys.Shift | Keys.Tab:
            case Keys.Control | Keys.PageUp: Cycle(-1); return true;
            case Keys.Control | Keys.D: ToggleBookmark(); return true;
            case Keys.Control | Keys.Shift | Keys.B: ToggleBar(); return true;
            case Keys.Control | Keys.Shift | Keys.O: OpenBookmarkManager(); return true;
            case Keys.Control | Keys.J: active?.Core?.OpenDefaultDownloadDialog(); return true;
            case Keys.Control | Keys.H: _ = OpenHistory(); return true;
            case Keys.F5:
            case Keys.Control | Keys.R: active?.Core?.Reload(); return true;
            case Keys.Alt | Keys.Left: active?.Core?.GoBack(); return true;
            case Keys.Alt | Keys.Right: active?.Core?.GoForward(); return true;
            case Keys.Alt | Keys.Home: Navigate(settings.HomePage); return true;
            case Keys.F11: SetFullscreen(!fullscreen); return true;
            case Keys.Escape when fullscreen && active?.Core?.ContainsFullScreenElement != true: SetFullscreen(false); return true;
        }
        var k = keyData & Keys.KeyCode;
        if ((keyData & Keys.Modifiers) == Keys.Control && k >= Keys.D1 && k <= Keys.D9 && tabs.Count > 0)
        {
            int idx = k == Keys.D9 ? tabs.Count - 1 : Math.Min(k - Keys.D1, tabs.Count - 1);
            Activate(tabs[idx]);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
