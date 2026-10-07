$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework C# compiler was not found.' }
$releaseDir = Join-Path $projectDir 'release'
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap(32, 32)
$drawing = [System.Drawing.Graphics]::FromImage($bitmap)
$drawing.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$drawing.Clear([System.Drawing.Color]::Transparent)
$accentBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40, 100, 72))
$whitePen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 2)
$roundPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$roundPath.AddArc(2, 2, 10, 10, 180, 90)
$roundPath.AddArc(20, 2, 10, 10, 270, 90)
$roundPath.AddArc(20, 20, 10, 10, 0, 90)
$roundPath.AddArc(2, 20, 10, 10, 90, 90)
$roundPath.CloseFigure()
$drawing.FillPath($accentBrush, $roundPath)
$drawing.DrawRectangle($whitePen, 6, 8, 20, 13)
$drawing.DrawLine($whitePen, 16, 21, 16, 25)
$drawing.DrawLine($whitePen, 11, 25, 21, 25)
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$iconPath = Join-Path $projectDir 'app.ico'
$stream = [System.IO.File]::Create($iconPath)
try { $icon.Save($stream) } finally { $stream.Dispose(); $icon.Dispose(); $drawing.Dispose(); $bitmap.Dispose(); $accentBrush.Dispose(); $whitePen.Dispose(); $roundPath.Dispose() }
$outputPath = Join-Path $releaseDir '屏幕省电时段.exe'
$sourceFiles = Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' | ForEach-Object FullName
$frameworkDir = Split-Path -Parent $compilerPath
$wpfDir = Join-Path $frameworkDir 'WPF'
$compilerArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output',
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Xml.dll',
    "/r:$(Join-Path $frameworkDir 'System.Xaml.dll')", "/r:$(Join-Path $wpfDir 'WindowsBase.dll')",
    "/r:$(Join-Path $wpfDir 'PresentationCore.dll')", "/r:$(Join-Path $wpfDir 'PresentationFramework.dll')",
    "/resource:$(Join-Path $projectDir 'src\AppWindow.xaml'),ScreenTimeoutScheduler.AppWindow.xaml",
    "/out:$outputPath", "/win32icon:$iconPath", "/win32manifest:$(Join-Path $projectDir 'app.manifest')") + $sourceFiles
& $compilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectDir '使用说明.txt') -Destination $releaseDir -Force
Write-Output "Built: $outputPath"
