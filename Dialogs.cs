using Microsoft.Web.WebView2.Core;

namespace Mizar;

static class Ui
{
    public static readonly Font Font = new("Segoe UI", 9.5f);
    public static readonly Font Bold = new("Segoe UI Semibold", 10.5f);

    public static Button Btn(string text, EventHandler? click = null)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 3, 6, 3) };
        if (click != null) b.Click += click;
        return b;
    }

    public static Label Header(string text) => new()
    {
        Text = text, Font = Bold, AutoSize = true, Margin = new Padding(0, 14, 0, 4)
    };
}

public class EditDialog : Form
{
    public readonly TextBox NameBox = new() { Width = 420 };
    public readonly TextBox UrlBox = new() { Width = 420 };

    public EditDialog(string title, string name, string? url)
    {
        Text = title;
        Font = Ui.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(14), Dock = DockStyle.Fill };
        t.Controls.Add(new Label { Text = "Nume", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 6) }, 0, 0);
        NameBox.Text = name;
        t.Controls.Add(NameBox, 1, 0);
        if (url != null)
        {
            t.Controls.Add(new Label { Text = "Adresă", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 6) }, 0, 1);
            UrlBox.Text = url;
            t.Controls.Add(UrlBox, 1, 1);
        }
        var ok = Ui.Btn("Salvează");
        ok.DialogResult = DialogResult.OK;
        var cancel = Ui.Btn("Anulează");
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        t.Controls.Add(buttons, 0, 2);
        t.SetColumnSpan(buttons, 2);
        Controls.Add(t);
        AcceptButton = ok;
        CancelButton = cancel;
        Theme.ApplyToDialog(this);
    }
}

public class SettingsForm : Form
{
    readonly Settings s;
    readonly TextBox home = new() { Width = 380 };
    readonly TextBox folder = new() { Width = 380, ReadOnly = true };
    readonly CheckBox ask = new() { Text = "Întreabă-mă unde salvez fiecare fișier", AutoSize = true };
    readonly CheckBox ads = new() { Text = "Blochează reclamele", AutoSize = true };
    readonly CheckBox trackers = new() { Text = "Blochează tracker-ele (Google Analytics, Facebook Pixel, Hotjar...)", AutoSize = true };
    readonly CheckBox thirdParty = new() { Text = "Blochează cookie-urile third-party (urmărirea între site-uri)", AutoSize = true };
    readonly CheckBox pw = new() { Text = "Oferă să salvez parolele și datele din formulare", AutoSize = true };
    readonly CheckBox bar = new() { Text = "Afișează bara de bookmark-uri", AutoSize = true };
    readonly CheckBox restore = new() { Text = "Redeschide tab-urile de data trecută", AutoSize = true };
    readonly CheckBox clearOnExit = new() { Text = "Șterge istoricul când închid Mizar", AutoSize = true };
    readonly CheckBox autoUpdate = new() { Text = "Caută actualizări automat, o dată pe lună", AutoSize = true };
    readonly ComboBox scheme = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 260 };
    static readonly (string key, string label)[] Schemes =
    {
        ("auto", "Ca Windows (automat)"), ("light", "Mereu luminoasă"), ("dark", "Mereu întunecată")
    };

    public SettingsForm(Settings settings, Func<CoreWebView2BrowsingDataKinds, Task> clear, Action import,
        Action manage, Action makeDefault, Func<Task> connectDrive, Func<Task> syncDrive, Action disconnectDrive, Action<string> openUrl,
        Func<Task> checkUpdates)
    {
        s = settings;
        Text = "Setări";
        Font = Ui.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(18, 4, 18, 14) };

        FlowLayoutPanel Row(params Control[] cs)
        {
            var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            foreach (var c in cs)
            {
                if (c is TextBox) c.Margin = new Padding(0, 5, 8, 3);
                r.Controls.Add(c);
            }
            return r;
        }

        home.Text = s.HomePage;
        folder.Text = s.DownloadFolder;
        ask.Checked = s.AskWhereToSave;
        ads.Checked = s.BlockAds;
        trackers.Checked = s.BlockTrackers;
        thirdParty.Checked = s.BlockThirdPartyCookies;
        pw.Checked = s.SavePasswords;
        bar.Checked = s.ShowBookmarksBar;
        restore.Checked = s.RestoreSession;
        clearOnExit.Checked = s.ClearHistoryOnExit;
        autoUpdate.Checked = s.AutoUpdate;

        foreach (var (_, label) in Schemes) scheme.Items.Add(label);
        scheme.SelectedIndex = Math.Max(0, Array.FindIndex(Schemes, x => x.key == s.WebColorScheme));

        col.Controls.Add(Ui.Header("Pagina de pornire"));
        col.Controls.Add(Row(home, Ui.Btn("Google", (_, _) => home.Text = "https://www.google.com")));
        col.Controls.Add(restore);
        col.Controls.Add(Row(Ui.Btn("Fă din Mizar browserul implicit...", (_, _) => makeDefault())));

        col.Controls.Add(Ui.Header("Descărcări"));
        col.Controls.Add(Row(folder, Ui.Btn("Schimbă...", (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = folder.Text, UseDescriptionForTitle = true, Description = "Unde salvez fișierele descărcate" };
            if (d.ShowDialog(this) == DialogResult.OK) folder.Text = d.SelectedPath;
        })));
        col.Controls.Add(ask);

        col.Controls.Add(Ui.Header("Confidențialitate"));
        col.Controls.Add(ads);
        col.Controls.Add(trackers);
        col.Controls.Add(thirdParty);
        col.Controls.Add(new Label
        {
            Text = "Dacă un site nu te lasă să te loghezi printr-o fereastră pop-up, debifează temporar opțiunea de mai sus.",
            AutoSize = true, MaximumSize = new Size(560, 0), Tag = "muted", Margin = new Padding(20, 0, 0, 6)
        });
        col.Controls.Add(pw);
        col.Controls.Add(clearOnExit);

        async Task Clear(CoreWebView2BrowsingDataKinds kinds, string what)
        {
            if (MessageBox.Show(this, $"Sigur ștergi {what}?", "Mizar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await clear(kinds);
            MessageBox.Show(this, "Gata.", "Mizar");
        }

        col.Controls.Add(Row(
            Ui.Btn("Golește cache-ul", async (_, _) => await Clear(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage, "cache-ul")),
            Ui.Btn("Șterge istoricul", async (_, _) => await Clear(CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory, "istoricul")),
            Ui.Btn("Șterge cookie-urile", async (_, _) => await Clear(CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.AllDomStorage, "cookie-urile (vei fi deconectat de pe toate site-urile)")),
            Ui.Btn("Șterge parolele", async (_, _) => await Clear(CoreWebView2BrowsingDataKinds.PasswordAutosave | CoreWebView2BrowsingDataKinds.GeneralAutofill, "parolele salvate și datele din formulare"))));

        col.Controls.Add(Ui.Header("Aspect"));
        col.Controls.Add(Row(new Label { Text = "Tema site-urilor:", AutoSize = true, Margin = new Padding(0, 7, 8, 3) }, scheme));
        col.Controls.Add(new Label
        {
            Text = "Pentru site-urile care au și variantă întunecată (Google, YouTube...).",
            AutoSize = true, MaximumSize = new Size(560, 0), Tag = "muted", Margin = new Padding(0, 0, 0, 6)
        });

        col.Controls.Add(Ui.Header("Bookmark-uri"));
        col.Controls.Add(bar);
        col.Controls.Add(Row(Ui.Btn("Gestionează...", (_, _) => manage()), Ui.Btn("Importă din Opera", (_, _) => import())));

        var driveStatus = new Label { AutoSize = true, Tag = "muted", Margin = new Padding(0, 8, 0, 2) };
        var btnDriveConn = Ui.Btn("");
        var btnDriveSync = Ui.Btn("Sincronizează acum");
        var btnDriveApi = Ui.Btn("Setări API...");

        void RefreshDriveUi()
        {
            bool on = DriveSync.IsConnected(s);
            btnDriveConn.Text = on ? "Deconectează" : "Conectează Google Drive...";
            btnDriveSync.Enabled = on;
            if (!on) driveStatus.Text = "Sincronizare Google Drive: neconectat (0 RAM în fundal).";
            else
            {
                var who = string.IsNullOrWhiteSpace(s.DriveEmail) ? "conectat" : s.DriveEmail;
                var when = s.DriveLastSyncUtc > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(s.DriveLastSyncUtc).ToLocalTime().ToString("dd.MM HH:mm")
                    : "niciodată";
                driveStatus.Text = $"Sincronizare Google Drive: {who} · ultima sincronizare: {when}";
            }
        }
        RefreshDriveUi();

        btnDriveConn.Click += (_, _) =>
        {
            if (DriveSync.IsConnected(s))
            {
                if (MessageBox.Show(this, "Deconectezi Google Drive? Bookmark-urile locale rămân intacte.", "Mizar",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                disconnectDrive();
                RefreshDriveUi();
                return;
            }
            if (string.IsNullOrWhiteSpace(s.DriveClientId))
            {
                using var apiDlg = new DriveApiDialog(s, openUrl);
                if (apiDlg.ShowDialog(this) != DialogResult.OK) return;
            }
            if (string.IsNullOrWhiteSpace(s.DriveClientId)) return;
            SaveFields();
            DialogResult = DialogResult.OK;
            Close();
            _ = connectDrive();
        };
        btnDriveSync.Click += async (_, _) =>
        {
            btnDriveSync.Enabled = false;
            driveStatus.Text = "Se sincronizează...";
            try { await syncDrive(); }
            finally { if (!IsDisposed) RefreshDriveUi(); }
        };
        btnDriveApi.Click += (_, _) =>
        {
            using var apiDlg = new DriveApiDialog(s, openUrl);
            apiDlg.ShowDialog(this);
        };

        col.Controls.Add(driveStatus);
        col.Controls.Add(Row(btnDriveConn, btnDriveSync, btnDriveApi));

        col.Controls.Add(Ui.Header("Actualizări"));
        var updStatus = new Label { AutoSize = true, Tag = "muted", Margin = new Padding(0, 4, 0, 2) };
        void RefreshUpdateUi()
        {
            var when = s.UpdateLastCheckUtc > 0
                ? DateTimeOffset.FromUnixTimeSeconds(s.UpdateLastCheckUtc).ToLocalTime().ToString("dd.MM.yyyy")
                : "niciodată";
            updStatus.Text = $"Versiunea {Updater.CurrentText} · ultima verificare: {when}";
        }
        RefreshUpdateUi();
        var btnUpd = Ui.Btn("Verifică acum");
        btnUpd.Click += async (_, _) =>
        {
            btnUpd.Enabled = false;
            updStatus.Text = "Se verifică...";
            try { await checkUpdates(); }
            finally { if (!IsDisposed) { btnUpd.Enabled = true; RefreshUpdateUi(); } }
        };
        col.Controls.Add(updStatus);
        col.Controls.Add(autoUpdate);
        col.Controls.Add(Row(btnUpd));

        var ok = Ui.Btn("Salvează");
        ok.DialogResult = DialogResult.OK;
        var cancel = Ui.Btn("Anulează");
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 18, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        col.Controls.Add(buttons);

        Controls.Add(col);
        AcceptButton = ok;
        CancelButton = cancel;
        Theme.ApplyToDialog(this);
    }

    void SaveFields()
    {
        var h = home.Text.Trim();
        s.HomePage = h.Length == 0 ? "https://www.google.com" : h;
        s.DownloadFolder = folder.Text;
        s.AskWhereToSave = ask.Checked;
        s.BlockAds = ads.Checked;
        s.BlockTrackers = trackers.Checked;
        s.BlockThirdPartyCookies = thirdParty.Checked;
        s.WebColorScheme = Schemes[Math.Max(0, scheme.SelectedIndex)].key;
        s.SavePasswords = pw.Checked;
        s.ShowBookmarksBar = bar.Checked;
        s.RestoreSession = restore.Checked;
        s.ClearHistoryOnExit = clearOnExit.Checked;
        s.AutoUpdate = autoUpdate.Checked;
        s.Save();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK) SaveFields();
        base.OnFormClosing(e);
    }
}

public class DriveApiDialog : Form
{
    public DriveApiDialog(Settings s, Action<string> openUrl)
    {
        Text = "Setări API Google Drive";
        Font = Ui.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var idBox = new TextBox { Width = 430, Text = s.DriveClientId ?? "" };
        var secBox = new TextBox { Width = 430, Text = s.DriveClientSecret ?? "" };

        var info = new Label
        {
            Text = "1. Creează un proiect în Google Cloud Console și activează Google Drive API.\n" +
                   "2. La Credentials → Create OAuth client ID → Desktop app.\n" +
                   "3. Lipește aici Client ID și Client Secret (rămân salvate doar local).",
            AutoSize = true, MaximumSize = new Size(440, 0), Tag = "muted", Margin = new Padding(0, 0, 0, 10)
        };

        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(18, 14, 18, 14) };
        p.Controls.Add(info);
        p.Controls.Add(Ui.Btn("Deschide Google Cloud Console", (_, _) =>
        {
            s.DriveClientId = idBox.Text.Trim();
            s.DriveClientSecret = secBox.Text.Trim();
            s.Save();
            openUrl("https://console.cloud.google.com/apis/credentials");
        }));
        p.Controls.Add(new Label { Text = "Client ID:", AutoSize = true, Margin = new Padding(0, 10, 0, 2) });
        p.Controls.Add(idBox);
        p.Controls.Add(new Label { Text = "Client Secret (dacă există):", AutoSize = true, Margin = new Padding(0, 8, 0, 2) });
        p.Controls.Add(secBox);

        var ok = Ui.Btn("Salvează");
        ok.DialogResult = DialogResult.OK;
        var cancel = Ui.Btn("Anulează");
        cancel.DialogResult = DialogResult.Cancel;
        var btns = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 14, 0, 0) };
        btns.Controls.Add(cancel);
        btns.Controls.Add(ok);
        p.Controls.Add(btns);

        ok.Click += (_, _) =>
        {
            s.DriveClientId = idBox.Text.Trim();
            s.DriveClientSecret = secBox.Text.Trim();
            s.Save();
        };

        Controls.Add(p);
        AcceptButton = ok;
        CancelButton = cancel;
        Theme.ApplyToDialog(this);
    }
}

public class BookmarkManagerForm : Form
{
    readonly BookmarkStore store;
    readonly Action<string> open;
    readonly TreeView tree = new() { Dock = DockStyle.Fill, HideSelection = false, AllowDrop = true, ShowNodeToolTips = true, FullRowSelect = true, ItemHeight = 24 };
    readonly ImageList images = new() { ColorDepth = ColorDepth.Depth32Bit };

    public BookmarkManagerForm(BookmarkStore store, Action<string> open)
    {
        this.store = store;
        this.open = open;
        Text = "Bookmark-uri";
        Font = Ui.Font;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Size = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(560));
        MinimizeBox = false;

        int sz = LogicalToDeviceUnits(16);
        images.ImageSize = new Size(sz, sz);
        images.Images.Add("folder", Icons.Folder(sz));
        images.Images.Add("page", Icons.Globe(sz));
        tree.ImageList = images;

        var side = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(8) };
        Button B(string t, Action a) { var b = Ui.Btn(t, (_, _) => a()); b.AutoSize = false; b.Width = LogicalToDeviceUnits(130); b.Height = LogicalToDeviceUnits(30); return b; }
        side.Controls.Add(B("Deschide", OpenSelected));
        side.Controls.Add(B("Folder nou", NewFolder));
        side.Controls.Add(B("Editează (F2)", EditSelected));
        side.Controls.Add(B("Șterge (Del)", DeleteSelected));
        side.Controls.Add(B("Mută sus", () => MoveSel(-1)));
        side.Controls.Add(B("Mută jos", () => MoveSel(1)));
        side.Controls.Add(new Label { Text = "Poți și trage cu\nmouse-ul elementele\nîn alt loc sau folder.", AutoSize = true, Tag = "muted", Margin = new Padding(0, 12, 0, 0) });

        tree.NodeMouseDoubleClick += (_, e) => { tree.SelectedNode = e.Node; if (e.Node?.Tag is Bookmark { IsFolder: false }) OpenSelected(); };
        tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) DeleteSelected();
            else if (e.KeyCode == Keys.F2) EditSelected();
            else if (e.KeyCode == Keys.Enter) OpenSelected();
        };
        tree.ItemDrag += (_, e) => { if (e.Item is TreeNode { Tag: Bookmark }) DoDragDrop(e.Item, DragDropEffects.Move); };
        tree.DragOver += (_, e) => e.Effect = DragDropEffects.Move;
        tree.DragDrop += OnDrop;

        Controls.Add(tree);
        Controls.Add(side);
        Rebuild();
        Theme.ApplyToDialog(this);
        Disposed += (_, _) => images.Dispose();
    }

    Image NodeImage(Bookmark b, out string key)
    {
        if (b.IsFolder) { key = "folder"; return images.Images["folder"]!; }
        var fav = OperaFavicons.For(b.Url);
        if (fav == null) { key = "page"; return images.Images["page"]!; }
        key = "u:" + b.Url;
        if (!images.Images.ContainsKey(key)) images.Images.Add(key, fav);
        return fav;
    }

    void Rebuild(Bookmark? select = null)
    {
        tree.BeginUpdate();
        tree.Nodes.Clear();
        var barRoot = new TreeNode("Bara de bookmark-uri") { Tag = store.Bar, ImageKey = "folder", SelectedImageKey = "folder" };
        var otherRoot = new TreeNode("Alte bookmark-uri") { Tag = store.Other, ImageKey = "folder", SelectedImageKey = "folder" };
        TreeNode? toSelect = null;

        void Add(TreeNodeCollection nodes, List<Bookmark> list)
        {
            foreach (var b in list)
            {
                NodeImage(b, out var key);
                var n = new TreeNode(b.DisplayName) { Tag = b, ImageKey = key, SelectedImageKey = key, ToolTipText = b.Url ?? "" };
                if (b.IsFolder) Add(n.Nodes, b.Children ??= new());
                nodes.Add(n);
                if (b == select) toSelect = n;
            }
        }

        Add(barRoot.Nodes, store.Bar);
        Add(otherRoot.Nodes, store.Other);
        tree.Nodes.Add(barRoot);
        tree.Nodes.Add(otherRoot);
        barRoot.Expand();
        otherRoot.Expand();
        if (toSelect != null) { toSelect.EnsureVisible(); tree.SelectedNode = toSelect; }
        tree.EndUpdate();
    }

    Bookmark? Selected => tree.SelectedNode?.Tag as Bookmark;

    List<Bookmark> TargetList()
    {
        var n = tree.SelectedNode;
        if (n == null) return store.Bar;
        if (n.Tag is List<Bookmark> l) return l;
        if (n.Tag is Bookmark { IsFolder: true } f) return f.Children ??= new();
        if (n.Tag is Bookmark b) return store.ParentOf(b) ?? store.Bar;
        return store.Bar;
    }

    void Changed(Bookmark? select)
    {
        store.NotifyChanged();
        Rebuild(select);
    }

    void OpenSelected()
    {
        if (Selected is { IsFolder: false } b) open(b.Url!);
    }

    void NewFolder()
    {
        using var d = new EditDialog("Folder nou", "Folder nou", null);
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var f = new Bookmark { Name = d.NameBox.Text.Trim(), Children = new() };
        TargetList().Add(f);
        Changed(f);
    }

    void EditSelected()
    {
        if (Selected is not { } b) return;
        using var d = new EditDialog(b.IsFolder ? "Editează folderul" : "Editează bookmark-ul", b.Name, b.Url);
        if (d.ShowDialog(this) != DialogResult.OK) return;
        b.Name = d.NameBox.Text.Trim();
        if (!b.IsFolder && d.UrlBox.Text.Trim().Length > 0) b.Url = MainForm.Normalize(d.UrlBox.Text);
        Changed(b);
    }

    void DeleteSelected()
    {
        if (Selected is not { } b) return;
        if (b.IsFolder && b.Children is { Count: > 0 } &&
            MessageBox.Show(this, $"Ștergi folderul „{b.DisplayName}” cu tot ce conține?", "Mizar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        store.Remove(b);
        Changed(null);
    }

    void MoveSel(int delta)
    {
        if (Selected is not { } b) return;
        var list = store.ParentOf(b);
        if (list == null) return;
        int i = list.IndexOf(b), j = i + delta;
        if (j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        Changed(b);
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(TreeNode)) is not TreeNode { Tag: Bookmark b }) return;
        var dst = tree.GetNodeAt(tree.PointToClient(new Point(e.X, e.Y)));

        if (dst?.Tag is Bookmark t)
        {
            if (t == b) return;
            if (b.IsFolder && BookmarkStore.IsDescendant(b, t)) return;
            store.Remove(b);
            if (t.IsFolder) (t.Children ??= new()).Add(b);
            else
            {
                var pl = store.ParentOf(t) ?? store.Bar;
                pl.Insert(pl.IndexOf(t), b);
            }
        }
        else
        {
            var target = dst?.Tag as List<Bookmark> ?? store.Bar;
            store.Remove(b);
            target.Add(b);
        }
        Changed(b);
    }
}

static class Icons
{
    public static Bitmap Folder(int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var b = new SolidBrush(Color.FromArgb(240, 180, 40));
        float k = s / 16f;
        g.FillRectangle(b, 1 * k, 3 * k, 6 * k, 3 * k);
        g.FillRectangle(b, 1 * k, 4.5f * k, 14 * k, 9 * k);
        return bmp;
    }

    public static Bitmap Globe(int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float k = s / 16f;
        using var pen = new Pen(Color.FromArgb(110, 115, 120), Math.Max(1, 1.2f * k));
        g.DrawEllipse(pen, 1.5f * k, 1.5f * k, 13 * k, 13 * k);
        g.DrawEllipse(pen, 5 * k, 1.5f * k, 6 * k, 13 * k);
        g.DrawLine(pen, 1.5f * k, 8 * k, 14.5f * k, 8 * k);
        return bmp;
    }
}

