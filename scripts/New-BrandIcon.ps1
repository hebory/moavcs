[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$projectRoot = Split-Path $PSScriptRoot -Parent
$source = Join-Path $projectRoot 'assets\branding\icon.xaml'
$output = Join-Path $projectRoot 'icon.png'

# Render the same orange monogram used by the app, with transparent corners.
$control = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText($source))
$size = [Windows.Size]::new(512, 512)
$control.Measure($size)
$control.Arrange([Windows.Rect]::new($size))
$control.UpdateLayout()
$bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(
    512, 512, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($control)
$encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream = [IO.File]::Create($output)
try { $encoder.Save($stream) } finally { $stream.Dispose() }
Write-Host "Created $output (512 x 512 PNG)"
