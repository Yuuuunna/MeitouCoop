@echo off
setlocal
cd /d "%~dp0"
set "MEITOU_IDENTITY_DIRECTORY=%LOCALAPPDATA%\Meitou\identities-player2"
MeitouClient.exe %*
if errorlevel 1 pause
