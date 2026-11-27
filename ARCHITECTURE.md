# Mizar architecture

Rules, commands and traps are in `AGENTS.md`. Open problems are in `STATUS.md`.

## Process model

- **One window, one `MainForm`.** It is a partial class split across three files:
  - `MainForm.cs`: window, tabs, navigation, features, self-test runner;
  - `NetRepair.cs`: network watch, repair, heal, black box, diagnostics;
  - `Updater.cs`: update check UI.
- **One shared `CoreWebView2Environment`** (`MainForm.envTask`) on `<data>\WebData`, so all tabs share one browser process.
  - If that process dies, `RecoverEngine` creates a new environment and gives every tab a new `WebView2` (`BrowserTab.ReplaceView`).
  - `engineGen` ignores the duplicate reports, one per tab, from the dead engine.
- **Private windows (Ctrl+Shift+N)** are extra `MainForm`s (`IsPrivate`, `mainWindow`) sharing the main window's settings, bookmarks and environment. Their tabs use an InPrivate controller (`ProfileName = "mizar-private"`), so they share one in-memory profile that the engine drops when the last private tab closes. They write nothing: no history, session, zoom or favicons, and `diag.log` gets `private` instead of hosts (`DH`/`DiagHost`). Closing the main window closes them; links from other apps always go to the main window.
- **Single instance per data folder** (`SingleInstance` in `AppServices.cs`): a mutex `Local\Mizar.Instance.<id>` plus a pipe `Mizar.Open.<id>`.
  - A second launch sends its URL, or `mizar-cmd:fixnet|netdiag`, and exits.
  - URLs that arrive before the session is ready wait in `pendingPipeUrls`.
  - `--selftest` skips the single-instance check.

## Modules

| File | Owns |
|---|---|
| `Program.cs` | Entry point and CLI flags; installs `ErrorLog`; hands off to a running instance; `Updater.CleanupOld()` at start, `Updater.ApplyStaged()` after the window closes. |
| `MainForm.cs` | Layout and custom caption (`WndProc`: `WM_NCCALCSIZE`, `WM_NCHITTEST`, `RecalcFrame`, `EnsureClientOnScreen`).<br>Tab lifecycle (`CreateTab`, `AddLazyTab`, `InitTab`, `Wire`, `Activate`, `CloseTab`, `RecoverEngine`) and sleeping (`SleepInactiveTabs`).<br>Zoom (`ApplySiteZoom`, `CheckZoom`, `zoomPoll`), DPI (`ApplyDpi`), theme (`ApplyTheme`), engine flags (`EngineOptions`).<br>Shortcuts (`ProcessCmdKey`, `ShortcutScript`, `RunShortcut`), downloads, session, `RunSelfTest`. |
| `TabStrip.cs` | `BrowserTab`: the per-tab model (`View`, `Url`, `PendingUrl`, `InFlight`, `Receivers`, `Crashes`, zoom state).<br>`TabStrip`: owner-drawn tabs and window buttons; empty areas return `HTTRANSPARENT`. |
| `ModernUi.cs` | `Theme` (palette, `Dpi`, `UiFont`, `DrawGlyph`, `ApplyToDialog`), `HitTestPanel`, `RoundButton`, `TextChip`, `AddressBox` (with the `SecurityLevel` chip), `ModernRenderer`, `Native` (P/Invoke). |
| `Storage.cs` | `AppPaths`, `Settings` (JSON), `SessionData`, `Bookmark`/`BookmarkStore` (atomic save, `.bak`, 10 daily backups, damaged-file recovery, Opera import), `FaviconCache`, `AdBlocker` (`Classify`, `FilterPatterns`). |
| `Dialogs.cs` | `EditDialog`, `SettingsForm`, `DriveApiDialog`, `BookmarkManagerForm` (tree with drag and drop). |
| `History.cs` | `HistoryStore`: `history.json`, at most 5000 entries, flushed every 10 s and on exit; `BuildPage` renders the history page HTML. |
| `Suggest.cs` | `SuggestBox`: address-bar drop-down. Bookmarks first, then history newest first; at most 6 rows, one per host + path. |
| `OperaFavicons.cs` | One-time import of Opera's favicons (the SQLite DB is copied to temp first) into `favicons/u_<hash>.png`; writes `opera-favicons.done`. |
| `DriveSync.cs` | Bookmark sync to `mizar-bookmarks.json` on Google Drive: REST v3, OAuth PKCE through a loopback listener, `drive.file` scope, tokens encrypted with DPAPI. |
| `AppServices.cs` | `InternalPages` (`/history`, `/crashed`), `ErrorLog` (`errors.log`, rotated at 512 KB), `DiagLog` (`diag.log`, rotated at 256 KB), `SingleInstance`, `DefaultBrowser`. |
| `NetRepair.cs` | Background network watch (`WireNetWatch`/`SetNetWatch`, `InFlight`), `FlushSockets`, `ResetServiceWorkers`, `RepairConnection`, `PageProbe`, `LogSlowLoad` → `WedgedWorker` → `HealWedgedWorker`, `BlackBox` (helpers `Cdp`, `Eval`), `OnPowerModeChanged`, `RunNetDiag`. |
| `YouTubeBlock.cs` + `YouTubeBuild.js` | YouTube ads, uBlock Origin's way, without the extension. About once a day: latest uBO release from GitHub (only `js/`, `lib/`) + uBO filters, quick fixes, unbreak and EasyList into `<data>\ytblock\work`. A hidden WebView (folder mapping `ytbuild.mizar.internal`) runs `YouTubeBuild.js`, which uses uBO's own parser and `make-scriptlets.js` to keep only YouTube rules and emit one script (scriptlets + hiding CSS in a constructed stylesheet) → `ytblock\youtube.js`. Every tab gets it via `AddScriptToExecuteOnDocumentCreated`; it returns at once outside youtube.com / youtube-nocookie.com. On only while "Blochează reclamele" is on. |
| `Updater.cs` | `Updater`: GitHub Releases check, ECDSA verify, stage, swap on exit, `--release-genkey`/`--release-sign`. |

## Data flows

- **Startup:** `Program` → `MainForm` → `Shown`. If `RestoreSession` is on, saved tabs come back lazily (title and icon only); otherwise one home-page tab opens. Then the pipe queue is drained, history cleared (if `ClearHistoryOnExit`), and the one-time jobs run (Opera favicons, Drive sync, update check one minute after start).
- **Tab start:** `InitTab` → `EnsureCoreWebView2Async(sharedEnv)` → `Wire` (events, filters, injected shortcut script) → `Navigate`. A lazy tab does this on its first `Activate`.
- **Requests:** ads and trackers are blocked inside each page: `ApplyPageAdBlock` turns on the CDP `Network` domain (no body buffering) and sends `AdBlocker.PagePatterns` through `Network.setBlockedURLs`. The domain then stays on in every tab, so `SetNetWatch` only flips its flag. Requests for `mizar.internal` are the only ones routed through `WebResourceRequested` (served by `InternalPages.Serve`). The old route, `AddWebResourceRequestedFilter` per ad domain (`AdFilter`), triggered the Gmail stall (confirmed 2026-11-27).
- **Background tab:**
  1. When hidden: `MemoryUsageTargetLevel.Low`, and CDP `Network` turns on to track `InFlight`.
  2. After `SleepAfter` (5 min) with no audio and no request in flight: `TrySuspendAsync`.
  3. When shown again: it resumes, and the network watch turns off.
- **Slow page:** `zoomPoll` (1 s, active tab) → `LogSlowLoad`. After 20 s it logs a `PageProbe`; if `WedgedWorker` matches, it runs `HealWedgedWorker`: `BlackBox` → `ResetServiceWorkers` → reload. All of it goes to `diag.log`.
- **Update:**
  1. Monthly or manual: one call to `api.github.com/.../releases/latest`.
  2. Download the zip and its `.sig`, verify the signature over `"<version>\n<sha256>"`, unpack to `update\staged`.
  3. On exit: rename running files to `*.old`, copy the new ones; on error, roll back.
  4. At the next start, delete the `*.old` files.
- **Settings:** `SettingsForm` → `Settings.Save` → `MainForm.ApplySettings`. Engine flags (cookies) apply only after a restart; `WebColorScheme` applies live.

## Decisions and why

- **WebView2 + WinForms instead of a Chromium fork or WPF:** the engine already ships with Windows 11, so Mizar itself stays small. Measured on 2026-11-13: about 400 MB of WebView2 plus about 65 MB for `Mizar.exe`, versus about 2.4 GB for Opera.
- **Lazy restored tabs:** no page process until a tab is shown. Verified: 3 restored tabs, 1 page process.
- **Sleeping never interrupts a request:** all tabs share one HTTP/2 connection per server group, and a frozen tab that stops reading could stall it for everyone. `InFlight` ignores WebSocket, EventSource and non-HTTP requests, and expires entries after 60 s.
- **Own blocker instead of Edge tracking prevention:** `EnableTrackingPrevention=false`. It was turned off during the Gmail experiment and stayed off, since Mizar blocks trackers itself.
- **Engine flags** (`EngineOptions`):
  - `--enable-features=HttpsUpgrades`: HTTPS-First, falling back to HTTP;
  - `--test-third-party-cookie-phaseout`: only way that works to block third-party cookies;
  - `AreHostObjectsAllowed=false`;
  - permissions left to the engine's own prompt.
- **Facebook Pixel blocked by path only** (`fbevents.js`, `facebook.com/tr`), so logins and embeds keep working. YouTube ads can't be blocked by domain: they come from the same servers as the videos.
- **Built-in pages on `https://mizar.internal/`:** real navigations, sent with `X-Frame-Options: DENY` and `frame-ancestors 'none'`. History commands (`postMessage`) are accepted only from the history page itself.
- **Self-update:**
  - signed with ECDSA, with the version inside the signed data, so an old zip can't be re-tagged as new;
  - checked at most monthly, with no background process between checks;
  - swapped only on exit.
- **Per-monitor DPI:** sizes go through `S(...)` and fonts are pixel-sized (`Theme.UiFont`); all of it is re-applied on `OnDpiChanged`. The owner's two monitors are 150% (primary, 2560x1600) and 100% (1920x1200, above it).
- **Page canvas:** a new tab uses the theme colour, to avoid a white flash; `ContentLoading` switches it to white.
- **Zoom per site:** `Settings.SiteZoom` maps host → factor (without "www."; 100% isn't stored). `ExpectedZoom` tells a user change apart from one Mizar made itself; zoom steps follow Chrome's levels.
- **Suggestions:**
  - 1–2 letters match only the start of a host or of a title word; 3 or more match anywhere.
  - Filtered out: queries longer than 80 characters, redirect titles, and titles that are really addresses.
- **Bookmarks:** saves are atomic. A damaged file is set aside as `bookmarks.damaged-*.json` and the newest good copy is restored.
- **History privacy:** `ClearHistoryOnExit` (on by default) wipes `history.json` on close. At start, `ClearData(BrowsingHistory | DownloadHistory)` also clears the engine's own history. `diag.log` still keeps host names (a known trade-off).
- **Service-worker reset** = page `unregister()` + `Storage.clearDataForOrigin` (`service_workers` only):
  - unregistering alone fixes a worker stuck "starting";
  - a running worker keeps controlling its open page until it is removed outright.

## Rejected alternatives

- **`NavigateToString` for built-in pages:** Back/Forward broke, and it has a 2 MB limit.
- **CDP `Network.setCookieControls`:** has no effect in WebView2.
- **Ad-filter source kind `All`:** it routed service-worker requests through Mizar.
- **Socket flush as the Gmail fix:** it never fixed anything. It stays only in the manual menu and after wake-up.
- **Automatic socket flush on slow loads:** it could cut downloads (owner's decision).
- **`ServiceWorker.stopAllWorkers` and `cache_storage` in the reset:** dropped on 2026-11-17, after two engine deaths during a reset that used them.
- **Attaching to the worker in the black box:** crashed a stuck worker's page process in tests.
- **Restoring the session by default:** the owner prefers a single home-page tab.
