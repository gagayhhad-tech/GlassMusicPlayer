# Changelog

## [1.0.1] - 2026-08-20

### Added
- **Discord Rich Presence** - shows the current track (title, artist) and playback timer in your Discord profile. Toggle in Settings -> Audio. Auto-reconnects if the pipe drops.
- **Glass-styled system tray menu** - the WinForms context menu was replaced with a custom WPF popup matching the app's liquid-glass design (translucent panel, rounded corners, hover states, app logo). Menu shows the current track and controls playback.

### Fixed
- **Tray menu not responding** - the tray icon now runs on its own STA thread with a dedicated WinForms message loop, so right-click and menu commands work reliably inside the WPF host.
- **Window drag** - the title bar now drags the window via pointer events (the `-webkit-app-region` CSS approach doesn't work on transparent WPF windows).
- **Window control icons** - minimize/maximize/close icons are now correctly sized and centered.
- **Maximized window overflowing** - a `WM_GETMINMAXINFO` hook keeps the maximized window inside the work area so it no longer covers the taskbar.
- **Equalizer negative gains** - negative band values now render correctly (red bar drops below center) instead of glitching.
- **Seek slider jitter** - dragging the progress bar no longer fights incoming state updates; the bar has full drag support with a thumb handle.
- **Volume slider** - replaced the layout-based `width` animation with GPU-composited `transform: scaleX`, added a gradient accent fill, a glowing thumb and smooth easing. Clicks glide, drags track the cursor exactly, the thumb stays inside the bar.
- **Discord timer accuracy** - the presence timer re-syncs after seeking or track changes (expected-position check).

### Changed
- Volume bar fill uses a theme accent gradient instead of flat grey.

## [1.0.0] - 2026-08-20

### Added
- Initial release: library scanning, playback controls, equalizer, crossfade, visualizer (Bars/Plasma/Aurora), karaoke/lyrics, Flow radio, audio analysis (BPM/energy/loudness), ReplayGain, sleep timer, playlists, favorites, theming, global media keys, taskbar integration.