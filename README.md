<p align="center"><img src="Assets/logo.png" alt="FrameBurst" width="480"></p>

# FrameBurst

A system tray screenshot tool for Windows built around one goal: capturing the highest-quality image
possible, with every step done on the GPU through Direct3D 11 (NVIDIA, AMD and Intel).

## How it works

1. **Capture.** Windows Graphics Capture hands over each monitor's frame straight from the compositor as a
   Direct3D 11 texture, with no GDI `BitBlt` copies. All monitors are captured at the same moment, each on
   the D3D11 device of the GPU that drives it. On a hybrid laptop the displays usually belong to the Intel
   iGPU, so that GPU does the capture.
2. **Process.** Frames are requested in FP16 (linear). A D3D11 compute shader (HLSL, Shader Model 5.0, any
   feature level 11 GPU) encodes them to 8-bit sRGB exactly, whether Windows composites in 8-bit or in FP16
   (Auto Color Management does this even on SDR monitors). Only the finished pixels are copied back to RAM.
   `--selftest <dir> --pixeltest` checks all 16,777,216 colours on every monitor.
3. **Encode.** A custom lossless PNG encoder filters rows adaptively in parallel and runs a multi-threaded
   deflate.

A typical run on an RX 9060 XT with 3 monitors (9.9 MP) takes about 45 ms from hotkey to pixels in RAM.
The compute shader itself runs in about 0.5 ms.

## Usage

| Input              | Action                                   |
|--------------------|------------------------------------------|
| PrintScreen        | Region (drag, or click a window)         |
| Ctrl+PrintScreen   | All monitors                             |
| Shift+PrintScreen  | Monitor under cursor                     |
| Alt+PrintScreen    | Active window                            |
| Click tray icon    | Region, after a 1-second delay           |
| Right-click tray   | Menu: capture modes, colour picker, Settings, Exit |

The hotkeys can be changed in Settings. If PrintScreen won't register, turn off *Settings › Accessibility ›
Keyboard › Use the Print screen key to open screen capture*.

Region overlay controls: drag to select, click to take the highlighted window, **Enter** takes the monitor
under the cursor, **F** takes the whole desktop, arrow keys move the cursor 1 px, **Esc** or right-click cancels.

### Editing in the overlay

A toolbar sits at the top centre of the monitor you are on. Edit the frozen screen first, then switch back
to **Select** (S) and drag a region or click a window. The capture includes your edits.

| Tool | Key | |
|---|---|---|
| Select / capture | S | the default: drag or click to capture |
| Pen | P | freehand |
| Highlighter | H | translucent marker |
| Arrow | A | drag from tail to head |
| Rectangle | R | box outline |
| Text | T | click, type; Enter = done, Shift+Enter = new line, Esc = discard |
| Blur | B | drag over anything; it permanently rewrites those pixels |

Colours are in the toolbar. The mouse wheel or `[` `]` changes line width, text size or blur strength.
Ctrl+Z undoes and Ctrl+Y redoes. Right-click cancels the stroke in progress.

Edits change only the pixels they cover, and everything else stays pixel-perfect (`--selftest <dir> --annotest`
verifies this).

### Screen colour picker

Right-click the tray icon and choose **Pick screen colour**. The screen freezes and a magnifier follows the
cursor, showing the hex and RGB values. Click (or press Enter) to copy the colour as `#RRGGBB` to the
clipboard. Arrow keys move 1 px, and Esc or right-click cancels. Colours come from the same pixel-exact
capture as screenshots (`--selftest <dir> --pickertest`).

### Output

Screenshots are saved as lossless PNG to `Pictures\FrameBurst` and copied to the clipboard as a DIB, a PNG,
and a file.

## Build

```
dotnet build -c Release
bin\Release\net8.0-windows10.0.22621.0\FrameBurst.exe
bin\Release\net8.0-windows10.0.22621.0\FrameBurst.exe --selftest <dir>   # headless end-to-end check
```

Requires Windows 10 version 2004 or later (or Windows 11), the .NET 8 runtime and a Direct3D 11 GPU.

## Limits

- **HDR isn't supported.** On a monitor in Windows HDR mode, captures are clipped to SDR and look washed out
  or blown out. Turn HDR off for that monitor before capturing it.
- DRM-protected video shows up black. Windows Graphics Capture enforces this.
- On Windows 10 a yellow capture border may flash briefly around the screen. Windows 11 hides it.
- The secure desktop (UAC, lock screen) cannot be captured.
- The cursor overlay is drawn as a plain bitmap, so inverting cursors (such as the I-beam) are approximated.
