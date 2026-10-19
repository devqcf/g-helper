<#
.SYNOPSIS
    Screenshots the G-Helper Game Bar widget on its own, at its real size.

.DESCRIPTION
    Launches the installed widget package as a plain foreground app - the "Outside Game Bar"
    path the Game Bar SDK's own testing guide recommends - and captures the window.

    Two things make this worth a script:

      * The widget is sized by Game Bar when it is hosted there, but nothing sizes it when it
        runs standalone, so the app resizes itself to the size Game Bar would use. The capture
        therefore shows the layout the user actually gets, not a 1024x768 accident.

      * Game Bar's own surface is a DirectComposition layer that neither EnumWindows nor UI
        Automation can see, so a widget hosted in Game Bar cannot be captured directly. The
        standalone window is the same XAML, the same code and the same bridge client.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File app/GameBar/scripts/Capture-Widget.ps1 -OutputPath .\widget.png
#>
[CmdletBinding()]
param(
    [string]$OutputPath = ".\widget.png",
    [int]$WaitSeconds = 25
)

$ErrorActionPreference = "Stop"

# ── Win32 interop ──────────────────────────────────────────────────────
if (-not ("WidgetCaptureNative" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WidgetCaptureNative
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        IntPtr ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            uint options,
            out uint processId);
        IntPtr ActivateForFile(IntPtr appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        IntPtr ActivateForProtocol(IntPtr appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    class ApplicationActivationManager { }

    public static uint Activate(string appUserModelId)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        uint processId;
        manager.ActivateApplication(appUserModelId, null, 0, out processId);
        return processId;
    }
}
'@
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

# ── Win32 interop for raising the window ───────────────────────────────
if (-not ("WidgetCaptureWindow" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class WidgetCaptureWindow
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int command);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Brings the largest visible window of a process to the front.</summary>
    public static bool Raise(uint processId)
    {
        IntPtr best = IntPtr.Zero;
        int bestArea = 0;

        EnumWindows((h, l) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != processId || !IsWindowVisible(h)) return true;

            RECT r;
            GetWindowRect(h, out r);
            int area = (r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);

        if (best == IntPtr.Zero) return false;

        ShowWindow(best, 9);   // SW_RESTORE
        BringWindowToTop(best);
        return SetForegroundWindow(best);
    }
}
'@
}

# ── Locate the installed package ───────────────────────────────────────
$package = Get-AppxPackage -Name 'GHelperGameBar*' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $package) {
    throw "GHelperGameBar is not installed. Run scripts/Deploy-Widget.ps1 first."
}

$manifest = Get-AppxPackageManifest $package
$appId = ($manifest.package.applications.application | Select-Object -First 1).id
$appUserModelId = "$($package.PackageFamilyName)!$appId"

Write-Host "package : $($package.PackageFullName)"
Write-Host "aumid   : $appUserModelId"

# ── Restart the widget so the capture shows a fresh window ─────────────
Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($package.InstallLocation, [StringComparison]::OrdinalIgnoreCase)
} | Stop-Process -Force -ErrorAction SilentlyContinue

# Game Bar's own panel is a full-screen overlay. If it is open it sits on top of the
# widget, so the capture would photograph Game Bar instead of the widget.
Get-Process -Name 'GameBar' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Start-Sleep -Seconds 1

$processId = [WidgetCaptureNative]::Activate($appUserModelId)
if ($processId -eq 0) { throw "the widget did not start" }
Write-Host "started : pid $processId"

# ── Wait for the window, then find its bounds ──────────────────────────
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$processId)

$bounds = $null
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 700

    $elements = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)

    for ($i = 0; $i -lt $elements.Count; $i++) {
        $element = $elements.Item($i)
        if ($element.Current.ControlType.ProgrammaticName -notmatch 'Window|Custom|Pane') { continue }

        $rect = $element.Current.BoundingRectangle
        if ($rect.Width -gt 200 -and $rect.Height -gt 150) {
            $bounds = $rect
            break
        }
    }

    if ($bounds) { break }
}

if (-not $bounds) { throw "the widget window never appeared" }

Write-Host ("window  : {0},{1} {2}x{3}" -f [int]$bounds.X, [int]$bounds.Y, [int]$bounds.Width, [int]$bounds.Height)

# ── Capture ────────────────────────────────────────────────────────────
[WidgetCaptureWindow]::Raise([uint32]$processId) | Out-Null
Start-Sleep -Seconds 2

$screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
$full = New-Object System.Drawing.Bitmap($screen.Width, $screen.Height)
$graphics = [System.Drawing.Graphics]::FromImage($full)
$graphics.CopyFromScreen($screen.Left, $screen.Top, 0, 0, $full.Size)
$graphics.Dispose()

$width = [int][Math]::Round($bounds.Width)
$height = [int][Math]::Round($bounds.Height)
$crop = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($crop)
$graphics.DrawImage(
    $full,
    (New-Object System.Drawing.Rectangle(0, 0, $width, $height)),
    (New-Object System.Drawing.Rectangle([int]$bounds.X, [int]$bounds.Y, $width, $height)),
    [System.Drawing.GraphicsUnit]::Pixel)
$graphics.Dispose()
$full.Dispose()

$directory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($OutputPath))
if ($directory -and -not (Test-Path $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }

$crop.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$crop.Dispose()

Write-Host "capture : $OutputPath" -ForegroundColor Green
