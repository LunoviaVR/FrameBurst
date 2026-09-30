# FrameBurst

A ShareX-style tray screenshot tool built around one goal: capturing the highest-quality image possible,
with every step done on the GPU (NVIDIA, AMD and Intel).

## How it works

1. **Capture.** DXGI Desktop Duplication hands over the compositor's own desktop surface as a D3D11
   texture, so there are no GDI `BitBlt` copies. Each monitor is captured on the GPU that drives it. On a
   hybrid laptop the displays usually belong to the Intel iGPU, so that GPU does the capture.
2. **Process.** An HLSL compute shader (SM 5.0, any D3D11 feature level 11 GPU) rotates portrait or flipped
   monitors and outputs 8-bit sRGB. An 8-bit desktop surface is copied through bit for bit. When Windows
   composites in FP16 (Auto Color Management does this even on SDR monitors), the linear surface is
   encoded back to sRGB exactly. `--selftest <dir> --pixeltest` checks all 16,777,216 colours on every
   monitor.
3. **Encode.** A custom lossless PNG encoder filters rows adaptively in parallel and runs a multi-threaded
   deflate.

A typical run on an RX 9060 XT with 3 monitors (9.9 MP): about 30 ms from hotkey to pixels in RAM. The GPU
shader time is 0.4 ms.

## Usage

| Default hotkey     | Action                                 |
|--------------------|----------------------------------------|
| PrintScreen        | Region (drag, or click a window)       |
| Ctrl+PrintScreen   | All monitors                           |
| Shift+PrintScreen  | Monitor under cursor                   |
| Alt+PrintScreen    | Active window                          |

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
and a file. Double-click the tray icon to open Settings. The **GPU** tab lists every adapter and display
and includes a capture benchmark.

If PrintScreen won't register, turn off *Settings › Accessibility › Keyboard › Use the Print screen key
to open screen capture*.

## Build

```
dotnet build -c Release
bin\Release\net8.0-windows\FrameBurst.exe
bin\Release\net8.0-windows\FrameBurst.exe --selftest <dir>   # headless end-to-end check
```

Requires Windows 10 1803+ / 11, the .NET 8 runtime and a D3D11 GPU.

## Limits

- **HDR isn't supported.** On a monitor in Windows HDR mode, captures are clipped to SDR and look washed out
  or blown out. Turn HDR off for that monitor before capturing it.
- DRM-protected video shows up black. This is a Desktop Duplication rule.
- The secure desktop (UAC, lock screen) cannot be captured.
- The cursor overlay is drawn as a plain bitmap, so inverting cursors (such as the I-beam) are approximated.
