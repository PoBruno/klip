<div align="center">

<img src="docs/demo.gif" alt="Klip in action: clipboard history, scrolling capture and the media editor" width="840">


# Klip

**Clipboard history, screen capture and recording for Windows 11, without the native limits.**

[![Build](https://github.com/PoBruno/klip/actions/workflows/ci.yml/badge.svg)](https://github.com/PoBruno/klip/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/PoBruno/klip?display_name=tag&sort=semver)](https://github.com/PoBruno/klip/releases/latest)
[![License: GPLv3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows11&logoColor=white)](#)

### Install

```powershell
winget install pobruno.Klip
```

or **[download the installer](https://github.com/PoBruno/klip/releases/latest)** (or the portable exe) from the latest release.

[Install](https://github.com/PoBruno/klip/releases/latest) - [Features](#features) - [Build from source](#build-from-source)

</div>

---

## What is Klip

The native Win+V panel and Snipping Tool look great, but they hold you back: the clipboard history is short and has no real search, and the capture editor is missing half the tools you want.

Klip keeps that same native look and feel, and removes the limits. Two things, done well:

- **Clipboard history** like Win+V, but unlimited, with real search, date filters, favorites, tabs and a colored emoji picker.
- **Screen capture** like Win+Shift+S, plus scrolling capture, GIF/MP4 screen recording and a proper editor.

Native WPF app, Fluent Design and Mica, lives in the tray. No Electron, no browser, no telemetry. It can take over the `Win+V` and `Win+Shift+S` shortcuts if you let it.

## Features

**Clipboard history**
- Unlimited, backed by local SQLite with full text search.
- Search as you type, filter by date, pin favorites, tabs per type.
- Keeps HTML/RTF formatting, or paste as plain text.
- Colored emoji and symbol picker, searchable in several languages.
- Built in secret detector so tokens and passwords do not linger.

**Screen capture**
- Faithful copy of the Win+Shift+S overlay.
- Modes: rectangle, window, full screen, free form.
- Scrolling capture to grab a whole page that does not fit on screen.
- Hold Ctrl while selecting to open the capture straight in the editor.
- Multi monitor aware, geometry in physical pixels.

**Screen recording**
- Record any region to MP4 (H.264 + AAC, hardware encoder, screen-content tuning) with system audio and microphones, or straight to GIF with its own encoder.
- Floating control hub during recording: drag it anywhere (any monitor), pause/resume, mute the mic or system audio, show/hide the cursor and the region border, all live.
- Crash-safe fragmented MP4: even if the app dies, what was recorded stays playable.

**Media editor**
- Timeline editing for recordings: split, trim, reorder and space out segments (gaps render as black), with undo/redo.
- GIF tools: reduce fps, resize, frame-accurate scrubbing; export MP4 to GIF.
- Exports through the built-in GIF encoder or `ffmpeg` when available.

**Editor**
- Pen, highlighter, shapes, arrow, free text, crop, rotate.
- Blur/redact, plus auto redact of emails/phones/cards via on-device OCR.
- Background remove, undo/redo, auto copy on every edit.

**System**
- Optional takeover of `Win+V` and `Win+Shift+S`, reverted cleanly on uninstall.
- Single instance, starts with Windows (optional), import/export history as `.zip`.

## Build from source

You need the **.NET 9 SDK** and Windows 11.

```powershell
git clone https://github.com/PoBruno/klip.git
cd klip

dotnet build Klip.sln            # build
dotnet test Klip.sln             # run the tests (xunit)
dotnet run --project src/Klip.App   # run the app (shows up in the tray)
```

Packaging:

```powershell
.\tools\build-exe.ps1          # single self contained exe -> publish\Klip.exe
.\tools\build-installer.ps1    # Inno Setup installer -> dist\Klip-Setup-<version>.exe
```

The installer script needs [Inno Setup 6](https://jrsoftware.org/isdl.php). Klip is not shipped as MSIX on purpose: the sandbox blocks the shortcut takeover and the global keyboard hook it relies on.

## Tech

- WPF on .NET 9 (`net9.0-windows`), C# 13, MVVM.
- Clean split: `Klip.Core` (pure domain), `Klip.Interop` (Win32 P/Invoke), `Klip.App` (WPF).
- SQLite with FTS5 for history and search.

## Contributing

Issues and pull requests are welcome. Planning something bigger? Open an issue first so we can talk it through.

## Credits

- Scrolling capture stitching inspired by [ShareX](https://github.com/ShareX/ShareX) (approach only, no reused code).
- Emoji artwork from [Twemoji](https://github.com/jdecked/twemoji), [CC-BY 4.0](https://creativecommons.org/licenses/by/4.0/). Emoji names from the [Unicode CLDR](https://cldr.unicode.org/).

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committer, reviewer and approver: [PoBruno](https://github.com/PoBruno)

Privacy: Klip runs fully on your machine and will not transfer any information to other networked systems unless specifically requested by the user. No telemetry.

## License

Klip is released under the [GNU GPLv3](LICENSE).

---

Portugues? Veja o [README-pt.md](README-pt.md).
