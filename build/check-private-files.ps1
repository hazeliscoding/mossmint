# Fails if any given path looks like private financial data: spreadsheets,
# bank exports, profile databases or backups. CI passes every tracked file.
# Synthetic fixtures are generated at test time, so none of these belong in git.
#
# Usage: ./build/check-private-files.ps1 -Paths (git ls-files) [-ExpectFailure]
param(
    [string[]]$Paths = @(),
    [switch]$ExpectFailure
)

$patterns = @(
    '\.(xlsx|xlsm|xlsb|xls|ods|numbers)$',
    '\.(ofx|qfx|qif)$',
    '\.(db|db-wal|db-shm|sqlite|sqlite3)$',
    '\.(mossmint|mmbak)$',
    '(^|/)private/'
)

$hits = @($Paths | Where-Object {
    $path = $_ -replace '\\', '/'
    $patterns | Where-Object { $path -match $_ } | Select-Object -First 1
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
