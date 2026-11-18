@echo off
rem CYD-EEZ quick command. Examples:  cyd   |   cyd doctor   |   cyd flash -Env cyd2usb
rem Full help:  powershell -File tools\cyd.ps1 -?   (or Get-Help .\tools\cyd.ps1 -Full)
powershell -NoProfile -File "%~dp0tools\cyd.ps1" %*
set "CYD_RC=%errorlevel%"
if not "%CYD_RC%"=="0" pause
exit /b %CYD_RC%
