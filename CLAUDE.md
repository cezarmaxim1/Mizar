# Mizar

> `AI.md` holds the same guide for other AI assistants (Gemini). When you change one, update the other.
> See [`ARCHITECTURE.md`](ARCHITECTURE.md) for the module ownership map and key symbols before reading `.cs` source files.

A minimal personal web browser for Windows, built on **WebView2** (the Edge/Chromium engine that ships with Windows 11) with **WinForms on .NET 10**. It moved off .NET 8 on 2026-11-14, because .NET 8 support ended on 2026-11-10. The owner speaks Romanian: all UI text is in Romanian (with diacritics), and replies to the owner should be in Romanian too.

## Product goals (in priority order)

1. **Low RAM use.** This is the most important goal. Don't add features that keep pages, processes or extensions alive in the background.
2. **Modern, minimalist UI** in Windows 11 style: rounded corners, flat surfaces, no classic WinForms look. Floating rounded tabs with right-edge title fade (no `...`), circular window buttons, and compact density (`36 px` tab strip, `38 px` nav bar).
3. **Only the essentials:**
   - tabs, address bar, back/forward/reload/home
   - bookmarks: a bar plus a manager, imported from Opera
   - downloads, saved to a folder chosen in settings
   - simple settings
   - opens on Google at startup
   - **a new tab (+, Ctrl+T) is an empty page** (`about:blank`, title "Tab nou", theme-coloured canvas, address bar focused). The owner wants it that way: don't load the home page there.

Explicitly **not** wanted: ads, AI features, sidebars, news, wallets, promotions, telemetry.

## Build and install

```bash
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -o "$LOCALAPPDATA/Programs/Mizar"
```

- **Before publishing,** close Mizar: `Stop-Process -Name Mizar`.
- **After publishing,** delete `*.xml`, `*.pdb` and `Microsoft.Web.WebView2.Wpf.dll` from the install folder. They aren't needed.
- **Default browser:** run `Mizar.exe --register` from the **installed** copy to register Mizar with Windows (HKCU only); `--unregister` undoes it. Run it **through Explorer** (see the MSIX gotcha); run straight from Claude's shell, it lands in a private registry Windows doesn't see. Re-registered through Explorer and verified on 2026-11-14. The registration stores the exe path, so never register the `bin\` build. Windows lets only the user pick the default: Settings → "Fă din Mizar browserul implicit..." registers and opens the right Windows page.
- **Shortcuts** live in `F:\Desktop\Mizar.lnk` and in the Start Menu (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Mizar.lnk`). Both point to `%LOCALAPPDATA%\Programs\Mizar\Mizar.exe`.
- **Icon:** run `python -I assets/make_icon.py` to build `Mizar.ico` (16 to 256 px), `assets/icon.png` and `assets/icon-preview.png` from `assets/icon-source.jpg`.
  - **The source:** a white split 4-point star plus a small gold star, supplied by the owner. It's a JPG with a fake checkerboard "transparency" baked in: 40 px squares, grey 56/105.
  - **How the script cleans it:** stars are separated by color. The drop shadow is re-rendered cleanly: blur, offset and opacity are fitted to the original, because the measured shadow is JPEG-noisy.
  - **After changing the icon,** check `icon-preview.png` on both the dark and the light backgrounds.

## Files

| File | What it holds |
|---|---|
| `MainForm.cs` | Main window: layout, custom title bar (`WndProc`: `WM_NCCALCSIZE` removes the caption, `WM_NCHITTEST` makes the empty tab-strip area draggable and the top edge resizable), tabs, navigation, bookmark bar, downloads, keyboard shortcuts, background-tab sleeping. |
| `TabStrip.cs` | `BrowserTab` model and the owner-drawn tab strip (floating rounded tabs with right-edge text fade, plus circular min/max/close buttons; the Close button's hit area and red hover fill extend to the right edge and top-right / bottom-right corners of the strip). Non-interactive areas return `HTTRANSPARENT` so the form can treat them as the caption. |
| `ModernUi.cs` | `Theme` palette (light and dark, follows Windows), `RoundButton`, `AddressBox` (pill-shaped address field), `ModernRenderer` (menus and bookmark bar), `Native` P/Invoke helpers. |
| `OperaFavicons.cs` | Reads icons from Opera's `Favicons` SQLite DB (copied to temp first, because Opera locks it) and stores them per bookmark URL (`u_<hash>.png`), falling back to per host. It runs once, then writes `opera-favicons.done`. |
| `Storage.cs` | `AppPaths`, `Settings` (JSON), `Bookmark`/`BookmarkStore` (JSON plus the Opera import), `FaviconCache`, `AdBlocker` domain list. |
| `DriveSync.cs` | Direct Google Drive REST API v3 sync for `mizar-bookmarks.json` (OAuth 2.0 + PKCE, `drive.file` scope, DPAPI-encrypted tokens, zero idle RAM). |
| `Dialogs.cs` | `EditDialog`, `SettingsForm`, `BookmarkManagerForm` (tree with drag & drop). |
| `History.cs` | `HistoryStore`: visits saved to `history.json` (max 5000, flushed every 10 s and on exit), plus the HTML of the history page. |
| `AppServices.cs` | `InternalPages` (built-in pages on `https://mizar.internal/`), `ErrorLog`, `DiagLog` (`diag.log`), `SingleInstance` (mutex + named pipe), `DefaultBrowser` (Windows registration). |
| `Suggest.cs` | `SuggestBox`: address-bar drop-down with matching bookmarks and history. |
| `Program.cs` | Startup: error handling, `--register`/`--unregister`, `--fixnet`/`--netdiag` (sent to the running window), single-instance hand-off. |
| `Updater.cs` | Self-update from GitHub Releases: monthly check, Settings "Verifică acum", ECDSA signature check, swap on exit, `--release-genkey`/`--release-sign` tools. |
| `release.ps1` | Publishes a release: bumps `<Version>`, builds, zips, signs, commits, tags, pushes, `gh release create`. |
| `NetRepair.cs` | `RepairConnection` (menu "Repară conexiunea": socket flush + service-worker reset + reload), `FlushSockets`, `ResetServiceWorkers`, `PageProbe`, slow-load logging, flush after the laptop wakes, `--netdiag` report. |

## Updates (GitHub Releases)

- **Repo:** `github.com/cezarmaxim1/Mizar` (public, so no token is needed to read releases).
- **When Mizar checks:** at most once every 30 days, one minute after start (`AutoUpdate`, `UpdateLastCheckUtc` in settings), or on demand from Settings → Actualizări → "Verifică acum". One request to `api.github.com`; nothing runs in the background between checks.
- **What a release holds:** `Mizar-win-x64.zip` (the publish folder without xml/pdb/Wpf dll) and `Mizar-win-x64.zip.sig`: an ECDSA P-256 signature of `"<version>\n<sha256 hex of zip>"`. The version is part of the signed data, so an old zip can't be re-tagged as new.
- **Signing key:** `%USERPROFILE%\.mizar-release\signing-key.pem` (private; folder ACL limited to the owner) plus `.pub`. The public key is built into `Updater.PublicKey`. **Never commit the key** (`*.pem` is gitignored). If it's lost, installed copies can't accept new releases until Mizar is reinstalled by hand with a new key, so the owner should keep a backup.
- **Applying:** a verified update is unpacked to `%LOCALAPPDATA%\Mizar\update\staged`. When Mizar closes, `Program` swaps it in: each running file is renamed to `*.old` (Windows allows that), the new one is copied, and everything is rolled back on error. If the user chose "Repornesc acum", Mizar relaunches. `CleanupOld` deletes the `*.old` files at the next start. Development builds (`\bin\`) never update.
- **Publishing:** commit first, then `powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.0.1`. Needs `gh` logged in.
- **Test:** `MIZAR_UPDATE_URL` points the check at a fake of the GitHub API (a local `http.server` with a `latest.json`). `--selftest <url> <out> --updatetest` checks and stages; the swap runs when the test exits. Verified on 2026-11-14 on a copy in %TEMP%: a tampered zip was rejected, and the signed 9.9.9 was installed over 1.0.0. It then started, removed its `*.old` files and reported "Ai ultima versiune".

## Built-in pages

- **Where they live:** `https://mizar.internal/history` and `/crashed` are served locally from `WebResourceRequested` (`InternalPages.Serve`) and never reach the network.
- **Why a real address:** they are real navigations, so Back/Forward/Reload work and there is no 2 MB limit. `NavigateToString` broke both, so don't go back to it.
- **Security:** pages are sent with `X-Frame-Options: DENY` and `frame-ancestors 'none'`. History commands (`postMessage` delete/clear) are accepted only when `e.Source` is the history page.
- **`BrowserTab.Internal`** is computed from the URL. Use `InternalPages.IsInternal(url)` to exclude built-in pages from history, zoom and the session.

## Reliability

- **Single instance per data folder:** a second launch sends its URL through a named pipe and exits. The running window opens it in a new tab (`OpenFromOtherInstance`). Self-tests skip this on purpose.
- **Unexpected exceptions** go to `errors.log` (rotated at 512 KB) and the window stays open. `--throw-test` injects one to prove it.
- **`diag.log`** (`DiagLog.Write`, in the data folder, rotated at 256 KB): one line per noteworthy event, so a problem can be looked up afterwards. Hosts only, never paths, titles or page content. It records: `start v<version>`; `slow load <host>` (active tab still loading after 20 s, once per navigation, with a page probe: images pending/broken, resource count, service-worker state); `nav failed <host> <WebErrorStatus>`; `process failed <kind>`; `sleep <host>` (tab suspended); `power resume`; `repair <host>` (page probe before, flush and worker-reset results, and the probe again 10 s later); `heal <host>` (automatic worker reset, see known issue 0); `netdiag` summaries. **Privacy note:** it does keep host names of problem pages and slept tabs even when history is wiped on close. To read the owner's copy, copy it out through Explorer (MSIX gotcha).
- **Session:** open tabs are saved to `session.json` every 5 s (when changed) and on close.
  - At startup they come back as **lazy** tabs (`PendingUrl`): only a title and an icon, and the page starts when the tab is first shown. Verified: 3 restored tabs, 1 page process.
  - `RestoreSession` controls it and is **off by default** (owner's choice, 2026-11-14): Mizar starts with a single home-page tab. When off, `SaveSession` deletes `session.json` instead of writing it.
- **History wiped on close:** `ClearHistoryOnExit` (on by default, checkbox in Settings → Confidențialitate). `FormClosing` calls `history.Clear()`; at startup `ClearData(BrowsingHistory | DownloadHistory)` runs too, for the engine's own history and in case the last run didn't close cleanly. Verified on 2026-11-14 with a WM_CLOSE test: `history.json` was `[]` and no `session.json` was left. (`Process.CloseMainWindow` does **not** close Mizar; post `WM_CLOSE` to the `WindowsForms10.Window` top-level window instead.)
- **Crash loop guard:** a renderer crash reloads the page, but after 3 crashes within a minute the tab goes to `/crashed`, with a manual retry link.
- **Bookmark safety:**
  - Every save is atomic (`.tmp` + move). The previous version goes to `bookmarks.bak`, and the first save each day goes to `backups/bookmarks-YYYY-MM-DD.json` (10 kept).
  - A damaged `bookmarks.json` is set aside as `bookmarks.damaged-*.json`, and the newest good copy is restored.
- **Favicons:** use `BrowserTab.SetFavicon(img, owned)`. Owned images are disposed when replaced and when the tab closes; shared cache images are never disposed.

## User data

Everything lives in `%LOCALAPPDATA%\Mizar\`:

| Path | Contents |
|---|---|
| `settings.json` | Settings |
| `bookmarks.json`, `bookmarks.bak`, `backups/` | Bookmarks, previous version, daily copies |
| `history.json` | History |
| `session.json` | Open tabs |
| `errors.log` | Unexpected errors |
| `diag.log` | Event log for diagnosing problems (hosts only) |
| `favicons/` | Site icons: `<host>.png`, and `u_<hash>.png` per bookmark URL |
| `WebData/` | WebView2 profile: cookies, saved passwords (encrypted by Edge/DPAPI), cache |

## How the RAM features work

- **One shared `CoreWebView2Environment`.** All tabs share a single browser process.
- **Hidden tabs** get `MemoryUsageTargetLevel.Low`.
- **Sleeping tabs:** tabs left in the background longer than `SleepAfter` (5 minutes) are put to sleep with `TrySuspendAsync`, unless they are playing audio. They resume automatically when shown.
  - **Never mid-request.** Background tabs turn on CDP `Network` events (`SetNetWatch`, off again when shown) and keep `InFlight` request ids; a tab with any request in flight isn't suspended. Reason: all tabs share one HTTP/2 connection per server group, and a frozen tab that stops reading a response may stall it for everyone (suspected cause of the Gmail stalls, see open issue 0; the owner's running-tracker tab syncs with Google Sheets in the background). Verified with `--selftest https://example.com/ <out> --sleepguard`: busy → not suspended, idle → suspended.
  - The CDP event receivers are kept in `BrowserTab.Receivers`; unreferenced ones stopped delivering events.
- **Ad and tracker blocking** use per-domain `AddWebResourceRequestedFilter` patterns (`AdBlocker.FilterPatterns()`), so only matching requests cross into .NET. Never add a catch-all `*` filter, and keep the source kind `Document` (not `All`): see known issue 0.
  - Each domain gets one pattern, `*://*domain/*`, which also lets look-alikes through; `Classify` rejects those by exact host.
  - About 150 ad domains and 60 tracker domains. YouTube ads can't be blocked by domain, because they come from the same servers as the videos.
- **Restored tabs are lazy** (see Reliability). Icon fonts are cached in `Theme.DrawGlyph`, and the folder icon is created once.

## Privacy

- **Blocking:** `AdBlocker.Classify()` labels each request as Ad or Tracker. Two settings control them: `BlockAds` and `BlockTrackers`.
- **Facebook Pixel** is blocked by path only (`connect.facebook.net/.../fbevents.js`, `facebook.com/tr`). The rest of facebook.com, "Login with Facebook" and embeds keep working.
- **Edge's own tracking prevention is off** (`EnableTrackingPrevention = false`): Mizar has its own blocker. See known issue 0 before turning it back on.
- **Third-party cookies** (`BlockThirdPartyCookies`, on by default) (currently **off** on the owner's PC as an experiment, see known issue 0) are blocked with the engine switch `--test-third-party-cookie-phaseout`, passed through `CoreWebView2EnvironmentOptions`. The setting applies only after a restart.
  - DevTools `Network.setCookieControls` returns ok but has **no effect** in WebView2. Don't use it.
  - Verified on 2026-11-14: whatismybrowser.com/detect/are-third-party-cookies-enabled reports "No".
- **Security indicator:** `AddressBox.Security` shows the state of the active tab, set by `MainForm.UpdateSecurity()`:
  - padlock for HTTPS;
  - red "Nesigur" chip for HTTP;
  - "Certificat invalid" when `ServerCertificateErrorDetected` fired (the engine still blocks the page itself);
  - clock icon for internal pages.

  The tooltip explains each state. Verified on 2026-11-14 with example.com, neverssl.com and expired.badssl.com.
- **HTTPS-First (`--enable-features=HttpsUpgrades`):** passed in `EngineOptions()`. Automatically upgrades `http://` navigations to `https://` and falls back to `http://` when a site does not support HTTPS. Verified on 2026-11-14: `http://example.com` upgrades to `https://example.com/`, while `http://neverssl.com` falls back and loads over HTTP.
- **Host objects disabled (`AreHostObjectsAllowed = false`):** set in `MainForm.Wire` so web pages cannot access `chrome.webview.hostObjects`. Permissions (`PermissionRequested`) are left on WebView2's default so the engine prompts the user (Allow / Block) when a site requests notifications, location, camera or microphone.
- **Dependency hygiene:** run `dotnet list package --include-transitive --vulnerable` after any package change. `Microsoft.Data.Sqlite` 8.0.x pulled in a vulnerable `SQLitePCLRaw.lib.e_sqlite3` 2.1.6, so it is now pinned to 10.0.15.
- **Self-test:** `Mizar.exe --selftest <url> <outfile> [--keys|--zoom|--histback|--crash|--suggest [--query <text>]|--newtab] [--scheme light|dark|auto]` loads the page, waits about 8 seconds, runs the chosen check, writes the blocked counters and the page text plus `<outfile>.page.png` (a screenshot of the page), then exits.
  - **Always set `MIZAR_DATA_DIR`** to a temp folder for tests. It isolates settings, bookmarks, history, session **and** the WebView2 profile, and lets a test run while the owner's Mizar is open.
  - In self-test mode Mizar also doesn't save settings or session. Without `MIZAR_DATA_DIR` it doesn't write history either. Earlier test runs overwrote the owner's `settings.json` and history.
  - Verified on 2026-11-14:
    - `--histback`: history page → link → Back shows the list again, and delete works.
    - `--crash`: after 3 `Page.crash` the tab lands on `/crashed`.
    - `--suggest`: typing "wiki" shows matches, prefix matches first. It also saves `<outfile>.suggest.png` and times the search: about 1 ms per keystroke with 5000 history entries.
    - `--newtab`: opens a tab the way Ctrl+T does, waits 15 s, then reports and captures it. A new tab is `about:blank` on purpose (owner's choice).
    - `--netlog`: reloads the page with CDP `Network` events on and lists failed requests as host + resource type + reason only, never paths or page content. Add `--notext` to skip the page text.
    - `--sleeptest`: puts the tab in the background, suspends it with `TrySuspendAsync`, waits (`MIZAR_SLEEP_SECONDS`, default 15), brings it back, and counts broken or pending images and does a same-origin sync XHR. Verified on 2026-11-14 on a public Google page and on the owner's Gmail (60 s): nothing broken after waking.
    - **`--noshot`:** skips every screenshot. **Mandatory** whenever a test touches the owner's real profile (mail, accounts), so private content is never captured.
  - **To test against the owner's real, logged-in profile,** launch the self-test through Explorer (see the MSIX gotcha), write the output under `F:\` (not AppData), and use `--notext --noshot`. Delete the output afterwards.
  - **Captures of hidden tabs never finish** (no frame is rendered), so every capture in the self-test has a 5 s timeout. Without it the test instance never closed.
  - **Window screenshots from outside:** call `SetProcessDPIAware` first, then `PrintWindow(h, hdc, 2)`. Without DPI awareness the capture is cropped.

Baseline measured on 2026-11-13, with one Google tab: about 400 MB of WebView2 processes plus about 65 MB for `Mizar.exe`, versus about 2.4 GB for Opera.

## Zoom per site

- **Storage:** `Settings.SiteZoom` maps a host (without "www.") to a zoom factor. Sites at 100% aren't stored.
- **Applying it:** `ApplySiteZoom` runs on `SourceChanged` and applies the saved zoom, or 100%, whenever the tab moves to a different host.
- **Recording it:** `OnZoomChanged` saves the zoom when the user changes it, and other open tabs on the same site follow.
- **`ZoomFactorChanged` is not reliable.** It never fired in tests, neither for code-set nor for synthetic zoom. So:
  - Mizar's own controls call `SetUserZoom`;
  - `zoomPoll` (1 s, active tab only) catches anything else (Ctrl+wheel, Ctrl+/-/0 handled by the engine);
  - `CheckZoom` also runs on tab switch and on `NavigationStarting`.
- **`ExpectedZoom`** tells a user change apart from one Mizar made itself.
- **Indicator:** `zoomChip` (a `TextChip`) next to the star shows "125%" when zoom isn't 100%. Clicking it opens bigger / smaller / back to 100%. Zoom steps follow Chrome's levels (`ZoomLevels`).
- **Test:** `--selftest <url> <out> --zoom`. Verified on 2026-11-14 on reddit.com: saved at 125%, other site at 100%, back on reddit at 125%.

## Two monitors and scaling (DPI)

The owner uses two monitors with **different scaling**: the primary 2560x1600 at 150% (DPI 144) and a 1920x1200 one above it at 100% (DPI 96). The app is `PerMonitorV2`.

- **Sizes and fonts follow the monitor the window is on.** `MainForm.ApplyDpi()` runs on `OnDpiChanged` (through `BeginInvoke`, after WinForms' own rescaling) and at `Shown`; it re-applies every `S(...)` size, the fonts, the bookmark bar and the menu, and sets `Theme.Dpi`. Before 2026-11-16 the sizes were fixed at the scaling Mizar started with, so on the other monitor the bars were 54 px instead of 36 (or the reverse).
- **Never write `new Font("Segoe UI", 9f)`.** A point-sized font is sized for the primary monitor's scaling and stays that size everywhere. Use `Theme.UiFont(pt, DeviceDpi)` (pixel-sized, cached); icon glyphs go through `Theme.DrawGlyph`, which uses `Theme.Dpi`. When adding a sized control or a font, add it to `ApplyDpi()` too.
- **Client area of a maximized window:** see known issue 3 (`RecalcFrame`, `EnsureClientOnScreen`).
- **Tests** (both need the two monitors connected):
  - `--selftest <url> <out> --monitortest`: on each monitor, expected vs actual bar heights and font heights; then the forced "content on the wrong monitor" state and its repair.
  - `powershell -ExecutionPolicy Bypass -File tests\two-monitors.ps1 -Exe <Mizar.exe> -Tag x`: 12 scenarios on an isolated copy (start maximized on the second monitor, restore, maximize, minimize, move a maximized window to the other monitor and back, F11 on each, normal window on the second and across both). Each checks that the client area is where it should be and that the empty tab strip still hit-tests as caption. Verified on 2026-11-16: 12 of 12, and screenshots on both monitors from both start positions.

## Dark mode

- **Detection:** `Theme.Dark` reads `HKCU\...\Themes\Personalize\AppsUseLightTheme`.
- **Live switching:** `SystemEvents.UserPreferenceChanged` triggers `MainForm.ApplyTheme()`, so the theme changes without a restart.
- **No hard-coded colors.** Always use the `Theme.*` properties.
- **Dialogs** call `Theme.ApplyToDialog(this)` at the end of their constructor. It also sets the dark title bar (DWM attribute 20) and dark scrollbars on the TreeView.
- **Web pages:** the `WebColorScheme` setting ("auto" by default, "light" or "dark") is applied to the profile's `PreferredColorScheme`. It changes only CSS `prefers-color-scheme` and applies live.
- **Page canvas must be white once content loads.** A new tab starts with the theme color as `DefaultBackgroundColor`, to avoid a white flash, and `ContentLoading` switches it to white. Many sites never set a background and rely on default white: altex.ro looked "half dark" on the dark canvas. `ApplyTheme` must **not** touch `DefaultBackgroundColor` of loaded tabs.

## Address-bar suggestions

- **Sources:** `SuggestBox.Find` searches bookmarks, then history from newest to oldest, at most 6 rows.
- **Matching:**
  - 1–2 letters match only the start of the site name or of a word in the title. "re" → reddit, not facebook.com/?ref=…
  - 3 or more letters match anywhere.
- **Filtered out:**
  - history entries with a query string longer than 80 characters (login and redirect links);
  - titles starting with "Se redirec…" or "Redirect…";
  - titles that are really addresses.
- **One row per host + path**, so reddit.com and reddit.com/?captcha=1 show once.
- **Text measurement:** measure without `EndEllipsis`. With it and no size limit, the width came back as just "r…".
- **Fonts:** title 11 pt, address 10 pt, rows 42 px (logical). The owner found the earlier 9.75/9 pt too small.

## Known issues & fixes (2026-11-14 / 2026-11-15)

0. **Gmail stuck: no icons, no background image, endless loading, "Not connected".** Seen on 2026-11-14 and again on 2026-11-15. **Cause found on 2026-11-15: Gmail's service worker was wedged.** Every image/font/script request of the page (gstatic.com, mail.google.com) stayed `Pending`, yet the engine's NetLog (`edge://net-export`) showed that every request which reached the network finished normally, and the same URLs downloaded fine with curl over IPv4 and IPv6. The requests were stuck inside the worker, before the network.
   - **What did NOT help:** F5, a socket flush, closing the other Google tabs, clearing the cache, a new Gmail tab. **What fixed it:** `edge://serviceworker-internals` → Unregister for `mail.google.com`, then F5.
   - **The 2026-11-14 theory was wrong:** "one dead HTTP/2 connection to Google" fitted the symptoms, but the flush never fixed anything. `FlushSockets()` stays (menu and after wake-up) because it is harmless there.
   - **Fix 1, `RepairConnection()`** (menu "Repară conexiunea", `Mizar.exe --fixnet`): flushes the sockets **and** resets the service workers of the active page's origin (`ResetServiceWorkers`: CDP `ServiceWorker.stopAllWorkers` + `Storage.clearDataForOrigin` with `service_workers,cache_storage`), then reloads. Cookies and logins are untouched. Verified with `--selftest http://localhost:<port>/ <out> --repairtest` on a local page with a service worker: registrations 1 → 0.
   - **Fix 2, ad-filter scope:** the `AddWebResourceRequestedFilter` patterns use `CoreWebView2WebResourceRequestSourceKinds.Document`, not `All`. With `All`, the engine routed service-worker requests through Mizar's window too. **Unproven** that this caused the wedge; it is the one thing Mizar did to service workers that other browsers don't. Trade-off, measured on a local test page: a worker that only handles its own site's requests (the usual case) still has its page's ads blocked (2 of 2); a worker that re-fetches **everything** itself lets those ads through (1 of 2).
   - **Other suspect (unproven):** `TrySuspendAsync` freezing a tab that shares the worker (the Google Sheets tab). Tabs aren't suspended while HTTP requests are in flight (`InFlight` ignores `WebSocket`/`EventSource`/non-HTTP and expires entries after 60 s).
   - **Recurred on 2026-11-15 17:05, two minutes after start, with fix 2 installed and no tab asleep** (`diag.log`: `imgPending=99 ... sw=activated ... asleep=0`). So neither the worker filter nor tab sleeping is the cause, and it went away by itself about a minute later. Re-reading the morning NetLog: during the stuck reload the network stack saw **44 requests, all to `mail.google.com`, and none to any cross-site host** (gstatic.com, googleusercontent.com). Cross-site requests are held before the network; same-site ones pass. A `www.google.com` iframe on reddit.com was pending too.
   - **Current experiment (from 2026-11-15 17:10), two changes at once by the owner's choice:** (a) Edge's tracking prevention is off (`EnableTrackingPrevention = false` in `EngineOptions()`; the engine then runs with `--disable-features=msEnhancedTrackingPreventionEnabled`); (b) the owner's `BlockThirdPartyCookies` is off, so `--test-third-party-cookie-phaseout` is no longer passed. Both gate cross-site requests. `diag.log` records `engine options: 3pcBlock=... trackingPrevention=off` at every start, and the page probe now splits pending images into `pendingSameSite` / `pendingCrossSite`. **If Gmail stays fine for several days,** turn the cookie setting back on to learn which of the two it was. **If it stalls again,** neither is the cause.
   - **Recurred on 2026-11-16 17:18 with the experiment in place** (3pc blocking off, tracking prevention off): `imgPending=132 of 132, pendingSameSite=75, pendingCrossSite=57, sw=activated`. So it is **not** cross-site gating either: same-site images were stuck too. Both experiment changes stay as they are for now (harmless); the owner can turn cookie blocking back on.
   - **Fix 3, automatic worker heal (2026-11-16):** when the active tab is still loading after 20 s, the page probe runs; if a service worker controls the page and at least 80% of 5+ images are still pending (`WedgedWorker`), `HealWedgedWorker` resets that site's service worker and reloads **that page only**, at most once per 5 minutes per site, logged as `heal <host>` in `diag.log`. No socket flush, so downloads and other tabs are untouched (the owner's 2026-11-15 objection was about the flush). Gmail may show its own "Reload site?" prompt first. Verified with `--selftest http://localhost:<port>/ <out> --healtest` against a page whose worker never answers image requests: 12/12 pending → healed → 0 pending, worker gone.
   - **Root cause still unknown.** Why Gmail's worker wedges only in Mizar is open; the heal makes it recover by itself.
   - **No socket flush on slow loads** (it could cut a download): owner's decision, 2026-11-15.
   - **If it recurs:** read `diag.log` first. Then run `Mizar.exe --netdiag F:\netdiag.txt` **through Explorer** (a `.lnk` with arguments works; a `.cmd` did not) while it's stuck: three phases, each a reload plus 15 s of counting pending/failed requests by host + type: as is, after a socket flush, after a service-worker reset. If the worker reset fixes it again although fix 2 is in, the filter wasn't the cause: look at tab sleeping next.

1. **Hand-off to the running window (fixed 2026-11-15):** URLs arriving on the pipe before `sessionReady` are queued in `pendingPipeUrls` via `EnqueueFromOtherInstance` and drained after the initial tab is created/restored. `OpenFromOtherInstance` restores minimized windows via `lastWindowState` so maximized windows stay maximized.
2. **`SaveWindow` while minimized (fixed 2026-11-15):** `lastWindowState` tracks the last non-minimized `WindowState`, so closing while minimized preserves `settings.Maximized`.

3. **Window ignores every click after the displays change (fixed 2026-11-16).** The owner has two monitors (primary 2560x1600, a 2880x1800 one above it). After a Wi-Fi drop and some idle time, the maximized window was on the primary monitor but its whole client area sat at the other monitor's coordinates (`GetWindowRect` -11,-11; `ClientToScreen(0,0)` 611,-1800). The process answered messages (`Responding = True`), pages kept loading, but every click landed on an empty non-client frame, so the app looked frozen. Cause: `WM_NCCALCSIZE` pins the client area of a maximized window to a monitor's work area, and it was last computed while the display layout was in flux; nothing recomputed it afterwards.
   - **Fix:** the monitor is now taken from the proposed window rectangle (`Native.WorkArea(RECT)`, `MonitorFromRect`); `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`/`SPI_SETWORKAREA` and power resume call `RecalcFrame()` (`SetWindowPos` with `SWP_FRAMECHANGED`); and `EnsureClientOnScreen()` checks on every `WM_ACTIVATE` that the client origin lies inside the window, logs `client area off-window` to `diag.log` and repairs it. So the worst case is one lost click.
   - **Test:** `--selftest <url> <out> --monitortest` (needs two monitors) forces the wrong work area through `testWrongWorkArea`, then checks detection and repair. Verified on 2026-11-16: broken at 407,-1200, healed at 0,0. A plain `SetWindowPos` move between monitors did **not** reproduce the bug, on the old build either.
   - **From outside,** the same repair is `SetWindowPos(hwnd, 0,0,0,0,0, SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_FRAMECHANGED)`; that is how the owner's stuck window was revived.

## Gotchas

- **The Claude desktop app is an MSIX package, and Windows redirects what its child processes write to AppData and HKCU.** Anything launched straight from Claude's shell is redirected: PowerShell, `Start-Process Mizar.exe`, `Mizar.exe --register`.
  - **Where writes go:** new files and folders land in `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\...`, not in the real `%LOCALAPPDATA%`. Registry writes also land in a private hive.
  - **What Claude's shell sees:** a merged view, where the redirected copy hides the real file. Found on 2026-11-14: Mizar instances started by Claude used a **separate** data folder (`...\LocalCache\Local\Mizar`), while the owner's instance, started from Explorer, used the real `%LOCALAPPDATA%\Mizar`. Claude kept reading the stale copy and thought settings weren't being saved.
  - **To start Mizar or run its commands for the owner,** go through Explorer, which runs the target outside the package. For commands with arguments, write a small `.cmd` and run `Start-Process explorer.exe -ArgumentList "\"path\to\file.cmd\""`.
  - **To read the owner's real data,** do the same: have the `.cmd` copy the file somewhere Claude has no redirected copy of.
  - **Not affected:** publishing to `%LOCALAPPDATA%\Programs\Mizar` reached the real folder (it already existed), and so did the shortcuts.
  - **Unaffected by design:** self-tests with `MIZAR_DATA_DIR` are redirected too, but consistently, so they still work.
  - **Leftover:** the redirected copy (`...\LocalCache\Local\Mizar`) still holds the data from sessions Claude started (cookies, logins, history). It is separate from the owner's real profile.

- **WebView2 swallows some shortcuts** (Ctrl+H, for example) when the page has focus, so `ProcessCmdKey` never sees them. `ShortcutScript` is injected into every page: it catches Ctrl+H/T/W/D/L/J, Ctrl+Shift+T/B/O, Ctrl+Tab and Ctrl+1..9, and sends `{mizarKey}` to `RunShortcut`. To test this, use `--selftest <url> <out> --keys`, which presses Ctrl+H through CDP `Input.dispatchKeyEvent`. **Never** use SendKeys from outside the app: the keys can land in another window.
- **Title-bar hit testing goes through three levels.** `TabStrip` returns `HTTRANSPARENT` for empty areas. Then the root `HitTestPanel` must **also** return `HTTRANSPARENT` (`PassThrough`); otherwise the form never sees the point, and dragging and top-edge resizing stop working. Finally the form returns `HTCAPTION`/`HTTOP`. As a fallback, `TabStrip.EmptyAreaPressed` sends `WM_NCLBUTTONDOWN` itself. Verified by sending `WM_NCHITTEST` to a test instance: empty strip gives 2, top edge 12, tab 1.
- **Maximized window:** in `WM_NCCALCSIZE`, when the window is maximized (`IsZoomed`), the client area is set exactly to the monitor's work area (`Native.WorkArea`). The monitor comes from the proposed window rectangle, and the result is re-checked after display changes (see known issue 3). Using the frame size instead clipped pixels at the top.

- **The root `TableLayoutPanel` needs a 100% `ColumnStyle`.** Without one, the column grows when the window is maximized and never shrinks back, which pushes the window buttons off-screen.

- **Windows PowerShell 5.1 corrupts UTF-8 files.** `Get-Content`/`Set-Content` without `-Encoding` read UTF-8 without a BOM as ANSI, which breaks the Romanian diacritics and the Segoe Fluent icon glyphs. Edit files with the Edit tool, or use `[IO.File]::ReadAllText(..., UTF8)`.
- **Use Unicode escapes for icon glyphs** (for example `"\uE72B"`) so source files stay ASCII-safe.
- **Icon font:** Segoe Fluent Icons, falling back to Segoe MDL2 Assets. The code points are the same in both.
- **Google sign-in** works through the normal web page. If Google ever blocks it as an "embedded browser", the fix is to adjust the user agent.
- **Opera import** reads `%APPDATA%\Opera Software\Opera Stable\Default\Bookmarks` (Chromium JSON). Many Opera bookmarks have no name; those show icon-only on the bar, like in Opera. Passwords and cookies can't be imported because Opera encrypts them with its own key, so the user signs in once.
