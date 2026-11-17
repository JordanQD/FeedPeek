$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$commit = '8678485b42e8da21e12ff938274dec9d7a910a69'
$framework = 'net10.0-windows10.0.26100.0'
$downloadRoot = Join-Path $repoRoot '.verify-obj/df-source'
$extractRoot = Join-Path $downloadRoot ([Guid]::NewGuid().ToString('N'))
$archive = Join-Path $extractRoot 'DesktopFlyouts.zip'
$intermediateRoot = (Join-Path $repoRoot '.verify-obj/df-arm64').Replace('\', '/') + '/'
$outputRoot = (Join-Path $repoRoot '.verify-bin/df-arm64').Replace('\', '/') + '/'
$destination = Join-Path $repoRoot 'third_party/DesktopFlyouts.WinUI/2.2.0/arm64'

New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
Invoke-WebRequest "https://codeload.github.com/0x5bfa/DesktopFlyouts/zip/$commit" -OutFile $archive -UseBasicParsing
Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot
$sourceRoot = Join-Path $extractRoot "DesktopFlyouts-$commit"
$projectFile = Join-Path $sourceRoot 'src/DesktopFlyouts.WinUI/DesktopFlyouts.WinUI.csproj'
if (-not (Test-Path -LiteralPath $projectFile)) {
    throw "The pinned upstream archive did not contain the expected project: $projectFile"
}

$buildProperties = @(
    "-p:TargetFrameworks=$framework",
    '-p:Platform=arm64',
    '-p:PlatformTarget=ARM64',
    '-r', 'win-arm64',
    '-p:PublishAot=false',
    '-p:WindowsSdkPackageVersion=10.0.26100.57',
    '-p:GeneratePackageOnBuild=false',
    "-p:BaseIntermediateOutputPath=$intermediateRoot",
    "-p:BaseOutputPath=$outputRoot"
)

Push-Location $sourceRoot
try {
    dotnet restore $projectFile @buildProperties
    if ($LASTEXITCODE -ne 0) {
        throw "DesktopFlyouts ARM64 restore failed (exit $LASTEXITCODE)."
    }
    dotnet build $projectFile -c Release @buildProperties --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "DesktopFlyouts ARM64 build failed (exit $LASTEXITCODE)."
    }
}
finally {
    Pop-Location
}

$builtRoot = Join-Path $outputRoot "arm64/Release/$framework/win-arm64"
$library = Join-Path $builtRoot 'DesktopFlyouts.WinUI.dll'
$resourceIndex = Join-Path $builtRoot 'DesktopFlyouts.WinUI.pri'
foreach ($file in @($library, $resourceIndex)) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Expected ARM64 runtime asset was not produced: $file"
    }
}

$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($library))
try {
    $reader.BaseStream.Position = 0x3c
    $peOffset = $reader.ReadInt32()
    $reader.BaseStream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0xAA64) {
        throw 'The built DesktopFlyouts DLL is not an ARM64 PE image.'
    }
}
finally {
    $reader.Dispose()
}

if ([Reflection.AssemblyName]::GetAssemblyName($library).Version -ne [Version]'2.2.0.0') {
    throw 'The built DesktopFlyouts assembly version does not match 2.2.0.0.'
}

New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -LiteralPath $library -Destination $destination -Force
Copy-Item -LiteralPath $resourceIndex -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Destination (Join-Path $destination 'LICENSE') -Force
Write-Host "ARM64 DesktopFlyouts 2.2.0 runtime assets copied to $destination"
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $destination 'DesktopFlyouts.WinUI.dll'), (Join-Path $destination 'DesktopFlyouts.WinUI.pri')
