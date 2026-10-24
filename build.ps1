param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows x64 with .NET Framework 4.8 is required.' }
$out = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$refs = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Security.dll','System.ServiceProcess.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Numerics.dll')
# Single-file program: the network script is embedded and written to Program Files by the elevated installer.
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output','/codepage:65001',('/out:' + (Join-Path $out 'MinePair.exe')),('/resource:' + (Join-Path $root 'src\Network.ps1') + ',MinePair.Network.ps1'))
$compilerArgs += $refs | ForEach-Object { '/reference:' + $_ }
$compilerArgs += Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
$stale = Join-Path $out 'Network.ps1'
if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale }
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $out -Force
if ($Test) {
    $testProcess = Start-Process -FilePath (Join-Path $out 'MinePair.exe') -ArgumentList '--self-test' -Wait -PassThru
    Get-Content -LiteralPath (Join-Path $out 'artifacts\tests.txt')
    if ($testProcess.ExitCode -ne 0) { throw 'Self-tests failed; see dist\artifacts\tests.txt' }
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\Network.Tests.ps1') -Script (Join-Path $root 'src\Network.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Network script tests failed' }
}
Write-Output (Join-Path $out 'MinePair.exe')
