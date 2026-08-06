# Buduje wersje portable i pakuje ja do dist\GameTranslatorOverlay-v{wersja}-win-x64.zip
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$props = [xml](Get-Content "$root\Directory.Build.props")
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Pakowanie GameTranslatorOverlay v$version ($Configuration)..."

# Czysty katalog docelowy: zadne pozostalosci z poprzednich buildow nie moga trafic do zipa.
if (Test-Path "$root\publish") { Remove-Item "$root\publish" -Recurse -Force }

dotnet publish src/GameTranslatorOverlay.App -c $Configuration -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded `
    -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish zakonczyl sie bledem ($LASTEXITCODE)" }

# PDB-y zawieraja lokalne sciezki dewelopera — nie dystrybuujemy ich.
Get-ChildItem "$root\publish" -Filter *.pdb | Remove-Item -Force

Copy-Item "$root\README.md", "$root\THIRD-PARTY-NOTICES.md" -Destination "$root\publish" -Force
Copy-Item "$root\docs\USER_GUIDE.md", "$root\docs\PRIVACY.md", "$root\docs\SECURITY.md" -Destination "$root\publish" -Force

New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$zip = "$root\dist\GameTranslatorOverlay-v$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$root\publish\*" -DestinationPath $zip

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Gotowe: $zip ($size MB)"
