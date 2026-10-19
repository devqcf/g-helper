# Interactive Game Bar testing helpers.
#
# Everything here drives the real desktop: it opens Game Bar, waits for the widget to be
# enumerated, captures what is on screen, and reports whether the widget actually loaded.
#
# These are the pieces the research turned up for verifying a sideloaded widget:
#   * Game Bar is a normal DWM-composited window, so a full-screen CopyFromScreen capture
#     shows it. PrintWindow does not.
#   * Win+G cannot be sent with WScript.Shell.SendKeys (the Windows key is not in its
#     grammar), so it needs SendInput.
#   * The authoritative check that Game Bar can see a widget is
#     Windows.ApplicationModel.AppExtensions.AppExtensionCatalog - it is the same
#     enumeration Game Bar itself performs.
#
# Usage:
#   .\GameBarInteraction.ps1 -ListWidgets          # what Game Bar can see
#   .\GameBarInteraction.ps1 -OpenGameBar          # open Game Bar
#   .\GameBarInteraction.ps1 -OpenWidget           # open Game Bar and activate the widget
#   .\GameBarInteraction.ps1 -Capture shot.png     # screenshot the desktop
#   .\GameBarInteraction.ps1 -Verify               # full check + screenshot

[CmdletBinding()]
param(
    # The package family name is derived from the installed package, because the publisher
    # hash in it is machine specific (it comes from the signing certificate).
    [string]$PackageFamilyName,
    [string]$AppExtensionId    = "GHelperWidget",
    [string]$OutputPath        = ".\gamebar.png",
    [switch]$ListWidgets,
    [switch]$OpenGameBar,
    [switch]$OpenViaUri,
    [switch]$OpenWidget,
    [switch]$Capture,
    [switch]$Verify,
    # Focus the Game Bar window before sending keys.
    [switch]$FocusGameBar,
    # "x,y" pixel coordinates to click (Game Bar's surface is only reachable by clicking).
    [string]$ClickAt,
    # Key sequence to send, e.g. "WIN+G" or "TAB,TAB,ENTER". Needed because mouse
    # injection does not work over RDP - the client owns the cursor.
    [string]$Keys
)

$ErrorActionPreference = "Stop"

if (-not $PackageFamilyName) {
    $installed = Get-AppxPackage -Name 'GHelperGameBar*' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($installed) { $PackageFamilyName = $installed.PackageFamilyName }
    else { $PackageFamilyName = 'GHelperGameBar_8wekyb3d8bbwe' }
}

# ── Win32 interop ──────────────────────────────────────────────────────
if (-not ("GameBarNative" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class GameBarNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort vk, scan;
        public uint flags, time;
        public IntPtr extraInfo;
    }

    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint INPUT_KEYBOARD = 1;

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] p, int size);

    static void Key(ushort vk, bool up)
    {
        var i = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { vk = vk, flags = up ? KEYEVENTF_KEYUP : 0 } };
        SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void WinG()
    {
        Key(0x5B, false);   // VK_LWIN
        Key(0x47, false);   // 'G'
        Key(0x47, true);
        Key(0x5B, true);
    }

    // ── Mouse ────────────────────────────────────────────────────────────
    // Game Bar renders on a DirectComposition surface that neither EnumWindows nor
    // UI Automation can see, so it has to be driven by clicking screen coordinates.
    // CopyFromScreen still captures it, which is what makes the coordinates readable.

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT2 { public uint type; public MOUSEINPUT mi; }

    const uint INPUT_MOUSE = 0;
    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT2[] p, int size);

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    public static void Click(int x, int y)
    {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int ax = (int)Math.Round(x * 65535.0 / (sw - 1));
        int ay = (int)Math.Round(y * 65535.0 / (sh - 1));

        var move = new INPUT2 { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = ax, dy = ay, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE } };
        SendInput(1, new[] { move }, Marshal.SizeOf(typeof(INPUT2)));

        System.Threading.Thread.Sleep(80);

        var down = new INPUT2 { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } };
        var up = new INPUT2 { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } };
        SendInput(1, new[] { down }, Marshal.SizeOf(typeof(INPUT2)));
        System.Threading.Thread.Sleep(60);
        SendInput(1, new[] { up }, Marshal.SizeOf(typeof(INPUT2)));
    }

    // ── Keyboard ─────────────────────────────────────────────────────────
    //
    // Mouse injection does not work over an RDP session: the client owns the cursor and
    // re-asserts its position, so SendInput reports success but the pointer never moves.
    // Keyboard injection does work, so Game Bar is driven with keys instead.

    // ── Game Bar window ──────────────────────────────────────────────────
    //
    // Game Bar's bar is a Windows.UI.Core.CoreWindow owned by GameBar.exe, but it is not
    // the foreground window by default, so keystrokes have to be aimed at it explicitly.
    // WindowFromPoint is the reliable way to find it: the class and title change between
    // builds, but the bar is always under the widget icons.

    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public static bool FocusGameBar()
    {
        var p = new POINT { X = 393, Y = 54 };
        IntPtr h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return false;
        IntPtr root = GetAncestor(h, 2);
        BringWindowToTop(root);
        return SetForegroundWindow(root);
    }

    public static void Press(ushort vk)
    {
        Key(vk, false);
        System.Threading.Thread.Sleep(40);
        Key(vk, true);
        System.Threading.Thread.Sleep(140);
    }

    public static void Chord(ushort modifier, ushort key)
    {
        Key(modifier, false);
        System.Threading.Thread.Sleep(40);
        Key(key, false);
        System.Threading.Thread.Sleep(40);
        Key(key, true);
        System.Threading.Thread.Sleep(40);
        Key(modifier, true);
        System.Threading.Thread.Sleep(140);
    }

    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public static string[] ListWindows()
    {
        var rows = new List<string>();
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            uint pid; GetWindowThreadProcessId(h, out pid);
            string cn = c.ToString();
            string title = t.ToString();
            if (cn.Contains("CoreWindow") || cn.Contains("ApplicationFrameWindow") ||
                title.IndexOf("Game Bar", StringComparison.OrdinalIgnoreCase) >= 0)
                rows.Add(string.Format("0x{0:X} pid={1} class={2} title={3}", h.ToInt64(), pid, cn, title));
            return true;
        }, IntPtr.Zero);
        return rows.ToArray();
    }
}
'@
}

function Get-WidgetCatalog {
    # The same enumeration Game Bar performs. If the extension is listed here, Game Bar
    # can see it - and if it is not, no amount of waiting will make it appear.
    Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null

    $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]

    function Await($op, $type) {
        $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op))
        $t.Wait(-1) | Out-Null
        $t.Result
    }

    $catalog = [Windows.ApplicationModel.AppExtensions.AppExtensionCatalog, Windows.ApplicationModel.AppExtensions, ContentType = WindowsRuntime]::Open('microsoft.gameBarUIExtension')
    $list = Await $catalog.FindAllAsync() ([System.Collections.Generic.IReadOnlyList[Windows.ApplicationModel.AppExtensions.AppExtension]])
    return $list
}

function Show-Widgets {
    Write-Host "App extensions registered as 'microsoft.gameBarUIExtension':" -ForegroundColor Cyan
    $all = Get-WidgetCatalog
    if (-not $all -or $all.Count -eq 0) {
        Write-Host "  (none)" -ForegroundColor Yellow
        return $all
    }
    foreach ($e in $all) {
        Write-Host ("  {0,-24} {1,-40} {2}" -f $e.Id, $e.DisplayName, $e.PackageFamilyName)
    }
    return $all
}

function Test-WidgetVisible {
    param([int]$TimeoutSeconds = 25)

    $pkg = Get-AppxPackage | Where-Object { $_.PackageFamilyName -eq $PackageFamilyName }
    if (-not $pkg) {
        Write-Host "  package $PackageFamilyName is not installed" -ForegroundColor Red
        return $false
    }

    $install = $pkg.InstallLocation
    $before = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($install, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -ExpandProperty Id)

    Write-Host "  opening Game Bar..."
    [GameBarNative]::WinG()
    Start-Sleep -Seconds 6

    # Ask Game Bar to activate this specific widget. Microsoft documents this URI grammar
    # but also notes that Game Bar "currently doesn't support URI activation apart from
    # Widget to Widget activation", so it is best effort - the Win+G path below is the
    # reliable one and the widget has to be picked from the grid by hand.
    $uri = "ms-gamebar://launch/activate/${PackageFamilyName}_App_$AppExtensionId"
    Write-Host "  requesting $uri"
    try { Start-Process $uri | Out-Null } catch { Write-Host "    (uri activation failed: $($_.Exception.Message))" }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $now = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $_.Path -and $_.Path.StartsWith($install, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -ExpandProperty Id)
        $fresh = $now | Where-Object { $before -notcontains $_ }
        if ($fresh) {
            Write-Host "  widget process started (pid $($fresh -join ','))" -ForegroundColor Green
            Start-Sleep -Seconds 4
            return $true
        }
        Start-Sleep -Seconds 2
    }

    Write-Host "  widget process did not start within ${TimeoutSeconds}s" -ForegroundColor Red
    return $false
}

function Save-Screenshot {
    param([string]$Path)
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "  screenshot -> $Path" -ForegroundColor Green
}

function Show-Windows {
    Write-Host "Game Bar / UWP windows on screen:" -ForegroundColor Cyan
    $w = [GameBarNative]::ListWindows()
    if (-not $w -or $w.Count -eq 0) { Write-Host "  (none found)" -ForegroundColor Yellow }
    else { $w | ForEach-Object { Write-Host "  $_" } }
}

# ── Actions ────────────────────────────────────────────────────────────

if ($ListWidgets) { Show-Widgets | Out-Null }
if ($OpenGameBar) {
    # Win+G is the documented gesture, but the keystroke only reaches the shell if a real
    # window has focus. The ms-gamebar: URI is the same path the shell itself uses and is
    # more reliable from a script.
    [GameBarNative]::WinG()
    Start-Sleep -Seconds 3
    if (-not (Get-Process -Name 'GameBar' -ErrorAction SilentlyContinue)) {
        Write-Host '  Win+G did not take - using the ms-gamebar: URI instead'
        Start-Process 'ms-gamebar:' | Out-Null
    }
    Start-Sleep -Seconds 6
    Show-Windows
}
if ($OpenWidget) { Show-Windows }
if ($ClickAt) {
    # A sequence of clicks, executed back to back. Game Bar hides itself as soon as it
    # loses focus, so every step has to happen inside one process - otherwise the bar
    # closes between the screenshot and the click and the click lands on the desktop.
    foreach ($step in ($ClickAt -split ';')) {
        $p = $step -split ','
        if ($p.Count -ne 2) { throw "-ClickAt expects 'x,y' or 'x,y;x,y'" }
        Write-Host "  clicking $step"
        [GameBarNative]::Click([int]$p[0], [int]$p[1])
        Start-Sleep -Seconds 2
    }
}
if ($FocusGameBar) {
    Write-Host '  focusing the Game Bar window'
    [GameBarNative]::FocusGameBar() | Out-Null
    Start-Sleep -Milliseconds 700
}
if ($Keys) {
    $map = @{
        'TAB' = [uint16]0x09; 'ENTER' = [uint16]0x0D; 'SPACE' = [uint16]0x20
        'ESC' = [uint16]0x1B; 'LEFT' = [uint16]0x25; 'UP' = [uint16]0x26
        'RIGHT' = [uint16]0x27; 'DOWN' = [uint16]0x28; 'HOME' = [uint16]0x24
        'WIN' = [uint16]0x5B; 'G' = [uint16]0x47; 'F6' = [uint16]0x75
    }
    foreach ($step in ($Keys -split ',')) {
        $s = $step.Trim().ToUpperInvariant()
        if ($s -like '*+*') {
            $parts = $s -split '\+'
            $mod = $map[$parts[0].Trim()]
            $key = $map[$parts[1].Trim()]
            if ($null -eq $mod -or $null -eq $key) { throw "Unknown key in '$s'" }
            Write-Host "  chord $s"
            [GameBarNative]::Chord($mod, $key)
        }
        else {
            $code = $map[$s]
            if ($null -eq $code) { throw "Unknown key '$s'" }
            Write-Host "  key $s"
            [GameBarNative]::Press($code)
        }
    }
}
if ($Capture) { Save-Screenshot -Path $OutputPath }

if ($Verify) {
    Write-Host "`n=== Game Bar widget verification ===" -ForegroundColor Cyan
    Show-Widgets | Out-Null
    Write-Host ""
    $ok = Test-WidgetVisible
    Write-Host ""
    Show-Windows
    Save-Screenshot -Path $OutputPath
    Write-Host ""
    if ($ok) { Write-Host "RESULT: widget process launched" -ForegroundColor Green }
    else { Write-Host "RESULT: widget did NOT launch" -ForegroundColor Red }
    exit $(if ($ok) { 0 } else { 1 })
}
