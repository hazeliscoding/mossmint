# Publishes Mossmint for win-x64 (self-contained) and packs a Velopack
# installer into artifacts/releases. The package carries binaries only: no
# profile, sample data or user file is ever copied into the publish folder.
#
# Usage: pwsh build/pack.ps1 [-Version 0.3.0]
# The version defaults to the one in Directory.Build.props. Velopack refuses to
# pack a version at or below one already in artifacts/releases: pass a higher
# -Version, or delete that folder for a local rebuild.
param(
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$publishDir = Join-Path $root "artifacts/publish/win-x64"
$releaseDir = Join-Path $root "artifacts/releases"
$icon = Join-Path $root "src/Mossmint.Desktop/Assets/mossmint.ico"

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish (Join-Path $root "src/Mossmint.Desktop/Mossmint.Desktop.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDir `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Guard: refuse to ship anything that looks like user data.
$private = Get-ChildItem $publishDir -Recurse -Include *.xlsx, *.xlsm, *.xls, *.csv, *.db, *.sqlite, *.mossmint, *.mmbak
if ($private) { throw "Publish output contains data files: $($private.FullName -join ', ')" }

dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }

$packArgs = @(
    "pack",
    "--packId", "Mossmint.Desktop",
    "--packTitle", "Mossmint",
    "--packAuthors", "Hazel Granados",
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--mainExe", "Mossmint.Desktop.exe",
    "--runtime", "win-x64",
    "--outputDir", $releaseDir
)
if (Test-Path $icon) { $packArgs += @("--icon", $icon) }

dotnet vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Get-ChildItem $releaseDir -File | Where-Object Name -ne "SHA256SUMS" | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "{0}  {1}" -f $hash, $_.Name
} | Set-Content (Join-Path $releaseDir "SHA256SUMS")

Get-Content (Join-Path $releaseDir "SHA256SUMS")
