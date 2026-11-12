@echo off
rem Installs Prosper Voice for the current user only: no admin rights, nothing outside your own profile.
rem   %LOCALAPPDATA%\Programs\ProsperVoice\ProsperVoice.exe, a Start menu shortcut and a desktop shortcut.
rem Keep ProsperVoice.exe next to this file (or in dist\), then double-click install.cmd.
setlocal
set SRC=%~dp0ProsperVoice.exe
if not exist "%SRC%" set SRC=%~dp0dist\ProsperVoice.exe
if not exist "%SRC%" (echo ProsperVoice.exe was not found next to install.cmd. & pause & exit /b 1)
set DEST=%LOCALAPPDATA%\Programs\ProsperVoice
taskkill /im ProsperVoice.exe /f >nul 2>&1
if not exist "%DEST%" mkdir "%DEST%"
copy /y "%SRC%" "%DEST%\ProsperVoice.exe" >nul || (echo Couldn't copy the app. & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s = New-Object -ComObject WScript.Shell;" ^
  "foreach ($dir in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop'))) {" ^
  "  $l = $s.CreateShortcut((Join-Path $dir 'Prosper Voice.lnk'));" ^
  "  $l.TargetPath = '%DEST%\ProsperVoice.exe'; $l.WorkingDirectory = '%DEST%';" ^
  "  $l.Description = 'Prosper Voice: dictation and Assistant'; $l.Save() }"
start "" "%DEST%\ProsperVoice.exe"
echo Prosper Voice is installed in %DEST% and running (look for the bar at the bottom of the screen and the tray icon).
echo To remove it: quit it from the tray, delete that folder, the two "Prosper Voice" shortcuts and %APPDATA%\ProsperVoice.
