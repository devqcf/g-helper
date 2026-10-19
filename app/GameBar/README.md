# G-Helper for Xbox Game Bar

An Xbox Game Bar widget for G-Helper: performance modes, GPU mode, panel refresh, FPS cap,
controller profile and the common toggles, all reachable with **Win + G** instead of a
standalone window.

G-Helper itself is a WinForms app and cannot be hosted by Game Bar — Game Bar renders widget
content as XAML. So this is a **separate, packaged UWP app** that talks to the running
G-Helper desktop process over a named pipe.

---

## How it fits together

```
┌──────────────────────────────┐         ┌───────────────────────────────┐
│  Xbox Game Bar               │         │  GHelper.exe (WinForms)       │
│  hosts GHelperWidget.xaml    │  named  │                               │
│  (this project, AppContainer)│◀──pipe──│  GameBarServer                │
│                              │         │  app/Helpers/GameBarServer.cs │
└──────────────────────────────┘         └───────────────────────────────┘
```

| Piece | Location |
| --- | --- |
| Widget UI | `GHelperWidget.xaml` / `.xaml.cs` |
| Settings page | `GHelperSettingsWidget.xaml` / `.xaml.cs` |
| Transport | `GHelperClient.cs` (Win32 `CreateFile`, not `NamedPipeClientStream`) |
| Wire protocol | `../Shared/GameBarProtocol.cs` (linked into **both** projects) |
| Server | `../Helpers/GameBarServer.cs` |

### Why a named pipe, and not loopback TCP

Loopback TCP looks like the obvious choice and it is a trap. From
[How to enable loopback and troubleshoot network isolation](https://learn.microsoft.com/en-us/previous-versions/windows/apps/hh780593):

> *"Network communications using an IP loopback address cannot be used for interprocess
> communication (between two different apps) in a Windows Runtime app since this is
> restricted by network isolation."*
>
> *"Loopback is permitted only for development purposes. **Usage by a Windows Runtime app
> installed outside of Visual Studio is not permitted.**"*

Visual Studio auto-grants a loopback exemption to the apps it deploys, so a TCP bridge
works perfectly while you develop and then fails for every real user.

Named pipes are what the [Game Bar documentation prescribes](https://learn.microsoft.com/en-us/xbox/game-bar/guide/communicating-apps)
for widget-to-desktop IPC. The catch is that `System.IO.Pipes.NamedPipeClientStream` is
rejected outright inside an AppContainer (*"not supported in app container"*,
[dotnet/runtime#25764](https://github.com/dotnet/runtime/issues/25764)), so the widget opens
the pipe with the Win32 `CreateFile` API instead — which AppContainer permits as long as the
pipe's security descriptor grants it access.

### Trust model

The pipe's DACL **has to** grant Everyone, because an AppContainer token carries neither the
interactive user's SID nor any other SID the desktop process could predict. To stop another
local user from driving the channel, `GameBarServer` resolves the connecting process
(`GetNamedPipeClientProcessId` → `OpenProcessToken`) and rejects the connection unless it is
running as the same user. A failure in that check denies rather than allows.

Beyond that, this is not a privilege boundary: a process already running as the user can
drive the ACPI interface directly. The channel must never be used to expose anything the
user could not already do.

---

## Controls

All of these are backed by existing G-Helper APIs and applied on its own threads — the widget
sends absolute values and re-reads state afterwards, so a rejected change snaps back instead
of silently lying.

| Widget control | G-Helper API |
| --- | --- |
| Performance mode | `modeControl.SetPerformanceMode(id)` |
| GPU mode (Eco / Standard / Ultimate) | `gpuControl.SetGPUMode(id)` |
| Screen refresh (Auto / min / max Hz) | `ScreenControl.SetAutoRefresh()` + `SetScreen()` |
| FPS cap (Off / 30…240) | `allyControl.SetFpsLimit(limit)` — ROG Ally / AMD only |
| Controller profile (Auto / Gamepad / Mouse / Skip) | `allyControl.SetModeDirect(mode)` — ROG Ally only |
| Fn Lock | `InputDispatcher.ToggleFnLock()` |
| Hardware overlay | `settingsForm.ToggleOverlay()` |
| Charge limit 100% | `BatteryControl.ToggleBatteryLimitFull()` |

Ally-only rows are hidden on other models. Everything else — fan curves, custom modes,
lighting, keybindings — stays in the G-Helper window, which remains the single owner of that
state.

**No telemetry.** The bridge deliberately does not poll sensors. G-Helper refreshes those on
its own timers (overlay, tray hover), and a second poller from the widget would double the
ACPI/NVML traffic and race with the existing ones.

---

## Building

### The widget has to be built with the .NET Native toolchain

`Microsoft.Gaming.XboxGameBar.dll` ships as a **.NET Native** library, so the widget must be
compiled with the .NET Native toolchain. A plain managed build dies on startup with
`System.BadImageFormatException` the moment `XboxGameBarWidget` is loaded — and because
that type is referenced by a field on `App`, the CLR resolves it while *loading* the type,
before the constructor runs and before any exception handler is in place. That is why all the
strong references to the SDK live in `WidgetHost.cs` and the pages hold the widget as
`object`: the type is then only loaded when a widget is actually being created.

`UseDotNetNativeToolchain` is set for the Debug configuration as well as Release, because
Debug is what you run under F5.

### Packaging

`scripts/Pack-Widget.ps1` builds the `.msix` without Visual Studio. It reads the generated
`.appxrecipe` and lays the files out exactly as it says, then packs that:

```powershell
powershell -ExecutionPolicy Bypass -File app\GameBar\scripts\Pack-Widget.ps1 `
    -Sign -Pfx cert.pfx -Password secret -Install
```

Packing the build output directory directly does **not** work: with the .NET Native
toolchain the real payload (the native bootstrapper, the native image, `ucrtbased.dll`) is
produced in `bin\<config>\ilc\`, while the managed build output next to it is not what
ships. The script handles that.

### Signing

The package is signed with a self-signed certificate. Windows will only install it if that
certificate is trusted **machine-wide**:

```powershell
certutil -addstore -f TrustedPeople GHelperGameBar.cer
```

`certutil -f` matters: adding to the `Root` store without it pops a modal warning that
blocks the command. `CurrentUser\TrustedPeople` is *not* sufficient — the deployment
service reports `0x800B0109: the root certificate of the signature must be trusted`.

### A packaging bug in Microsoft's .NET Native toolchain packages

`runtime.win10-x64.microsoft.net.native.{compiler,sharedlibrary} 2.3.15-rel-2605-2102`
register their framework appx with `<Version>2.3.60521.0</Version>`, but the appx files
they actually ship are version `2.3.60518.0`. The manifest therefore demands a minimum
that no package on any feed satisfies, and the widget cannot be installed:

```
0x80073CF3 ... it depends on a platform that cannot be found
```

The `GHelperFixNativeFrameworkVersion` target in `GHelper.GameBar.csproj` corrects the
declared version to the one that is actually shipped. Remove it once Microsoft fixes the
package.

The matching framework packages still have to be installed on the machine:

```powershell
Add-AppxPackage runtime.win10-x64.microsoft.net.native.compiler\2.2.15-rel-2605-2102\tools\Runtime\x64\Microsoft.NET.Native.Runtime.2.3.appx
Add-AppxPackage runtime.win10-x64.microsoft.net.native.sharedlibrary\2.3.15-rel-2605-2102\tools\SharedLibrary\chk\Native\Microsoft.NET.Native.Framework.Debug.2.3.appx
```

### The widget needs permission to reach G-Helper

The widget is an **AppContainer** process, and an AppContainer token does not honour an
`Everyone` ACE in a pipe's DACL — its access check compares against the package SID and
capability SIDs in the token. So G-Helper grants the widget's package SID explicitly, which
it reads from the registry:

```
HKLM\SOFTWARE\Microsoft\SecurityManager\CapAuthz\ApplicationsEx\<package full name>
    PackageSid
```

(The Game Bar documentation spells that key `ApplicationEx`; on Windows 11 it is
`ApplicationsEx`. Both are tried.) The same-user check on the server is what keeps another
local account out, since the DACL has to be open enough for AppContainers in general.

### Requirements

- **Visual Studio 2022 (Community or higher) with the "Universal Windows Platform
  development" workload.** Build Tools is *not* enough — see the trap below.
- **The Windows SDK**, and `TargetPlatformVersion` in `GHelper.GameBar.csproj` must name an
  SDK you actually have installed. It currently targets `10.0.22621.0`; lowering it does
  not fall back to an older SDK, the project simply stops resolving
  `Windows.ApplicationModel` and fails to compile.
- NuGet source `https://api.nuget.org/v3/index.json`

The main G-Helper app has none of these requirements. It builds and ships on its own.

> **Build Tools trap.** Passing `--add Microsoft.VisualStudio.Workload.Universal` to
> *Build Tools* is accepted without complaint but installs nothing. The symptom is a
> confusing `MSB4019` about a missing `Microsoft.Windows.UI.Xaml.CSharp.targets` (with
> `dotnet build`) or `CS0518: predefined type 'System.Object' is not defined` on every line
> (with VS MSBuild), with no mention of a missing workload. Install full Visual Studio if
> you hit either.

### Steps

1. Open `app/GameBar/GHelper.GameBar.csproj` in Visual Studio.
2. Let it restore `Microsoft.Gaming.XboxGameBar` (the SDK) and
   `Microsoft.NETCore.UniversalWindowsPlatform`.
3. Select a **signing certificate**. The manifest declares `Publisher="CN=G-Helper"`; if you
   use a different certificate subject, update that attribute to match, or the package will
   fail to install.
4. **F5** to deploy.

Press **Win + G**, open the widget grid, and pick **G-Helper**.

### Checking your changes without the UWP workload

`scripts/Check-WidgetSources.ps1` compiles the widget's C# against hand-written WinRT stubs
on any machine with just the .NET SDK. It pins `LangVersion` to 7.3 (the ceiling the widget
actually builds at under .NET Native) and regenerates the per-page partial that XAML codegen
would normally produce — so a control referenced from code-behind but missing from the XAML
fails the build.

```powershell
powershell -ExecutionPolicy Bypass -File app/GameBar/scripts/Check-WidgetSources.ps1
```

It does not validate the XAML itself, the manifest, or the assembly/proxy registration.
Those are only checked by a real UWP build, so run this as a fast pre-check rather than a
substitute.

---

## Testing the bridge

The widget cannot be run without installing its package, so the IPC layer is covered by an
integration test that drives the **real** `GameBarServer` and the **real** `GHelperClient.cs`
(the file that actually ships, compiled from source rather than copied).

```powershell
# 1. Self-contained: starts its own server, no hardware needed
dotnet run --project app/GameBar/scripts/BridgeTest

# 2. Live: against a G-Helper you have already started
#    (build first - building GHelper.csproj runs its KillRunningGHelper target,
#     which signals a running instance to exit)
dotnet build app/GameBar/scripts/BridgeTest
Start-Process app/bin/x64/Debug/net10.0-windows/GHelper.exe
dotnet run --project app/GameBar/scripts/BridgeTest -- --live
```

What they cover: the pipe name and security descriptor, the same-user check, line framing
over a byte-mode pipe, `PING`/`STATE`/`SET`, protocol-level rejection of bad input, the
"never report success for a command that did not run" contract, resilience to garbage, and
reconnection after the server stops and restarts.

`--live` additionally drives every `SET` through the real settings form and reads real
hardware state. It re-issues values that are *already* set, so it never changes the machine's
power mode, refresh rate or toggles.

Not covered: rendering inside Game Bar itself. That needs the package installed and Win+G.

### Driving the desktop

`scripts/GameBarInteraction.ps1` drives the real Game Bar so the widget can be exercised
end to end:

```powershell
.\app\GameBar\scripts\GameBarInteraction.ps1 -ListWidgets          # what Game Bar can see
.\app\GameBar\scripts\GameBarInteraction.ps1 -OpenGameBar          # open Game Bar
.\app\GameBar\scripts\GameBarInteraction.ps1 -Capture shot.png     # screenshot the desktop
.\app\GameBar\scripts\GameBarInteraction.ps1 -Verify               # full check + screenshot
```

`-ListWidgets` uses `AppExtensionCatalog.Open("microsoft.gameBarUIExtension")` - the same
enumeration Game Bar itself performs, so it answers "can Game Bar see this widget?" without
needing the GUI.

Two things worth knowing if you script this:

- **Game Bar renders on a DirectComposition surface** that neither `EnumWindows` nor UI
  Automation can reach. `CopyFromScreen` captures it fine; `PrintWindow` does not. Find the
  window with `WindowFromPoint` under the widget icons.
- **Mouse injection does not work over an RDP session.** The client owns the cursor and
  re-asserts its position, so `SendInput` reports success but the pointer never moves.
  Keyboard injection does work, and UI Automation's `InvokePattern` works on the widget's
  own buttons - which is enough to press them programmatically and confirm the change
  reaches G-Helper.

### Other scripts

- `scripts/Generate-Assets.ps1` regenerates the package and widget icon PNGs from
  `app/favicon.ico`. Re-run it if the logo changes.

### Notes

- The project is intentionally **not** referenced from `app/GHelper.sln`. CI runs
  `dotnet build app/GHelper.sln` with no UWP workload and would fail. It is excluded from the
  WinForms project's default globs in `app/GHelper.csproj` so the two builds stay separate.
- `GHelper.exe` must be running for the widget to show anything. If it is not, the widget
  shows *"G-Helper not running"* and retries every second.
- `<LangVersion>7.3</LangVersion>` is set deliberately. `Shared/GameBarProtocol.cs` compiles
  into both this project and the .NET 10 app, so it must stay inside the intersection of
  both; without the pin a C# 8+ construct would build here and break under .NET Native.
- `test` a widget while attached to a debugger. Game Bar has special requirements for
  debugging widgets — see
  [Testing and debugging your widget](https://learn.microsoft.com/en-us/xbox/game-bar/guide/testing-debugging).

---

## Distribution

To distribute the widget:

- **Sideload:** publish an `.msix` signed with a certificate trusted on the target machine,
  then run `Add-AppxPackage` or double-click the package.
- **Microsoft Store:** publish as a UWP app. The widget needs a store policy exception if it
  links to, or directly communicates with, the unsideloaded G-Helper desktop app — see
  [Communicating with desktop apps](https://learn.microsoft.com/en-us/xbox/game-bar/guide/communicating-apps).
  Alternatively publish the desktop app and the widget in a single package using
  [Desktop Bridge](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-root).

Nothing in CI builds or publishes the widget today; `build.yml` and `release.yml` only handle
`GHelper.exe`.