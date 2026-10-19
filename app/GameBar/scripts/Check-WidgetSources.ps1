<#
.SYNOPSIS
    Type-checks the Xbox Game Bar widget sources without the UWP workload.

.DESCRIPTION
    The widget is a UWP project, so building it normally needs Visual Studio with the
    "Universal Windows Platform development" workload plus the Windows SDK. This script
    gives contributors on a plain .NET SDK machine a real compile of the widget's C#:

      * Compiles every widget .cs file together with Shared/GameBarProtocol.cs at
        LangVersion 7.3, which is the ceiling the widget actually builds at (.NET Native).
        This is what keeps the shared protocol file honest on both sides.

      * Substitutes hand-written stubs for the WinRT / Microsoft.Gaming.XboxGameBar types,
        since the real ones only exist through SDK metadata.

      * Regenerates the per-page partial class that XAML codegen normally produces,
        derived from the x:Name attributes in each .xaml. That is the point of the exercise:
        a control referenced from code-behind but missing from the XAML is a build error,
        and this catches it without a full UWP toolchain.

    It does NOT validate the XAML itself, the manifest, or the assembly/proxy registration.
    For those, build the project in Visual Studio.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File app/GameBar/scripts/Check-WidgetSources.ps1
#>
[CmdletBinding()]
param(
    # Defaults to the repository root. Resolved in the body rather than here, because
    # $PSScriptRoot is not populated inside a param() block when run via -File.
    [string]$RepoRoot
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
}

$gameBar = Join-Path $RepoRoot "app\GameBar"
$work    = Join-Path ([IO.Path]::GetTempPath()) ("ghelper-widgetcheck-" + [Guid]::NewGuid().ToString("N"))

New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    # ── Copy the sources under test ────────────────────────────────────────
    # Every .cs in the widget project, so a new file cannot be silently left out of the
    # check. The per-page partials are generated below instead.
    Get-ChildItem $gameBar -Filter *.cs | ForEach-Object {
        Copy-Item $_.FullName $work -Force
    }
    Copy-Item (Join-Path $RepoRoot "app\Shared\GameBarProtocol.cs") $work -Force
    Copy-Item (Join-Path $PSScriptRoot "WinRtStubs.cs") $work -Force

    # ── Stand in for XAML codegen ──────────────────────────────────────────
    # NOTE: this element map is deliberately incomplete. It only needs to cover controls the
    # code-behind actually names. An element absent here falls back to TextBlock, which is
    # enough to catch "referenced from code but not in the XAML" without needing every UWP
    # control to be listed. It does NOT validate the XAML markup - a control that is not in
    # the UWP control set (UniformGrid, say) will not be caught here, only by a real build.
    #
    # The values are fully qualified, because the generated fields have to match the stubs
    # exactly: a named brush lives in Windows.UI.Xaml.Media, not in Windows.UI.Xaml.Controls.
    $elementTypes = @{
        'TextBlock'     = 'Windows.UI.Xaml.Controls.TextBlock'
        'Button'        = 'Windows.UI.Xaml.Controls.Button'
        'ToggleSwitch'  = 'Windows.UI.Xaml.Controls.ToggleSwitch'
        'StackPanel'    = 'Windows.UI.Xaml.Controls.StackPanel'
        'Grid'          = 'Windows.UI.Xaml.Controls.Grid'
        'ScrollViewer'  = 'Windows.UI.Xaml.Controls.ScrollViewer'
        'Border'        = 'Windows.UI.Xaml.Controls.Border'
        'ProgressBar'   = 'Windows.UI.Xaml.Controls.ProgressBar'
        'Ellipse'       = 'Windows.UI.Xaml.Shapes.Ellipse'
        'Rectangle'     = 'Windows.UI.Xaml.Shapes.Rectangle'
        'SolidColorBrush' = 'Windows.UI.Xaml.Media.SolidColorBrush'
    }

    foreach ($page in 'GHelperWidget', 'GHelperSettingsWidget') {
        $xaml = Get-Content (Join-Path $gameBar "$page.xaml") -Raw

        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add("// Generated stand-in for XAML codegen. Do not edit - see Check-WidgetSources.ps1.")
        $lines.Add("namespace GHelper.GameBar")
        $lines.Add("{")
        $lines.Add("    public sealed partial class $page")
        $lines.Add("    {")
        $lines.Add("        public void InitializeComponent() { }")

        foreach ($match in [regex]::Matches($xaml, 'x:Name="(\w+)"')) {
            $name = $match.Groups[1].Value
            $type = 'Windows.UI.Xaml.Controls.TextBlock'
            $open = [regex]::Match($xaml, "<(?<t>\w+)[^>]*x:Name=`"$([regex]::Escape($name))`"")
            if ($open.Success -and $elementTypes.ContainsKey($open.Groups['t'].Value)) {
                $type = $elementTypes[$open.Groups['t'].Value]
            }
            $lines.Add("        public $type $name;")
        }

        $lines.Add("    }")
        $lines.Add("}")
        Set-Content (Join-Path $work "$page.g.cs") ($lines -join "`n") -Encoding UTF8
    }

    # ── Project ────────────────────────────────────────────────────────────
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <Nullable>disable</Nullable>
    <AssemblyName>WidgetCheck</AssemblyName>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <NoWarn>CS0067;CS0169;CS0414;CS0649;CS0108</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="WinRtStubs.cs" />
    <Compile Include="GHelperWidget.g.cs" />
    <Compile Include="GHelperSettingsWidget.g.cs" />
    <Compile Include="App.xaml.cs" />
    <Compile Include="GHelperClient.cs" />
    <Compile Include="GHelperWidget.xaml.cs" />
    <Compile Include="GHelperSettingsWidget.xaml.cs" />
    <Compile Include="WidgetHost.cs" />
    <Compile Include="WidgetLog.cs" />
    <Compile Include="GameBarProtocol.cs" />
  </ItemGroup>
</Project>
"@
    Set-Content (Join-Path $work "WidgetCheck.csproj") $project -Encoding UTF8

    Write-Output "Checking widget sources at C# 7.3 (work dir: $work)"
    dotnet build (Join-Path $work "WidgetCheck.csproj") -c Debug --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Widget source check FAILED"
    }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}