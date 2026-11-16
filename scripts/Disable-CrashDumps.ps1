#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$StateDirectory = (Join-Path $env:LOCALAPPDATA 'FeedPeek\CrashDumpConfiguration')
)

$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) {
    throw 'Run this script in 64-bit PowerShell.'
}

$registryPath = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\FeedPeek.exe'
$statePath = Join-Path $StateDirectory 'state.json'
$backupPath = Join-Path $StateDirectory 'FeedPeek.exe.reg'
if (-not (Test-Path -LiteralPath $statePath)) {
    throw 'No saved baseline was found. Use the same Windows account and StateDirectory as Enable-CrashDumps.ps1.'
}

$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
if ($state.ExistedBefore -and -not (Test-Path -LiteralPath $backupPath)) {
    throw 'The original registry backup is missing. No configuration has been changed.'
}

# This exact per-application key is the only registry key removed or restored.
if (Test-Path -LiteralPath $registryPath) {
    Remove-Item -LiteralPath $registryPath -Recurse -Force
}

if ($state.ExistedBefore) {
    & reg.exe import $backupPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not restore the original configuration. The backup is retained at $backupPath."
    }
}

Remove-Item -LiteralPath $statePath
if (Test-Path -LiteralPath $backupPath) {
    Remove-Item -LiteralPath $backupPath
}

Write-Host 'The original FeedPeek crash dump configuration has been restored. Existing dump files were kept.'
