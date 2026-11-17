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
2. **Read the evidence** in `%LOCALAPPDATA%\Mizar\diag.log`: the `slow load`, `blackbox`, `heal`, `process failed` and `engine restart` lines around the stall.
   - Expected: `running=starting` again.
   - `running=running` with fetches timing out → the worker's own logic is waiting on something.
   - `no worker version reported` → the engine failed to start the worker.
   - Check that no `engine restart` line follows the heal.
3. **Remove the remaining Mizar-only differences, one at a time, 2–3 days each,** and note each start date here:
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
  - Fallback experiment for the owner: turn cookie blocking off, sign in, restart twice.

- **Wrong URL in the address bar:** it once showed a Facebook URL while the Gmail tab was active. Not reproduced, not investigated.
- **English buttons:** the Yes/No buttons in `MessageBox` dialogs are in English (system strings).
- **Signing key backup:** the owner should keep a copy of `%USERPROFILE%\.mizar-release\` somewhere safe.
- **Files to delete by hand:** `F:\mizar-netlog.json` (contains browsing URLs), `F:\mizar-copylog.cmd`, `F:\mizar-diag-copy.log`, and the `mz-*` folders in `%TEMP%`.
