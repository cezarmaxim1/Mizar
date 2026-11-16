param([string]$Exe, [string]$Tag)
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class M2 { public delegate bool P(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
[DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
[DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
public struct RECT { public int L,T,R,B; } public struct POINT { public int X,Y; }
public struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT min, max; public RECT normal; } }
'@
[M2]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
Add-Type -AssemblyName System.Windows.Forms
$screens = [System.Windows.Forms.Screen]::AllScreens
$prim = $screens | ? Primary; $sec = $screens | ? { -not $_.Primary } | Select-Object -First 1
$d = Join-Path $env:TEMP "mz-mon2-$Tag"; if (Test-Path $d) { [IO.Directory]::Delete($d, $true) }; [IO.Directory]::CreateDirectory($d) | Out-Null
# start maximized on the SECOND monitor
$sw = $sec.WorkingArea
$json = '{ "WindowBounds": [' + ($sw.X + 200) + ',' + ($sw.Y + 150) + ',1400,900], "Maximized": true, "AutoUpdate": false }'
[IO.File]::WriteAllText((Join-Path $d 'settings.json'), $json)
$env:MIZAR_DATA_DIR = $d
$p = Start-Process $Exe -ArgumentList "https://example.com/" -PassThru
Start-Sleep 9
$target = [uint32]$p.Id; $script:h = [IntPtr]::Zero
[M2]::EnumWindows({ param($w,$l) $q=0; [M2]::GetWindowThreadProcessId($w,[ref]$q)|Out-Null; if ($q -eq $target -and [M2]::IsWindowVisible($w)) { $sb=New-Object Text.StringBuilder 256; [M2]::GetClassName($w,$sb,256)|Out-Null; if ($sb.ToString() -like 'WindowsForms10.Window*') { $script:h = $w } }; $true }, [IntPtr]::Zero) | Out-Null
$script:fails = 0
function Check([string]$name, $expect) {
  Start-Sleep -Milliseconds 900
  $c=New-Object M2+RECT; [M2]::GetClientRect($script:h,[ref]$c)|Out-Null; $pt=New-Object M2+POINT; [M2]::ClientToScreen($script:h,[ref]$pt)|Out-Null
  $w=New-Object M2+RECT; [M2]::GetWindowRect($script:h,[ref]$w)|Out-Null
  $inside = $pt.X -ge $w.L -2 -and $pt.Y -ge $w.T - 2 -and ($pt.X + $c.R) -le $w.R + 2 -and ($pt.Y + $c.B) -le $w.B + 2
  $ok = $inside
  if ($expect) { $ok = $ok -and $pt.X -eq $expect.X -and $pt.Y -eq $expect.Y -and $c.R -eq $expect.Width -and $c.B -eq $expect.Height }
  # hit tests: empty strip area (right of tabs, left of buttons) and a page point
  $hx = $pt.X + [int]($c.R * 0.6); $hy = $pt.Y + 12
  $ht = [M2]::SendMessage($script:h, 0x84, [IntPtr]::Zero, [IntPtr]((($hy -band 0xFFFF) -shl 16) -bor ($hx -band 0xFFFF)))
  if (-not $ok) { $script:fails++ }
  "{0,-34} client {1},{2} {3}x{4} window {5},{6} zoomed={7} dpi={8} hitStrip={9} {10}" -f $name,$pt.X,$pt.Y,$c.R,$c.B,$w.L,$w.T,[M2]::IsZoomed($script:h),[M2]::GetDpiForWindow($script:h),$ht,$(if($ok){'OK'}else{'FAIL'})
}
function MoveMaximized($screen) {
  $pl = New-Object M2+WINDOWPLACEMENT; $pl.length = [Runtime.InteropServices.Marshal]::SizeOf($pl); [M2]::GetWindowPlacement($script:h,[ref]$pl)|Out-Null
  $a = $screen.WorkingArea; $r = New-Object M2+RECT; $r.L=$a.X+200; $r.T=$a.Y+150; $r.R=$a.X+1600; $r.B=$a.Y+1050
  # the way Win+Shift+Arrow is done: restore, move, maximize
  [M2]::ShowWindow($script:h, 9) | Out-Null
  [M2]::SetWindowPos($script:h,[IntPtr]::Zero,$r.L,$r.T,$r.R-$r.L,$r.B-$r.T,0x0004 -bor 0x0010) | Out-Null
  [M2]::ShowWindow($script:h, 3) | Out-Null
}
"screens: primary $($prim.Bounds) work $($prim.WorkingArea) | second $($sec.Bounds) work $($sec.WorkingArea)"
Check "start maximized on second" $sec.WorkingArea
[M2]::ShowWindow($script:h, 9) | Out-Null;           Check "restored on second" $null
[M2]::ShowWindow($script:h, 3) | Out-Null;           Check "maximized again on second" $sec.WorkingArea
[M2]::ShowWindow($script:h, 6) | Out-Null; Start-Sleep 1; [M2]::ShowWindow($script:h, 9) | Out-Null; Check "minimize + restore on second" $sec.WorkingArea
MoveMaximized $prim;                                  Check "moved maximized to primary" $prim.WorkingArea
MoveMaximized $sec;                                   Check "moved maximized back to second" $sec.WorkingArea
[M2]::PostMessage($script:h, 0x100, [IntPtr]0x7A, [IntPtr]::Zero) | Out-Null; Check "F11 fullscreen on second" $sec.Bounds
[M2]::PostMessage($script:h, 0x100, [IntPtr]0x7A, [IntPtr]::Zero) | Out-Null; Check "F11 back (maximized) on second" $sec.WorkingArea
MoveMaximized $prim
[M2]::PostMessage($script:h, 0x100, [IntPtr]0x7A, [IntPtr]::Zero) | Out-Null; Check "F11 fullscreen on primary" $prim.Bounds
[M2]::PostMessage($script:h, 0x100, [IntPtr]0x7A, [IntPtr]::Zero) | Out-Null; Check "F11 back on primary" $prim.WorkingArea
[M2]::ShowWindow($script:h, 9) | Out-Null
$a=$sec.WorkingArea; [M2]::SetWindowPos($script:h,[IntPtr]::Zero,$a.X+300,$a.Y+200,1500,1000,0x0004 -bor 0x0010) | Out-Null; Check "normal window on second" $null
# straddling both monitors
[M2]::SetWindowPos($script:h,[IntPtr]::Zero,$prim.Bounds.X+700,$prim.Bounds.Y-400,1500,1000,0x0004 -bor 0x0010) | Out-Null; Check "normal window across both" $null
[M2]::PostMessage($script:h, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
if (-not $p.WaitForExit(10000)) { Stop-Process -Id $p.Id -Force }
"saved: " + ([IO.File]::ReadAllText((Join-Path $d 'settings.json')) -replace '\s+',' ' | Select-String -Pattern '"WindowBounds": \[[^\]]*\]|"Maximized": \w+' -AllMatches | % { $_.Matches.Value -join ' ' })
"FAILS=$script:fails"
if (Test-Path (Join-Path $d 'errors.log')) { "errors.log:"; Get-Content (Join-Path $d 'errors.log') -TotalCount 8 }
Get-Content (Join-Path $d 'diag.log') -EA SilentlyContinue | Select-String "off-window|display"
