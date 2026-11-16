param(
    [string]$SourceSvg = (Join-Path $PSScriptRoot '../src/FeedPeek.App/Assets/FeedPeek.svg')
)

$ErrorActionPreference = 'Stop'
$magick = (Get-Command magick -ErrorAction Stop).Source
$assets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/FeedPeek.App/Assets'))
$master = Join-Path ([IO.Path]::GetTempPath()) "FeedPeek-icon-$([guid]::NewGuid()).png"

function Invoke-Magick {
    & $magick @args
    if ($LASTEXITCODE -ne 0) { throw "ImageMagick failed: $args" }
}

try {
    # Render the vector at high resolution before downsampling, including gradients and alpha.
    Invoke-Magick -background none -density 3072 $SourceSvg -colorspace sRGB -depth 8 "PNG32:$master"

    $images = @(
        @{ Name = 'StoreLogo.png'; Size = 50; Canvas = '50x50' }
        @{ Name = 'LockScreenLogo.scale-200.png'; Size = 48; Canvas = '48x48' }
        @{ Name = 'Square44x44Logo.scale-200.png'; Size = 88; Canvas = '88x88' }
        @{ Name = 'Square150x150Logo.scale-200.png'; Size = 300; Canvas = '300x300' }
        @{ Name = 'Wide310x150Logo.scale-200.png'; Size = 300; Canvas = '620x300' }
        @{ Name = 'SplashScreen.scale-200.png'; Size = 480; Canvas = '1240x600' }
    )
    foreach ($size in @(16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256)) {
        $images += @{ Name = "Square44x44Logo.targetsize-${size}_altform-unplated.png"; Size = $size; Canvas = "${size}x${size}" }
    }
    foreach ($image in $images) {
        Invoke-Magick $master -filter Lanczos -resize "$($image.Size)x$($image.Size)" -background none -gravity center -extent $image.Canvas -strip "PNG32:$(Join-Path $assets $image.Name)"
    }

    Invoke-Magick $master -define 'icon:auto-resize=256,128,96,64,48,40,32,24,20,16' (Join-Path $assets 'AppIcon.ico')
    & (Join-Path $PSScriptRoot 'Update-TrayIcons.ps1')
}
finally {
    Remove-Item -LiteralPath $master -ErrorAction SilentlyContinue
}
