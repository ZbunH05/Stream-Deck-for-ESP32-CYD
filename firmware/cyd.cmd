@echo off
powershell -NoProfile -File "%~dp0tools\cyd.ps1" %*
set "CYD_RC=%errorlevel%"
if not "%CYD_RC%"=="0" pause
exit /b %CYD_RC%
