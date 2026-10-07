@echo off
setlocal
rem Runs Export-LegacyData.ps1 in 64-bit PowerShell, then retries in 32-bit
rem PowerShell if the Access Database Engine is only installed for 32-bit.
rem Any arguments are passed through, e.g. run-export.cmd -Source "C:\PunchClock\PunchClock.accdb"

set "PS64=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "PS64=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
set "PS32=%SystemRoot%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe"
set "SCRIPT=%~dp0Export-LegacyData.ps1"
rem Started from a PowerShell 7 window, PSModulePath points Windows PowerShell at
rem PowerShell 7's modules and cmdlets such as Get-FileHash fail to load. Without the
rem variable, Windows PowerShell uses its own default module path.
set "PSModulePath="

"%PS64%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "RC=%ERRORLEVEL%"
if not "%RC%"=="3" goto done
if not exist "%PS32%" goto done
echo.
echo Retrying with 32-bit PowerShell...
"%PS32%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "RC=%ERRORLEVEL%"

:done
echo.
echo Exit code: %RC%
pause
exit /b %RC%
