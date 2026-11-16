param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '../src/FeedPeek.App/Assets/Tray')
)

$ErrorActionPreference = 'Stop'
$magick = (Get-Command magick -ErrorAction Stop).Source
$assets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/FeedPeek.App/Assets'))

# Preserve the supplied artwork at each size instead of resizing the smallest image.
foreach ($variant in @(
    @{ Color = 'black'; Output = 'TrayIconDark.ico' }
    @{ Color = 'white'; Output = 'TrayIconLight.ico' }
)) {
    $frames = foreach ($size in @(16, 20, 24, 32, 40, 48, 64)) {
        Join-Path $SourceDirectory "$($variant.Color)/FeedPeek-tray-$($variant.Color)-$size.png"
    }
    & $magick @frames (Join-Path $assets $variant.Output)
    if ($LASTEXITCODE -ne 0) { throw "Failed to generate $($variant.Output)" }
}
