# Changelog

## 1.0.6

- **Two trickshot screenshots:** one where the kill streaks are strongest, and one where the damage number is clearest (they're rarely at their best in the same frame). Kills on bots get only the damage-number picture, since bots don't get kill streaks. Damage numbers are recognized in every color: blue (shield, including headshots on shields), yellow (headshot) and white (health).
- Fixed trickshot screenshots that showed a loading screen. ClipShot now uses the replay to find exactly when the final kill happened and only looks there, instead of searching the end of the clip (which can be the lobby or a loading screen).
- Trickshots say **Scoped** or **No-scope** more often. When the replay doesn't record your aim, ClipShot checks the clip itself for the sniper scope view right before the kill. It shows in the trickshot's title and file name, for example "50m_no-scope_…".
- Fixed sound and video drifting out of sync in some clips. ClipShot couldn't always tell which recording file was still being written, and then skipped its sync correction.
- Clip viewer: new **Find damage number** button next to **Find kill streaks**.

## 1.0.5

- **Trickshot screenshots.** When a trickshot is saved, ClipShot also saves a picture of the final kill at the moment the kill streaks are most visible (weak or strong streaks, it picks that clip's best frame). Turn it off in Settings.
- **Screenshot key (F10).** Press it in game to save a picture of that moment. If you press it just after a kill, ClipShot picks the frame where the kill streaks were most visible.
- **Screenshot from any clip.** In My Clips, select a clip and click **Screenshot**. Scrub or step frame by frame (arrow keys), or click **Find kill moment**, then **Save screenshot**.
- Screenshots are kept separate from clips: they're saved in **Pictures\Decrypt's ClipShot** and have their own **Screenshots** page in the app.

## 1.0.4

- **Long recording** for YouTube videos and full sessions. Press **F9** (or click **Start long recording** on the Dashboard) to start, and press it again to stop and save. Recordings show up in My Clips under **Recordings**. Change the key in Settings. If Fortnite closes, the recording is saved automatically.
- Simpler wording throughout the app. The Activity panel now only shows things that matter to you (clip saved, trickshot found, problems and what to do), and technical details go to a log file instead.

## 1.0.3

- Meter Checker shows how each sniper kill was hit: **Scoped**, **No-scope**, or **Aimed (iron sights)** for scopeless snipers like the Hunting Rifle.
- More snipers are recognized by name: Suppressed, Bolt-Action and Semi-Auto Sniper Rifles join the Heavy, Heavy Impact, Hunting, Automatic, Boom and Lawless Tracking Rifles.
- New "Name this weapon" link in Meter Checker: if a weapon only shows as "Sniper Rifle" or "Pistol", give it its real name once and ClipShot remembers it.
- Fixed Hunting Rifle kills that could be labelled as no-scopes.

## 1.0.2

- Meter Checker shows real weapon names from a weapon list (for example "Usopp's Slingshot" instead of "Slingshot Harmless Thrift Pistol", and "Assault Rifle" instead of "Assault Standard"). Weapons not in the list yet show their type (like "Pistol"). Hover a weapon to see the raw Fortnite tags.
- Usopp's Slingshot and other heavy-ammo weapons count for automatic trickshots.

## 1.0.1

- ClipShot now tells you when an update is available and installs it with one click (Update now). Turn this off in Settings, or check manually with "Check for updates now".
- Optional microphone recording (Settings → Audio → Record my microphone) with its own volume. It's mixed by the audio recorder, not the video recorder, so it doesn't cost game FPS like the old microphone option did.
- The volume slider is now "Game volume" and only affects game sound.
- Automatic trickshots now count any sniper **or any heavy-ammo weapon** (for example the Hand Cannon or Flint-Knock Pistol) for the final kill of a Victory Royale, not only snipers.

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
