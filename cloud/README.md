# Discord sign-in and friends' clips (on hold)

Started, not finished, not part of the app build.

Decisions so far:
- Friends are added inside ClipShot by Discord username (Discord doesn't let
  apps read your Discord friends list without special approval).
- Only clips you choose are shared, as a smaller 720p copy with their
  trickshot details.
- Hosting: Supabase free tier (Discord sign-in, database) + Cloudflare R2
  (videos, 10 GB free).
- Sign-in required the first time, then remembered; ClipShot still records
  offline.

Done:
- `supabase/schema.sql`: database and security rules (tested on Postgres:
  strangers see nothing, friends see only finished clips, nobody can fake
  requests, profiles or uploads).
- `supabase/functions/clips/index.ts`: server function that hands out upload
  and viewing links for R2 and marks uploads finished.
- `app-draft/CloudService.cs.txt`: the app's sign-in (PKCE through the browser,
  session encrypted with DPAPI), friends and sharing code. Rename to `.cs` and
  move into `gui/DecryptsClipShot` to continue.

Still to do: sign-in window, Friends and Friends' clips pages, Share button,
setup guide (Supabase, Discord app, R2), privacy policy.
