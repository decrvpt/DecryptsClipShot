-- Decrypt's ClipShot online service: accounts, friends and shared clips.
-- Paste this whole file into Supabase → SQL Editor → New query → Run.
-- Safe to run again: it only creates what's missing.
--
-- Security model (row-level security, enforced by the database itself):
--   * Everyone signed in can look up profiles (to add friends by name).
--   * You only see friendships you're part of. You can send requests, only the
--     person you asked can accept, and either side can remove a friendship.
--   * You see your own shared clips and your accepted friends' finished ones.
--   * A clip is only marked finished by the server after its video is
--     actually uploaded, and video files are only reachable through short-lived
--     links handed out by the "clips" function after the same checks.

-- ---------- Profiles (one per Discord account) ----------

create table if not exists public.profiles (
    id uuid primary key references auth.users (id) on delete cascade,
    username text not null,
    display_name text,
    avatar_url text,
    created_at timestamptz not null default now()
);
create unique index if not exists profiles_username_unique on public.profiles (lower(username));

-- Discord's username ("decrvpt"), display name and avatar from the sign-in.
create or replace function public.discord_username(meta jsonb, fallback uuid)
returns text language sql immutable as $$
    select coalesce(
        nullif(split_part(coalesce(meta ->> 'name', meta ->> 'full_name', meta ->> 'user_name', ''), '#', 1), ''),
        'player_' || left(fallback::text, 8))
$$;

create or replace function public.sync_profile()
returns trigger language plpgsql security definer set search_path = public as $$
declare
    wanted text := public.discord_username(new.raw_user_meta_data, new.id);
    chosen text := wanted;
begin
    -- Usernames are unique; in the rare clash, add a short suffix.
    if exists (select 1 from public.profiles where lower(username) = lower(wanted) and id <> new.id) then
        chosen := wanted || '_' || left(new.id::text, 4);
    end if;
    insert into public.profiles (id, username, display_name, avatar_url)
    values (new.id, chosen,
            coalesce(new.raw_user_meta_data -> 'custom_claims' ->> 'global_name', new.raw_user_meta_data ->> 'full_name'),
            new.raw_user_meta_data ->> 'avatar_url')
    on conflict (id) do update
        set username = excluded.username,
            display_name = excluded.display_name,
            avatar_url = excluded.avatar_url;
    return new;
end $$;

drop trigger if exists on_auth_user_saved on auth.users;
create trigger on_auth_user_saved
    after insert or update of raw_user_meta_data on auth.users
    for each row execute function public.sync_profile();

alter table public.profiles enable row level security;
drop policy if exists "profiles are visible to signed-in users" on public.profiles;
create policy "profiles are visible to signed-in users" on public.profiles
    for select to authenticated using (true);
-- No insert/update/delete for users: profiles follow the Discord account.
revoke insert, update, delete on public.profiles from anon, authenticated;

-- ---------- Friends ----------

create table if not exists public.friendships (
    requester uuid not null references public.profiles (id) on delete cascade,
    addressee uuid not null references public.profiles (id) on delete cascade,
    status text not null default 'pending' check (status in ('pending', 'accepted')),
    created_at timestamptz not null default now(),
    primary key (requester, addressee),
    check (requester <> addressee)
);
-- One friendship per pair, whichever way round it was asked.
create unique index if not exists friendships_one_per_pair
    on public.friendships (least(requester, addressee), greatest(requester, addressee));

alter table public.friendships enable row level security;

drop policy if exists "see your friendships" on public.friendships;
create policy "see your friendships" on public.friendships
    for select to authenticated using (auth.uid() in (requester, addressee));

drop policy if exists "send friend requests" on public.friendships;
create policy "send friend requests" on public.friendships
    for insert to authenticated with check (requester = auth.uid() and status = 'pending');

drop policy if exists "accept requests sent to you" on public.friendships;
create policy "accept requests sent to you" on public.friendships
    for update to authenticated using (addressee = auth.uid()) with check (addressee = auth.uid() and status = 'accepted');

drop policy if exists "remove your friendships" on public.friendships;
create policy "remove your friendships" on public.friendships
    for delete to authenticated using (auth.uid() in (requester, addressee));

-- Only the status can change (who asked whom can't be rewritten).
revoke update on public.friendships from anon, authenticated;
grant update (status) on public.friendships to authenticated;

create or replace function public.are_friends(a uuid, b uuid)
returns boolean language sql stable security definer set search_path = public as $$
    select exists (
        select 1 from public.friendships
        where status = 'accepted'
          and ((requester = a and addressee = b) or (requester = b and addressee = a)))
$$;

-- ---------- Shared clips ----------

create table if not exists public.shared_clips (
    id uuid primary key default gen_random_uuid(),
    owner uuid not null default auth.uid() references public.profiles (id) on delete cascade,
    title text not null check (char_length(title) between 1 and 120),
    category text check (char_length(category) <= 40),
    distance_m numeric check (distance_m >= 0 and distance_m < 100000),
    weapon text check (char_length(weapon) <= 80),
    shot text check (char_length(shot) <= 40),
    rarity text check (char_length(rarity) <= 40),
    killer text check (char_length(killer) <= 80),
    victim text check (char_length(victim) <= 80),
    duration_s numeric check (duration_s >= 0 and duration_s < 36000),
    size_bytes bigint,
    status text not null default 'uploading' check (status in ('uploading', 'ready')),
    created_at timestamptz not null default now()
);
create index if not exists shared_clips_owner_created on public.shared_clips (owner, created_at desc);

alter table public.shared_clips enable row level security;

drop policy if exists "see your clips and friends' clips" on public.shared_clips;
create policy "see your clips and friends' clips" on public.shared_clips
    for select to authenticated
    using (owner = auth.uid() or (status = 'ready' and public.are_friends(owner, auth.uid())));

drop policy if exists "share your own clips" on public.shared_clips;
create policy "share your own clips" on public.shared_clips
    for insert to authenticated with check (owner = auth.uid() and status = 'uploading');

drop policy if exists "edit your own clips" on public.shared_clips;
create policy "edit your own clips" on public.shared_clips
    for update to authenticated using (owner = auth.uid()) with check (owner = auth.uid());

drop policy if exists "remove your own clips" on public.shared_clips;
create policy "remove your own clips" on public.shared_clips
    for delete to authenticated using (owner = auth.uid());

-- Users may edit the description fields only. Status and size are set by the
-- "clips" function after it checks the uploaded file.
revoke update on public.shared_clips from anon, authenticated;
grant update (title) on public.shared_clips to authenticated;
revoke insert on public.shared_clips from anon;
