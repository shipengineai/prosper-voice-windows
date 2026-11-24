# Prosper as a Python script

Prosper's dictation, spoken edits and Assistant in **one Python file**, `prosper.pyw`. Use it from PowerToys Command
Palette bookmarks, or keep it running for the global shortcuts.

- **No install, no admin rights, no identity check.** It is a script, not a package, so Windows asks for nothing.
- **No extra packages.** It uses only Python's standard library and Windows' own DLLs: the microphone through winmm,
  the clipboard and keystrokes through user32, encryption through DPAPI. Needs Python 3.9 or newer.

## Set up

1. Put `prosper.pyw` in a folder you keep, for example `Documents\Prosper`. Get it from the latest
   [release](../../../releases/latest) or from this folder.
2. Double-click it. **Prosper settings** opens: paste your OpenAI API key, add a line *About you*, click **Save**, then
   **Test connection**. The key is encrypted for your Windows account.
3. If Windows blocks the microphone: *Settings → Privacy & security → Microphone →* allow desktop apps.

## Use it from PowerToys Command Palette

In Prosper settings, the buttons under *Use it from PowerToys Command Palette* copy the exact line for each command.
In Command Palette (Win+Alt+Space), type **Add bookmark**, paste the line as the path, and give it a name:

| Bookmark name | What it does |
|---|---|
| Prosper Dictate | Starts dictation into the app you're typing in; run it again (or click ✓ on the pill) to finish |
| Prosper Assistant | Starts the Assistant; run it again to finish |
| Prosper Ask | Type to the Assistant about the selected text, when you can't talk: opens the Ask box |
| Prosper Add screen | While the Assistant is recording, adds your screen to the request |
| Prosper History | What you said and what Prosper answered |
| Prosper Settings | Key, models, About you, MCP server |

Each line looks like this, using your own Python and file path:

```
"C:\Users\you\AppData\Local\Programs\Python\Python313\pythonw.exe" "C:\Users\you\Documents\Prosper\prosper.pyw" dictate
```

`pythonw.exe` runs it without a console window. Command Palette also lets you give a bookmark its own hotkey.

## Global shortcuts

Tick *Start with Windows* in settings, or run `pythonw prosper.pyw run`. Prosper then stays in the background with
**Ctrl+Alt+Space** (dictate), **Ctrl+Alt+A** (Assistant), **Ctrl+Alt+T** (type to the Assistant), **Ctrl+Alt+S**
(add screen) and **Ctrl+Alt+H** (history). *Start with Windows* writes only your own `HKCU\…\Run` value, so it needs
no admin either. The bookmarks keep working alongside it.

Change any of them under *Shortcuts* in settings: click one and press the keys you want (Backspace turns it off, Esc
keeps the old one). Settings says straight away if another app or another Prosper action already has those keys, and
**Save** applies them without a restart.

## What it does

- **Dictation** is transcribed, cleaned up (no ums, proper punctuation) and pasted where the cursor is.
- **With text selected**, what you say is an instruction for that text ("make this formal", "translate to Hindi"),
  and the result replaces the selection.
- **The Assistant** writes what you ask for. It gets your request, the selected text, the app's name, *About you*,
  and the screen if you clicked **Add screen**. If a text field has focus, the answer is typed there; otherwise it opens
  in a window with Copy. Every answer is also copied.
- **Typing instead of talking** (an open office, a meeting): select text and press Ctrl+Alt+T (or run the *Prosper
  Ask* bookmark). A small box opens near the bottom of the screen with the selection, quick actions (Fix grammar,
  Shorter, More formal, Reply to this) and a line to type what you want. The answer shows in the box first and is
  copied: **Replace selection** (Ctrl+Enter) puts it in the app, or type again to refine it. If the Assistant needs
  something only you know, such as who a reply is for, it asks one short question there instead of guessing; a voice
  request that needs an answer opens the same box. Esc or clicking elsewhere closes it.
- **MCP (optional):** enter a server URL and an access token in settings. Tools on your *Always allowed* list run
  straight away; every other tool call asks first.

While it records, a small pill at the bottom of the screen shows ✕ · timer · ✓, plus *Add screen* for the
Assistant. Then it says what it's doing ("Transcribing", "Thinking") and confirms ("Typed · copied"). The pill never
takes the focus from your app, and it is kept out of screenshots and screen shares.

## Privacy

Same rules as the Prosper app:

- The API key, MCP token and history (`%APPDATA%\ProsperPy`) are encrypted with Windows DPAPI for your account.
- The log has timings and status codes only.
- Only your AI endpoint, and your MCP server if you add one, are contacted.
- Screenshots are taken only when you ask.
- Terminals never get a Ctrl+C.

## Compared with the app and the extension

| | `ProsperVoice.exe` | Command Palette extension | `prosper.pyw` |
|---|---|---|---|
| Install | none | MSIX package (admin once, to trust its certificate) | none |
| Needs | Windows 10/11 | PowerToys | Python 3.9+ |
| Answers and history inside Command Palette | — | yes | no (own windows) |
| MCP sign-in in the browser (OAuth) | yes | yes | token only |
| Clipboard put back after paste | every format | every format | text only |

## Tests

`python prosper-py/test_prosper.py` runs anywhere. It covers the OpenAI request flow against a local fake server,
including an MCP approval round, plus the PNG encoder, the WAV silence check and the prompts. On Windows it also
checks DPAPI, the clipboard, window detection and screenshots. The *Prosper Python script* workflow runs it on Windows
with Python 3.9 and 3.13.
