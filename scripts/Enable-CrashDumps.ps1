#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$DumpFolder = (Join-Path $env:LOCALAPPDATA 'CrashDumps'),
    [string]$StateDirectory = (Join-Path $env:LOCALAPPDATA 'FeedPeek\CrashDumpConfiguration')
)

$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) {
    throw 'Run this script in 64-bit PowerShell.'
}

$registryPath = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\FeedPeek.exe'
$nativeRegistryPath = 'HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\FeedPeek.exe'
$statePath = Join-Path $StateDirectory 'state.json'
$backupPath = Join-Path $StateDirectory 'FeedPeek.exe.reg'

$DumpFolder = [IO.Path]::GetFullPath($DumpFolder)
New-Item -ItemType Directory -Path $DumpFolder -Force | Out-Null
New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null

# Keep the original baseline when enabling again or changing the dump folder.
if (-not (Test-Path -LiteralPath $statePath)) {
    $existedBefore = Test-Path -LiteralPath $registryPath
    if ($existedBefore) {
        & reg.exe export $nativeRegistryPath $backupPath /y | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not back up the existing FeedPeek crash dump configuration.'
        }
    }

    @{ ExistedBefore = $existedBefore } | ConvertTo-Json |
        Set-Content -LiteralPath $statePath -Encoding UTF8
}

if (-not (Test-Path -LiteralPath $registryPath)) {
    New-Item -Path $registryPath -Force | Out-Null
}
New-ItemProperty -LiteralPath $registryPath -Name DumpFolder -PropertyType ExpandString -Value $DumpFolder -Force | Out-Null
New-ItemProperty -LiteralPath $registryPath -Name DumpType -PropertyType DWord -Value 2 -Force | Out-Null
New-ItemProperty -LiteralPath $registryPath -Name DumpCount -PropertyType DWord -Value 3 -Force | Out-Null

Write-Host "FeedPeek full crash dumps enabled: $DumpFolder (maximum 3 files)."
Write-Host "Original per-app configuration saved in: $StateDirectory"
Get-ItemProperty -LiteralPath $registryPath | Select-Object DumpFolder, DumpType, DumpCount
