# Temporary CI probe: which GetOleDbSchemaTable calls does ACE accept?
$src = (Resolve-Path 'PunchClock/PunchClock.accdb').ProviderPath
$G = [Data.OleDb.OleDbSchemaGuid]
foreach ($mode in @('Read', $null, 'Share Deny None', 'Read|Share Deny None')) {
    $b = New-Object Data.Common.DbConnectionStringBuilder
    $b['Provider'] = 'Microsoft.ACE.OLEDB.16.0'; $b['Data Source'] = $src
    if ($mode) { $b['Mode'] = $mode }
    $b['Jet OLEDB:Database Password'] = 'admin123'
    $c = New-Object Data.OleDb.OleDbConnection($b.ConnectionString)
    try { $c.Open() } catch { Write-Host "mode=$mode open FAILED: $($_.Exception.GetBaseException().Message)"; continue }
    foreach ($case in @(
        @{ n = 'Tables 4 nulls'; g = $G::Tables; r = [object[]]@($null, $null, $null, $null) },
        @{ n = 'Tables null'; g = $G::Tables; r = $null },
        @{ n = 'Tables empty'; g = $G::Tables; r = [object[]]@() },
        @{ n = 'Tables TABLE'; g = $G::Tables; r = [object[]]@($null, $null, $null, 'TABLE') },
        @{ n = 'Columns Shift'; g = $G::Columns; r = [object[]]@($null, $null, 'Shift', $null) },
        @{ n = 'Primary_Keys Shift'; g = $G::Primary_Keys; r = [object[]]@($null, $null, 'Shift') })) {
        try { $t = $c.GetOleDbSchemaTable($case.g, $case.r); Write-Host "mode=$mode $($case.n): ok $($t.Rows.Count) rows" }
        catch { Write-Host "mode=$mode $($case.n): FAILED $($_.Exception.GetBaseException().Message)" }
    }
    try { $cmd = $c.CreateCommand(); $cmd.CommandText = 'SELECT COUNT(*) FROM [Shift]'; Write-Host "mode=$mode count: $($cmd.ExecuteScalar())" }
    catch { Write-Host "mode=$mode count FAILED: $($_.Exception.GetBaseException().Message)" }
    $c.Dispose()
}
