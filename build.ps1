#requires -Version 5.1
<#
    Builds the native shim (CMake/MSVC) and the managed projects, staging
    everything into dist\ in the layout the launcher expects:

        dist\SMLoader.Launcher.exe
        dist\SMLoader.Shim.dll
        dist\SMLoader.Core.dll  + runtimeconfig.json + SMLoader.Api.dll
        dist\Mods\NoclipMod\NoclipMod.dll
        dist\Mods\PhysgunMod\PhysgunMod.dll
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$shimBuild = Join-Path $root 'build\shim'

Write-Host "==> Configuring native shim" -ForegroundColor Cyan
cmake -S (Join-Path $root 'src\SMLoader.Shim') -B $shimBuild -A x64
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }

Write-Host "==> Building native shim ($Configuration)" -ForegroundColor Cyan
cmake --build $shimBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "CMake build failed" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null

$shimDll = Join-Path $shimBuild "$Configuration\SMLoader.Shim.dll"
if (-not (Test-Path $shimDll)) { throw "Shim not found at $shimDll" }
Copy-Item $shimDll $dist -Force
Write-Host "    -> $((Join-Path $dist 'SMLoader.Shim.dll'))"

# The PDB is what makes a user-submitted crash dump readable; shipping the DLL
# without it throws that away at the moment it is needed.
$shimPdb = Join-Path $shimBuild "$Configuration\SMLoader.Shim.pdb"
if (Test-Path $shimPdb) { Copy-Item $shimPdb $dist -Force }

Write-Host "==> Building SMLoader.Core" -ForegroundColor Cyan
dotnet build (Join-Path $root 'src\SMLoader.Core\SMLoader.Core.csproj') -c $Configuration -o $dist --nologo
if ($LASTEXITCODE -ne 0) { throw "SMLoader.Core build failed" }

Write-Host "==> Building SMLoader.Launcher" -ForegroundColor Cyan
dotnet build (Join-Path $root 'src\SMLoader.Launcher\SMLoader.Launcher.csproj') -c $Configuration -o $dist --nologo
if ($LASTEXITCODE -ne 0) { throw "SMLoader.Launcher build failed" }

Write-Host "==> Building mods" -ForegroundColor Cyan
dotnet build (Join-Path $root 'mods\NoclipMod\NoclipMod.csproj') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "NoclipMod build failed" }

dotnet build (Join-Path $root 'mods\PhysgunMod\PhysgunMod.csproj') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "PhysgunMod build failed" }

Write-Host ""
Write-Host "Build complete. Run it with:" -ForegroundColor Green
Write-Host "    .\dist\SMLoader.Launcher.exe -dev"
