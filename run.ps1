#requires -Version 5.1
<# Convenience wrapper: build if needed, then launch the game with SMLoader. #>
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GameArgs)

$ErrorActionPreference = 'Stop'
$launcher = Join-Path $PSScriptRoot 'dist\SMLoader.Launcher.exe'

if (-not (Test-Path $launcher)) {
    & (Join-Path $PSScriptRoot 'build.ps1')
}

& $launcher @GameArgs
