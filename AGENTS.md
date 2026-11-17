# Mizar

Personal, minimal web browser for Windows 11 (WebView2 + WinForms). Single user: the owner.

- **Priorities, in order:** (1) low RAM: never keep pages, processes or extensions alive in the background; (2) Windows 11 look: rounded, flat, compact (36 px tab strip, 38 px nav bar); (3) only the essentials.
- **Not wanted:** ads, AI features, sidebars, news, wallets, promotions, telemetry.
- **Owner's choices, don't revert:**
  - new tab (+, Ctrl+T) = `about:blank` titled "Tab nou", not the home page;
  - Google at startup;
  - session restore off by default.
- **Language:** the owner is Romanian. UI text is Romanian with diacritics; reply to the owner in Romanian. Code and docs stay in English.

For modules, data flow and design decisions, read `ARCHITECTURE.md`. For what's done, what's open, and the Gmail investigation, read `STATUS.md`.

## Stack

- .NET 10 (`net10.0-windows`), WinForms, `ApplicationHighDpiMode=PerMonitorV2`. Moved from .NET 8 on 2026-11-14, when .NET 8 support ended.
- `Microsoft.Web.WebView2` 1.0.2903.40; the runtime is the one shipped with Windows 11.
- `Microsoft.Data.Sqlite` pinned to 10.0.15: 8.0.x pulled in a vulnerable `SQLitePCLRaw.lib.e_sqlite3`.
- Version: `<Version>` in `Mizar.csproj`. Latest release: 1.0.3; `main` is ahead of it (see `STATUS.md`).
- Repo: `github.com/cezarmaxim1/Mizar` (public), branch `main`.

## Commands

- **Build:** `dotnet build -c Release` → `bin\Release\net10.0-windows\Mizar.exe`.
- **Install for the owner.** Local source is the base: the owner's copy is installed from here, never through the self-updater. The owner allows closing their Mizar for this without asking.
  1. Close it: post `WM_CLOSE` to its `WindowsForms10.Window*` top-level window. `Process.CloseMainWindow` does nothing; force-kill only if it doesn't exit.
  2. `dotnet publish -c Release -r win-x64 --self-contained false -o "$LOCALAPPDATA/Programs/Mizar"`
  3. Delete `*.xml`, `*.pdb` and `Microsoft.Web.WebView2.Wpf.dll` from that folder.
  4. Relaunch it, and tell the owner it was restarted: open tabs are lost.
- **Routine after a change:** test → install locally → `git push`.
- **Release:** only when the owner asks. Commit first, then `powershell -ExecutionPolicy Bypass -File release.ps1 -Version X.Y.Z`. Needs `gh` logged in and the signing key. Release notes in Romanian.
- **Self-test:** `Mizar.exe --selftest <url> <outfile> [flags]`. Loads the page, waits about 8 s, runs the check, writes `<outfile>` (counters, page text) plus `<outfile>.page.png`, then exits.
  - Always set `MIZAR_DATA_DIR` to a temp folder. It isolates settings, bookmarks, history, session and the WebView2 profile, and lets a test run while the owner's Mizar is open.
  - `--noshot`: no screenshots. `--notext`: no page text. Both are **mandatory** on the owner's real logged-in profile; write that output under `F:\` and delete it afterwards.
  - Checks:

    | Flag | Checks |
    |---|---|
    | `--keys` | Ctrl+H sent through CDP |
    | `--zoom` | per-site zoom |
    | `--histback` | history page → link → Back |
    | `--crash` | 3 renderer crashes → `/crashed` |
    | `--suggest [--query t]` | address-bar suggestions |
    | `--newtab` | a new tab |
    | `--netlog` | failed requests |
    | `--sleeptest` | suspend/resume; `MIZAR_SLEEP_SECONDS`, default 15 |
    | `--sleepguard` | busy tab not suspended |
    | `--flushtest` | socket flush |
    | `--updatetest` | update check and staging; `MIZAR_UPDATE_URL` points it at a fake API |
    | `--monitortest` | DPI and wrong-monitor repair; needs 2 monitors |
    | `--scheme light\|dark\|auto` | page colour scheme |

  - These need a local page served with `python -m http.server`:

    | Flag | Page needed | Checks |
    |---|---|---|
    | `--repairtest` | a page with `sw.js` | manual repair |
    | `--healtest` | a page with `stuck-sw.js`, whose worker never answers images | automatic heal |
    | `--stucktest [--oldreset] [--nobox]` | a page with `loop-sw.js`, whose script loops forever when restarted | reset of a worker stuck "starting" |
    | `--enginetest` | any page | kills the browser process; Mizar must recover |

    The test pages are not in the repo. The table above says what each must contain.
- **Two-monitor scenarios:** `powershell -ExecutionPolicy Bypass -File tests\two-monitors.ps1 -Exe <Mizar.exe> -Tag x`. 12 window-placement scenarios on an isolated copy; needs both monitors.
- **After any package change:** `dotnet list package --include-transitive --vulnerable`.
- **Icon:** `python -I assets/make_icon.py` builds `Mizar.ico`, `assets/icon.png` and `assets/icon-preview.png` from `assets/icon-source.jpg`. Check the preview on both the dark and the light background.
- **Default browser:** `Mizar.exe --register` / `--unregister` (HKCU only). Run it from the **installed** copy, because it stores the exe path. Windows lets only the user pick the default (Settings → "Fă din Mizar browserul implicit...").
- **Network repair from outside:** `Mizar.exe --fixnet` runs the repair. `Mizar.exe --netdiag <out.txt>` writes a 3-phase stall report. Both are sent to the running window.

## Locations

- `*.cs` (repo root): all source. The module map is in `ARCHITECTURE.md`.
- `assets/`: icon source and generator.
- `tests/two-monitors.ps1`: the only scripted test outside `--selftest`.
- `release.ps1`: release pipeline.
- `bin\`: dev builds. They never self-update; never register them as the default browser.
- `%LOCALAPPDATA%\Programs\Mizar\`: the owner's install. Shortcuts `F:\Desktop\Mizar.lnk` and `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Mizar.lnk` point there.
- `%LOCALAPPDATA%\Mizar\`: user data (`$MIZAR_DATA_DIR` in tests).
  - `settings.json`
  - `bookmarks.json`, `bookmarks.bak`, `backups/`
  - `history.json`, `session.json`
  - `errors.log`, `diag.log`
  - `favicons/`
  - `update/staged`
  - `WebData/`: the WebView2 profile
- `%USERPROFILE%\.mizar-release\signing-key.pem` (+ `.pub`): release signing key. **Never commit it** (`*.pem` is gitignored). If it's lost, installed copies reject new releases until Mizar is reinstalled by hand.
- `google-drive-api.txt` (gitignored): OAuth client ID and secret. **Never print or commit it.**

## Code conventions (not visible from a single file)

- **Colours:** always `Theme.*`, never hard-coded.
- **Fonts:** never `new Font("Segoe UI", 9f)`, because point sizes follow the primary monitor only. Use `Theme.UiFont(pt, DeviceDpi)`.
  - Any new sized control or font must also be re-applied in `MainForm.ApplyDpi()`.
- **Icon glyphs:** Unicode escapes (`"\uE72B"`) drawn through `Theme.DrawGlyph`. Font: Segoe Fluent Icons, falling back to Segoe MDL2 Assets (same code points).
- **Dialogs:** call `Theme.ApplyToDialog(this)` at the end of the constructor.
- **Favicons:** only through `BrowserTab.SetFavicon(img, owned)`. Shared cache images are never disposed.
- **Built-in pages:** exclude them from history, zoom and session with `InternalPages.IsInternal(url)`.
- **Keyboard shortcuts:** add to both `ProcessCmdKey` and `ShortcutScript`/`RunShortcut`, because WebView2 swallows keys while the page has focus.
- **CDP event receivers:** keep them referenced (`BrowserTab.Receivers`), or they stop delivering events.
- **`diag.log`:** hosts and counts only, never paths, titles or page content.
- **Self-test mode** doesn't save settings or session. Keep it that way: earlier tests overwrote the owner's data.
- **Docs:** when you verify behaviour, write it down with the date. Update `STATUS.md` when something is finished or found.

## Traps (each has broken before)

- **Built-in pages must stay real navigations** (`https://mizar.internal/...` served from `WebResourceRequested`). `NavigateToString` broke Back/Forward and has a 2 MB limit.
- **Ad-filter patterns:** never add a catch-all `*` filter, and keep the source kind `Document`, not `All`. With `All`, service-worker requests were routed through Mizar.
- **`ApplyTheme` must not touch `DefaultBackgroundColor` of loaded tabs.** Pages without a background expect white (altex.ro looked half dark).
- **`ZoomFactorChanged` never fires reliably;** zoom is polled (`zoomPoll`).
- **CDP `Network.setCookieControls` returns ok but does nothing** in WebView2. Third-party cookies are blocked with the engine flag instead.
- **Window layout:**
  - The root `TableLayoutPanel` needs a 100% `ColumnStyle`. Without it the window buttons slide off-screen after maximizing.
  - Title-bar hit testing returns `HTTRANSPARENT` at **both** `TabStrip` and `HitTestPanel` (`PassThrough`). Break either and drag and top-edge resize stop working.
  - Maximized `WM_NCCALCSIZE` uses the work area of the monitor from the **proposed** rect; display changes trigger `RecalcFrame`. Otherwise the client area lands on the other monitor and every click is lost.
- **No `SendKeys` from outside the app:** keys land in other windows. Use CDP `Input.dispatchKeyEvent`.
- **Captures:**
  - Screenshots of hidden tabs never finish; every capture has a 5 s timeout.
  - For window screenshots from outside: call `SetProcessDPIAware`, then `PrintWindow(h, hdc, 2)`. Without DPI awareness the capture is cropped.
- **Windows PowerShell 5.1 corrupts UTF-8** (`Get-Content`/`Set-Content` without `-Encoding`): diacritics and glyphs break. Use `[IO.File]::ReadAllText(path, UTF8)` or an editor that keeps UTF-8.
- **Suggestions:** measure text without `EndEllipsis`, or the width comes back as just "r…".
- **Network:**
  - No automatic socket flush on slow loads: it can cut downloads (owner's decision).
  - Never attach CDP to a stuck service worker (`Target.attachToTarget`): it crashed the page's process (`STATUS.md`).
- **Google sign-in** works through the normal page. If Google ever blocks it as an "embedded browser", adjust the user agent.
