# Changelog

## 1.0.3

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
