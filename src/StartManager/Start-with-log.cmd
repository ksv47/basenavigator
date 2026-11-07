@echo off
setlocal
set "BN_LOG_DIR=%TEMP%\BaseNavigator-logs-%RANDOM%-%RANDOM%"
mkdir "%BN_LOG_DIR%"
if errorlevel 1 exit /b 1
set "DOTNET_HOST_TRACE=1"
set "DOTNET_HOST_TRACE_VERBOSITY=4"
set "DOTNET_HOST_TRACEFILE=%BN_LOG_DIR%\host.log"
set "COREHOST_TRACE=1"
set "COREHOST_TRACE_VERBOSITY=4"
set "COREHOST_TRACEFILE=%BN_LOG_DIR%\host.log"
"%~dp0BaseNavigator.exe" --log-level debug --log-dir "%BN_LOG_DIR%" 1>"%BN_LOG_DIR%\stdout.txt" 2>"%BN_LOG_DIR%\stderr.txt"
set "BN_EXIT=%ERRORLEVEL%"
>"%BN_LOG_DIR%\result.txt" echo ExitCode: %BN_EXIT%
echo Logs: %BN_LOG_DIR%
start "" explorer.exe "%BN_LOG_DIR%"
exit /b %BN_EXIT%
