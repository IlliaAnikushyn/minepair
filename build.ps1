param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows x64 with .NET Framework 4.8 is required.' }
$out = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$refs = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Security.dll','System.ServiceProcess.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output','/codepage:65001',('/out:' + (Join-Path $out 'MinePair.exe')))
$compilerArgs += $refs | ForEach-Object { '/reference:' + $_ }
$compilerArgs += Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
[IO.File]::WriteAllText((Join-Path $out 'Network.ps1'), [IO.File]::ReadAllText((Join-Path $root 'src\Network.ps1'), [Text.Encoding]::UTF8), [Text.UTF8Encoding]::new($true))
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $out -Force
if ($Test) {
    $testProcess = Start-Process -FilePath (Join-Path $out 'MinePair.exe') -ArgumentList '--self-test' -Wait -PassThru
    if ($testProcess.ExitCode -ne 0) { throw 'Self-tests failed; see dist\artifacts\tests.txt' }
    Get-Content -LiteralPath (Join-Path $out 'artifacts\tests.txt')
}
Write-Output (Join-Path $out 'MinePair.exe')
