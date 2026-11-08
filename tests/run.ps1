param([string]$Results = (Join-Path ([IO.Path]::GetTempPath()) ('WindowWatch-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Results) { throw 'Choose an unused results directory.' }
$null = New-Item -ItemType Directory -Path $Results
$Results = (Resolve-Path -LiteralPath $Results).Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$target = Join-Path $Results 'test-window.exe'
$checks = Join-Path $Results 'checks.exe'
& $compiler /nologo /target:winexe "/out:$target" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'TestWindow.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test window build failed.' }
& (Join-Path (Split-Path $PSScriptRoot -Parent) 'build.ps1') -Destination $checks -TestSource (Join-Path $PSScriptRoot 'Checks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test app build failed.' }
$demo = Start-Process -FilePath $target -WindowStyle Hidden -PassThru -ArgumentList ('"' + (Join-Path $Results 'go') + '"')
$preferencesKey = 'Software\WindowWatch-test-' + [guid]::NewGuid().ToString('N')
try {
    foreach ($phase in 'capture','restart') {
        $check = Start-Process -FilePath $checks -WindowStyle Hidden -PassThru -ArgumentList ('"' + $Results + '"', $preferencesKey, $phase)
        if (-not $check.WaitForExit(45000)) { $check.Kill(); throw "$phase validation timed out." }
        $report = if ($phase -eq 'restart') { 'restart.log' } else { 'result.log' }
        Get-Content -LiteralPath (Join-Path $Results $report)
        if ($check.ExitCode -ne 0) { throw "Validation failed. See $Results" }
    }
    Write-Output "Capture artifacts are in $Results"
} finally {
    if (-not $demo.HasExited) { $demo.Kill() }
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($preferencesKey, $false)
}
