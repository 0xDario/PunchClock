<#
.SYNOPSIS
    Checks that schema rowset restrictions reach OLE DB as plain strings. Runs on pwsh 7 or
    Windows PowerShell 5.1, no ACE needed.

.DESCRIPTION
    Table names come from the Tables rowset through a pipeline, so PowerShell wraps them in
    PSObject. Inside an object[] passed to a .NET method they stay wrapped, and ACE rejected
    them with "The parameter is incorrect" when reading the Columns rowset.

.EXAMPLE
    pwsh tests/Test-SchemaRestrictions.ps1
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Export-LegacyData.ps1')

Add-Type -TypeDefinition @'
public static class SchemaRestrictionProbe {
    // Reports what a .NET method such as GetOleDbSchemaTable receives.
    public static string Types(object[] restrictions) {
        if (restrictions == null) return "<null>";
        var types = new string[restrictions.Length];
        for (int i = 0; i < restrictions.Length; i++)
            types[i] = restrictions[i] == null ? "null" : restrictions[i].GetType().FullName;
        return string.Join(",", types);
    }
}
'@

$failed = 0
function Check([string]$Name, [string]$Actual, [string]$Expected) {
    if ($Actual -eq $Expected) { Write-Host "ok    $Name" } else { Write-Host "FAIL  $Name : $Actual (expected $Expected)"; $script:failed++ }
}

# Build the table list the way the exporter does.
$dt = New-Object Data.DataTable
[void]$dt.Columns.Add('TABLE_NAME'); [void]$dt.Columns.Add('TABLE_TYPE')
[void]$dt.Rows.Add('Shift', 'TABLE'); [void]$dt.Rows.Add('Employee', 'TABLE')
$userTables = @(@($dt.Rows) | Where-Object { $_['TABLE_TYPE'] -eq 'TABLE' } | ForEach-Object { [string]$_['TABLE_NAME'] } | Sort-Object)

foreach ($tname in $userTables) {
    Check "Columns $tname" ([SchemaRestrictionProbe]::Types((ConvertTo-SchemaRestrictions @($null, $null, $tname, $null)))) 'null,null,System.String,null'
    Check "Primary_Keys $tname" ([SchemaRestrictionProbe]::Types((ConvertTo-SchemaRestrictions @($null, $null, $tname)))) 'null,null,System.String'
}
Check 'all nulls' ([SchemaRestrictionProbe]::Types((ConvertTo-SchemaRestrictions @($null, $null, $null, $null)))) 'null,null,null,null'
Check 'no restrictions' ([SchemaRestrictionProbe]::Types((ConvertTo-SchemaRestrictions $null))) '<null>'

if ($failed) { Write-Host "$failed failed"; exit 1 }
Write-Host 'Schema restrictions reach .NET as plain strings.'
