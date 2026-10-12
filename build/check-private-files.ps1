# Fails if any given path looks like private financial data: spreadsheets,
# bank exports, profile databases or backups. CI passes every tracked file.
# Synthetic test data is generated at test time, so none of these belong in git, with
# one exception: the compatibility fixtures' databases and backups, which each release's
# own code makes (build/capture-fixture.ps1).
#
# Usage: ./build/check-private-files.ps1 -Paths (git ls-files) [-ExpectFailure]
param(
    [string[]]$Paths = @(),
    [switch]$ExpectFailure
)

$patterns = @(
    '\.(xlsx|xlsm|xlsb|xls|ods|numbers)$',
    '\.(ofx|qfx|qif|csv)$',
    '\.(db|db-wal|db-shm|sqlite|sqlite3)$',
    '\.(mossmint|mmbak)$',
    '(^|/)private/'
)
$fixture = '^tests/Compatibility/\d+\.\d+\.\d+/(backup\.mmbak|data/Profiles/[0-9a-f]{32}/(profile|snapshots/[^/]+)\.db)$'

$hits = @($Paths | Where-Object {
    $path = $_ -replace '\\', '/'
    $path -notmatch $fixture -and ($patterns | Where-Object { $path -match $_ } | Select-Object -First 1)
})

if ($ExpectFailure) {
    if ($hits.Count -eq 0) { Write-Error "Positive control failed: the guard accepted $($Paths -join ', ')"; exit 1 }
    Write-Output "Positive control passed: the guard rejects $($hits -join ', ')"
    exit 0
}

if ($hits.Count -gt 0) {
    Write-Error "Private-looking files are tracked: $($hits -join ', ')"
    exit 1
}
Write-Output "No private-looking files among $($Paths.Count) tracked paths."
