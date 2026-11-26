# Mizar status (updated 2026-11-17)

## Done and verified

Dates are when the feature was last verified.

- **Browsing:** tabs, navigation, address bar with suggestions, bookmark bar and manager, Opera import (bookmarks and favicons), downloads to a chosen folder, settings, per-site zoom, history page, dark mode (live). 2026-11-14.
- **Privacy:** ad and tracker blocking, third-party cookies blocked, HTTPS-First, security chip in the address bar, history wiped on exit. 2026-11-14.
- **Reliability:**
  - single instance with URL hand-off;
  - session restore (lazy tabs, off by default);
  - renderer-crash guard, `errors.log`, `diag.log`.
- **Window:** fix for the window that ignored every click after a display change, plus per-monitor DPI. 2026-11-16, 12/12 in `tests\two-monitors.ps1`.
- **Self-update** from GitHub Releases. Releases 1.0.1, 1.0.2 and 1.0.3 are published.
- **Google Drive bookmark sync** (`DriveSync.cs`).
- **Gmail mitigations:** manual repair, automatic heal, black box, engine restart (see below).
- **Not in any release yet:** commits after `v1.0.3` on `main`: the black box (`ffadc1d`) and the engine restart with the new reset (`3059911`). The owner's machine runs them from a local install.

## In progress: Gmail stall

### Symptom

Gmail loads its frame, but every icon, the background image and the fonts stay loading forever; Gmail shows "Not connected".

- Seen on 2026-11-14, 11-15 (twice), 11-16 17:18 and 11-17 08:52, sometimes two minutes after start.
- F5, closing other tabs, clearing the cache and opening a new Gmail tab don't help.
- By hand, it was fixed by `edge://serviceworker-internals` → Unregister `mail.google.com` → F5.
- The owner says other browsers never do this.

### What the evidence says

- **The requests are held inside Gmail's service worker, before the network.** The engine's NetLog showed every request that reached the network finishing normally, and curl fetched the same URLs fine.
- **First black box, 2026-11-17 08:52** (`diag.log` lines `blackbox mail.google.com ...`):
  - 106 of 106 images pending;
  - a fetch through the worker hits `TIMEOUT`; the same fetch bypassing the worker returns `200`;
  - Cache Storage (4 caches), IndexedDB (3 databases) and the storage estimate (20 MB) all answer;
  - the engine reports `status=activated running=starting clients=1`, so **the worker never finishes starting**;
  - inside the worker, `1+1` answered but the async probes didn't. That attach step has since been removed.
- **Reproduced locally** by `--stucktest`: a worker whose script loops forever when restarted gives the same picture. So "script never finishes, or the start never completes" fits the evidence; why Gmail's does that is unknown.

### Ruled out (the stall recurred with each of these changed)

- Network or a dead HTTP/2 connection: the flush never helped, and curl worked.
- Ad-filter scope: still stalled with source kind `Document`.
- Tab sleeping: stalled with `asleep=0`.
- Third-party cookie blocking and Edge tracking prevention: stalled with both off, and same-site images were stuck too.

### Side effect: the engine died, now fixed

On 2026-11-16 17:19 (manual repair) and on 2026-11-17 08:52 (automatic heal), the whole browser process died with `0xC0000005` during the service-worker reset, and the window stayed blank.

- The real trigger is unproven. In tests, only attaching to the stuck worker crashed anything, and only the page's process.
- Fixed in `3059911`:
  - the reset no longer calls `stopAllWorkers` or clears `cache_storage`;
  - the black box no longer attaches to the worker;
  - `RecoverEngine` restarts the engine if it dies anyway.

### Where the code is

- `NetRepair.cs`:
  - `LogSlowLoad`, `WedgedWorker` (worker active, 5 or more images, at least 80% pending);
  - `HealWedgedWorker` (at most once per 5 min per site);
  - `BlackBox`, `PageProbe`, `ResetServiceWorkers`, `RepairConnection`, `RunNetDiag`.
- `MainForm.cs`:
  - `EngineOptions` (engine flags);
  - `Wire` → `ProcessFailed` → `RecoverEngine`;
  - `SleepInactiveTabs`, `SetNetWatch`;
  - the startup `ClearData`;
  - `RunSelfTest` (`--stucktest`, `--healtest`, `--repairtest`, `--enginetest`).

### Next steps, in order

1. **Wait for the next stall.** The heal runs by itself; Gmail may first ask "Reload site?", answer Reload.
   - As of 2026-11-19 09:30: no Gmail `slow load`, heal or engine death since 2026-11-17 08:52 (about 48 h, several restarts).
   - **Recurred 2026-11-19 17:58**, after a 2.5-day pause, about 4 h after start: `img=132 imgPending=132`, `tabs=4 asleep=0`. Black box identical to 11-17: fetch through the worker `TIMEOUT`, bypassing it `200`, storage answers, `running=starting`. The heal worked in 4 s with no engine death (`unregister ok:1, removed ok`; 10 s later `imgPending=0 sw=none`). So the stall is reproducible in kind, the new reset is safe, and the root cause is still "Gmail's worker never finishes starting, only in Mizar".
   - No visible link to tab sleeping: Gmail itself slept only on 11-15, 11-17 13:06 and 11-18, never right before a stall, and nothing was asleep at three of the four stalls.
2. **Read the evidence** in `%LOCALAPPDATA%\Mizar\diag.log`: the `slow load`, `blackbox`, `heal`, `process failed` and `engine restart` lines around the stall.
   - Expected: `running=starting` again.
   - `running=running` with fetches timing out → the worker's own logic is waiting on something.
   - `no worker version reported` → the engine failed to start the worker.
   - Check that no `engine restart` line follows the heal.
3. **Remove the remaining Mizar-only differences, one at a time, 2–3 days each,** and note each start date here:
   - **(a) ruled out:** with it off, Gmail stalled again on 2026-11-19 18:07, 2.5 min after start (same black box: `running=starting`). It is back on. Two stalls 8 min apart, the second right after a restart that followed a heal (fresh worker registration), so stalls can come within minutes.
   - **(b), (c), (d) ruled out:** all three off from 2026-11-19 18:10; Gmail stalled on 2026-11-20 08:12 (same black box, heal clean; Mizar had been open overnight). All three are back on.
   - **Engine versions match:** WebView2 Runtime and Edge are both 157.0.4341.60, so Edge is a fair control. **The owner keeps Gmail open in Edge from 2026-11-20.** If Edge stalls too, the cause is the engine or the account, not Mizar.
   - **`TabSleeping` cleared:** back on from 2026-11-23 20:48 with `AdFilter` still off; no Gmail stall in ~3 days (to 2026-11-26 18:30).
   - **Running now: `AdFilter` back on too (2026-11-26 18:40), the confirmation test.** No Gmail stall at all from 2026-11-20 08:17 to now (~6 days) while the filter was off; before, stalls came every 1–3 days. Stall within ~3 days → the ad filter (`AddWebResourceRequestedFilter` + `WebResourceRequested`) is the trigger, and the fix is a way to block ads that leaves Gmail alone. No stall → it was something else that went away.
   - Former running note: `TabSleeping` back on (`2026-11-23 20:48`), `AdFilter` still off.
     - Both were off from `2026-11-20 08:17` to `2026-11-23 20:48` (**~84 h / 3.5 days, zero Gmail stalls**). Release 1.0.4 shipped with both off.
     - Turning them back on one at a time: `TabSleeping = true` first (`diag.log` shows `tabSleeping=on adFilter=off`).
     - Stall now → `TabSleeping` is the trigger. No stall for 3–4 days → turn `AdFilter = true` next to test the `WebResourceRequested` filter.
   - (a) `MemoryUsageTargetLevel.Low` for hidden tabs;
   - (b) the startup `ClearData(BrowsingHistory | DownloadHistory)`;
   - (c) the background CDP `Network.enable` watch;
   - (d) `--enable-features=HttpsUpgrades`.

   A stall while one is off clears that one.
4. **Control test:** Gmail in Edge on the same machine for a few days. If Edge stalls too, the cause is the engine version or the account, not Mizar.
5. **During a live stall,** optionally:
   - open `edge://serviceworker-internals` in a Mizar tab and note the `mail.google.com` entry's status and its log;
   - or run `Mizar.exe --netdiag <out.txt>`: three phases (as is, after a flush, after a worker reset).

## Known bugs and loose ends

- **Third-party cookie blocking stays OFF for the owner** (owner, 2026-11-26): with it on, Google signed them out almost daily. `diag.log` start lines show `3pcBlock=False`. The setting defaults to on for new installs.

- **Focus ring on the page's first link after switching tabs** (2026-11-20, e.g. "Skip to main content" on Reddit). Cause, read from the WinForms WebView2 IL: `Select(directed: true)` stores `MoveFocus(Next)`, and hiding the focused old WebView made WinForms select the next control that way. `Activate` now focuses the new tab before hiding the old one. Not reproducible in a self-test (no foreground window); awaits the owner's check.

- **PDF save asked twice** (2026-11-18, with `AskWhereToSave` on): the PDF viewer's Save shows the engine's dialog, then `OnDownloadStarting` showed Mizar's. Now `IsPdfViewerSave` (PDF whose download Uri is the tab's own page) keeps the engine's choice. Each download logs `download <host> mime= pdfViewerSave= ask=`. Link downloads verified with `--downloadtest`; the viewer case awaits the owner's check: if it still asks twice, read the `download` line.
  - 2026-11-19: a real save from the viewer logged `pdfViewerSave=True ask=True`, so Mizar skipped its own dialog. An image download the same day correctly logged `pdfViewerSave=False`.

- **Signed out of Google at every restart** (since 2026-11-16, Google only; other sites stay signed in; the account chooser is empty too, so every Google cookie is gone).
  - Facts so far: no Mizar code deletes cookies (only the Settings buttons); the profile's `Cookies` file is saved normally.
  - Suspect, unproven: third-party cookie blocking, turned back on 2026-11-16 17:26.
  - **Evidence being collected (from 2026-11-17):** `LogGoogleCookies` in `MainForm.cs` writes `cookies <when> google: N [domain/name:expiry|s]` (no values) to `diag.log`:
    - `<when>` is `start`, `after history wipe` (the startup `ClearData`), or `close`;
    - `s` marks a session cookie.
  - **How to read it, comparing the last `close` with the next `start`:**
    - sign-in cookies (`SID`, `HSID`, `SSID`, `__Secure-1PSID`, `LSID`…) shown as `s` at close → they expire by design; find why Google sets them that way;
    - dated at close but missing at start → something removes them on shutdown or startup;
    - present at `start` but gone `after history wipe` → `ClearData` is the cause.
  - Verified on an isolated profile: the lines are written and closing still works.
  - 2026-11-17 11:28 (reinstall, closed with `WM_CLOSE`): all 30 Google cookies present at `start`, dated, kept after the wipe; the owner **stayed signed in**. Second suspect: an unclean end. The engine crashed on 2026-11-16 17:19 and 2026-11-17 08:52; Google rotates `__Secure-1PSIDTS` often, and a crash may leave an older copy on disk that Google then rejects. Watch whether a sign-out follows a `process failed BrowserProcessExited` or a start with no `close` line before it.
  - 2026-11-17 to 11-19: every `close` → `start` pair kept 30–31 Google cookies, with no engine death. The owner hasn't reported a sign-out since.
  - **2026-11-23 08:08–08:30 and 13:14–13:15 (reproduced twice, zero crashes):** both runs started cleanly with all 30 dated Google cookies intact at `start` and `after history wipe`, with no `process failed` and clean `close` lines beforehand. Mid-session (08:08→08:30), Google cookies dropped from **30 to 4** (`AEC`, `SEARCH_SAMESITE`, `SOCS`, `__Secure-ENID`), and at 13:15 Gmail redirected to `accounts.google.com/v3/signin/identifier` with `3pcBlock=True` (`--test-third-party-cookie-phaseout`). This rules out startup/shutdown/crash cookie loss and points directly to Google's server revoking the session while `--test-third-party-cookie-phaseout` blocks `3P` cookie rotation (`__Secure-3PSIDTS` / `RotateCookies`).
  - Next step: turn third-party cookie blocking off (`BlockThirdPartyCookies = false`), sign in once, and watch for a few days.

- **Wrong URL in the address bar (fixed 2026-11-20):** `UpdateAddress()` returned early whenever `address.Focused` was true, and clicking a non-focusable `TabStrip` tab, bookmark bar item, or nav button never moved focus out of `address`. Now `Activate` forces `UpdateAddress(force: true)` and moves focus to web tabs (`tab.Url != "about:blank"`), and `Navigate`/Back/Forward/Reload unfocus `address` so the active page's URL is always shown.
- **English buttons:** the Yes/No buttons in `MessageBox` dialogs are in English (system strings).
- **Signing key backup:** the owner should keep a copy of `%USERPROFILE%\.mizar-release\` somewhere safe.
- **Files to delete by hand:** `F:\mizar-netlog.json` (contains browsing URLs), `F:\mizar-copylog.cmd`, `F:\mizar-diag-copy.log`, and the `mz-*` folders in `%TEMP%`.
