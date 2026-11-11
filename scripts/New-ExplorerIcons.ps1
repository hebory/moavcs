[CmdletBinding()]
param([switch]$Preview)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$projectRoot = Split-Path $PSScriptRoot -Parent
$assetRoot = Join-Path $projectRoot 'assets\explorer-icons'
$icons = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $assetRoot 'icons.xaml')))
$icons.Add('moavcs', [Windows.Markup.XamlReader]::Parse(
    [IO.File]::ReadAllText((Join-Path $projectRoot 'assets\branding\icon.xaml'))))
$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)

function Render-IconPng($control, [int]$pixels) {
    $control.Measure([Windows.Size]::new($control.Width, $control.Height))
    $control.Arrange([Windows.Rect]::new(0, 0, $control.Width, $control.Height))
    $control.UpdateLayout()
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.DrawRectangle([Windows.Media.VisualBrush]::new($control), $null,
        [Windows.Rect]::new(0, 0, $pixels, $pixels))
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(
        $pixels, $pixels, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() } finally { $stream.Dispose() }
}

foreach ($name in @($icons.Keys | Sort-Object)) {
    $frames = foreach ($size in $sizes) { ,(Render-IconPng $icons[$name] $size) }
    $path = Join-Path $assetRoot "$name.ico"
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($path))
    try {
        # ICONDIR, then one ICONDIRENTRY per PNG frame. Zero means 256 pixels.
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose() }
    Write-Host "Created $path"
}

if ($Preview) {
    $panel = [Windows.Controls.StackPanel]::new()
    $panel.Orientation = 'Horizontal'
    foreach ($background in @('#FFFFFF', '#242424')) {
        $column = [Windows.Controls.StackPanel]::new()
        $column.Width = 300
        $column.Background = [Windows.Media.BrushConverter]::new().ConvertFromString($background)
        foreach ($name in @('moavcs', 'commit', 'update', 'push', 'diff', 'blame', 'log', 'status', 'open', 'clone', 'init', 'remote', 'settings')) {
            $row = [Windows.Controls.StackPanel]::new()
            $row.Orientation = 'Horizontal'; $row.Height = 40
            foreach ($size in @(16, 24, 32)) {
                $image = [Windows.Controls.Image]::new()
                $image.Width = $size; $image.Height = $size; $image.Margin = '10,0,10,0'
                $decoder = [Windows.Media.Imaging.IconBitmapDecoder]::new(
                    [Uri]::new((Join-Path $assetRoot "$name.ico")), 'PreservePixelFormat', 'OnLoad')
                $image.Source = $decoder.Frames | Where-Object PixelWidth -EQ $size | Select-Object -First 1
                [void]$row.Children.Add($image)
            }
            $label = [Windows.Controls.TextBlock]::new()
            $label.Text = $name; $label.VerticalAlignment = 'Center'; $label.FontSize = 14
            $label.Foreground = if ($background -eq '#FFFFFF') { [Windows.Media.Brushes]::Black } else { [Windows.Media.Brushes]::White }
            [void]$row.Children.Add($label); [void]$column.Children.Add($row)
        }
        [void]$panel.Children.Add($column)
    }
    $size = [Windows.Size]::new(600, 520)
    $panel.Measure($size); $panel.Arrange([Windows.Rect]::new($size)); $panel.UpdateLayout()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(600, 520, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($panel)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $previewRoot = Join-Path $projectRoot 'artifacts\screenshots'
    [void][IO.Directory]::CreateDirectory($previewRoot)
    $stream = [IO.File]::Create((Join-Path $previewRoot 'explorer-icons.png'))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}
