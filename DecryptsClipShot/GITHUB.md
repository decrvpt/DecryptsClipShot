# Putting ClipShot on GitHub

Players never build anything. They click a download link, open
`DecryptsClipShot-Setup.exe`, and click **Install**.

You don't need to build the installer by hand either. When you push a version
tag, GitHub builds it on its own Windows machines and publishes it on the
**Releases** page. The automation lives in `.github/workflows/release.yml`.

---

## One-time setup (about 15 minutes)

### 1. Install Git

Download Git for Windows from <https://git-scm.com/download/win> and install it
with the default options. Open a new **Command Prompt** and check:

```cmd
git --version
```

Tell Git who you are (this name appears on your commits):

```cmd
git config --global user.name "Ayden Burnor"
git config --global user.email "you@example.com"
```

Use the email on your GitHub account. GitHub can hide it for you: Settings → Emails → "Keep my email address private".

### 2. Create the repository on GitHub

1. Go to <https://github.com/new>.
2. Repository name: `DecryptsClipShot`.
3. Choose **Public**. Players can download releases from a public repository
   without a GitHub account. A private repository needs them to sign in.
4. Leave "Add a README", ".gitignore" and "license" **unticked**, because the
   project already has them.
5. Click **Create repository**.

### 3. Upload the project

Unzip the project somewhere permanent, for example `C:\Dev\DecryptsClipShot`.
Open Command Prompt in that folder and run:

```cmd
cd C:\Dev\DecryptsClipShot
git init
git add .
git status
```

`git status` lists what will be uploaded. Make sure **no** `ffmpeg.exe`,
`dist\`, `build\`, `bin\` or `obj\` appear; `.gitignore` already excludes them.
Then:

```cmd
git commit -m "Decrypt's ClipShot 1.0.0"
git branch -M main
git remote add origin https://github.com/YOUR-USERNAME/DecryptsClipShot.git
git push -u origin main
```

The first push opens a browser window to sign in to GitHub. Refresh the
repository page and your code is there.

### 4. Let the workflow publish releases

On GitHub, open the repository's **Settings → Actions → General**. Under
**Workflow permissions**, choose **Read and write permissions**, then click **Save**.

---

## Publishing the first release

```cmd
git tag v1.0.0
git push origin v1.0.0
```

Then:

1. Open the repository's **Actions** tab. A run called **Release** appears.
   It takes about 10–15 minutes. A green tick means it worked.
2. Open the **Releases** page (right-hand side of the repository's main page).
   **Decrypt's ClipShot 1.0.0** is there with `DecryptsClipShot-Setup.exe`
   attached.

### The link to give players

This link always downloads the **newest** setup, so you never have to update it:

```
https://github.com/YOUR-USERNAME/DecryptsClipShot/releases/latest/download/DecryptsClipShot-Setup.exe
```

Put it in your Discord, YouTube descriptions, TikTok bio, and so on. Or share the
friendlier release page:
`https://github.com/YOUR-USERNAME/DecryptsClipShot/releases/latest`

---

## Pushing an update

Every time you change something:

```cmd
git add .
git commit -m "Short description of what changed"
git push
```

That only saves the code on GitHub; players won't see anything yet. When you
want players to get it, publish a new version:

1. Add a section at the top of `CHANGELOG.md` for the new version, written for
   players. **This text is shown in ClipShot's "Update available" popup** and
   on the release page:

   ```markdown
   ## 1.0.2

   - Fixed clips sometimes missing the last second.
   - New: choose where the Activity log is saved.
   ```

2. Commit it, then tag the same version number (it must be higher than the last one):

   ```cmd
   git add .
   git commit -m "Release 1.0.2"
   git push
   git tag v1.0.2
   git push origin v1.0.2
   ```

GitHub builds `1.0.2` and publishes it. Every installed copy of ClipShot (1.0.1
or newer) shows an **Update available** popup the next time it opens. Players
click **Update now** and ClipShot downloads the new setup, closes, updates and
offers to reopen. If ClipShot is minimized to the tray, a notification appears
instead. The download link also always serves the newest version.

Version numbers: `1.0.1` for fixes, `1.1.0` for new features, `2.0.0` for big changes.

---

## Adding new weapons each season

Fortnite replays name weapons by internal codename, not the name players see
(Usopp's Slingshot is `Slingshot.HarmlessThrift`). ClipShot translates them
with `local_parser/DecryptsClipShot.ReplayParser/weapons.json`.

When Meter Checker shows only a type like **Pistol** or **Shotgun** for a new
weapon:

1. Hover the weapon name and note the tag, e.g. `Weapon.Ranged.Shotgun.Brave.Lantern`.
2. Add an entry to `weapons.json`:

   ```json
   {
     "name": "Gilded Dragoon Shotgun",
     "match": ["Shotgun.Brave.Lantern"],
     "class": "Shotgun",
     "ammo": "Shells",
     "sniper": false,
     "source": "Seen in a replay, November 2026"
   }
   ```

   Set `"ammo": "Heavy"` or `"sniper": true` when the weapon should count for
   automatic trickshots. Add `"mythicName"` if the Mythic version has its own name.
3. Add a line to `CHANGELOG.md` and publish a new version as usual.

Fortnite.GG and fndb.gg list each season's weapons with their ammo type.

## Handy extras

**Check that it builds without publishing anything:** Actions tab → **Release**
→ **Run workflow** → type a version like `1.0.1` → **Run workflow**. When it
finishes, open the run and download the files under **Artifacts** to test them.

**A release build failed (red X):** open the run, click the red step, and read
the error. Fix it, commit and push, then publish with the *next* version
number. To reuse the same number, delete the tag first:

```cmd
git tag -d v1.0.1
git push origin :refs/tags/v1.0.1
```

**Building on your own PC** (optional, for testing): see "For developers"
in `README.md`. It needs Visual Studio, CMake, Ninja, the .NET SDKs, WiX and
`ffmpeg.exe`. The GitHub workflow installs all of these by itself.

**Prefer buttons to commands?** GitHub Desktop (<https://desktop.github.com>)
can do the commit and push steps. Tags are easiest with the two `git tag`
commands above, or by drafting a new release on the website with a new
`vX.Y.Z` tag.

---

## Things to know before sharing widely

- **"Windows protected your PC" warning.** Unsigned downloads trigger Microsoft
  SmartScreen. Players can click **More info → Run anyway**. The release notes
  already say this. A code-signing certificate (for example from Certum, SSL.com
  or Azure Trusted Signing) removes the warning once it's added to the workflow.
- **Test each release** before sharing it: download the setup from the release
  page and try a fresh install, an update from the previous version, Fix, Remove,
  and a few clips with sound.
- **FFmpeg licence.** The workflow bundles a GPL build of FFmpeg (it includes
  the x264 encoder). Distributing it means you must also offer FFmpeg's source:
  `THIRD_PARTY_NOTICES.md` (installed with the app) already links to it.
- **Your own licence.** Add a `LICENSE` file for ClipShot's own code before
  making the repository public (<https://choosealicense.com> helps you pick).
- **WiX licence.** `installer\DecryptsClipShot.Installer.wixproj` accepts the
  WiX Toolset OSMF EULA, which WiX v7 requires for every build. It's free unless
  your WiX-built projects earn US$10,000 or more a year.
