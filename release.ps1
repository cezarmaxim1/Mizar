# Publishes a new Mizar version on GitHub Releases; installed copies pick it up
# (monthly check, or Settings > "Verifică acum").
#
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.0.1
#
# Needs: .NET SDK, git (push access), GitHub CLI "gh" (logged in: gh auth login),
# and the signing key %USERPROFILE%\.mizar-release\signing-key.pem (never commit it; back it up).
# ASCII only on purpose: Windows PowerShell 5.1 reads scripts without a BOM as ANSI.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = ""
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3" }
$key = Join-Path $env:USERPROFILE '.mizar-release\signing-key.pem'
if (-not (Test-Path $key)) { throw "Signing key not found: $key" }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "GitHub CLI (gh) is not installed" }
if (git status --porcelain) { throw "Commit or stash your changes first (git status is not clean)" }

# 1. version number in Mizar.csproj
$proj = Join-Path $PSScriptRoot 'Mizar.csproj'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$xml = [IO.File]::ReadAllText($proj, $utf8)
$xml = [regex]::Replace($xml, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
[IO.File]::WriteAllText($proj, $xml, $utf8)

# 2. build into a clean staging folder, without the files Mizar doesn't need
$stage = Join-Path $PSScriptRoot 'bin\release-stage'
$out = Join-Path $PSScriptRoot 'bin\release-out'
foreach ($d in $stage, $out) { if (Test-Path $d) { [IO.Directory]::Delete($d, $true) } }
dotnet publish -c Release -r win-x64 --self-contained false -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Get-ChildItem $stage -Recurse -Include *.xml, *.pdb, Microsoft.Web.WebView2.Wpf.dll | ForEach-Object { [IO.File]::Delete($_.FullName) }

# 3. zip + signature (made by the freshly built Mizar.exe, which also checks it against its own public key)
[IO.Directory]::CreateDirectory($out) | Out-Null
$zip = Join-Path $out 'Mizar-win-x64.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
$p = Start-Process (Join-Path $stage 'Mizar.exe') -ArgumentList '--release-sign', "`"$zip`"", $Version, "`"$key`"" -Wait -PassThru
if ($p.ExitCode -ne 0 -or -not (Test-Path "$zip.sig")) { throw "Signing failed (see $zip.error.txt)" }

# 4. commit + tag + push, then the GitHub release with both files
git commit -am "Release v$Version"
git tag "v$Version"
git push origin HEAD "v$Version"
if ($Notes -eq "") { $Notes = "Mizar $Version" }
gh release create "v$Version" $zip "$zip.sig" --title "Mizar $Version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Write-Host "Released v$Version"
