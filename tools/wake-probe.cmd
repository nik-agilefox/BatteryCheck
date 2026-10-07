@echo off
rem Who wakes the discrete GPU: correlates GPU wake-ups with per-process CPU activity (no admin rights needed).
rem Usage: tools\wake-probe.cmd [seconds, default 1200]. Run on battery; raw data goes to %TEMP%\bc_wakeprobe.csv.
setlocal
cd /d "%~dp0.."
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist bin mkdir bin
"%CSC%" /nologo /optimize+ /platform:x64 /codepage:65001 /r:System.Web.Extensions.dll /r:System.Core.dll /out:bin\WakeProbe.exe src\Core\*.cs tools\WakeProbe.cs
if errorlevel 1 exit /b 1
bin\WakeProbe.exe %*
