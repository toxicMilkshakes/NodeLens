$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft\NET\Framework64\v4.0.30319'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskOut = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $taskOut -Force | Out-Null
$taskRefs = @('System.dll','System.Core.dll','System.Drawing.dll','System.Web.Extensions.dll','System.Security.dll','System.Windows.Forms.dll','System.Xaml.dll') | ForEach-Object { '/r:' + (Join-Path $taskFramework $_) }
$taskRefs += @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll') | ForEach-Object { '/r:' + (Join-Path "$taskFramework\WPF" $_) }
Push-Location (Join-Path $PSScriptRoot 'src')
try {
    & "$taskFramework\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /win32manifest:app.manifest /win32icon:NodeLens.ico "/out:$taskOut\NodeLens.exe" /resource:Widget.xaml,NodeLens.Widget.xaml @taskRefs Core.cs Monitor.cs Ui.cs TaskbarPanel.cs Program.cs
    if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
    Copy-Item NodeLens.exe.config $taskOut -Force
    & "$taskFramework\csc.exe" /nologo /target:exe /platform:x64 "/out:$taskOut\Tests.exe" "/r:$taskOut\NodeLens.exe" @taskRefs (Join-Path $PSScriptRoot 'tests\Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
} finally { Pop-Location }
