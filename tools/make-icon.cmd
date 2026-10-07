@echo off
rem Regenerates src\Gui\app.ico from tools\IconGen.cs (and bin\icon-preview.png).
setlocal
cd /d "%~dp0.."
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist bin mkdir bin
"%CSC%" /nologo /platform:x64 /codepage:65001 /r:System.Drawing.dll /out:bin\IconGen.exe tools\IconGen.cs
if errorlevel 1 exit /b 1
bin\IconGen.exe src\Gui\app.ico bin\icon-preview.png
if errorlevel 1 exit /b 1
del bin\IconGen.exe
echo Icon: src\Gui\app.ico
