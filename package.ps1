param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
& (Join-Path $PSScriptRoot 'bin\Tests.exe') (Join-Path $PSScriptRoot 'bin\test-results.txt')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; package was not created.' }
$packageName = "NodeLens-$Version-Windows-x64"
$packageRoot = Join-Path $PSScriptRoot 'outputs\releases'
$packageStage = Join-Path $packageRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $packageStage -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'bin\NodeLens.exe') $packageStage
Copy-Item (Join-Path $PSScriptRoot 'bin\NodeLens.exe.config') $packageStage
Copy-Item (Join-Path $PSScriptRoot 'docs\使用说明.txt') $packageStage
$packageZip = Join-Path $packageRoot "$packageName.zip"
Compress-Archive -LiteralPath (Join-Path $packageStage 'NodeLens.exe'),(Join-Path $packageStage 'NodeLens.exe.config'),(Join-Path $packageStage '使用说明.txt') -DestinationPath $packageZip -Force
$packageHash = (Get-FileHash -LiteralPath $packageZip -Algorithm SHA256).Hash.ToLowerInvariant()
"$packageHash  $packageName.zip" | Set-Content -LiteralPath (Join-Path $packageRoot "$packageName.sha256") -Encoding ASCII
Write-Output "Package: $packageZip"
