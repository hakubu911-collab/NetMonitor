param([switch]$Package)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$candidates = @(
  "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
  "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw 'Microsoft .NET Framework 4.x C# compiler (csc.exe) was not found.' }
$outDir = Join-Path $root 'dist\NetMonitor'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$exe = Join-Path $outDir 'NetMonitor.exe'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
  "/out:$exe" "/win32icon:$(Join-Path $root 'resources\app.ico')" `
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  (Join-Path $root 'src\NetMonitor.cs')
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE" }
Copy-Item (Join-Path $root 'README.md') (Join-Path $outDir '使用说明.txt') -Force
Copy-Item (Join-Path $root 'LICENSE') $outDir -Force
if ($Package) {
  $zip = Join-Path $root 'dist\NetMonitor-v1.0.0-windows.zip'
  if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
  Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip -CompressionLevel Optimal
  $digest = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
  Set-Content -LiteralPath (Join-Path $root 'dist\SHA256SUMS.txt') -Value "$digest  NetMonitor-v1.0.0-windows.zip" -Encoding ascii
  Write-Output "Created $zip"
}
Write-Output "Created $exe"


