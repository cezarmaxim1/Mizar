# Mizar

> `AI.md` holds the same guide for other AI assistants (Gemini). When you change one, update the other.

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
- **Default browser:** run `Mizar.exe --register` from the **installed** copy to register Mizar with Windows (HKCU only); `--unregister` undoes it. The registration stores the exe path, so never register the `bin\` build. Windows lets only the user pick the default: Settings → "Fă din Mizar browserul implicit..." registers and opens the right Windows page.
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
| `AppServices.cs` | `InternalPages` (built-in pages on `https://mizar.internal/`), `ErrorLog`, `SingleInstance` (mutex + named pipe), `DefaultBrowser` (Windows registration). |
| `Suggest.cs` | `SuggestBox`: address-bar drop-down with matching bookmarks and history. |
| `Program.cs` | Startup: error handling, `--register`/`--unregister`, single-instance hand-off. |

## Built-in pages

- **Where they live:** `https://mizar.internal/history` and `/crashed` are served locally from `WebResourceRequested` (`InternalPages.Serve`) and never reach the network.
- **Why a real address:** they are real navigations, so Back/Forward/Reload work and there is no 2 MB limit. `NavigateToString` broke both, so don't go back to it.
- **Security:** pages are sent with `X-Frame-Options: DENY` and `frame-ancestors 'none'`. History commands (`postMessage` delete/clear) are accepted only when `e.Source` is the history page.
- **`BrowserTab.Internal`** is computed from the URL. Use `InternalPages.IsInternal(url)` to exclude built-in pages from history, zoom and the session.

## Reliability

- **Single instance per data folder:** a second launch sends its URL through a named pipe and exits. The running window opens it in a new tab (`OpenFromOtherInstance`). Self-tests skip this on purpose.
- **Unexpected exceptions** go to `errors.log` (rotated at 512 KB) and the window stays open. `--throw-test` injects one to prove it.
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
| `favicons/` | Site icons: `<host>.png`, and `u_<hash>.png` per bookmark URL |
| `WebData/` | WebView2 profile: cookies, saved passwords (encrypted by Edge/DPAPI), cache |

## How the RAM features work

- **One shared `CoreWebView2Environment`.** All tabs share a single browser process.
- **Hidden tabs** get `MemoryUsageTargetLevel.Low`.
- **Sleeping tabs:** tabs left in the background longer than `SleepAfter` (5 minutes) are put to sleep with `TrySuspendAsync`, unless they are playing audio. They resume automatically when shown.
- **Ad and tracker blocking** use per-domain `AddWebResourceRequestedFilter` patterns (`AdBlocker.FilterPatterns()`), so only matching requests cross into .NET. Never add a catch-all `*` filter.
  - Each domain gets one pattern, `*://*domain/*`, which also lets look-alikes through; `Classify` rejects those by exact host.
  - About 150 ad domains and 60 tracker domains. YouTube ads can't be blocked by domain, because they come from the same servers as the videos.
- **Restored tabs are lazy** (see Reliability). Icon fonts are cached in `Theme.DrawGlyph`, and the folder icon is created once.

## Privacy

- **Blocking:** `AdBlocker.Classify()` labels each request as Ad or Tracker. Two settings control them: `BlockAds` and `BlockTrackers`.
- **Facebook Pixel** is blocked by path only (`connect.facebook.net/.../fbevents.js`, `facebook.com/tr`). The rest of facebook.com, "Login with Facebook" and embeds keep working.
- **Third-party cookies** (`BlockThirdPartyCookies`, on by default) are blocked with the engine switch `--test-third-party-cookie-phaseout`, passed through `CoreWebView2EnvironmentOptions`. The setting applies only after a restart.
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
    - `--newtab`: opens a tab the way Ctrl+T does, waits 15 s, then reports and captures it.
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

## Known open issues (2026-11-14)

1. **Hand-off to the running window failed once.** In a test that started from a restored session (copy of the owner's data), a second launch with a URL exited normally, but no new tab appeared in the session and the window ended up minimized. An earlier hand-off test without a restored session worked. Investigate `OpenFromOtherInstance` and the pipe listener; `errors.log` stayed empty.
2. **`SaveWindow` while minimized saves `Maximized = false`,** so the next start isn't maximized. Fix: skip the window part of the save when `WindowState == Minimized`, or keep the last known state.

## Gotchas

- **WebView2 swallows some shortcuts** (Ctrl+H, for example) when the page has focus, so `ProcessCmdKey` never sees them. `ShortcutScript` is injected into every page: it catches Ctrl+H/T/W/D/L/J, Ctrl+Shift+T/B/O, Ctrl+Tab and Ctrl+1..9, and sends `{mizarKey}` to `RunShortcut`. To test this, use `--selftest <url> <out> --keys`, which presses Ctrl+H through CDP `Input.dispatchKeyEvent`. **Never** use SendKeys from outside the app: the keys can land in another window.
- **Title-bar hit testing goes through three levels.** `TabStrip` returns `HTTRANSPARENT` for empty areas. Then the root `HitTestPanel` must **also** return `HTTRANSPARENT` (`PassThrough`); otherwise the form never sees the point, and dragging and top-edge resizing stop working. Finally the form returns `HTCAPTION`/`HTTOP`. As a fallback, `TabStrip.EmptyAreaPressed` sends `WM_NCLBUTTONDOWN` itself. Verified by sending `WM_NCHITTEST` to a test instance: empty strip gives 2, top edge 12, tab 1.
- **Maximized window:** in `WM_NCCALCSIZE`, when the window is maximized (`IsZoomed`), the client area is set exactly to the monitor's work area (`Native.WorkArea`). Using the frame size instead clipped pixels at the top.

- **The root `TableLayoutPanel` needs a 100% `ColumnStyle`.** Without one, the column grows when the window is maximized and never shrinks back, which pushes the window buttons off-screen.

- **Windows PowerShell 5.1 corrupts UTF-8 files.** `Get-Content`/`Set-Content` without `-Encoding` read UTF-8 without a BOM as ANSI, which breaks the Romanian diacritics and the Segoe Fluent icon glyphs. Edit files with the Edit tool, or use `[IO.File]::ReadAllText(..., UTF8)`.
- **Use Unicode escapes for icon glyphs** (for example `"\uE72B"`) so source files stay ASCII-safe.
- **Icon font:** Segoe Fluent Icons, falling back to Segoe MDL2 Assets. The code points are the same in both.
- **Google sign-in** works through the normal web page. If Google ever blocks it as an "embedded browser", the fix is to adjust the user agent.
- **Opera import** reads `%APPDATA%\Opera Software\Opera Stable\Default\Bookmarks` (Chromium JSON). Many Opera bookmarks have no name; those show icon-only on the bar, like in Opera. Passwords and cookies can't be imported because Opera encrypts them with its own key, so the user signs in once.
