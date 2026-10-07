@echo off
rem Builds with the C# compiler bundled with .NET Framework 4.8 - nothing to install.
setlocal
cd /d "%~dp0"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"
if not exist "%CSC%" (
    echo csc.exe not found: %CSC%
    exit /b 1
)
if not exist bin mkdir bin
set "COMMON=/nologo /optimize+ /platform:x64 /codepage:65001"
rem Core: rules.json is embedded; JSON is parsed with System.Web.Extensions (part of .NET Framework)
set "CORE=/r:System.Web.Extensions.dll /resource:src\Core\rules.json,BatteryCheck.rules.json"

rem Console version
"%CSC%" %COMMON% %CORE% /target:exe /win32icon:src\Gui\app.ico /out:bin\BatteryCheck.exe src\Core\*.cs src\Console\*.cs
if errorlevel 1 exit /b 1
echo Built: bin\BatteryCheck.exe

rem Window version (WPF)
rem Icon: src\Gui\app.ico (regenerate with tools\make-icon.cmd)
"%CSC%" %COMMON% %CORE% /target:winexe /out:bin\BatteryCheckGui.exe /win32manifest:src\Gui\app.manifest /win32icon:src\Gui\app.ico ^
    /lib:"%FW%\WPF" /r:PresentationCore.dll /r:PresentationFramework.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll ^
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
    src\Core\*.cs src\Gui\*.cs
if errorlevel 1 exit /b 1
echo Built: bin\BatteryCheckGui.exe

rem Installer: both exe embedded as resources; per-user, no admin. The manifest (asInvoker) is required:
rem without it Windows treats an exe named *Setup* as an installer and asks for administrator rights.
"%CSC%" %COMMON% /target:winexe /out:bin\BatteryCheckSetup.exe /win32manifest:src\Gui\app.manifest /win32icon:src\Gui\app.ico ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
    /resource:bin\BatteryCheckGui.exe,payload.BatteryCheckGui.exe /resource:bin\BatteryCheck.exe,payload.BatteryCheck.exe ^
    src\Setup\*.cs src\Core\AppInfo.cs
if errorlevel 1 exit /b 1
echo Built: bin\BatteryCheckSetup.exe
