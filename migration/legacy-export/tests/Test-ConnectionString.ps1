<#
.SYNOPSIS
    Checks the ACE connection string the exporter builds. Runs on pwsh 7 or Windows PowerShell 5.1,
    no ACE needed.

.EXAMPLE
    pwsh tests/Test-ConnectionString.ps1
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Export-LegacyData.ps1')

$failed = 0
$cases = @(
    @{ Path = 'C:\PunchClock\PunchClock.accdb'; Password = 'admin123' },
    @{ Path = 'C:\Users\Dario Turchi\OneDrive - Personal\Desktop\legacy-export-20261007-164900\source\PunchClock.accdb'; Password = 'admin123' },
    @{ Path = "C:\Users\O'Neil\Desk;top\Punch=Clock.accdb"; Password = 'a b;c=d"e''f' }
)
foreach ($case in $cases) {
    # The exporter's path comes from Join-Path, which PowerShell wraps in a PSObject.
    $path = [Management.Automation.PSObject]::AsPSObject($case.Path)
    $cs = New-AceConnectionString 'Microsoft.ACE.OLEDB.16.0' $path $case.Password
    $parsed = New-Object Data.Common.DbConnectionStringBuilder
    $parsed.set_ConnectionString($cs)  # dot assignment would add a "ConnectionString" key, the same trap
    $keys = @($parsed.Keys | ForEach-Object { $_ })
    $expected = @('provider', 'data source', 'mode', 'jet oledb:database password')
    $ok = ($keys.Count -eq 4) -and -not (Compare-Object $keys $expected) -and
        $parsed['Data Source'] -eq $case.Path -and $parsed['Jet OLEDB:Database Password'] -eq $case.Password -and
        $parsed['Provider'] -eq 'Microsoft.ACE.OLEDB.16.0' -and $parsed['Mode'] -eq 'Read' -and
        $cs.StartsWith('Provider=Microsoft.ACE.OLEDB.16.0;Data Source=')
    if ($ok) { Write-Host "ok    $cs" } else { Write-Host "FAIL  $cs (keys: $($keys -join ', '))"; $failed++ }
}
if ($failed) { Write-Host "$failed failed"; exit 1 }
Write-Host 'All connection strings parse back to the four ACE keywords.'
