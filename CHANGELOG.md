# Changelog

## 1.0.10

- **Trickshots work on revival projects.** Older Fortnite builds save replays in a format ClipShot couldn't read, so every revival match went to Rejected. ClipShot now reads them using the decompression file that comes with the game itself. Keep the game open for a few seconds after a match so the check can finish (only needed the first time).
- **Sharper clips.** Clips looked soft in busy scenes because the size limit added in 1.0.5 was too tight. Every Clip size setting now allows more detail: a 2-minute 1080p 60 FPS clip is about 300 MB on Balanced (was 180 MB), 150 MB on Smaller and 500 MB on Best quality. Change it in Settings → Recording → **Clip size**.

## 1.0.9

- **Clips show only your game.** Notifications, other windows and your desktop stay out of clips. ClipShot records Fortnite's window, or the game you have open full screen, and switches to the whole screen when no game is open. Change it in Settings → Recording → **What video to record**.
- **Fortnite revival projects are recognized.** Older Fortnite builds used by revivals now count as Fortnite for automatic trickshots, game sound and game video.
- **Fixed recording not starting until Fortnite opened.** Some installs still had "Only record while Fortnite is running" turned on from an older version. It's turned off once by this update; you can turn it back on in Settings → Fortnite.
- **Cleaner Settings.** Settings now has tabs (General, Recording, Audio, Fortnite, Shortcuts and Advanced) instead of one long page, and Save settings is always at the bottom.

## 1.0.8

- **Clips record only your game's sound.** Music, YouTube, calls and other apps stay out of your clips. ClipShot records Fortnite's sound, or the game you have open full screen in any other game. Change it in Settings → Audio → **What sound to record**.
- **Add Discord or other apps.** Turn on **Also record Discord** to include your calls, or list other apps (like Spotify.exe) to add them too.
- Game sound only needs Windows 11. On older Windows, ClipShot records everything playing on your PC, like before.
- The update notes now always show what's new in that version. Before, some updates showed notes from an older version.

## 1.0.5

- **Much smaller clips.** Clips could reach 1 GB for 2 minutes (AMD graphics cards were hit hardest), because recording had no size limit in busy scenes. ClipShot now caps the file size, and a new **Clip size** setting lets you pick Smaller, Balanced (the default, about 180 MB for 2 minutes at 1080p 60 FPS) or Best quality. Works on NVIDIA, AMD, Intel and CPU recording.
- **Your furthest shots.** A new Furthest Shots page ranks your automatic trickshots by distance, with the weapon, shot, who you eliminated and a button to watch the clip. Only shots ClipShot confirmed from the match itself count; Meter Checker results and manual clips can't be added. You can hide bot kills, and trickshots you already have are added the first time you open it. ClipShot also keeps a copy of each trickshot's match replay.
- **Automatic trickshots are sniper-only now.** A win only counts as a trickshot when your final kill was with a sniper. For any other weapon, save the clip yourself with **F8**.
- **Fixed clips getting stuck in Pending.** If you left a match early or didn't win, the end-of-match clip could sit in Pending forever. Every automatic clip now gets a final answer: trickshots go to Trickshots, everything else goes to Rejected (or is removed if you've turned off keeping clips that don't qualify). Clips that were already stuck are moved to Rejected the next time ClipShot opens.
- Removed the recording-time counter from the Home page. (Long recordings still show how long they've been running.)

## 1.0.1

- **Trickshot details in My Clips.** Each trickshot now shows its weapon and shot (for example "Heavy Sniper Rifle · No-scope"), who eliminated who with their platforms, and the weapon's rarity, right under the clip.
- **Meter Checker works while ClipShot is recording.** It reads replays at low priority so your game always comes first.
- Fixed the Meter Checker showing the wrong rarity, such as a "Rare" Heavy Sniper Rifle. Rarity is now only taken when the replay names it outright, and weapons that only come in certain rarities can't be shown with one they don't have. If the replay doesn't say, the Heavy Sniper shows "Epic or Legendary".

## 1.0.0

The first official release of Decrypt's ClipShot: a clipping app made for Fortnite trickshotters that works with any game.

- **Always ready to clip.** ClipShot records in the background from the moment it opens and keeps the last few minutes ready. Press **F8** to save a clip of a great moment, in any game.
- **Automatic trickshot clips.** When you win a Fortnite match with a sniper or heavy-ammo final kill, ClipShot clips it for you and names it with the distance, the exact weapon, and whether it was scoped or a no-scope (for example "53m_no-scope").
- **Trickshot screenshots.** Every trickshot also gets two pictures: one where the swag lines are strongest and one where the damage number is clearest.
- **Screenshots any time.** Press **F10** in game, or open any clip, step through it frame by frame and save the exact moment.
- **Long recording.** Press **F9** to record a full session for YouTube, and **F9** again to save it.
- **Meter Checker.** Open any Fortnite replay to see its final and furthest kills: distance, killer and victim, weapon and rarity.
- **Game sound and microphone,** each with its own volume, kept in sync with the video for the whole session.
- **Light on your PC.** Recording uses your graphics card when it can. **Optimize for my PC** in Settings picks the best setup for you.
- **Opens with Windows** (optional), quietly in the system tray, and updates itself when a new version comes out.
