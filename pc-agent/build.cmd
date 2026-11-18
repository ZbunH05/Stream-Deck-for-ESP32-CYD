@echo off
rem Dich CYD PC Agent bang csc.exe co san trong .NET Framework 4 cua Windows (khong can cai Visual Studio hay .NET SDK).
rem Ket qua: pc-agent\bin\CydAgent.exe
setlocal
rem Trang cai dat CYD Deck (web\index.html) duoc doc tu dia luc chay, khong dich vao exe.
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"
if not exist "%CSC%" (
  echo [LOI] Khong thay %CSC%
  exit /b 1
)
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /warn:4 ^
  /out:"%~dp0bin\CydAgent.exe" ^
  /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  /r:System.Security.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /r:"%FW%\System.Runtime.dll" ^
  /r:"%WINDIR%\System32\WinMetadata\Windows.Foundation.winmd" /r:"%WINDIR%\System32\WinMetadata\Windows.Media.winmd" ^
  "%~dp0src\*.cs"
rem WinMetadata: nhac dang phat (DeckMedia.cs), co san trong Windows 10/11, khong can Windows SDK
if errorlevel 1 (
  echo [LOI] Dich khong duoc
  exit /b 1
)
echo [OK] %~dp0bin\CydAgent.exe
