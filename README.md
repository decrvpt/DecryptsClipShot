# Decrypt's ClipShot

A Windows clipping app made for Fortnite. ClipShot keeps the last few minutes of your game ready to save. Press **F8** after a great moment, and when you win with a sniper or heavy-ammo shot, the trickshot is clipped automatically and titled with its distance.

No OBS, accounts, API keys or subscriptions.

## Download

**[Download DecryptsClipShot-Setup.exe](../../releases/latest/download/DecryptsClipShot-Setup.exe)**, open it, and click **Install**.

- If Windows says "Windows protected your PC", click **More info → Run anyway**. This appears because the download isn't code-signed yet.
- No administrator password is needed. ClipShot installs for your Windows account only.
- To update, download and open the newest setup, then click **Update**. Your clips and settings stay.
- To fix or remove ClipShot, run the setup again and choose **Fix ClipShot** or **Remove ClipShot**. You can also remove it from Windows Settings → Apps. Your saved clips are never deleted.

**Requirements:** Windows 10 or 11 (64-bit). An NVIDIA, AMD or Intel graphics card gives the smoothest gameplay while recording.

## Getting started

1. **Turn on Fortnite replays.** In Fortnite's settings, turn on **Record Replays**. ClipShot needs this to find trickshots automatically. Manual clips work without it.
2. **Open ClipShot → Settings → Optimize for my PC**, then **Save settings**. ClipShot tests your graphics card and picks smooth settings.
3. **Click Start ClipShot** on the Dashboard, then launch Fortnite. Recording starts by itself when the game opens.
4. **Press F8** after a great moment to save the last 90 seconds.

Fortnite in **Borderless Windowed** mode works best.

## Where your clips go

Clips are saved in your **Videos** folder, under **Decrypt's ClipShot**. In the app, **My Clips** shows them in two groups:

- **Trickshots**: Victory Royales where your final kill used any sniper or any heavy-ammo weapon (such as the Hand Cannon), titled with the distance (for example `184m_2026-09-01_19-47-36.mp4`).
- **All clips**: everything you saved with F8, and automatic clips that didn't qualify as trickshots.

Double-click a clip to watch it. Select one and click **Rename** to rename it. You can choose another folder in **Settings → Storage**.

## Meter Checker

Open any Fortnite replay to see its **Final Kill** and **Furthest Kill**: distance in meters, killer and victim (with platform), weapon and rarity. It works for friends' kills too. Click **Stop ClipShot** first so analysis can't affect a game in progress. Replays are read on your PC and never uploaded.

Fortnite keeps replays in `%LOCALAPPDATA%\FortniteGame\Saved\Demos`.

## Settings

Most players only need **Optimize for my PC**. The main options are:

| Setting | What it does | Default |
| --- | --- | --- |
| Quality | Balanced, higher quality, or smoother gameplay | Balanced |
| Frame rate | 30 FPS is lightest; 60 FPS gives smoother clips | 30 FPS |
| Resolution | Full, 1080p or 720p | 1080p |
| Clip length | How much is saved when you press the shortcut | 90 seconds |
| After a win, keep recording for | Extra footage after a Victory Royale | 15 seconds |
| Game volume | Game sound level in saved clips | 100% |
| Record my microphone | Adds your voice to clips, with its own volume | Off |
| Clip shortcut | The key that saves a clip | F8 |

Technical options (encoder, FFmpeg, buffer size) are under **Show advanced settings**.

## Troubleshooting

**Fortnite loses FPS while recording.** Click **Optimize for my PC**, choose 30 FPS, and try 720p.

**Clips stutter.** Use Fortnite in Borderless Windowed mode. Try 30 FPS. If Activity mentions "compatibility capture", reinstall ClipShot.

**No sound in clips.** In Settings → Audio, turn on **Record game sound** and choose **Default Windows output**. If your game sound plays through a different headset or speaker, pick that device.

**My voice isn't in clips.** Turn on **Record my microphone** in Settings → Audio, pick your mic, save, then stop and start ClipShot. If it still doesn't work, open Windows **Settings → Privacy & security → Microphone** and turn on **Let desktop apps access your microphone**.

**An automatic clip stays in Pending.** ClipShot saved the video but couldn't confirm a trickshot from the replay, so it kept the clip to be safe. Make sure **Record Replays** is on in Fortnite. The Activity panel shows the reason.

**Something else is wrong.** Run the setup again and choose **Fix ClipShot**. Settings are stored in `%LOCALAPPDATA%\Decrypts ClipShot\trickclip.ini`. Deleting that file resets every setting.

## Privacy

- Replays are analyzed on your PC and never uploaded.
- ClipShot doesn't inject into Fortnite or read its memory. It records your screen and game sound like any screen recorder.
- Your microphone is only recorded if you turn on **Record my microphone** in Settings → Audio.

---

## For developers

| Folder | What it is |
| --- | --- |
| `src/trickclip.c` | Capture engine (C): runs FFmpeg, keeps the rolling video/audio buffers, saves and aligns clips, watches for finished matches |
| `audio_helper/` | Game-audio loopback capture (C#, NAudio) feeding the engine |
| `local_parser/` | Fortnite replay analysis (C#, FortniteReplayReader) |
| `gui/` | The ClipShot app (C#, WPF) |
| `installer/` | WiX MSI package |
| `setup/` | One-button setup wizard that carries and runs the MSI |
| `.github/workflows/release.yml` | Builds and publishes a release when a `v*` tag is pushed |

**Releasing:** push a version tag (`git tag v1.0.1` then `git push origin v1.0.1`). GitHub builds the installer and publishes it on the Releases page. See [GITHUB.md](GITHUB.md).

**Building locally** (optional): install Visual Studio 2022 (Desktop development with C++), CMake, Ninja, the .NET 8 and 10 SDKs, and put `ffmpeg.exe` beside the build scripts. Then, from an *x64 Native Tools Command Prompt*:

```cmd
build_gui.bat          :: app only, output in dist\DecryptsClipShot
build_msi.bat 1.0.1    :: full installer, output in dist\Installer
```

`build_msi.bat` uses WiX v7, whose OSMF EULA is accepted in `installer/DecryptsClipShot.Installer.wixproj` (free unless your WiX-built projects earn US$10,000 a year).

**How recording stays in sync:**
- **Video** keeps real capture timestamps, using FFmpeg's `fps` filter on `ddagrab` output, with a 16-frame capture queue.
- **Audio** is a gap-free 48 kHz stream: the helper keeps WASAPI loopback alive with a silent stream and fills any stall with silence.
- **Saving a clip:** the engine copies both live buffer files back to back, cuts the video, measures its exact length, then cuts the audio to that span.
- **Timing check:** the Activity log shows a check line every minute. Drift should stay near 0 ms.

**Silent install** (IT / managed PCs):

```cmd
msiexec /i DecryptsClipShot-Setup.msi /qn DESKTOPSHORTCUT=0 STARTUPSHORTCUT=1
```

See [CHANGELOG.md](CHANGELOG.md) for release history and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for licences.
