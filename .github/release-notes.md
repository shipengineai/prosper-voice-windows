## Install (no admin rights)

1. Download `ProsperVoice.exe` and `install.cmd` into the same folder.
2. Double-click `install.cmd`. It installs for your Windows account only, in `%LOCALAPPDATA%\Programs\ProsperVoice`, adds Start menu and desktop shortcuts, and starts the app.
3. Add your OpenAI API key in Settings. It is encrypted for your Windows account.

Running `ProsperVoice.exe` from any folder works too. Requires Windows 10 or 11 (.NET Framework 4.8 is built in).

**Windows SmartScreen** may warn that the app is from an unknown publisher, because the file isn't code-signed. Click **More info**, then **Run anyway**. To check that the file is the one built here, compare it with `SHA256SUMS.txt`:

```powershell
Get-FileHash .\ProsperVoice.exe -Algorithm SHA256
```

Built on GitHub Actions from this tag with `build.cmd`; the self-test passed before publishing.
