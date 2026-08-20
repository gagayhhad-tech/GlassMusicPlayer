# Glass Music Player 🎧

A glassmorphism-style music player for Windows, built with **WPF (.NET 8)** and a **WebView2** (HTML/CSS/JS) user interface. Plays your local audio library with a live audio visualizer, karaoke mode, audio analysis and an AI-like "Flow" radio.

## Features

- **Library** — scan a folder, drag & drop files into the window, delete tracks, favorites (hearts), albums grouped by name with track numbers.
- **Playback** — play/pause, next/previous, shuffle, repeat (all/one), seek, volume, mute, **global media keys** (Play/Pause/Next/Prev) and taskbar control support.
- **Playback queue** — reorder by drag & drop, per-track actions, multi-select.
- **Playlists** — create, rename, delete, add/remove tracks.
- **Equalizer** — 10-band EQ with presets.
- **Crossfade** — smooth transitions between tracks (configurable duration).
- **Visualizer** — background glass bars plus a **fullscreen visualizer** with three modes: Bars, Plasma, Aurora; accent-colored, spectrum-driven.
- **Karaoke / Lyrics** — synced lyrics highlighting in fullscreen karaoke mode, static lyrics, online lyrics search.
- **Flow radio** — builds an endless queue of similar tracks (energy, BPM, artist/album affinity) without stopping playback.
- **Audio analysis engine** — per-track BPM, energy, loudness and peak values (autocorrelation-based BPM detection), cached locally.
- **ReplayGain** — automatic per-track volume normalization toward −14 LUFS.
- **Sleep timer** — play then stop automatically.
- **Theming** — accent color, light/dark glass theme, custom background.
- **Background visualizer toggle** — can be disabled in Settings.

## Screenshots

_(Add your screenshots here, e.g. `docs/screenshots/main.png`)_

## Requirements

- **Windows 10 / 11** (64-bit)
- **WebView2 Runtime** (Evergreen) — usually already installed on Windows 11; if not, [download it](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)
- No .NET runtime needed for the **self-contained release** (`win-x64`)

## Download

Grab the latest build from the [Releases](../../releases) page:

- `GlassMusicPlayer-vX.Y.Z-win-x64.zip` — self-contained, no prerequisites besides WebView2

## Building from source

Prerequisites: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet restore
dotnet build GlassMusicPlayer.csproj -c Debug
dotnet run
```

### Publish a release (self-contained, win-x64)

```bash
dotnet publish GlassMusicPlayer.csproj -c Release -r win-x64 --self-contained true \
  -o release -p:DebugType=none -p:DebugSymbols=false
```

The output goes to the `release/` folder — zip it and attach to a GitHub Release.

## User data

The app keeps its data in `%APPDATA%\GlassMusicPlayer`:

- `settings.json` — settings (volume, theme, equalizer, ReplayGain, visualizer, …)
- `favorites.json` — favorite tracks
- `playlists.json` — playlists
- `analysis.json` — cached audio analysis (BPM/energy/loudness)
- `ipc.log` — diagnostics log

## Tech stack

- **C# / .NET 8** (WPF + Windows Forms interop)
- **WebView2** for the UI (single `wwwroot/index.html` with embedded CSS/JS)
- **NAudio** — playback, spectrum capture, crossfade
- **TagLibSharp** — metadata (title, artist, album, track number)
- **MathNet.Numerics** — signal processing for the analysis engine
- **Bootstrap Icons** — UI icons
- **Svg.Skia** — icon rendering

## License

This project is provided as-is. If you want to add a license (MIT, etc.), create a `LICENSE` file and reference it here.