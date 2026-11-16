# Changelog

## 1.0.0

First public release.

**Recording**
- Fixed clips drifting out of sync (and ending early) in long sessions. Video now keeps real capture timestamps instead of counting frames, so dropped frames no longer slow recorded time; about 2.5 s of delay built up after 20 minutes before.
- Video encoding can no longer fall more than about half a second behind (the capture queue went from 2048 frames to 16).
- Game audio no longer leaves gaps or goes missing during quiet moments, and it restarts by itself if a headset is unplugged.
- Clips line up audio with the exact span of video saved, instead of assuming both buffers ended at the same moment.
- Automatic Victory Royale clips now end a set time after the match actually ended, so the winning shot isn't cut off.
- Clips are now saved in your Videos folder (Videos\Decrypt's ClipShot). Existing clips are moved there once.
- New clip volume setting.
- A timing check appears in Activity every minute.

**App**
- New Dashboard with one Start ClipShot button and a getting-started guide.
- Settings use sliders and plain choices; technical options are under Show advanced settings.
- Meter Checker shows Final Kill and Furthest Kill cards with distance, killer and victim (with platform), weapon and rarity.
- "Start with Windows" option opens ClipShot quietly in the system tray.

**Install**
- One-button setup (DecryptsClipShot-Setup.exe) with update, fix and remove.
- No administrator rights needed.
- Releases are built and published automatically on GitHub.
