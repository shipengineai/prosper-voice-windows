@echo off
rem Builds ProsperVoice.exe with the C# compiler that ships inside Windows (.NET Framework 4.8).
rem No Visual Studio, no SDK, no downloads, no admin rights: anyone can rebuild and audit it.
rem First it draws the logo (src\Logo.cs) into dist\prosper.ico, which becomes the .exe's own icon.
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
if not exist "%FW%\csc.exe" (echo .NET Framework 4.8 was not found. & exit /b 1)
cd /d "%~dp0"
if not exist dist mkdir dist
set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Security.dll /r:System.Web.Extensions.dll /r:"%FW%\WPF\UIAutomationClient.dll" /r:"%FW%\WPF\UIAutomationTypes.dll" /r:"%FW%\WPF\WindowsBase.dll"
"%FW%\csc.exe" /nologo /target:exe /out:dist\MakeIcon.exe /main:Prosper.MakeIcon %REFS% src\*.cs tools\MakeIcon.cs
if errorlevel 1 exit /b 1
dist\MakeIcon.exe dist
if errorlevel 1 exit /b 1
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu /out:dist\ProsperVoice.exe /win32manifest:app.manifest /win32icon:dist\prosper.ico %REFS% src\*.cs
if errorlevel 1 exit /b 1
echo Built dist\ProsperVoice.exe
