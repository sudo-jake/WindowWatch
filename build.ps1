param([string]$Destination = (Join-Path $PSScriptRoot 'WindowWatch.exe'), [string]$TestSource)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$metadata = Join-Path $env:WINDIR 'System32\WinMetadata'
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.dll','System.Runtime.InteropServices.WindowsRuntime.dll','System.Runtime.WindowsRuntime.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
$references += @('Windows.Foundation.winmd','Windows.Graphics.winmd','Windows.Storage.winmd','Windows.System.winmd') | ForEach-Object { '/r:' + (Join-Path $metadata $_) }
$sources = @((Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs').FullName)
$entry = @()
if ($TestSource) {
    $sources += $TestSource
    $entry += '/main:WindowWatch.Diagnostics'
}
$viewer = '/resource:' + (Join-Path $PSScriptRoot 'viewer.html') + ',viewer.html'
$sessionPage = '/resource:' + (Join-Path $PSScriptRoot 'session.html') + ',session.html'
$icon = Join-Path $PSScriptRoot 'WindowWatch.ico'
& (Join-Path $framework 'csc.exe') /nologo /optimize+ /unsafe+ /platform:x64 /target:winexe "/out:$Destination" "/win32icon:$icon" "/resource:$icon,WindowWatch.WindowWatch.ico" $viewer $sessionPage @entry @references @sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
