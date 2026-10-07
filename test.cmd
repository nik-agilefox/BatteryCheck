@echo off
rem Builds and runs the core tests (tests\Tests.cs) on synthetic logs. Exit code = number of failed tests.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist bin mkdir bin
"%CSC%" /nologo /optimize+ /platform:x64 /codepage:65001 /r:System.Web.Extensions.dll /resource:src\Core\rules.json,BatteryCheck.rules.json /target:exe /out:bin\tests.exe src\Core\*.cs tests\*.cs
if errorlevel 1 exit /b 1
bin\tests.exe
