<#
.SYNOPSIS
    Checks that Export-LegacyData.ps1 writes Employee.csv and Shift.csv byte-identical
    to a Jackcess export of the same file, without needing ACE OLEDB.

.DESCRIPTION
    Loads the exporter's functions, rebuilds each table from the Jackcess CSV as a
    DataTable shaped like the ACE reader (DateTime plus the raw CDbl double), runs
    Export-ReaderToCsv over it, and compares bytes. Runs on pwsh 7 or Windows PowerShell 5.1.

.EXAMPLE
    pwsh tests/Test-CsvFormat.ps1 -JackcessExport ./legacy-export-20261002-190000
#>
param([Parameter(Mandatory = $true)][string]$JackcessExport)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Export-LegacyData.ps1')

$inv = [Globalization.CultureInfo]::InvariantCulture
$failed = 0
foreach ($name in 'Employee', 'Shift') {
    $src = Join-Path $JackcessExport "$name.csv"
    $lines = [IO.File]::ReadAllText($src).Split(@("`r`n"), [StringSplitOptions]::RemoveEmptyEntries)
    $header = $lines[0].Split(',')
    $table = New-Object Data.DataTable
    $spec = New-Object 'System.Collections.Generic.List[object]'
    $sourceCols = @($header | Where-Object { -not $_.EndsWith('_OADate') })
    foreach ($c in $sourceCols) {
        $isDate = $header -contains "${c}_OADate"
        $type = if ($isDate) { [datetime] } elseif ($c -match 'ID$|PinCode|IsActive') { [int] } else { [string] }
        $ord = $table.Columns.Count
        [void]$table.Columns.Add($c, $type)
        $spec.Add(@{ Name = $c; Kind = 'value'; Ordinal = $ord })
        if ($isDate) {
            [void]$table.Columns.Add("raw_$ord", [double])
            $spec.Add(@{ Name = "${c}_OADate"; Kind = 'oadate'; DateOrdinal = $ord; RawOrdinal = $ord + 1 })
        }
    }
    foreach ($line in $lines[1..($lines.Length - 1)]) {
        if ($lines.Length -lt 2) { break }
        # Fixture fields hold no commas or quotes.
        $f = $line.Split(',')
        $row = $table.NewRow()
        for ($i = 0; $i -lt $header.Length; $i++) {
            $h = $header[$i]
            $target = if ($h.EndsWith('_OADate')) { 'raw_' + $table.Columns.IndexOf($h.Substring(0, $h.Length - 7)) } else { $h }
            if ($f[$i] -eq '') { $row[$target] = [DBNull]::Value; continue }
            if ($h.EndsWith('_OADate')) { $row[$target] = [double]::Parse($f[$i], $inv) }
            elseif ($table.Columns[$h].DataType -eq [datetime]) { $row[$target] = [datetime]::ParseExact($f[$i], "yyyy-MM-dd'T'HH:mm:ss.fff", $inv) }
            else { $row[$target] = $f[$i] }
        }
        $table.Rows.Add($row)
    }
    $out = [IO.Path]::GetTempFileName()
    $result = Export-ReaderToCsv -Reader $table.CreateDataReader() -Spec $spec.ToArray() -KeyOrdinal 0 -Path $out
    $same = [Convert]::ToBase64String([IO.File]::ReadAllBytes($out)) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($src))
    Write-Host ("{0,-9} rows={1,-4} ids={2}..{3} sub-ms={4} byte-identical={5}" -f $name, $result.Rows, $result.MinId, $result.MaxId, $result.SubMillisecondDates, $same)
    if (-not $same) { $failed++; Get-Content $out | Select-Object -First 3 }
    Remove-Item $out
}

# Field quoting rules shared with the Jackcess exporter.
$cases = @(
    @{ In = $null; Out = '' }, @{ In = [DBNull]::Value; Out = '' }, @{ In = ''; Out = '""' },
    @{ In = 'plain'; Out = 'plain' }, @{ In = 'a,b'; Out = '"a,b"' }, @{ In = 'say "hi"'; Out = '"say ""hi"""' },
    @{ In = " pad"; Out = '" pad"' }, @{ In = "two`r`nlines"; Out = "`"two`r`nlines`"" }, @{ In = 0123; Out = '123' },
    @{ In = $true; Out = 'true' }, @{ In = 44564.136225138885; Out = '44564.136225138885' }, @{ In = [double]0.1; Out = '0.1' }
)
foreach ($c in $cases) {
    $got = Format-Field $c.In
    if ($got -ne $c.Out) { $failed++; Write-Host "Format-Field($($c.In)) = [$got], expected [$($c.Out)]" }
}
Write-Host "quoting cases: $($cases.Count - $failed) checked"
if ($failed) { Write-Host "FAILED: $failed"; exit 1 }
Write-Host 'PASS'
