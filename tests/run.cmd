@echo off
rem Builds and runs the self-test (no API key needed; it records one second from the microphone, unless --no-mic).
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
cd /d "%~dp0.."
if not exist dist mkdir dist
"%FW%\csc.exe" /nologo /target:exe /out:dist\SelfTest.exe /main:Prosper.Tests ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Net.Http.dll /r:System.Security.dll /r:System.Web.Extensions.dll ^
  /r:"%FW%\WPF\UIAutomationClient.dll" /r:"%FW%\WPF\UIAutomationTypes.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  src\*.cs tests\Smoke.cs
if errorlevel 1 exit /b 1
dist\SelfTest.exe %*
