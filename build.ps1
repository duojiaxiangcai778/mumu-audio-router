# Build MuMuAudioRouter with the built-in .NET Framework 4.8 csc.exe (no SDK needed)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src\MuMuAudioRouter.cs'
$out  = Join-Path $root 'MuMuAudioRouter.exe'

$fw64 = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$fw32 = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'

$csc = Join-Path $fw64 'csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $fw32 'csc.exe' }
if (-not (Test-Path $csc)) { throw "csc.exe not found (.NET Framework 4.x required)" }

$fwDir = Split-Path -Parent $csc

$refNames = @('System.dll', 'System.Core.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Management.dll')
$cscArgs = New-Object System.Collections.ArrayList
[void]$cscArgs.Add('/nologo')
[void]$cscArgs.Add('/target:winexe')
[void]$cscArgs.Add('/optimize+')
[void]$cscArgs.Add('/platform:anycpu')
[void]$cscArgs.Add('/codepage:65001')
[void]$cscArgs.Add('/warn:4')
foreach ($n in $refNames) {
    $p = Join-Path $fwDir $n
    if (Test-Path $p) { [void]$cscArgs.Add('/reference:"' + $p + '"') }
    else { [void]$cscArgs.Add('/reference:' + $n) }
}
[void]$cscArgs.Add('/out:"' + $out + '"')
[void]$cscArgs.Add('"' + $src + '"')

Write-Host 'Compiling...' -ForegroundColor Cyan
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compile failed, exit code $LASTEXITCODE" }

$fi = Get-Item $out
Write-Host ("Done: {0}  ({1:N1} KB)" -f $fi.FullName, ($fi.Length / 1KB)) -ForegroundColor Green
