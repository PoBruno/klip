<div align="center">

<img src="docs/banner.png" alt="Klip" width="720">


# Klip

**A better clipboard history and screenshot tool for Windows 11.**

Everything the built-in Win+V panel and Snipping Tool should have been, in one small app that lives in your tray.

[![Build](https://github.com/PoBruno/klip/actions/workflows/ci.yml/badge.svg)](https://github.com/PoBruno/klip/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/PoBruno/klip?display_name=tag&sort=semver)](https://github.com/PoBruno/klip/releases/latest)
[![License: GPLv3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows11&logoColor=white)](#)

[Download](#download) - [Features](#features) - [Build from source](#build-from-source) - [Contributing](#contributing)

</div>

---

## Why Klip

Klip started as a fix for two things that annoy me on Windows every single day.

The **Win+V** clipboard panel looks great and feels great, the UI is smooth and native, but the actual history behind it is weak: short, no real search, it forgets stuff, and you cannot organize anything. The experience is good, the feature is not.

The **Win+Shift+S** capture is the opposite kind of frustration: grabbing the shot works fine, but the little editor that pops up after is missing half the tools you actually want, so you end up pasting into some other app just to draw an arrow.

So the idea is simple: keep the native look and feel that Microsoft already got right, and fix everything behind it. Same panel, same overlay, none of the limits.

## What is Klip

Klip does two things and tries to do them really well:

- **Clipboard history** with the exact look and feel of the native Win+V flyout, minus the limits. Unlimited history, real search, date filters, favorites, tabs per content type.
- **Screen capture** that mirrors the Win+Shift+S overlay, plus the parts the native tool is missing: scrolling capture and a proper editor.

It runs as a native WPF app, uses Fluent Design and Mica, and can take over the `Win+V` and `Win+Shift+S` shortcuts if you let it. No Electron, no browser, no telemetry.

## Features

### Clipboard history
- Unlimited history backed by a local SQLite database (with full text search).
- Same look as the native Win+V panel, so there is nothing new to learn.
- Search as you type, filter by date, pin favorites.
- Tabs per type: text, images, files, links.
- Images stored on disk with real thumbnails and an LRU cache, so scrolling stays smooth.
- Paste keeps the original formatting (HTML and RTF), or paste as plain text when you want.
- Built in secret detector that flags things like tokens and passwords so they do not linger in history.
- Respects the clipboard formats that password managers use to opt out.

### Screen capture
- Faithful copy of the Win+Shift+S overlay: dim, toolbar, dotted border, the selected area stays lit.
- Modes: rectangle, window, full screen, free form.
- **Scrolling capture** to grab a whole page that does not fit on screen (stitching approach inspired by ShareX, see [credits](#credits)).
- Multi monitor aware, geometry always in physical pixels so nothing drifts on mixed DPI setups.

### Quick editor
- Post capture editor in the Snipping Tool style: pen, highlighter, shapes, arrow, crop.
- Free text on top of the image, Excalidraw style.
- Auto copy to the clipboard on every edit, so the latest version is always ready to paste.

### System integration
- Optional takeover of `Win+V` and `Win+Shift+S`. Klip manages the registry keys for you and reverts them cleanly on uninstall.
- Single instance, starts with Windows (optional), sits quietly in the tray.
- Import and export your history as a `.zip`.

## Download

> Note: the first public release is being prepared. Links below go live once it ships.

### winget (recommended)
```powershell
winget install pobruno.Klip
```

### Installer
Grab `Klip-Setup-<version>.exe` from the [latest release](https://github.com/PoBruno/klip/releases/latest). It installs per user (no admin needed), adds Start menu and optional desktop shortcuts, and the uninstaller puts your shortcut keys back the way they were.

### Portable
Prefer no install? Grab `Klip-<version>-portable.exe`. It is a single self contained file, so you can drop it anywhere and run it. No .NET install required.

## Build from source

You need the **.NET 9 SDK** and Windows 11.

```powershell
git clone https://github.com/PoBruno/klip.git
cd klip

dotnet build Klip.sln            # build
dotnet test Klip.sln             # run the tests (xunit)
dotnet run --project src/Klip.App   # run the app (shows up in the tray)
```

### Packaging

```powershell
.\tools\build-exe.ps1          # single self contained exe -> publish\Klip.exe
.\tools\build-installer.ps1    # Inno Setup installer -> dist\Klip-Setup-<version>.exe
```

The installer script needs [Inno Setup 6](https://jrsoftware.org/isdl.php) (`winget install JRSoftware.InnoSetup`).

> Klip is not shipped as MSIX on purpose: the sandbox blocks the shortcut takeover (Win+V) and the global keyboard hook that Klip relies on.

## Releases

CI runs the build and tests on every push and pull request. Pushing a `vX.Y.Z` tag builds everything, runs the tests, and publishes a GitHub Release with the installer and the portable exe attached.

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## Tech

- WPF on .NET 9 (`net9.0-windows`), C# 13.
- Clean split: `Klip.Core` (pure domain, no WPF), `Klip.Interop` (all the Win32 P/Invoke), `Klip.App` (WPF, MVVM).
- [WPF-UI](https://github.com/lepoco/wpfui) for Fluent theming and Mica, [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) for MVVM, [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) for the tray.
- SQLite with FTS5 for history and search.

## Contributing

Issues and pull requests are welcome. If you are planning something bigger, open an issue first so we can talk it through before you write a lot of code.

## Credits

- The scrolling capture stitching is inspired by the algorithm used in [ShareX](https://github.com/ShareX/ShareX). Klip only references the approach, it does not reuse their code.
- Emoji artwork from [Twemoji](https://github.com/jdecked/twemoji) by Twitter, licensed under [CC-BY 4.0](https://creativecommons.org/licenses/by/4.0/).
- Emoji names and search keywords from the [Unicode CLDR](https://cldr.unicode.org/).

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committer and reviewer: [PoBruno](https://github.com/PoBruno)
- Approver: [PoBruno](https://github.com/PoBruno)

Privacy policy: this program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. Klip runs fully on your machine, with no telemetry.

## License

Klip is released under the [GNU GPLv3](LICENSE).

---

Portugues? Veja o [README-pt.md](README-pt.md).
