<div align="center">

# PaperNote

**Just write.**

A quiet notes app for Windows. Opens like Notepad, organizes like the big ones,
and every note stays on your disk as a plain Markdown file.

Free · ~20 MB · No account · No cloud · Works offline

[**Download the latest release**](https://github.com/se-kunal/PaperNote/releases/latest)

</div>

---

## Why this exists

Notepad forgets everything the moment you close it. Word is a cargo ship you board
to write one sentence. Notepad++ is for code. And every modern notes app wants an
account, a cloud, and a subscription to hold your grocery list.

PaperNote is the missing middle: instant like Notepad, organized and rich like the
big apps, and local-first like software used to be. Your notes are plain `.md` files
in your Documents folder. If PaperNote disappeared tomorrow, your notes wouldn't notice.

## Install

1. Download `PaperNote-Setup-x.x.x.exe` from the [latest release](https://github.com/se-kunal/PaperNote/releases/latest).
2. Run it. Installs in seconds, no admin rights needed.
3. That's it. PaperNote opens when the installer finishes and lives in your system tray.

> The installer fetches the Microsoft WebView2 runtime once if your PC doesn't have it
> (most Windows 10/11 machines already do).

**Requirements:** Windows 10 or 11, 64-bit.

## First five minutes

| Do this | Get this |
|---|---|
| Press **Ctrl + Alt + N** anywhere in Windows | A quick-note card pops over whatever you're doing. Type, click away, saved. |
| Press **Ctrl + Shift + N** | The full app opens with a fresh note. |
| Type **/** in a note | Slash commands: headings, lists, tables, diagrams, templates. |
| Press **Ctrl + K** | The command palette. Everything is in there. |
| Press **F11** | Focus mode. Just you and the page. |

Notes title themselves from the first line and save as you type. There is no save button.

## What it does

**Capture**
- Global quick-note hotkey (`Ctrl+Alt+N`) that works from any app
- Lives in the tray; closing the window keeps it one keystroke away
- Drag or "Open with PaperNote" any `.md`, `.txt`, `.csv`, `.json` or `.log` file to preview it, and keep it as a note only if you want

**Write**
- Clean editor with Markdown, checklists, tables, images and code blocks
- Paste cells from Excel, they land as a real table
- Tables can sum, average and count their own columns, and fill down number or date series
- Type a flowchart as text, PaperNote draws the diagram (Mermaid, built in)
- Paste screenshots straight into a note, drag to resize
- Templates: meeting notes, project tracker, weekly status, project brief, standup, 1:1

**AI (optional, bring your own OpenAI key)**
- Paste a raw meeting transcript, PaperNote cleans it section by section in front of you,
  then writes the minutes: decisions, action items, owners
- Paste a rough draft, get back a clear, send-ready version in your own voice
- No key? A lower-quality offline cleanup mode still works. The key is stored encrypted
  on your machine and is only ever sent to OpenAI.

**Organize & find**
- Folders, tags, pinned notes, instant full-text search as you type
- Soft delete with a trash you can recover from

**Own & share**
- Every note is a plain Markdown file in `Documents\PaperNote` — grep it, back it up, open it in anything
- Lock private notes with a password, encrypted on your device
- Share a note with a teammate over the office network directly, no cloud in the middle
- Export any note to PDF or Markdown
- Sticky mode: float any note on top of every window while you work

**Feel**
- Warm paper theme, light and dark
- Serif or sans writing font, ruled lines if you like them
- Word count and reading time on every note

## Where your notes live

```
Documents\PaperNote\
├── Meetings\
│   └── Q3 Kickoff.md
├── Ideas\
│   └── that standup thought.md
└── ...
```

Plain files, human names, no lock-in. The app keeps a small search index in
`%AppData%\PaperNote`, but the files are the truth. Delete the app, keep the notes.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl + Alt + N` | Quick note from anywhere in Windows |
| `Ctrl + Shift + N` | Open app with a new note |
| `Ctrl + K` | Command palette |
| `/` | Slash commands in the editor |
| `F11` | Focus mode (hide sidebars) |
| `Esc` | Discard quick note |
| `Ctrl + Enter` | Save quick note |

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download), [Node.js](https://nodejs.org/),
and [Inno Setup 6](https://jrsoftware.org/isinfo.php) (installer only).

```powershell
git clone https://github.com/se-kunal/PaperNote.git
cd papernote

# one-click: builds the editor, publishes the app, produces the installer
./build.ps1
```

For day-to-day development:

```powershell
# build the TipTap editor bundle
cd app/editor-web
npm install
npm run build

# run the app
cd ../PaperNote
dotnet run
```

## FAQ

**Is it really free?** Yes. The optional AI features use your own OpenAI key, so those
API calls are between you and OpenAI.

**Does it phone home?** No. No telemetry, no account, no analytics. The only network
traffic is the AI calls you explicitly trigger and the LAN sharing you explicitly enable.

**Can I sync between machines?** Your notes are plain files in Documents, so any sync
tool you already trust (OneDrive, Syncthing, a USB stick) works on them.

**What happens to my notes if I uninstall?** Nothing. They stay in `Documents\PaperNote`.
The uninstaller doesn't touch them.

**Mac or Linux?** Windows only for now.

## Feedback

This is an early build, built in public. If something breaks or something's missing,
[open an issue](https://github.com/se-kunal/PaperNote/issues). I read everything and I ship fast.

---

Built by [Kunal Shokeen](https://www.linkedin.com/in/kunalshokeen)
