# Temporary CI probe: which earlier schema read breaks the Columns read?
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'migration\legacy-export\Export-LegacyData.ps1')
$src = (Resolve-Path 'PunchClock/PunchClock.accdb').ProviderPath
$OLE = [Data.OleDb.OleDbSchemaGuid]
function Step($c, [string]$Name, [scriptblock]$Body) {
    try { $r = & $Body; Write-Host "  $Name : ok $r" } catch { Write-Host "  $Name : FAILED $($_.Exception.GetBaseException().Message)" }
}
foreach ($first in 'none', 'Foreign_Keys', 'Views', 'Procedures', 'Views+Procedures') {
    $c = New-Object Data.OleDb.OleDbConnection((New-AceConnectionString 'Microsoft.ACE.OLEDB.16.0' $src 'admin123'))
    $c.Open()
    Write-Host "after $first"
    foreach ($g in ($first -split '\+')) { if ($g -ne 'none') { Step $c "read $g" { @(Get-SchemaRows $c $OLE::$g $null).Count } } }
    Step $c 'Columns Employee' { @(Get-SchemaRows $c $OLE::Columns @($null, $null, 'Employee', $null)).Count }
    Step $c 'Columns Employee (TABLE_NAME only, 3)' { @(Get-SchemaRows $c $OLE::Columns @($null, $null, 'Employee')).Count }
    Step $c 'Columns all' { @(Get-SchemaRows $c $OLE::Columns $null).Count }
    Step $c 'Primary_Keys Employee' { @(Get-SchemaRows $c $OLE::Primary_Keys @($null, $null, 'Employee')).Count }
    Step $c 'SELECT COUNT' { $cmd = $c.CreateCommand(); $cmd.CommandText = 'SELECT COUNT(*) FROM [Employee]'; $cmd.ExecuteScalar() }
    $c.Dispose()
}
