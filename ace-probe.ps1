# Temporary CI probe: replay the exporter's schema calls step by step.
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'migration\legacy-export\Export-LegacyData.ps1')
$src = (Resolve-Path 'PunchClock/PunchClock.accdb').ProviderPath
function Try-Step([string]$Name, [scriptblock]$Body) {
    try { $r = & $Body; Write-Host "$Name : ok $r" } catch { Write-Host "$Name : FAILED $($_.Exception.GetBaseException().GetType().Name) $($_.Exception.GetBaseException().Message)" }
}
foreach ($enum in $false, $true) {
    if ($enum) { Try-Step 'enumerator' { @((New-Object Data.OleDb.OleDbEnumerator).GetElements().Rows).Count } }
    $c = New-Object Data.OleDb.OleDbConnection((New-AceConnectionString 'Microsoft.ACE.OLEDB.16.0' $src 'admin123'))
    $c.Open()
    $OLE = [Data.OleDb.OleDbSchemaGuid]
    Try-Step "enum=$enum Get-SchemaRows Tables" { @(Get-SchemaRows $c $OLE::Tables @($null, $null, $null, $null)).Count }
    Try-Step "enum=$enum direct Tables" { $c.GetOleDbSchemaTable($OLE::Tables, [object[]]@($null, $null, $null, $null)).Rows.Count }
    $rows = @(Get-SchemaRows $c $OLE::Tables $null)
    foreach ($r in $rows) { Write-Host "  table $($r['TABLE_NAME']) type $($r['TABLE_TYPE'])" }
    foreach ($t in 'Employee', 'Shift') {
        Try-Step "enum=$enum Get-SchemaRows Columns $t" { @(Get-SchemaRows $c $OLE::Columns @($null, $null, $t, $null)).Count }
        Try-Step "enum=$enum Get-SchemaRows Primary_Keys $t" { @(Get-SchemaRows $c $OLE::Primary_Keys @($null, $null, $t)).Count }
    }
    Try-Step "enum=$enum Foreign_Keys" { @(Get-SchemaRows $c $OLE::Foreign_Keys $null).Count }
    Try-Step "enum=$enum Views" { @(Get-SchemaRows $c $OLE::Views $null).Count }
    Try-Step "enum=$enum Procedures" { @(Get-SchemaRows $c $OLE::Procedures $null).Count }
    $c.Dispose()
}
