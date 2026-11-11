# Upstream log

Bale copies the official Silo TV apps and talks to silo-server, so upstream changes can change
what Bale has to do. This log records each review of the official repositories: what changed and
whether Bale needs to act. Bale's deliberate deviations (AGENTS.md) are noted, not acted on.

A review runs every morning with `scripts/upstream-check.sh` (`status`, then `log <repo>` and
`advance <repo>` once its entry is written). Commits that only touch other platforms' build
setup, CI, translations or store metadata get no line unless they change behaviour.

Impact tags: **act** (Bale should change; also listed under Open items), **later** (affects a
part Bale hasn't built yet; check when building it), **deviation** (Bale differs on purpose),
**none** is left out.

## Last reviewed

| Repo | Commit | Reviewed |
|---|---|---|
| silo-android | `48015d4ee3ba` | 2026-11-10 |
| silo-apple | `e792abdcb523` | 2026-11-10 |
| silo-server | `5159df6dcf52` | 2026-11-10 |
| silo-roku | `4b9b36f64601` | 2026-11-10 |

The first review (2026-11-10) started from the commit each local clone was made at.

## Open items

Deferred for now: playback comes first (2026-11-10). Grouped by area, newest review first. Strike an item when it ships or is decided against.

- **API contract:** bump `SILO_REF` in `scripts/generate-api.sh`, regenerate and check the build
  (server 2026-11-10: `max_profiles`, `binding`, `stored_subtitle_id`, catalog IDs, nullable enums,
  device headers; Android/Apple: remove-failed requests). The pin is older than the
  server clone, so the bump also brings in changes from before this log started.
- **Profiles:** the primary profile can't have a library limit any more (server `df9f5501`);
  hide Add at `max_profiles` (server `451cba6c`).
- **Profile menu:** drop Watchlist and Favorites (Android `136de70ad`, Apple `ddebb9fa`).
- **Remember-profile offer:** new copy, "Ask Every Time" / "Remember", Back = Ask Every Time
  (Android `6b832ca73`).
- **Card focus style:** one shared style, no glow, drop shadow, current-episode outline
  (Android `51d4f9013`, Apple `9e94e24e`).
- **More Like This chain:** first pick pushes, later picks replace (Android `238134a1b`,
  Apple `fd40ce61`).
- **Temporary password:** 403 `password_change_required` state on TV (Android `65664d7ce`,
  Apple `04807485`).
- **Several accounts per server / Change Account:** large; plan as its own task (Android
  `623bb3513`, Apple `54cf0f34`).
- **Remove failed requests:** needs the contract bump (Android `4600d910c`, Apple `df891d2e`).
- **Not doing: box sets.** The Silo developer said box sets reached the server by mistake and
  reversed the decision (2026-11-10), so Bale doesn't build them even though the clients shipped
  them (Android `67e6e224a`, Apple `08c28c8e`). Ignore later box-set commits unless that changes.
- **Smaller:** device name in sign-in (`bd6ade492`), hero logo grace 700 ms (`e57fbf3f6`),
  Calendar DIGITAL/PHYSICAL badges (`64ae5f08c`), Search clear button (`548e3cb77`), remote Stop
  key (`969c0a812`), unplayable subtitle rows (`461240c6d`), return-from-player episode
  (`e727c9af5`), declined device-code lead (`0548b9255`/`538e2e9a`), port error outline
  (`d1837b92`).
- **To check:** Continue Watching focus by series id (`45a869871`), episode rail centring
  (`d9dc3f3de`), Change Server forgetting a never-signed-in server (`0a387afa0`), hero action
  memory on Up (Apple `067e6cee`, no Android match), Home rows after the server update.

## Reviews

### 2026-11-10: backlog since each clone

First review, covering everything since the clones were made. Commits tagged **none** are
counted, not listed. Each **act** was checked against Bale's source.

#### silo-android (`e15865529bb8..48015d4ee3ba`, 78 commits, 28 none)

- `6b832ca73` act: the remember-profile offer gains a message, "Ask Every Time" / "Remember" side
  by side, focus on Remember, Back = Ask Every Time. Bale still has "Yes" / "Not now"
  (ProfilesPage).
- `136de70ad` act: the profile menu drops Watchlist and Favorites; order is Switch Profile,
  History, Requests, Watch Party, Settings, Switch Server, Sign Out. Bale still has both rows
  (ShellPage).
- `51d4f9013` act: one card focus style (1.05 lift, 2dp white outline at 70%, black drop
  shadow, no glow), also for the current item. Bale has per-card scales and a white glow
  (CardRailMetrics, Skyline styles).
- `bd6ade492` act: sign-in sends the device's name. Bale sends the hardware model
  (DeviceIdentity).
- `e57fbf3f6` act: the hero holds the text title 700 ms while the logo loads, at once on
  failure. Bale waits 350 ms (BrowsePage).
- `64ae5f08c` act: Calendar badges add DIGITAL and PHYSICAL release types (CalendarFeed). Badges
  only; the Calendar focus deviation is untouched.
- `548e3cb77` act: Search gets a clear (X) button while text is present; Right reaches it,
  Select clears and refocuses the field (SearchPage).
- `238134a1b` act: More Like This chain: first pick pushes, later picks replace, Back returns to
  the chain start, re-picking the shown title is ignored. Bale always pushes.
- `969c0a812` act: the remote Stop key leaves the player on key-down. Bale doesn't handle
  `VirtualKey.Stop`.
- `4600d910c` act: remove failed requests (`DELETE /api/v2/requests/{id}`,
  `remove_failed_supported`). Needs the contract bump.
- `67e6e224a` not doing: box sets (page, search results, row on movie pages). The developer
  called them a mistake and reversed the decision. `6f3e36820`: library grids refuse
  `group=box_set` with 422 (Bale never sends it).
- `461240c6d` act: subtitle rows flagged `unplayable` (or embedded with no codec) are neither
  offered nor auto-picked (PlaybackTrackChoices, track Auto).
- `e727c9af5` act/later: back from the player, the series page selects the episode that was on
  screen, or the next one if it reached credits or the end. Bale keeps the same episode; the
  Up Next and shuffle parts wait for player phase 5.
- `623bb3513` act: several accounts per server with a Change Account screen. Large.
- `65664d7ce` act: `password_change_required` at sign-in. Bale has no handling; read the TV
  behaviour before building.
- `45a869871`, `1c9f4bfd1` check: Continue Watching / Next Up cards are keyed by series id so
  focus follows a show when its episode advances. Bale restores focus by card identity.
- `d9dc3f3de` check: the episode rail opens centred on next-up and retries when the season
  arrives late.
- `0548b9255` act: declined device-code body "Show a new code to try again." and wrapping
  buttons.
- `0a387afa0` check: Change Server forgets a server that was never signed in to.
- Already matched: `9c7d4ca26` sign-out dialog, `e9657ee7d`/`77e316ff5` address validation,
  `3b1a79293` password errors, `4c0a2178c` Requests row, `a6453471b` local resume copy.
- later: `d4ff20673` embedded PGS on direct play, `hls_remux_subtitle_renditions_v1`, in-place
  subtitle switching, sidecar retry (HUD phase 4); `76dee90ad` subtitles stay off when Auto
  resolves to none (confirm MediaPlayer never shows a default-flagged embedded track);
  `8102d0302` `marker_segments` (intro skip, Up Next); `7e4580db1` HUD audio output mode;
  `7e30f42b3` AI subtitle jobs carry `session_id`; `670f5ba0a` combined
  `resolution_hdr_audio` badge; `864cd1d6f`, `8fff98ba3` Search "Available to request";
  phone pairing (`ad92e00e9`, `585111f08`, `588a62ba4`, `c03110468`, `25ebeef18`, `d7f8e4e65`,
  `2b2af2794`); `b72e75058` offline Favorites/Watchlist dialog; `50b94f3a7` "Translating
  description…"; audiobooks (`e9861fa66`, `ee178d887`); app language (`a57894556`,
  `59f15e534`, `edfa7681b`, `c0fd4ff85`).
- Note: `43f05529e` adds an AGPL platform-library exception to Android's licence.

#### silo-apple (`148d5d70f8b8..e792abdcb523`, 67 commits, 44 none)

The tvOS app made most of the same changes as Android TV; the Android commit is given where it
matches.

- `fd40ce61` act: More Like This chain in two Backs (Android `238134a1b`).
- `d1837b92` act: an error outlines the field it is about. Bale always marks the address on a
  port error (MainPage.Setup); a bad Port value should outline Port and open Advanced options.
- `538e2e9a` act: the declined device-code state gets its own lead and drops "on your phone".
  Bale keeps the "Scan the code…" lead with no code shown (Android `0548b9255`).
- `9e94e24e` act: one card focus style and an outline on the current episode in the Series row
  (Android `51d4f9013`). Bale's Explore-all card lifts 1.10, the episode ring is 3 px at 90%,
  and there is no current-episode outline.
- `ddebb9fa` act: profile menu without Watchlist and Favorites, "Change Account" instead of
  "Switch Server" (Android `136de70ad`). The parity backlog's profile-menu line is out of date.
- `04807485` act: temporary password: 403 `password_change_required` everywhere except account
  read, password change and sign-out; the TV points to phone or web and offers Sign out
  (Android `65664d7ce`).
- `54cf0f34` act: several accounts per server, Change Account, Settings › Accounts; an
  account's only PIN-less profile opens directly; a server with no account is removed (Android
  `623bb3513`).
- `df891d2e` act: remove failed requests from the card's context menu with confirmation; 204
  and 404 read as removed, 409 re-reads and says not removed; not retried (Android
  `4600d910c`).
- `067e6cee` check: Up from season pills or a rail returns to the last focused hero action,
  else Play. Bale only remembers it while a selector restores focus. No Android TV match found,
  and the parity backlog records "Up from season tabs lands on Version" as matching Android, so
  check the current APK first.
- Already matched: `88498ec7` fixed top bar and Requests in the profile menu (drop the
  "top-menu controls" entry from the deferred Settings list), `9723df00` `group=box_set` 422,
  `55841508` Requests keeps the top bar.
- not doing: `08c28c8e` box sets (see Open items). later: `cc87791c` embedded text subtitles from the stream instead of a
  whole-file extract, with `hls_remux_subtitle_renditions_v1`, `hls_subtitle_renditions_all_v1`,
  `hls_rendition`, `subtitle_track_changed` replans and stream indices (HUD phase 4);
  `3bd75734` Up Next autoplays only the next episode or a shuffle pick (phase 5); `1a7cceea`
  buffered line from the engine, hidden until a seek lands, `c9fb59be` a `seek_reanchor`
  commit resumes unless paused, `1f10c72d` resume during a scrub cancels it (phase 3
  scrubber); `7baff3ef` combined badge and HDR10+ suffix; `2a9a1b32` app language;
  `05753bf4` phone pairing of several servers and accounts; `fd258d7d` audiobook Favorite and
  Finished; `5ac5267b` the "TrueHD Atmos and DTS:X" setting is hidden: don't copy it into the
  Playback pane.

#### silo-server (`e3af98b06af2..5159df6dcf52`, 180 commits, 157 none)

The v3 capability rules Bale relies on (platform-attested entries, no level lists, null
bitrate, no Dolby Vision, progressive off) are unchanged.

- `df9f5501` act: the primary profile (or an account's first) can't have a library limit;
  create/PATCH with one fails validation. Bale's profile editor (ProfileForm,
  ProfileAccessLimits) still offers it. Check what the official TV editor does.
- `451cba6c` act: `listProfiles` returns `max_profiles`. Bale still shows Add at the limit and
  gets a 409.
- `6685aab9` regenerate and check: nullable request enums list `null`, including
  `ProfileUpdate.max_playback_quality`; check Bale's explicit-null PATCH still works.
- `73b7f651` regenerate and check: sign-in operations declare the `X-Silo-Device-*` headers; a
  device id over 128 characters gets 422. Bale's id is well under that.
- `759ffda3` regenerate and check: optional device sign-in `binding` / `binding_secret`;
  unbound requests are unchanged. Use it when phone pairing is built.
- `37aa92a8` regenerate and check: subtitle rows gain `stored_subtitle_id`; `subtitle_ready`
  goes out on the playback socket (Bale has none yet).
- `3d114b11` regenerate and check: catalog cards carry `imdb_id`/`tmdb_id`/`tvdb_id`.
- `f3259cd0`, `4e3020a2`, `c649a1c2`, `3b1eb304` check on the console: empty Home sections are
  left out, curated rows are cut to `item_limit`, the separate Continue Watching row has one
  card per series, web-only heroes go only to the web client family.
- later: section `title_key`/`title_args` and `section_title_keys` (`191dbc5a`, `002ee3d3`,
  `c9074d9b`, `30a6abd1`); `ui.app_language` (`78620091`); Watch Party guest control policy
  (`d7ed5a0c`); `server_dv7_to_dv81` DV 7 → 8.1 over fMP4 HLS (`6eea70e7`, only if Bale ever
  claims DV); HEVC one-level-up tolerance for exact evidence (`1688ca0c`, not used:
  Bale sends platform-attested); progressive remux `progressive_range_refusal_v1` and aborted
  responses on source failure (`ea6e6641`, `df5f496a`, when progressive is enabled: treat a
  short body as a transport failure); titles allowed above a profile's rating limit
  (`8af01b27`); diagnostics manifest additions (`14800fd2`).

#### silo-roku (`4b9b36f64601`, 0 commits)

Cloned today; nothing to review.
