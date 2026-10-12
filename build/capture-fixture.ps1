# Captures a release's compatibility fixture into tests/Compatibility/<version>/: a synthetic
# profile and backup made by that release's own import tool, the record counts today's code
# reads from them, and SHA256SUMS pinning every file. Fixtures are captured once and never
# regenerated (AGENTS.md).
#
# Usage: pwsh build/capture-fixture.ps1 -Tag v0.7.0
param([Parameter(Mandatory)][string]$Tag)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if ($Tag -notmatch '^v(\d+\.\d+\.\d+)$') { throw "Not a release tag: $Tag" }
$version = $Matches[1]
$repo = git rev-parse --show-toplevel
$out = Join-Path $repo "tests/Compatibility/$version"
if (Test-Path $out) { throw "tests/Compatibility/$version already exists; fixtures are never regenerated." }

$work = Join-Path ([IO.Path]::GetTempPath()) "mossmint-fixture-$([guid]::NewGuid().ToString('N'))"
git worktree add --detach "$work/release" $Tag
try {
    # 0.3–0.5's tools can't write the synthetic workbooks, so today's tool writes them for every release.
    dotnet run --project "$repo/tools/Mossmint.ImportTool" -c Release -- --write-samples "$work/workbooks"
    dotnet run --project "$work/release/tools/Mossmint.ImportTool" -c Release -- --data-root "$out/data" --profile Fixture `
        --secrets "$out/secrets.txt" --report "$work/report.txt" --backup "$out/backup.mmbak" (Get-ChildItem "$work/workbooks/*.xlsx").FullName

    # The tests need only the passphrase, and a secret scanner rightly flags a recovery key.
    $secrets = Get-Content "$out/secrets.txt" | Where-Object { $_ -notlike 'Recovery key: *' }
    [IO.File]::WriteAllText("$out/secrets.txt", ($secrets -join "`n") + "`n")

    # Opening a profile upgrades it, so today's code counts the records in a copy.
    Copy-Item "$out/data" "$work/check" -Recurse
    dotnet run --project "$repo/src/Mossmint.Desktop" -c Release -- --verify-upgrade "$work/check" "$out/secrets.txt" "$out/backup.mmbak" "$out/upgrade-check.json"

    $lines = Get-ChildItem $out -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($out, $_.FullName) -replace '\\', '/'
        "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $relative"
    }
    [IO.File]::WriteAllText("$out/SHA256SUMS", ($lines -join "`n") + "`n")
    Write-Output "Captured $Tag in tests/Compatibility/$version."
}
catch {
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    throw
}
finally {
    git worktree remove --force "$work/release"
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
