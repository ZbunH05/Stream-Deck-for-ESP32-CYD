@echo off
setlocal
if "%~2"=="" (
  echo Dung: make_sample_images.cmd ^<file .ttf^> ^<file .codepoints^>
  exit /b 2
)
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "KEYART=%~dp0..\..\pc-agent\src\KeyArt.cs"
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%FW%\csc.exe" /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /warn:4 ^
  /out:"%~dp0bin\make_sample_images.exe" ^
  /r:System.Drawing.dll ^
  "%~dp0make_sample_images.cs" "%KEYART%"
if errorlevel 1 exit /b 1
"%~dp0bin\make_sample_images.exe" "%~dp0.." "%~1" "%~2"
