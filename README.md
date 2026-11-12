# Prosper Voice for Windows

Prosper's voice features on a PC: dictation into any app, spoken edits of selected text, and an Assistant that sees
your selection and, when you ask, your screen. It's one small `.exe` (about 130 KB) for Windows 10 and 11. Nothing is
installed and no admin rights are needed: copy it to the laptop and run it.

## Use

| | Bubble | Shortcut |
|---|---|---|
| Dictate into the app you're typing in (click again to stop) | click | Ctrl+Alt+Space |
| Assistant: ask for an email, a reply, an answer… | double-click | Ctrl+Alt+A |
| Add the screen to the Assistant request | **📷 Add screen** beside the bubble | Ctrl+Alt+S |
| History and follow-up chat | hold | Ctrl+Alt+H |
| Menu: settings, hide the bubble, quit | right-click | |

- **Dictation** is transcribed, cleaned up (no ums, proper punctuation) and pasted where the cursor is.
- **With text selected**, what you say is an instruction for that text ("make this formal", "translate to Hindi"),
  and the result replaces the selection.
- **The Assistant** writes what you ask for. It gets your request, the selected text, the app's name, *About you*
  from settings, and the screen if you clicked **📷 Add screen**. If a text field has focus, the answer is typed
  there; otherwise it opens in a card with Insert, Copy and Open chat. Every answer is also copied.
- **History** keeps every conversation: what you said, the screenshot, the answer, and each tool call. You can type
  or speak follow-ups there, or attach an audio file (📎, up to 25 MB) to get its transcript or ask about it.
- **MCP (optional, off until you connect a server)**: in settings, enter a server URL and then either *Sign in*
  (OAuth, in your browser) or paste an access token. Tools on your *Always allowed* list run straight away; every
  other tool call asks Allow / Always allow / Cancel first.

## Set up

1. Put `ProsperVoice.exe` and `install.cmd` in the same folder and double-click `install.cmd`. It installs for you
   only, with no admin rights: the app goes in `%LOCALAPPDATA%\Programs\ProsperVoice`, with a Start menu shortcut
   and a desktop shortcut, and starts. Running the `.exe` from anywhere you like works too.
2. Settings opens: paste your OpenAI API key, add a line about yourself, and click **Save**.
3. If Windows blocks the microphone, turn on *Settings → Privacy & security → Microphone → Let desktop apps access
   your microphone*.
4. Optional: tick *Start Prosper when I sign in to Windows*.

SmartScreen may warn about an unsigned app the first time you run it: choose *More info → Run anyway*. Some company
laptops block unsigned programs completely (AppLocker / WDAC). If yours does, ask IT to allow it or to build and sign
it themselves (see [Build](#build)).

## Security

Built to be safe on a work laptop:

- **No admin rights, nothing installed.** It runs as you (`asInvoker`). It installs no services, drivers or keyboard
  hooks. Shortcuts use `RegisterHotKey`, so no keystrokes are read. *Start with Windows* only writes your own
  `HKCU\…\Run` value.
- **Only talks to your AI endpoint**, and to an MCP server if you connect one. No telemetry, no updates, no other
  hosts. The endpoint can be api.openai.com or your company's **Azure OpenAI** or other compatible endpoint
  (`https://` only).
- **Network:** TLS 1.2 or newer, through the proxy Windows uses, with your Windows sign-in where the proxy asks for it.
- **Secrets** (API key, MCP tokens) are encrypted with Windows DPAPI for your account only. They are never shown,
  logged or written in plain text.
- **History** is in `%APPDATA%\ProsperVoice\history`, one file per conversation (screenshots included), each
  encrypted with DPAPI for your account. Turn it off under *Keep history on this PC*, or delete it with *Clear history*.
- **Screenshots** are taken only when you click **📷 Add screen** or press Ctrl+Alt+S, and only of the monitor you're
  working on. The bubble hides while the picture is taken.
- **Clipboard:** Prosper borrows the clipboard to read a selection and to paste, then puts back what was there. Text
  it pastes is marked so Windows clipboard history (Win+V) and cloud clipboard sync skip it. Copied answers are never
  synced to the cloud.
- **Terminals are left alone:** Prosper never sends Ctrl+C to a terminal window, where it would stop the running
  program.
- **The log** (`%APPDATA%\ProsperVoice\prosper.log`) has timings and status codes only: never text, audio,
  screenshots or keys.
- **What goes to the AI provider:** your recording, then its transcript with any selected text, the app's name,
  *About you*, and the screenshot if you added one. OpenAI doesn't train on API data by default; check your own
  provider's terms.
- **MCP actions** are off by default. When on, each tool call asks first unless you put that tool on the *Always
  allowed* list.
- **Uninstall:** quit from the tray, delete the `.exe`, the folder `%APPDATA%\ProsperVoice` and, if you turned it
  on, the `ProsperVoice` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Build

The source is in `src/`. It builds with the C# compiler that ships inside Windows (.NET Framework 4.8), so you need no
Visual Studio, SDK or downloads:

```bat
build.cmd
```

The output is `dist\ProsperVoice.exe`. `tests\run.cmd` runs a self-test that needs no API key and
records one second from the microphone. The code is plain C# 5 with WinForms and no third-party libraries, so it's
easy to audit.
