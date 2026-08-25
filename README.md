# BadWolf ASCII Player

A Windows desktop player that renders local video frames as real-time ASCII art while FFmpeg decodes both video and audio.

## Stack

- .NET 8
- WPF
- `ffmpeg.exe` for decoded/scaled RGB video frames and PCM audio
- `ffprobe.exe` for video metadata
- NAudio for Windows audio output

The application does **not** use WPF `MediaElement` or Windows Media Player, so Windows Media Player is not required.

## Requirements

1. Windows 10/11.
2. Visual Studio 2022 with the **.NET desktop development** workload, or .NET 8 SDK.
3. FFmpeg Windows build containing `ffmpeg.exe` and `ffprobe.exe`.

Put both executables in either:

- the solution-level `tools/` directory (recommended; MSBuild copies them to the output automatically), or
- any directory on `PATH`.

The app also checks beside its own executable and in its output `tools/` directory.

## Run

Open `BadWolfAsciiPlayer.sln`, restore NuGet packages, select the `BadWolfAsciiPlayer` project, and press F5.

Then either:

- click **Open video**, or
- drag and drop a local `MP4`, `MKV`, `AVI`, `MOV`, `WEBM`, `M4V`, or `WMV` file into the window.

## Controls

- Play / Pause
- Seek
- Volume
- Color or monochrome ASCII
- Bitmap or selectable-text display mode
- Select and copy ASCII characters directly with the mouse and `Ctrl+C`
- **Copy frame** to copy the complete current frame as plain ASCII text
- Edge enhancement: **Off / Low / Medium / High**
- 80 / 120 / 160 / 200 / 240 columns
- 15 / 24 / 30 / 60 ASCII FPS

When text is selected in **Selectable text** mode, the displayed text is temporarily kept stable so the selection is not lost while playback continues.

Edge enhancement is calculated before the final ASCII downsampling. FFmpeg supplies a 3x denser RGB frame and the renderer uses a Canny-style pipeline: 5x5 Gaussian smoothing, color-aware Scharr gradients, non-maximum suppression, and adaptive hysteresis thresholding. Luminance and chroma boundaries are analyzed separately and the strongest response is retained, so contours with similar brightness but different color can still survive. Confirmed contours increase glyph density and brightness instead of being replaced by directional slash characters.

## How it works

Bitmap video path:

`FFmpeg -> 3x RGB24 analysis frame -> Gaussian blur -> luminance/chroma Scharr gradients -> non-maximum suppression -> adaptive hysteresis -> ASCII renderer -> WPF WriteableBitmap`

Selectable-text video path:

`FFmpeg -> 3x RGB24 analysis frame -> Gaussian blur -> luminance/chroma Scharr gradients -> non-maximum suppression -> adaptive hysteresis -> glyph conversion -> read-only WPF TextBox`

Audio path:

`FFmpeg -> 48 kHz stereo PCM -> NAudio -> Windows audio device`

A playback clock shared by the controls and ASCII decoder keeps the rendered frames aligned with audio. Stale video frames are dropped if rendering falls behind.

## Notes

This version intentionally has no YouTube/network support.
