@echo off
setlocal
cd /d "%~dp0"
set "MEITOU_IDENTITY_DIRECTORY="
MeitouClient.exe %*
if errorlevel 1 pause
