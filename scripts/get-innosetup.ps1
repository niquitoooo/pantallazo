<#
  Deja el compilador de Inno Setup (ISCC.exe) en .tools\innosetup\<versión>\ sin instalar nada en Windows:
  baja el paquete NuGet oficial "Tools.InnoSetup" (que trae Inno Setup completo) y lo descomprime.
  Devuelve la ruta de ISCC.exe.
#>
param([string]$Version = '6.7.3')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root ".tools\innosetup\$Version"
$iscc = Get-ChildItem $dest -Recurse -Filter ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if ($iscc) { return $iscc.FullName }

New-Item -ItemType Directory -Force $dest | Out-Null
$pkg = Join-Path $dest "tools.innosetup.$Version.zip"
$url = "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$Version/tools.innosetup.$Version.nupkg"
Write-Host "Bajando Inno Setup $Version de nuget.org…"
Invoke-WebRequest $url -OutFile $pkg -UseBasicParsing
Expand-Archive $pkg -DestinationPath $dest -Force
Remove-Item $pkg -Force
$iscc = Get-ChildItem $dest -Recurse -Filter ISCC.exe | Select-Object -First 1
if (-not $iscc) { throw "El paquete no trae ISCC.exe" }
return $iscc.FullName
