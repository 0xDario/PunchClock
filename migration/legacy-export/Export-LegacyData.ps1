#Requires -PSEdition Desktop
<#
.SYNOPSIS
    Exports Employee and Shift from the legacy PunchClock.accdb to CSV with a
    reconciliation manifest. Read-only: the source file is never opened.

.DESCRIPTION
    1. Hashes the source .accdb (SHA-256) and copies it to <OutDir>\source\.
    2. Opens the copy read-only through ACE OLEDB and writes Employee.csv and
       Shift.csv ordered by primary key, keeping legacy IDs and raw timestamps.
    3. Cross-checks rows written against SELECT COUNT(*), re-hashes the copy to
       prove it was not modified, and writes manifest.json.

    Output format is identical to jackcess/LegacyExport.java. See README.md.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Export-LegacyData.ps1 -Source 'C:\PunchClock\PunchClock.accdb'

.NOTES
    Needs Windows PowerShell 5.1 (System.Data.OleDb) and an ACE OLEDB provider
    of the same bitness as the PowerShell process. If only the 32-bit Access
    Runtime is installed, run C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$OutDir = (Join-Path (Get-Location) ('legacy-export-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string]$Password = 'admin123'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$Tool = 'ps-Export-LegacyData/1'
$Tables = @(
    @{ Name = 'Employee'; Key = 'EmployeeID' },
    @{ Name = 'Shift';    Key = 'ShiftID' }
)
$Inv = [Globalization.CultureInfo]::InvariantCulture
$Utf8NoBom = New-Object Text.UTF8Encoding $false

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# RFC 4180 field: NULL is empty, empty string is "", quote when needed.
function Format-Field($Value) {
    if ($null -eq $Value -or $Value -is [DBNull]) { return '' }
    $s = [Convert]::ToString($Value, $Inv)
    if ($s.Length -eq 0 -or $s.IndexOfAny([char[]]@(',', '"', "`r", "`n")) -ge 0 -or $s -ne $s.Trim()) {
        return '"' + $s.Replace('"', '""') + '"'
    }
    $s
}

function Format-IsoLocal($Value) {
    if ($Value -is [DBNull]) { return '' }
    ([datetime]$Value).ToString("yyyy-MM-dd'T'HH:mm:ss.fff", $Inv)
}

# Access stores dates as an OLE Automation double. Emit the shortest decimal
# that round-trips, which is what Java's Double.toString produces.
function Format-OADate($Value) {
    if ($Value -is [DBNull]) { return '' }
    $d = ([datetime]$Value).ToOADate()
    foreach ($p in 15, 16, 17) {
        $s = $d.ToString("G$p", $Inv)
        if ([double]::Parse($s, $Inv) -eq $d) { return $s }
    }
}

function Get-AceProvider {
    $installed = (New-Object System.Data.OleDb.OleDbEnumerator).GetElements() | ForEach-Object { $_.SOURCES_NAME }
    foreach ($p in 'Microsoft.ACE.OLEDB.16.0', 'Microsoft.ACE.OLEDB.12.0') {
        if ($installed -contains $p) { return $p }
    }
    $bits = if ([Environment]::Is64BitProcess) { '64' } else { '32' }
    throw "No ACE OLEDB provider is registered for this $bits-bit PowerShell. " +
        'Try the other bitness (32-bit: C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe) ' +
        'or install the Microsoft Access Database Engine redistributable.'
}

function Export-Table($Connection, [string]$Name, [string]$Key, [string]$Dir) {
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = "SELECT * FROM [$Name] ORDER BY [$Key]"
    $reader = $cmd.ExecuteReader()
    try {
        $header = New-Object System.Collections.Generic.List[string]
        $isDate = @()
        for ($i = 0; $i -lt $reader.FieldCount; $i++) {
            $header.Add($reader.GetName($i))
            $date = $reader.GetFieldType($i) -eq [datetime]
            $isDate += $date
            if ($date) { $header.Add($reader.GetName($i) + '_OADate') }
        }

        $csv = New-Object Text.StringBuilder
        [void]$csv.Append(($header -join ',') + "`r`n")
        $rows = 0
        $minId = $null
        $maxId = $null
        $keyOrdinal = $reader.GetOrdinal($Key)
        while ($reader.Read()) {
            $fields = New-Object System.Collections.Generic.List[string]
            for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                $v = $reader.GetValue($i)
                if ($isDate[$i]) {
                    $fields.Add((Format-IsoLocal $v))
                    $fields.Add((Format-OADate $v))
                } else {
                    $fields.Add((Format-Field $v))
                }
            }
            [void]$csv.Append(($fields -join ',') + "`r`n")
            $id = [long]$reader.GetValue($keyOrdinal)
            if ($null -eq $minId -or $id -lt $minId) { $minId = $id }
            if ($null -eq $maxId -or $id -gt $maxId) { $maxId = $id }
            $rows++
        }
    } finally {
        $reader.Close()
    }

    # Independent count. The row count Access keeps in the table header is not
    # used: it is only refreshed on compact and is stale in every repo copy.
    $count = $Connection.CreateCommand()
    $count.CommandText = "SELECT COUNT(*) FROM [$Name]"
    $counted = [long]$count.ExecuteScalar()
    if ($rows -ne $counted) { throw "${Name}: wrote $rows rows, COUNT(*) returned $counted" }

    $file = Join-Path $Dir "$Name.csv"
    [IO.File]::WriteAllText($file, $csv.ToString(), $Utf8NoBom)

    [ordered]@{
        name        = $Name
        file        = "$Name.csv"
        row_count   = $rows
        count_check = "COUNT(*) = $counted"
        primary_key = $Key
        min_id      = $minId
        max_id      = $maxId
        columns     = @($header)
        sha256      = Get-Sha256 $file
    }
}

$sourceItem = Get-Item -LiteralPath $Source
if ((Test-Path -LiteralPath $OutDir) -and (Get-ChildItem -LiteralPath $OutDir -Force)) {
    throw "Output folder is not empty: $OutDir"
}
$snapshotDir = Join-Path $OutDir 'source'
New-Item -ItemType Directory -Path $snapshotDir -Force | Out-Null

# Snapshot first and export from the snapshot, so the exported rows provably
# come from the exact bytes recorded in the manifest.
$sourceHash = Get-Sha256 $sourceItem.FullName
$snapshot = Join-Path $snapshotDir $sourceItem.Name
Copy-Item -LiteralPath $sourceItem.FullName -Destination $snapshot
if ((Get-Sha256 $snapshot) -ne $sourceHash) { throw 'Snapshot hash differs from source; the file changed during copy. Close the PunchClock app and retry.' }

$provider = Get-AceProvider
$conn = New-Object System.Data.OleDb.OleDbConnection(
    "Provider=$provider;Data Source=$snapshot;Mode=Read;Jet OLEDB:Database Password=$Password")
$conn.Open()
try {
    $tableResults = @(foreach ($t in $Tables) { Export-Table $conn $t.Name $t.Key $OutDir })
} finally {
    $conn.Close()
    [System.Data.OleDb.OleDbConnection]::ReleaseObjectPool()
}

if ((Get-Sha256 $snapshot) -ne $sourceHash) { throw 'Snapshot changed during export.' }
$lock = [IO.Path]::ChangeExtension($snapshot, '.laccdb')
if (Test-Path -LiteralPath $lock) { Remove-Item -LiteralPath $lock -ErrorAction SilentlyContinue }

$manifest = [ordered]@{
    tool            = $Tool
    exported_at_utc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", $Inv)
    source          = [ordered]@{
        path           = $sourceItem.FullName
        size_bytes     = $sourceItem.Length
        last_write_utc = $sourceItem.LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $Inv)
        sha256         = $sourceHash
        snapshot       = "source/$($sourceItem.Name)"
        provider       = $provider
    }
    timestamps      = 'site-local wall clock, no offset; *_OADate is the Access date double'
    tables          = $tableResults
}
$json = $manifest | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $OutDir 'manifest.json'), $json, $Utf8NoBom)
$json
Write-Host "Export written to $OutDir" -ForegroundColor Green
