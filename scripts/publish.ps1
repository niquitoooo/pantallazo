<#
  Publica Pantallazo autocontenido (no necesita .NET instalado) y arma las dos entregas:
    dist\Pantallazo-<versión>-instalador.exe   instalador (Inno Setup, por usuario, sin administrador)
    dist\Pantallazo-<versión>-win-x64.zip      versión portable
  Uso:  .\scripts\publish.ps1  [-Version 1.2.3] [-SinInstalador]
  Sin -Version usa la del proyecto (<Version> en src\Pantallazo\Pantallazo.csproj).
#>
param([string]$Runtime = 'win-x64', [string]$Version, [switch]$SinInstalador)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }

$out = Join-Path $root 'dist\Pantallazo'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

& $dotnet publish (Join-Path $root 'src\Pantallazo\Pantallazo.csproj') -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out `
    $(if ($Version) { "-p:Version=$Version" })
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló ($LASTEXITCODE)" }

Copy-Item (Join-Path $root 'README.md') $out
Copy-Item (Join-Path $root 'docs\LICENCIAS.md') $out
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $out 'LICENSE.txt')

$version = (Get-Item (Join-Path $out 'Pantallazo.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
$zip = Join-Path $root "dist\Pantallazo-$version-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Write-Host "Listo:      $out"
Write-Host "Portable:   $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"

if (-not $SinInstalador) {
    $iscc = & (Join-Path $PSScriptRoot 'get-innosetup.ps1')
    $setup = Join-Path $root "dist\Pantallazo-$version-instalador.exe"
    if (Test-Path $setup) { Remove-Item $setup -Force }
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$out" "/DOutputDir=$(Join-Path $root 'dist')" (Join-Path $root 'installer\Pantallazo.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup falló ($LASTEXITCODE)" }
    Write-Host "Instalador: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
}
