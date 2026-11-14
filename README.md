# Mizar

A minimal, low-RAM web browser for Windows 11 built with **WinForms (.NET 10)** and **WebView2**.

## Highlights

- **Low RAM usage:** single shared `CoreWebView2Environment`, `MemoryUsageTargetLevel.Low` for background tabs, automatic tab sleeping after 5 minutes (unless playing audio), and lazy session restoration.
- **Minimal UI:** custom Windows 11 title bar with floating rounded tabs, circular window buttons, automatic light/dark theme following Windows, and Romanian UI.
- **Privacy & security:** built-in domain-level ad and tracker blocking (`AddWebResourceRequestedFilter`), third-party cookies blocked by default, HTTPS-First upgrades, per-site zoom, and optional automatic history clearing on exit.
- **Bookmarks & sync:** bookmark bar and tree manager, manual Opera bookmark import, and optional zero-idle-RAM Google Drive API bookmark sync (OAuth 2.0 + PKCE with DPAPI-encrypted tokens).

## Build & Run

Requires [.NET 10 SDK](https://dotnet.microsoft.com/) on Windows 10/11 (with WebView2 Runtime, built into Windows 11).

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Programs\Mizar"
```
