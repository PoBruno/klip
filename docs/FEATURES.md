# Klip features and shortcuts

Everything Klip does, area by area, with the keys that go with it. New here? The [README](../README.md) has the short tour.

- [Global shortcuts and tray](#global-shortcuts-and-tray)
- [Clipboard history (Win+V panel)](#clipboard-history)
- [Screen capture](#screen-capture)
- [Screen recording](#screen-recording)
- [Image editor](#image-editor)
- [Video editor](#video-editor)
- [Settings](#settings)
- [Data, uninstall and portable](#data-uninstall-and-portable)

## Global shortcuts and tray

| Action | Default | Notes |
|---|---|---|
| Open history | `Ctrl+Shift+V` | Set it to `Win+V` to replace the Windows panel |
| New capture | `Ctrl+Shift+S` | Set it to `PrintScreen` or `Win+Shift+S` to replace the Snipping Tool |
| Stop recording | `Ctrl+Shift+X` | Only registered while a recording runs |
| Capture text (OCR) | off | Suggested `Ctrl+Shift+T` |
| Repeat last region | off | Suggested `Ctrl+Shift+R` |
| Record video | off | Suggested `Ctrl+Shift+G`, opens the capture in MP4 mode |

- Change any of them in **Settings > Shortcuts**. A shortcut taken by another app shows a warning in the tray.
- **Taking over Windows keys:** picking `Win+V`, `Win+Shift+S` or `PrintScreen` makes Klip take that key from Windows (registry, with a backup of the original values). Taking `Win+Shift+S` also disables `Win+S`, a Windows limitation. Every card has **Give back to Windows**, and **Restore default shortcuts** undoes all of it.
- If a Windows update hands a key back to the Windows tool, Klip tells you once.
- **Tray:** left click opens the history (or stops a recording). The menu has capture, capture last region, video editor, pinned images, pause history, ignore the next copy and settings.

## Clipboard history

Everything you copy, kept in a local SQLite database with full text search. Opens next to the text caret (or the mouse).

**What it does**
- **Tabs:** Recent, Favorites, Images, Text, Files, Snippets, Emoji. Snippets and Emoji can be hidden.
- **Search** as you type (just start typing) and **filter by date**: today, yesterday, last 7 or 30 days.
- **Pin and favorite:** pinned items stay on top, and neither pinned nor favorites are ever cleaned up.
- **Paste several items at once:** Shift+click picks items to paste together (one per line), Ctrl+click picks a sequence where every Ctrl+V pastes the next one. Up to 20 items, no confirm step: pick and paste. Esc gives the old clipboard back. An unused sequence cancels itself after 2 minutes.
- **Collect mode** (panel menu): everything you copy is gathered, then **Paste all**.
- **Pause** the history for 15 min, 1 h or until resumed, or **ignore the next copy**.
- **Snippets:** saved texts with a name and an optional alias. Type the alias in search and press Enter. Placeholders `{date}` `{time}` `{datetime}` `{clipboard}`.
- **Emoji and symbols** picker, colored, searchable.
- **Preview** with Space: long text, images with zoom and the text found in the image (OCR) with a copy button.
- **Paste variants:** plain text, or **type as keystrokes** for fields that block paste. Optional "always paste as plain text", with a list of apps that keep formatting.
- **Drag a card out** into any app or Explorer: images become png files, text becomes text.
- **Secrets:** tokens, keys and connection strings are not stored by default. Optionally keep them for 30 s to 2 min, masked. Copies from password managers are never stored.
- **Excluded apps:** nothing copied from them is saved.

**Keys while the panel is open**

| Key | Action |
|---|---|
| `Enter` | Paste (or copy only, see Settings). With a pick, paste the pick |
| `Shift+Enter` | Paste as plain text |
| `Ctrl+Enter` | Copy only, panel stays open |
| `Alt+Enter` | Type as keystrokes (text) |
| `Ctrl+1` ... `Ctrl+9` | Copy the Nth card (numbers show while Ctrl is held) |
| `Alt+1` ... `Alt+9` | Paste the Nth card |
| `Space` | Preview (Up, Down, PgUp, PgDn scroll it) |
| `Up` / `Down` | Move the selection |
| `Shift+Up` / `Shift+Down` | Add cards to a paste-together pick |
| `Ctrl+V` | With a pick: paste it and close |
| `Del` | Delete the item |
| `Ctrl+P` | Pin or unpin |
| `Ctrl+D` | Favorite or unfavorite |
| `Ctrl+E` | Open the image in the editor |
| `Ctrl+F` | Focus search |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next or previous tab |
| `Esc` | Clear the pick, then the search, then close |

**Mouse**

| Gesture | Action |
|---|---|
| Click | Paste (adds to the pick when one is active) |
| `Shift+click` | Pick to paste together |
| `Ctrl+click` | Pick to paste in sequence |
| `Alt+click` on an image | Open in the editor |
| Right click or `...` | Item menu: paste, plain text, copy, preview, save as, open in editor, pin on screen, copy text from image, save as snippet, pin, favorite, delete |
| Drag | Drop the item into another app or folder |

## Screen capture

Opens like the Windows overlay: every monitor freezes and the toolbar shows on the one under the mouse.

**Modes:** rectangle, window, full screen, free form, **scrolling** (scroll at your own pace, then Done), **text** (OCR straight to the clipboard), **color** (HEX, RGB or HSL), and GIF or MP4 recording.

- **Open in editor:** hold `Ctrl` when you release the selection (the key can be changed), or set captures to always open in the editor.
- **Delay:** 3, 5 or 10 seconds.
- **Magnifier** with coordinates and pixel color.
- The result goes to the clipboard and the history, with a notification: click it to edit, or pin it.
- **Pin to screen:** the image floats on top. Drag to move, wheel to zoom, `Ctrl+wheel` for opacity, double click or `Esc` to close, right click for click through.
- **Repeat last region:** captures the last rectangle again, no overlay.
- **Auto save** (optional) to `Pictures\Screenshots` or a folder you pick.
- **Paste an image as a file:** with an image on the clipboard, `Ctrl+V` in an Explorer folder or on the desktop saves it there as a png. The address bar, search and rename boxes keep normal paste. File name template: `Klip {date} {time}`.

| Key | Action |
|---|---|
| `1` `2` `3` `4` | Rectangle, window, full screen, free form |
| `R` | Scrolling capture |
| `T` | Text (OCR) |
| `C` | Color picker |
| `G` / `V` | GIF / MP4 recording (press again for options) |
| Arrow keys | Move the cursor 1 px (`Shift`: 10 px) |
| `Ctrl` while releasing | Open in the editor |
| `Esc` | Cancel |

## Screen recording

Pick GIF (`G`) or MP4 (`V`) in the capture overlay and drag a region. A 3 second countdown, then a border around the region and a small toolbar you can drag anywhere.

- **MP4:** 30 or 60 fps, three quality levels, output size (native, 1440, 1080, 720), system audio and any number of microphones mixed. Hardware encoder (H.264, HEVC or AV1 when available).
- **GIF:** 10, 15 or 20 fps, scale 100, 75 or 50%.
- **Toolbar:** stop, pause and resume (MP4), mute the mic, mute system audio, show or hide the cursor and the border. Warns if frames are being dropped.
- **Meeting mode** (MP4): nothing on screen, stop from the tray or the hotkey.
- Stop with the toolbar, `Ctrl+Shift+X` or a click on the tray icon.
- Saved to `Videos\Gravacoes de Tela` (configurable) and added to the history. A GIF opens in the video editor when it is done.
- Crash safe: the MP4 is written in fragments, so a crash keeps what was recorded.

## Image editor

A whiteboard canvas: pan freely, zoom on the cursor, paste images onto it. Every edit is copied to the clipboard on its own.

**Tools:** select, pen, highlighter (straight lines, `Ctrl` for freehand), eraser, rectangle, ellipse, line, arrow, text, emoji, crop, blur. Plus rotate, **remove background**, **extract text** (OCR), **auto redact** (emails, phones, cards, IPs and ID numbers found by OCR), pin to screen and save as PNG or JPEG.

| Key | Action |
|---|---|
| `V` `P` `H` `E` | Select, pen, highlighter, eraser |
| `R` `O` `L` `A` | Rectangle, ellipse, line, arrow |
| `T` `C` `B` | Text, crop, blur |
| `Enter` / `Esc` | Apply / cancel the crop |
| `Del` | Delete the selection |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo |
| `Ctrl+C` | Copy now |
| `Ctrl+V` | Paste an image onto the canvas |
| `Ctrl+S` | Save as |
| `Ctrl+0` | Fit to window |
| `Ctrl+wheel` | Zoom at the pointer |
| `Space` + drag | Pan |
| `Shift` while drawing | Keep proportions, snap lines to 45 degrees |

## Video editor

Opens GIF and MP4 files: from a recording, the history, the tray, drag and drop or `Ctrl+V`.

- **Timeline:** drag the playhead to scrub, drag a segment's edges to trim, drag its body to move it (it snaps to neighbors). Gaps play as black.
- **Tracks (MP4):** duplicate a segment to a new track, set volume and opacity per clip, drop segments between tracks.
- **Right click a segment:** remove leaving a gap, delete and close the gap, swap with the previous or next, duplicate to a new track.
- **Audio:** mute or set the volume.
- **GIF tools:** reduce fps (10, 15, 20) and resize (100 to 25%).
- **Export** to GIF or MP4. MP4 export and MP4 to GIF need `ffmpeg`; Klip finds it on the PATH or offers to download it.

| Key | Action |
|---|---|
| `Space` | Play / pause |
| `S` | Split at the playhead |
| `Del` | Remove the segment, leave a gap |
| `Shift+Del` | Delete the segment and close the gap |
| `Left` / `Right` | Previous / next frame |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo |
| `Ctrl+D` | Duplicate to a new track (MP4) |
| `Ctrl+V` | Open a file from the clipboard |

## Settings

| Page | What is there |
|---|---|
| General | Start with Windows, theme (system, light, dark), language (15, applied right away) |
| Shortcuts | The six shortcuts, give back to Windows, restore defaults, the list of panel keys |
| Clipboard | Panel position and size, what Enter does, tabs, plain text paste, images as files, cleanup limits (items, images, disk space, age) |
| Capture | Starting mode, what happens after a capture, editor key, magnifier, color format, auto save |
| Recording | Folder, MP4 fps, quality, size and codec, audio devices, GIF fps and scale, meeting mode, ffmpeg path |
| Privacy | Secrets, excluded apps, clear history on exit |
| Maintenance and backup | Export and import the history as `.zip`, disk usage, compact the database, diagnostics |

## Data, uninstall and portable

- **Data** lives in `%LOCALAPPDATA%\Klip`: settings, the history database (`data\klip.db`) and its images. Nothing leaves your machine.
- **Uninstall** gives `Win+V`, `Win+Shift+S` and `PrintScreen` back to Windows (after the next sign in). It keeps your data folder, delete it by hand for a clean slate. If you used the admin option for `Win+V`, run **Restore default shortcuts** before uninstalling.
- **Portable exe:** the same app in one file, same data folder. It has no uninstaller, so run **Settings > Shortcuts > Restore default shortcuts** before deleting it if Klip took over any Windows key.
- **Command line:** `--minimized` starts in the tray, `--settings` opens Settings, `--verbose` writes a detailed log to `%LOCALAPPDATA%\Klip\startup.log`.
