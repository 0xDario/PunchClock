# Temporary CI probe: trace the real exporter up to its failure.
$out = Join-Path $env:RUNNER_TEMP 'probe-export'
$log = Join-Path $env:RUNNER_TEMP 'probe-trace.txt'
& powershell -NoProfile -ExecutionPolicy Bypass -Command "Set-PSDebug -Trace 1; & '$PWD\publish\migration\export\Export-LegacyData.ps1' -Source '$PWD\PunchClock\PunchClock.accdb' -OutDir '$out' -SiteTimeZone 'Eastern Standard Time' -NoZip" *> $log
Write-Host "exit $LASTEXITCODE"
Get-Content $log | Select-Object -Last 60
