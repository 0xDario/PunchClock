<#
.SYNOPSIS
    Exports the legacy PunchClock.accdb to CSV with a reconciliation manifest.
    Read-only: the source file is never opened.

.DESCRIPTION
    1. Refuses to run while an Access lock file shows the database is open.
    2. Hashes the source .accdb (SHA-256) and copies it to <OutDir>\source\.
    3. Opens the copy through ACE OLEDB and exports every user table (Employee
       and Shift are required) ordered by primary key, keeping legacy IDs and the
       raw stored timestamps, plus saved query definitions to access.queries.csv.
    4. Asks Access for control totals (COUNT(*), per-column non-NULL counts, sums
       of numeric columns, date bounds) through a query independent of the row
       export, and fails if the row count disagrees.
    5. Re-hashes the copy and the source, writes manifest.json and
       SHA256SUMS.txt, and zips the folder for hand-off.

    Employee.csv and Shift.csv are byte-identical to jackcess/LegacyExport.java
    for the same file. See README.md for the format.

    Exit codes: 0 ok, 1 unexpected error, 2 bad input or password,
    3 no ACE OLEDB provider in this bitness, 4 reconciliation failure,
    5 database in use, 6 file changed during copy or export.

.EXAMPLE
    .\run-export.cmd -Source 'C:\PunchClock\PunchClock.accdb'

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Export-LegacyData.ps1 -Source 'C:\PunchClock\PunchClock.accdb'
#>
[CmdletBinding()]
param(
    # Production PunchClock.accdb. A file picker opens when omitted.
    [string]$Source,
    # Output folder. Defaults to Desktop\legacy-export-<timestamp>.
    [string]$OutDir,
    # Database password. The legacy app ships with admin123.
    [string]$Password = 'admin123',
    # Windows time zone ID of the site where punches were recorded. Defaults to this machine's.
    [string]$SiteTimeZone,
    # Export even though an Access lock file is present (only if it is known to be stale).
    [switch]$Force,
    # Skip creating <OutDir>.zip.
    [switch]$NoZip
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Tool = 'ps-Export-LegacyData/2'
$script:RequiredTables = @('Employee', 'Shift')
$script:Inv = [Globalization.CultureInfo]::InvariantCulture
$script:Utf8NoBom = New-Object Text.UTF8Encoding $false

# OleDbType values reported in DATA_TYPE of the Columns schema rowset.
$script:TypeCategory = @{
    2 = 'integer'; 3 = 'integer'; 16 = 'integer'; 17 = 'integer'; 18 = 'integer'
    19 = 'integer'; 20 = 'integer'; 21 = 'integer'
    4 = 'float'; 5 = 'float'
    6 = 'decimal'; 14 = 'decimal'; 131 = 'decimal'
    7 = 'datetime'; 133 = 'datetime'; 134 = 'datetime'; 135 = 'datetime'
    11 = 'boolean'
    8 = 'text'; 129 = 'text'; 130 = 'text'; 200 = 'text'; 201 = 'text'; 202 = 'text'; 203 = 'text'
    72 = 'guid'
    128 = 'binary'; 204 = 'binary'; 205 = 'binary'
}

function Get-TypeCategory([int]$OleDbType) {
    if ($script:TypeCategory.ContainsKey($OleDbType)) { return $script:TypeCategory[$OleDbType] }
    return 'other'
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Shortest decimal that round-trips, no exponent for normal OADates; matches
# Java's Double.toString (minus the trailing ".0") used by the Jackcess exporter.
function Format-Double([double]$d) {
    foreach ($p in 15, 16, 17) {
        $s = $d.ToString("G$p", $script:Inv)
        if ([double]::Parse($s, $script:Inv) -eq $d) { return $s }
    }
}

# Scalar to text, culture-invariant. $null for NULL.
function Format-Scalar($Value) {
    if ($null -eq $Value -or $Value -is [DBNull]) { return $null }
    if ($Value -is [string]) { return $Value }
    if ($Value -is [datetime]) { return $Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", $script:Inv) }
    if ($Value -is [bool]) { if ($Value) { return 'true' } else { return 'false' } }
    if ($Value -is [double] -or $Value -is [single]) { return Format-Double ([double]$Value) }
    if ($Value -is [byte[]]) { return [Convert]::ToBase64String($Value) }
    if ($Value -is [guid]) { return $Value.ToString('B') }
    return [Convert]::ToString($Value, $script:Inv)
}

# RFC 4180 field: NULL is empty, empty string is "", quote when needed.
function Format-Field($Value) {
    $s = Format-Scalar $Value
    if ($null -eq $s) { return '' }
    if ($s.Length -eq 0 -or $s.IndexOfAny([char[]]@(',', '"', "`r", "`n")) -ge 0 -or $s -ne $s.Trim()) {
        return '"' + $s.Replace('"', '""') + '"'
    }
    $s
}

# Writes $Reader to $Path following $Spec, one entry per output column:
#   @{ Name; Kind = 'value'; Ordinal }                     a source column
#   @{ Name; Kind = 'oadate'; DateOrdinal; RawOrdinal }    the stored date double;
#     RawOrdinal -1 derives it with ToOADate() from the DateTime.
# Returns row count, min/max of $KeyOrdinal, and how many stored dates carry
# sub-millisecond precision (the ISO column would then not be exact).
function Export-ReaderToCsv {
    param(
        [Data.IDataReader]$Reader,
        [object[]]$Spec,
        [int]$KeyOrdinal,
        [string]$Path
    )
    $csv = New-Object Text.StringBuilder
    [void]$csv.Append([string]::Join(',', @($Spec | ForEach-Object { Format-Field $_.Name })) + "`r`n")
    $rows = 0
    $minId = $null
    $maxId = $null
    $subMs = 0
    $fields = New-Object 'System.Collections.Generic.List[string]'
    while ($Reader.Read()) {
        $fields.Clear()
        foreach ($col in $Spec) {
            if ($col.Kind -eq 'value') {
                $fields.Add((Format-Field $Reader.GetValue($col.Ordinal)))
                continue
            }
            $dt = $Reader.GetValue($col.DateOrdinal)
            if ($col.RawOrdinal -ge 0) { $raw = $Reader.GetValue($col.RawOrdinal) }
            elseif ($dt -is [datetime]) { $raw = $dt.ToOADate() }
            else { $raw = [DBNull]::Value }
            if ($dt -is [datetime] -and $raw -is [double] -and $dt.ToOADate() -ne $raw) { $subMs++ }
            $fields.Add((Format-Field $raw))
        }
        [void]$csv.Append([string]::Join(',', $fields) + "`r`n")
        if ($KeyOrdinal -ge 0) {
            $id = [long]$Reader.GetValue($KeyOrdinal)
            if ($null -eq $minId -or $id -lt $minId) { $minId = $id }
            if ($null -eq $maxId -or $id -gt $maxId) { $maxId = $id }
        }
        $rows++
    }
    [IO.File]::WriteAllText($Path, $csv.ToString(), $script:Utf8NoBom)
    return @{ Rows = $rows; MinId = $minId; MaxId = $maxId; SubMillisecondDates = $subMs }
}

# Control totals come from Access in one aggregate query, a path independent of
# the row export, so the importer has numbers to reconcile against.
function Get-ControlTotalsSql([string]$Table, [object[]]$Columns) {
    $parts = New-Object 'System.Collections.Generic.List[string]'
    $parts.Add('COUNT(*) AS [rows_]')
    for ($i = 0; $i -lt $Columns.Count; $i++) {
        $c = $Columns[$i]
        $parts.Add("COUNT([$($c.name)]) AS [nn_$i]")
        if ($c.category -eq 'integer' -or $c.category -eq 'decimal' -or $c.category -eq 'float') { $parts.Add("SUM([$($c.name)]) AS [sum_$i]") }
        if ($c.category -eq 'datetime') {
            $parts.Add("MIN([$($c.name)]) AS [min_$i]")
            $parts.Add("MAX([$($c.name)]) AS [max_$i]")
        }
    }
    "SELECT $([string]::Join(', ', $parts)) FROM [$Table]"
}

function Read-ControlTotals([Data.IDataReader]$Reader, [object[]]$Columns) {
    if (-not $Reader.Read()) { throw 'Control totals query returned no row.' }
    $totals = [ordered]@{}
    for ($i = 0; $i -lt $Columns.Count; $i++) {
        $c = $Columns[$i]
        $t = [ordered]@{ non_null = [long]$Reader['nn_' + $i] }
        if ($c.category -eq 'integer' -or $c.category -eq 'decimal' -or $c.category -eq 'float') { $t.sum = Format-Scalar $Reader['sum_' + $i] }
        if ($c.category -eq 'datetime') {
            $t.min = Format-Scalar $Reader['min_' + $i]
            $t.max = Format-Scalar $Reader['max_' + $i]
        }
        $totals[$c.name] = $t
    }
    @{ Rows = [long]$Reader['rows_']; Columns = $totals }
}

function ConvertTo-SchemaRestrictions([object[]]$Restrictions) {
    # Values that came through a pipeline (table names from the Tables rowset) arrive as
    # PSObject wrappers, and PowerShell does not unwrap elements of an array passed to a
    # .NET method. OLE DB cannot marshal a PSObject and fails with "The parameter is
    # incorrect", so pass plain strings. Restrictions are always strings or null.
    if ($null -eq $Restrictions) { return ,$null }
    $plain = New-Object object[] $Restrictions.Length
    for ($i = 0; $i -lt $Restrictions.Length; $i++) {
        if ($null -ne $Restrictions[$i]) { $plain[$i] = [string]$Restrictions[$i] }
    }
    ,$plain
}

function Get-SchemaRows($Connection, [guid]$Schema, [object[]]$Restrictions) {
    try { @($Connection.GetOleDbSchemaTable($Schema, (ConvertTo-SchemaRestrictions $Restrictions)).Rows) }
    catch {
        $name = @([Data.OleDb.OleDbSchemaGuid].GetFields() | Where-Object { $_.GetValue($null) -eq $Schema } | ForEach-Object { $_.Name }) -join '/'
        $what = if ($null -eq $Restrictions) { 'none' } else { (@($Restrictions | ForEach-Object { if ($null -eq $_) { '*' } else { "'$_'" } }) -join ', ') }
        throw "Reading the $name schema (restrictions: $what) failed: $($_.Exception.GetBaseException().Message)"
    }
}

function Get-RowValue($Row, [string]$Name) {
    if ($Row.Table.Columns.Contains($Name) -and -not ($Row[$Name] -is [DBNull])) { return $Row[$Name] }
    $null
}

function New-AceConnectionString([string]$Provider, [string]$DataSource, [string]$Password) {
    # Set every key through the indexer, by its OLE DB keyword name. PowerShell routes
    # $builder.DataSource = ... to the dictionary key "DataSource" (no space), which ACE does
    # not know: it fails with "Could not find installable ISAM". The builder quotes values,
    # so spaces, ; and quotes in the path or password are safe. Typed parameters also turn
    # PowerShell's PSObject wrappers into plain strings.
    $builder = New-Object Data.Common.DbConnectionStringBuilder
    $builder['Provider'] = $Provider
    $builder['Data Source'] = $DataSource
    $builder['Mode'] = 'Read'
    $builder['Jet OLEDB:Database Password'] = $Password
    $builder.ConnectionString
}

$script:Conn = $null
$script:CleanupDir = $null

function Exit-WithError([int]$Code, [string]$Message) {
    Write-Host ''
    Write-Host "ERROR: $Message" -ForegroundColor Red
    # A failed run leaves nothing behind: the partial folder holds a copy of the database.
    if ($script:Conn) { $script:Conn.Dispose(); $script:Conn = $null }
    if ($script:CleanupDir -and (Test-Path -LiteralPath $script:CleanupDir)) {
        [Data.OleDb.OleDbConnection]::ReleaseObjectPool()
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()  # let ACE drop its lock on the copy
        Remove-Item -LiteralPath $script:CleanupDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    exit $Code
}

# Dot-sourcing loads the functions only (used by the tests).
if ($MyInvocation.InvocationName -eq '.') { return }

# Name the failing line, so a report from the field points at the bug.
trap {
    $e = $_.Exception.GetBaseException()
    Exit-WithError 1 ($e.Message + [Environment]::NewLine + $_.InvocationInfo.PositionMessage + [Environment]::NewLine +
        'Details for support: ' + $e.GetType().FullName + [Environment]::NewLine + $e.StackTrace)
}

$started = Get-Date
$bitness = if ([Environment]::Is64BitProcess) { '64-bit' } else { '32-bit' }
Write-Host "PunchClock legacy export ($bitness Windows PowerShell $($PSVersionTable.PSVersion))"

# --- Source -----------------------------------------------------------------------
if (-not $Source) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $dlg = New-Object Windows.Forms.OpenFileDialog
        $dlg.Title = 'Select the production PunchClock.accdb'
        $dlg.Filter = 'Access database (*.accdb;*.mdb)|*.accdb;*.mdb|All files (*.*)|*.*'
        if ($dlg.ShowDialog() -eq [Windows.Forms.DialogResult]::OK) { $Source = $dlg.FileName }
    }
    catch { $Source = Read-Host 'Full path to PunchClock.accdb' }
}
if (-not $Source -or -not (Test-Path -LiteralPath $Source -PathType Leaf)) { Exit-WithError 2 "Database file not found: '$Source'" }
$sourceItem = Get-Item -LiteralPath $Source
# Access names the lock .ldb for .mdb files and .laccdb for .accdb files.
$lockExt = if ($sourceItem.Extension -eq '.mdb') { '.ldb' } else { '.laccdb' }
$lockFile = [IO.Path]::ChangeExtension($sourceItem.FullName, $lockExt)
if ((Test-Path -LiteralPath $lockFile) -and -not $Force) {
    Exit-WithError 5 ("The database is open ($lockFile exists). Close PunchClock and Access on every machine " +
        'that uses this file, then run again. Use -Force only if the lock file is known to be stale.')
}

if (-not $OutDir) {
    $OutDir = Join-Path ([Environment]::GetFolderPath('Desktop')) ('legacy-export-' + $started.ToString('yyyyMMdd-HHmmss'))
}
if ((Test-Path -LiteralPath $OutDir) -and @(Get-ChildItem -LiteralPath $OutDir -Force).Count -gt 0) {
    Exit-WithError 2 "Output folder is not empty: $OutDir"
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).ProviderPath
$script:CleanupDir = $OutDir
$snapshotDir = Join-Path $OutDir 'source'
New-Item -ItemType Directory -Path $snapshotDir -Force | Out-Null

# --- Snapshot ------------------------------------------------------------------
# Export from a snapshot so the rows provably come from the exact bytes in the manifest.
Write-Host "Source:   $($sourceItem.FullName)"
$sourceHash = Get-Sha256 $sourceItem.FullName
$snapshot = Join-Path $snapshotDir $sourceItem.Name
Copy-Item -LiteralPath $sourceItem.FullName -Destination $snapshot
if ((Get-Sha256 $snapshot) -ne $sourceHash) {
    Exit-WithError 6 'The database changed while it was being copied. Close PunchClock everywhere and run again.'
}
Write-Host "SHA-256:  $sourceHash"

# --- Connect -------------------------------------------------------------------
$candidates = New-Object 'System.Collections.Generic.List[string]'
try {
    $registered = @((New-Object Data.OleDb.OleDbEnumerator).GetElements().Rows | ForEach-Object { [string]$_['SOURCES_NAME'] })
    foreach ($p in 'Microsoft.ACE.OLEDB.16.0', 'Microsoft.ACE.OLEDB.12.0') { if ($registered -contains $p) { $candidates.Add($p) } }
}
catch { }
if ($candidates.Count -eq 0) { $candidates.Add('Microsoft.ACE.OLEDB.16.0'); $candidates.Add('Microsoft.ACE.OLEDB.12.0') }

$provider = $null
foreach ($p in $candidates) {
    $connectionString = New-AceConnectionString $p $snapshot $Password
    $c = New-Object Data.OleDb.OleDbConnection($connectionString)
    try {
        $c.Open()
        $script:Conn = $c
        $provider = $p
        break
    }
    catch {
        $err = $_.Exception.GetBaseException()
        $c.Dispose()
        # An unregistered provider throws InvalidOperationException; anything else is a real failure.
        if (-not ($err -is [InvalidOperationException])) {
            $hint = if ($err.Message -match 'password') { ' Pass the right password with -Password.' } else { '' }
            Exit-WithError 2 "Could not open the database with ${p}: $($err.Message)$hint"
        }
    }
}
if (-not $script:Conn) {
    $other = if ([Environment]::Is64BitProcess) { '32-bit' } else { '64-bit' }
    Exit-WithError 3 ("No ACE OLEDB provider is registered for $bitness processes. Run the $other PowerShell " +
        '(run-export.cmd retries automatically) or install the Microsoft Access Database Engine 2016 ' +
        'Redistributable matching your Office bitness.')
}
$conn = $script:Conn
Write-Host "Provider: $provider"

$warnings = New-Object 'System.Collections.Generic.List[string]'
$failures = New-Object 'System.Collections.Generic.List[string]'
$tableResults = New-Object 'System.Collections.Generic.List[object]'
$OLE = [Data.OleDb.OleDbSchemaGuid]
try {
    # --- Tables ------------------------------------------------------------------
    $tableRows = Get-SchemaRows $conn $OLE::Tables @($null, $null, $null, $null)
    $userTables = @($tableRows | Where-Object { $_['TABLE_TYPE'] -eq 'TABLE' } | ForEach-Object { [string]$_['TABLE_NAME'] } | Sort-Object)
    $linkedTables = @($tableRows | Where-Object { $_['TABLE_TYPE'] -eq 'LINK' -or $_['TABLE_TYPE'] -eq 'PASS-THROUGH' } |
        ForEach-Object { [string]$_['TABLE_NAME'] })
    foreach ($req in $script:RequiredTables) {
        if ($userTables -notcontains $req) { Exit-WithError 2 "Required table '$req' not found. Tables present: $($userTables -join ', ')" }
    }
    if ($linkedTables.Count -gt 0) { $warnings.Add("Linked tables not exported (their data lives in another file): $($linkedTables -join ', ')") }

    # --- Relationships and saved queries (best effort) ------------------------------
    $relations = @()
    try {
        $relations = @(Get-SchemaRows $conn $OLE::Foreign_Keys $null | ForEach-Object {
                [ordered]@{
                    name = [string](Get-RowValue $_ 'FK_NAME')
                    pk_table = [string](Get-RowValue $_ 'PK_TABLE_NAME'); pk_column = [string](Get-RowValue $_ 'PK_COLUMN_NAME')
                    fk_table = [string](Get-RowValue $_ 'FK_TABLE_NAME'); fk_column = [string](Get-RowValue $_ 'FK_COLUMN_NAME')
                    update_rule = [string](Get-RowValue $_ 'UPDATE_RULE'); delete_rule = [string](Get-RowValue $_ 'DELETE_RULE')
                }
            })
    }
    catch { $warnings.Add("Relationships not read: $($_.Exception.GetBaseException().Message)") }

    # The pay report's record source is usually a saved query; report layouts are not reachable through OLE DB.
    $queries = New-Object 'System.Collections.Generic.List[object]'
    foreach ($q in @(@{ Guid = $OLE::Views; Name = 'TABLE_NAME'; Sql = 'VIEW_DEFINITION'; Kind = 'select' },
            @{ Guid = $OLE::Procedures; Name = 'PROCEDURE_NAME'; Sql = 'PROCEDURE_DEFINITION'; Kind = 'action-or-parameter' })) {
        try {
            foreach ($r in (Get-SchemaRows $conn $q.Guid $null)) {
                $queries.Add([ordered]@{ name = [string]$r[$q.Name]; kind = $q.Kind; sql = [string](Get-RowValue $r $q.Sql) })
            }
        }
        catch { $warnings.Add("Saved queries ($($q.Kind)) not read: $($_.Exception.GetBaseException().Message)") }
    }
    $qcsv = New-Object Text.StringBuilder
    [void]$qcsv.Append("name,kind,sql`r`n")
    foreach ($q in $queries) { [void]$qcsv.Append((Format-Field $q.name) + ',' + (Format-Field $q.kind) + ',' + (Format-Field $q.sql) + "`r`n") }
    # Access object names cannot contain '.', so this name never collides with a <table>.csv.
    [IO.File]::WriteAllText((Join-Path $OutDir 'access.queries.csv'), $qcsv.ToString(), $script:Utf8NoBom)

    # --- Export each table ---------------------------------------------------------
    foreach ($tname in $userTables) {
        $columns = @(Get-SchemaRows $conn $OLE::Columns @($null, $null, $tname, $null) |
            Sort-Object { [int]$_['ORDINAL_POSITION'] } |
            ForEach-Object {
                $type = [int]$_['DATA_TYPE']
                [ordered]@{
                    name = [string]$_['COLUMN_NAME']
                    oledb_type = ([Data.OleDb.OleDbType]$type).ToString()
                    category = Get-TypeCategory $type
                    nullable = [bool]$_['IS_NULLABLE']
                    max_length = Get-RowValue $_ 'CHARACTER_MAXIMUM_LENGTH'
                }
            })

        $pk = @()
        try {
            $pk = @(Get-SchemaRows $conn $OLE::Primary_Keys @($null, $null, $tname) |
                Sort-Object { [int]$_['ORDINAL'] } | ForEach-Object { [string]$_['COLUMN_NAME'] })
        }
        catch { $warnings.Add("Primary key of $tname not read: $($_.Exception.GetBaseException().Message)") }
        # Access cannot sort OLE Object or Attachment fields, so a keyless table is
        # ordered by its first sortable column, or left unordered if it has none.
        $orderBy = @($pk)
        if ($pk.Count -eq 0) {
            $sortable = @($columns | Where-Object { $_.category -ne 'binary' -and $_.category -ne 'other' } | Select-Object -First 1)
            if ($sortable.Count -gt 0) { $orderBy = @($sortable[0].name) }
            $by = if ($orderBy.Count -gt 0) { "ordered by $($orderBy[0])" } else { 'unordered (no sortable column)' }
            $warnings.Add("$tname has no primary key; rows $by")
        }

        $cmd = $conn.CreateCommand()
        $cmd.CommandText = Get-ControlTotalsSql $tname $columns
        $rd = $cmd.ExecuteReader()
        try { $totals = Read-ControlTotals $rd $columns } finally { $rd.Dispose() }

        # Raw dates come from CDbl() in Access when it accepts the expression. Access can
        # raise expression errors while fetching, so the fallback wraps the whole export.
        $orderSql = if ($orderBy.Count -gt 0) { ' ORDER BY ' + [string]::Join(', ', @($orderBy | ForEach-Object { "[$_]" })) } else { '' }
        $csvPath = Join-Path $OutDir "$tname.csv"
        $oadateSource = $null
        $result = $null
        foreach ($attempt in 'access-cdbl', 'dotnet-tooadate') {
            $spec = New-Object 'System.Collections.Generic.List[object]'
            $select = New-Object 'System.Collections.Generic.List[string]'
            $keyOrdinal = -1
            foreach ($c in $columns) {
                $ord = $select.Count
                $select.Add("[$($c.name)]")
                $spec.Add(@{ Name = $c.name; Kind = 'value'; Ordinal = $ord })
                if ($pk.Count -eq 1 -and $c.name -eq $pk[0] -and $c.category -eq 'integer') { $keyOrdinal = $ord }
                if ($c.category -eq 'datetime') {
                    $rawOrd = -1
                    if ($attempt -eq 'access-cdbl') {
                        $rawOrd = $select.Count
                        $select.Add("IIf([$($c.name)] Is Null, Null, CDbl([$($c.name)])) AS [raw_$ord]")
                    }
                    $spec.Add(@{ Name = "$($c.name)_OADate"; Kind = 'oadate'; DateOrdinal = $ord; RawOrdinal = $rawOrd })
                }
            }
            $cmd = $conn.CreateCommand()
            $cmd.CommandText = "SELECT $([string]::Join(', ', $select)) FROM [$tname]$orderSql"
            try {
                $rd = $cmd.ExecuteReader()
                try { $result = Export-ReaderToCsv -Reader $rd -Spec $spec.ToArray() -KeyOrdinal $keyOrdinal -Path $csvPath }
                finally { $rd.Dispose() }
                $oadateSource = $attempt
                break
            }
            catch {
                if ($attempt -ne 'access-cdbl') { throw }
                Write-Host "  CDbl() export of $tname failed, deriving raw dates in .NET: $($_.Exception.GetBaseException().Message)" -ForegroundColor Yellow
            }
        }

        $status = 'ok'
        if ($result.Rows -ne $totals.Rows) {
            $status = 'MISMATCH'
            $failures.Add("${tname}: COUNT(*) = $($totals.Rows), CSV has $($result.Rows) rows")
        }
        if ($result.SubMillisecondDates -gt 0) {
            $warnings.Add("${tname}: $($result.SubMillisecondDates) stored dates have sub-millisecond precision; *_OADate is exact, the ISO column is rounded")
        }
        Write-Host ("{0,-10} {1,7} rows   COUNT(*) {2,7}   {3}" -f $tname, $result.Rows, $totals.Rows, $status)

        $pkValue = if ($pk.Count -eq 1) { $pk[0] } else { @($pk) }
        $tableResults.Add([ordered]@{
                name = $tname
                file = "$tname.csv"
                row_count = $result.Rows
                count_check = "COUNT(*) = $($totals.Rows)"
                primary_key = $pkValue
                min_id = $result.MinId
                max_id = $result.MaxId
                columns = @($spec | ForEach-Object { $_.Name })
                sha256 = Get-Sha256 $csvPath
                oadate_source = $oadateSource
                column_types = @($columns)
                control_totals = $totals.Columns
            })
    }
}
finally {
    if ($script:Conn) { $script:Conn.Dispose(); $script:Conn = $null }
    [Data.OleDb.OleDbConnection]::ReleaseObjectPool()
}

# --- Prove nothing was written, then the manifest ------------------------------------
# ACE holds the snapshot's lock file until its COM objects are finalized, which the
# 32-bit engine does noticeably later than the 64-bit one. The lock file is not part of
# the export, and a held one breaks hashing and zipping, so wait for it to go.
$lock = [IO.Path]::ChangeExtension($snapshot, $lockExt)
for ($try = 1; Test-Path -LiteralPath $lock; $try++) {
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    try { Remove-Item -LiteralPath $lock -Force -ErrorAction Stop }
    catch {
        if ($try -ge 40) { Exit-WithError 6 "The Access engine still holds $lock. Close every program that uses Access and run again." }
        Start-Sleep -Milliseconds 250
    }
}
if ((Get-Sha256 $snapshot) -ne $sourceHash) { $failures.Add('The snapshot changed during the export.') }
if ((Get-Sha256 $sourceItem.FullName) -ne $sourceHash) {
    $failures.Add('The source database changed during the export; PunchClock was probably used. Re-run with it closed.')
}

$tz = [TimeZoneInfo]::Local
$tzSource = 'export machine'
if ($SiteTimeZone) { $tz = [TimeZoneInfo]::FindSystemTimeZoneById($SiteTimeZone); $tzSource = 'parameter' }
$manifest = [ordered]@{
    tool = $script:Tool
    exported_at_utc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", $script:Inv)
    exported_by = [ordered]@{
        machine = $env:COMPUTERNAME
        windows_user = "$env:USERDOMAIN\$env:USERNAME"
        powershell = "$($PSVersionTable.PSVersion) $bitness"
    }
    source = [ordered]@{
        path = $sourceItem.FullName
        size_bytes = $sourceItem.Length
        last_write_utc = $sourceItem.LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $script:Inv)
        sha256 = $sourceHash
        snapshot = "source/$($sourceItem.Name)"
        provider = $provider
    }
    timestamps = 'site-local wall clock, no offset; *_OADate is the Access date double'
    site_time_zone = [ordered]@{
        id = $tz.Id
        standard_name = $tz.StandardName
        base_utc_offset = $tz.BaseUtcOffset.ToString()
        observes_dst = $tz.SupportsDaylightSavingTime
        source = $tzSource
    }
    # ToArray, not @(): @() on a List[object] throws "Argument types do not match" (PowerShell bug).
    tables = $tableResults.ToArray()
    linked_tables = @($linkedTables)
    relationships = @($relations)
    queries = [ordered]@{ file = 'access.queries.csv'; count = $queries.Count }
    warnings = $warnings.ToArray()
    failures = $failures.ToArray()
}
[IO.File]::WriteAllText((Join-Path $OutDir 'manifest.json'), ($manifest | ConvertTo-Json -Depth 8), $script:Utf8NoBom)

$sums = @(Get-ChildItem -LiteralPath $OutDir -File -Recurse | Sort-Object FullName | ForEach-Object {
        "$(Get-Sha256 $_.FullName)  $($_.FullName.Substring($OutDir.Length + 1).Replace('\', '/'))"
    })
[IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"), $script:Utf8NoBom)

if ($failures.Count -gt 0) {
    # Keep the folder: the manifest records what went wrong.
    $script:CleanupDir = $null
    Exit-WithError 4 ("Reconciliation failed (details in manifest.json):`n  " + ($failures -join "`n  "))
}

$zip = $null
if (-not $NoZip) {
    # validate_export.py accepts either path separator inside the zip.
    $zip = "$OutDir.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($OutDir, $zip)
}

Write-Host ''
foreach ($w in $warnings) { Write-Host "Warning: $w" -ForegroundColor Yellow }
Write-Host "Export:   $OutDir"
if ($zip) { Write-Host "Zip:      $zip" }
Write-Host 'Contains employee names, plaintext PINs and a full copy of the database. Share it only through the private project.' -ForegroundColor Yellow
Write-Host 'Done.' -ForegroundColor Green
exit 0
