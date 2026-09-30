<p align="center"><img src="Assets/logo.png" alt="FrameBurst" width="480"></p>

# FrameBurst

A system tray screenshot tool for Windows built around one goal: capturing the highest-quality image
possible, with every step done on the GPU through Direct3D 11 (NVIDIA, AMD and Intel).

- **Pixel-exact captures.** Every colour on screen comes out exactly as displayed, verified across all
  16,777,216 colours.
- **Fast.** About 45 ms from hotkey to pixels in memory for three monitors.
- **Works over full-screen games.** The capture hotkeys take priority even when a full-screen app has the keyboard.
- **Built-in editor.** Pen, highlighter, arrows, boxes, text and blur, applied before you pick the region.
- **Screen colour picker** with a magnifier.
- **Lossless PNG** saved to disk and copied to the clipboard.
- **Automatic updates** from GitHub releases.

## Install

1. Download `FrameBurst-Setup-<version>.exe` from the
   [latest release](https://github.com/LunoviaVR/FrameBurst/releases/latest).
2. Run it. No admin rights are needed; it installs for your user account by default (choose *all users* in
   the first step if you prefer). .NET is included, so nothing else needs to be installed.
3. Choose whether FrameBurst should start when you sign in to Windows (on by default) and whether you want a
   desktop shortcut.

FrameBurst runs in the system tray (the notification area next to the clock). To uninstall it, use
*Settings › Apps › Installed apps › FrameBurst*.

**Requirements:** Windows 10 version 2004 or later, or Windows 11, on a 64-bit PC with a Direct3D 11 GPU.

### Updates

FrameBurst checks GitHub for a new release once a day. When one is available a notification appears; click it
to see what's new, then confirm to download and install. FrameBurst closes, updates and starts again on its
own. The installer's SHA-256 checksum is compared with the one GitHub publishes before it runs.

You can also check at any time from the tray menu (**Check for updates…**) and turn automatic checks off in
*Settings › Updates*.

## Using FrameBurst

### Taking a screenshot

| Input              | Captures                                       |
|--------------------|------------------------------------------------|
| PrintScreen        | A region (drag, or click a window)             |
| Ctrl+PrintScreen   | All monitors                                   |
| Shift+PrintScreen  | The monitor under the cursor                   |
| Alt+PrintScreen    | The active window                              |
| Click tray icon    | A region, after a 1-second delay               |

Every mode is also in the tray icon's right-click menu. The hotkeys can be changed in Settings.

If PrintScreen opens the Windows Snipping Tool instead, turn off *Settings › Accessibility › Keyboard › Use
the Print screen key to open screen capture*.

### Choosing a region

A region capture freezes the screen and shows an overlay across all monitors:

| Action | |
|---|---|
| Drag | Select a rectangle |
| Click | Take the window under the cursor (it is highlighted) |
| Enter | Take the whole monitor under the cursor (or the current selection while dragging) |
| F | Take the whole desktop (all monitors) |
| Arrow keys | Move the cursor 1 px for precise edges |
| Esc or right-click | Cancel |

A magnifier next to the cursor shows the pixels around it, the screen coordinates and the colour under the
cursor.

### Editing before you capture

A toolbar sits at the top centre of the monitor you are on. Draw on the frozen screen first, then switch back
to **Select** (S) and drag a region or click a window. The capture includes your edits.

| Tool | Key | |
|---|---|---|
| Select / capture | S | The default: drag or click to capture |
| Pen | P | Freehand |
| Highlighter | H | Translucent marker |
| Arrow | A | Drag from tail to head |
| Rectangle | R | Box outline |
| Text | T | Click and type. Enter = done, Shift+Enter = new line, Esc = discard |
| Blur | B | Drag over anything to hide it. It permanently rewrites those pixels |

Pick a colour from the toolbar. The mouse wheel or `[` `]` changes the line width, text size or blur
strength. Ctrl+Z undoes and Ctrl+Y redoes (also on the toolbar). Right-click cancels the stroke in progress.

Edits change only the pixels they cover; everything else stays pixel-perfect.

### Screen colour picker

Right-click the tray icon and choose **Pick screen colour**. The screen freezes and a magnifier follows the
cursor, showing the hex and RGB values. Click, or press Enter or Space, to copy the colour as `#RRGGBB` to the
clipboard. Arrow keys move 1 px, and Esc or right-click cancels. The colours come from the same pixel-exact
capture as screenshots.

### Where screenshots go

By default each screenshot is:

- saved as a lossless PNG to `Pictures\FrameBurst`,
- copied to the clipboard as an image, a PNG and a file, so it pastes into chat apps, image editors and
  File Explorer alike,
- announced with a notification. Click it to show the file in File Explorer.

### Settings

Open Settings from the tray icon's right-click menu (**Settings…**).

| Tab | Options |
|---|---|
| Capture | Hotkeys for each mode, a capture delay in milliseconds, include the mouse cursor |
| Output | Save folder, file name pattern, save to file, copy to clipboard, show notification, PNG compression |
| Updates | Current version, check for updates automatically, link to the release notes |

The file name pattern is plain text with a date in `{braces}` using .NET date format codes, for example
`FrameBurst_{yyyy-MM-dd_HH-mm-ss-fff}`. The **Read more.** link under it lists every code. PNG compression
only trades file size for speed; the image is lossless at every setting.

Settings are stored in `%APPDATA%\FrameBurst\settings.json`, and a log is written to
`%APPDATA%\FrameBurst\frameburst.log`.

## How it works

1. **Capture.** Windows Graphics Capture hands over each monitor's frame straight from the compositor as a
   Direct3D 11 texture, with no GDI `BitBlt` copies. All monitors are captured at the same moment, each on
   the D3D11 device of the GPU that drives it. On a hybrid laptop the displays usually belong to the Intel
   iGPU, so that GPU does the capture.
2. **Process.** Frames are requested in FP16 (linear). A D3D11 compute shader (HLSL, Shader Model 5.0, any
   feature level 11 GPU) encodes them to 8-bit sRGB exactly, whether Windows composites in 8-bit or in FP16
   (Auto Color Management does this even on SDR monitors). Only the finished pixels are copied back to RAM.
3. **Encode.** A custom lossless PNG encoder filters rows adaptively in parallel and runs a multi-threaded
   deflate.

A typical run on an RX 9060 XT with 3 monitors (9.9 MP) takes about 45 ms from hotkey to pixels in RAM.
The compute shader itself runs in about 0.5 ms.

The hotkeys are registered with Windows and also watched by a low-level keyboard hook. Full-screen games
often read the keyboard directly with Windows hotkeys disabled; the hook sees the key first, so the capture
still starts, and the game does not receive the key press.

## Limits

- **HDR isn't supported.** On a monitor in Windows HDR mode, captures are clipped to SDR and look washed out
  or blown out. Turn HDR off for that monitor before capturing it.
- DRM-protected video shows up black. Windows Graphics Capture enforces this.
- On Windows 10 a yellow capture border may flash briefly around the screen. Windows 11 hides it.
- The secure desktop (UAC prompts, the lock screen) cannot be captured.
- Games running as administrator, or protected by some anti-cheat software, may block the hotkeys.
- The cursor overlay is drawn as a plain bitmap, so inverting cursors (such as the I-beam) are approximated.

## Building from source

Requires the .NET 8 SDK.

```
dotnet build -c Release
bin\Release\net8.0-windows10.0.22621.0\FrameBurst.exe
```

### Self-tests

`FrameBurst.exe --selftest <dir>` runs a headless end-to-end check: it lists GPUs and monitors, captures every
monitor, checks that the PNG encoder round-trips bit-exactly and reports timings. Extra checks:

| Flag | Checks |
|---|---|
| `--pixeltest` | All 16,777,216 colours come out exactly on every monitor |
| `--annotest` | Edits change only the pixels they cover |
| `--pickertest` | The colour picker reads exact screen colours |
| `--uitest` | Drives the real overlay: tools, undo/redo and a region selection |

### Installer

Install [Inno Setup 6](https://jrsoftware.org/isinfo.php), then run:

```
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

This publishes a self-contained build and writes `publish\FrameBurst-Setup-<version>.exe`. The version comes
from `<Version>` in `FrameBurst.csproj`, or from `-Version 1.2.0`.

### Releasing

Pushing a version tag builds the installer on GitHub Actions and publishes it as a release, which installed
copies then pick up through the updater:

```
git tag v1.1.0
git push origin v1.1.0
```

## License

FrameBurst is free software: you can redistribute it and/or modify it under the terms of the
[GNU General Public License v3.0](LICENSE) as published by the Free Software Foundation, either version 3 of
the License, or (at your option) any later version. It is distributed in the hope that it will be useful, but
WITHOUT ANY WARRANTY; see the license for details.
