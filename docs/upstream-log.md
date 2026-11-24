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
| silo-android | `b7c0412af6b4` | 2026-11-24 |
| silo-apple | `d9bbdc77c102` | 2026-11-24 |
| silo-server | `64f9ee20ecee` | 2026-11-24 |
| silo-roku | `6389fccb8ead` | 2026-11-24 |

The first review (2026-11-10) started from the commit each local clone was made at.

## Open items

Deferred for now: playback comes first (2026-11-10). Grouped by area, newest review first. Strike an item when it ships or is decided against.

- ~~**API contract:** bump `SILO_REF`~~ Done 2026-11-16: pinned to server `92ee051878c2`, additive only;
  also generates `GET /api/v2/capabilities/ratings` and `DELETE /api/v2/requests/{id}`.
- ~~**API contract:** bump to server `217ff25abc5b`~~ Done 2026-11-18, additive: `Section.collections`
  (`SectionCollectionCard` with backdrop), `HomeCapabilities.collection_rows`. The notification
  reason fields the server dropped (`03489137`) were never generated (their paths aren't included).
- ~~**Edition / Version Auto:**~~ Done 2026-11-16 (Rust `formatting::select_version`): one edition
  per key, history names only the edition (`last_edition_key`, then `last_file_id`), an Edition
  pick keeps Version on Auto, server order (resolution, HDR, size), display matching inside the
  edition. Not yet on the console. Version icon (Material VideoLibrary) not checked.
- **Edition / Version Auto, opening edition (2026-11-19):** with no history, pick the edition by name
  class first (unnamed or standard/default, then theatrical, then any other cut); only editions of the
  best class present compare their Auto files (fit, resolution, HDR, server order). Bale's
  `formatting::opening_edition` still compares quality first and uses the name only as a tiebreak, so a
  2160p HDR Extended Cut beats a 1080p Standard cut (Android `e15879514`, Apple `b54fefb1`, Roku
  `3e7ee3a`, web `66105aa`).
- **Series page:** ~~Specials first as season zero~~ (done 2026-11-16: season tabs and the
  continuous episode row; a series still opens on a numbered season). ~~The last special continues
  into S1E1~~ (with Up Next, 2026-11-16) (Android `69e39ad45`, Apple `c466777f`, Roku
  `a41d106`). ~~The Show tab gives way to More › Show Series Info~~ (done 2026-11-16, verified on
  0.1.566 with Specials first and Left from S1E1 into Specials; the rail keeps its scroll instead of
  recentring on the suggested episode; Android `3632eda4e`, `064b8d0b7`, `c1371178b`).
- **Card overlays:** ~~watch status top-right with episodes left~~ (emerald check / episodes-left pill
  on posters, emerald check on episode stills), ~~keyline progress capsule~~ (Android `f65fe6917`),
  done 2026-11-16, not yet on the console. Still open: the web badge layout (edge rails, three
  corners, seven presets, style layer, uploaded images; Android `2d3620d50`) and one overlay scale
  (`cc4fd6662`).
- ~~**Card focus style:**~~ done 2026-11-16: 2 dp white outline at 70% and a dark drop shadow, no
  glow, the same outline on the current episode (Android `51d4f9013`).
- ~~**Library sort options:**~~ done 2026-11-17, not yet on the console: the web's list (up to 17,
  by library type), Rotten Tomatoes gated by the ratings capability, scrolling Sort panel (Android
  `a60dac2d0`, Apple `55da563c`). `CatalogSort.cs` ports the shared `CatalogSort.kt`;
  `RatingSourcesFeed` probes `GET /api/v2/capabilities/ratings` per profile; a remembered known
  sort stays in use and listed, only an unknown one falls back to Date Added. Not copied: Android
  re-keying rows when the RT answer lands while the panel is open (Bale rebuilds rows on open) and
  Apple's mixed-library Type-filter narrowing (Bale browses mixed libraries per tab side, as Android).
- **Profiles:** ~~hide Add at `max_profiles` (server `451cba6c`); names wrap to two lines on Who's
  watching (Android `34cef244f`, Apple `d0e87752`)~~ done 2026-11-16, not yet on the console. Still
  open: the primary profile can't have a library limit any more (server `df9f5501`; no TV client
  change yet, so Bale waits); `requests_allowed` toggle once the official TV editor has one
  (server `91611b42`).
- ~~**Profile menu:**~~ Done 2026-11-16, verified on 0.1.566 (Add Profile row not seen: three profiles): Watchlist and Favorites dropped
  (they live under For You); History, Requests, divider, Settings, profile row, Switch Server, red
  Sign Out; header server first, no header divider. The profile row is Switch Profile / Add
  Profile / none by profile count and `max_profiles`. Bale's Add Profile opens the picker's
  new-profile form (primary PIN first) through the picker, so cancelling leaves the viewer on the
  picker rather than back on the screen the menu was on. Switch Server stays until Change Account
  (several accounts) is built. Watch Party: Bale has none. (Android `136de70ad`, `8b9b3f017`,
  `84ea05a6b`; Apple `ddebb9fa`, `3abeaf4f`.)
- ~~**Remember-profile offer:**~~ done 2026-11-16: the explanation, then Ask Every Time and
  Remember with focus on Remember; Back answers Ask Every Time (Android `6b832ca73`).
- ~~**More Like This chain:**~~ done 2026-11-16, verified on 0.1.566 (two picks, one Back to the start): the first pick pushes,
  later picks replace, picking the start title goes back, a repeat pick of the title on screen
  opens nothing (Android `238134a1b`, Apple `fd40ce61`).
- ~~**Temporary password:**~~ done 2026-11-16: Who's watching (and any screen using
  `ProfileRepository.ErrorMessage`) says where to change it on 403 `password_change_required`
  (Android `65664d7ce`, Apple `04807485`). Not checked: focus on Sign out.
- **Several accounts per server / Change Account:** large; plan as its own task (Android
  `623bb3513`, Apple `54cf0f34`). Includes Change Account at launch when two or more accounts
  are saved (Android `ce24a6b5c`, Apple `8d9b2395`, Roku `4a22785`).
- **Back from Switch Profile** returns to the previous profile and page without its PIN (Apple
  `7bb8fe3d`); hold until Android `6091fb9e6` merges, then untick the parity-backlog line.
- ~~**Remove failed requests:**~~ done 2026-11-18, not yet on the console (Android `4600d910c`:
  `TvRequestsPage.kt`, `RequestsViewModels.kt`, `RequestsRepository.kt`, `RequestModels.kt`,
  `RequestMutations.kt`; Apple `df891d2e` agrees). Long-press / Menu on a failed request that
  delivered nothing offers Close + Remove request when the server reports
  `remove_failed_supported`; then "Remove this request?" with Keep request focused first. One
  `DELETE /api/v2/requests/{id}`, never retried: 204 "Request removed", 404 leaves silently,
  409 conflict keeps the row and says why, an uncertain answer re-reads and says either. Focus
  moves to the card that took the removed one's place. The request detail page has no Remove (as
  on both TV apps).
- **Not doing: box sets.** The Silo developer said box sets reached the server by mistake and
  reversed the decision (2026-11-10), so Bale doesn't build them even though the clients shipped
  them (Android `67e6e224a`, Apple `08c28c8e`). Ignore later box-set commits unless that changes.
- ~~**Rows of collections:**~~ done 2026-11-18, verified on the console (0.1.687; owner, backdrop and tint follow focus). Follows Android TV's own
  version, merged since (`db87f835f`: `TvCollectionRow.kt`, `TvCollectionCard.kt`,
  `TvSkylineSectionFeed.kt`, `TvHomeSections.kt`, `TvMainShell.kt`, `SectionModels.kt`): Home and
  library `collection_posters` sections as dense 88×132 poster rails of every card (no 20-card cap,
  no Explore all, a featured flag ignored); the focused card's title and backdrop (else poster) in
  the marquee, no detail fetch, no card menu; caption "3 items", or "by Name" for another
  profile's personal collection; no counts on a Movies/Series-scoped library page; Home keeps a
  server collection only when its library is a TV-visible one (none while the library list is
  unknown). A server collection opens the collection grid; on a library page both kinds open as
  the Collections tab opens them. Gap: Home's personal collection opens Bale's collection grid
  (with Sort/Filter), not Android's separate `TvCollectionDetailScreen` (title, Shuffle, grid).
  Bale reads the profile list for "by Name" only when a row holds another profile's collection
  (Android uses its cached list). Apple `6eea7cf7`, Roku `8183af8`/`d240f47`, server
  `4a0ab33b`/`217ff25a`.
- **Smaller** (worked through 2026-11-18, none yet on the console): ~~device name in sign-in~~
  (`bd6ade492`; console name, model kept for playback diagnostics), ~~hero logo grace 700 ms~~
  (`e57fbf3f6`), ~~Calendar DIGITAL/PHYSICAL badges~~ (`64ae5f08c`, Apple `a82ff603`), ~~Search
  clear button~~ (`548e3cb77`), ~~remote Stop key~~ (`969c0a812`; already handled since the 11-16
  transport keys), ~~unplayable subtitle rows~~ (`461240c6d`), ~~return-from-player episode~~
  (`e727c9af5`), ~~declined device-code lead~~ (`0548b9255`/`538e2e9a`; the code actions wrap),
  ~~port error outline~~ (`d1837b92`), ~~no Explore all on a playback row that shows its whole
  list~~ (`d1c36e7be`, Apple `8655b06b`), ~~requests re-probe on foreground~~ (`98d077170`, probe
  only), ~~grouped title counts~~ (`b2b0fb565`, Apple `710169c9`). Still open: detail drawn from the
  opened card (`62dc95035`): large (a seed model from every card origin, partial hero, actions
  gated until the fetch, 27 files upstream).
- **To check** (2026-11-18): ~~Continue Watching focus by series id~~ (`45a869871`; fixed: progress rows
  key episodes by series), ~~episode rail centring~~ (`d9dc3f3de`; checked, already matches: instant
  centre once per season load, retried until realized; the carousel-join parts don't apply to Bale's
  one-season rail), ~~Change Server forgetting a never-signed-in server~~ (`0a387afa0`; fixed: a server
  with no saved account), ~~season list re-read after playback~~ (`b7cc29ec8`; fixed: season watched
  state; Bale already awaits the stop), ~~stacked simultaneous text cues~~ (`3afd7006f`, Apple
  `3d4d7d1f`; checked in code: every cue is a Layer 0 Default event without position, so libass
  collision handling stacks them, earliest at the bottom; console look not yet confirmed), ~~cancellable
  connect~~ (Apple `2d7a4011`; fixed: Back reads Cancel), ~~top-bar menu timing~~ (Apple `933381d6`;
  checked, already matches: Down always opens a tab's menu; the rest is tvOS focus-engine specific),
  ~~focus after removing a card~~ (Apple `dce56e3c`; checked, already matches: BrowseFocus clamps to the
  removed index), ~~sidecar subtitle labels without file names~~ (server `4ac66bd6`; checked, already
  matches: Bale never reads file names; numbered labels pass through), ~~version order after the server
  sort~~ (server `85d1c166`; checked, already matches `select_version`), ~~Home rows for a
  quality-capped profile~~ (server `0b579aa6`; server-side only), ~~hero action memory on Up~~ (Apple
  `067e6cee`; done 2026-11-18, the owner's choice over Android's selectorFocus: entering the hero row
  lands on the last focused action, else Play, Watchlist while Play can't take focus; reset per page),
  ~~series year ranges~~ (Apple `9718f17b`; done, the owner's choice over Android's one year: card
  captions in Home and library rows, grids, search and history read show_status/last_air_date),
  ~~subtitle carried across a Version change~~ (Apple `417508d9`; done, the owner's choice over
  Android's onVersionSelected: a subtitle pick moves to the new file's matching track, Auto and Off
  carry; a Version pick keeps an audio ordinal the new file has, an Edition file change drops it),
  ~~deleted active profile on Home~~ (Apple `b5c73e3a`; fixed: the profile list names no profile but
  keeps the PIN proof; Home's 404 not_found re-reads it and, only when the active profile is gone,
  returns to Who's watching, as the shell's list read does; Bale's picker never opens a lone profile by
  itself; no Android counterpart). Still open: Home rows after the server update (console check). ~~source-open start budget~~
  (Apple `b9446d5d`; done 2026-11-18: a 45 s budget per mount on every route, docs/player-plan.md
  "Start budget").

- **Home watch state across devices (2026-11-20):** Apple (`0414ff74`) and Roku (`c1841ae`) now
  subscribe to the `silo.events.v2` socket's `user_state` channel while Home is on screen (ticket per
  profile, backoff to 30 s, Roku also polls every 120 s) and re-read Home on `user_state.changed` for
  the active profile and on each (re)subscribe; the server now publishes it after every stop that
  writes progress (`165ab2b`, `playback_stopped: true`) and closes sockets with codes 4001–4004
  (`f31842a`). Android TV already listens and now also re-reads when Home returns, retrying a
  transient section failure up to three times (1/2/4 s; `1b4732d90`). Bale has no events socket
  (`HomeFeedState.cs`: one refresh on return only), so another device's playback or a watched toggle
  stays stale until Bale's own return refresh.
- **Top-bar menu timing (2026-11-20):** menus open 120 ms after focus reaches a tab (was 180) and swap
  at once between neighbouring menu tabs (was 80); Down right after Back closed a menu opens it again
  (Android `5d90cf32f`, `TvTopMenuBar.kt`, `TvShellFocusState.kt`). Bale's
  `TopMenuPreviewTiming.Delay` still returns 180/80 ms; check the Down-after-Back case on the console.

- **Later, from the 2026-11-18 review** (tracked so they aren't lost; none needs work today):
  Not interested on cards and title pages (Apple `29c0dd43`; hold until Android TV has it), the
  per-corner Home wash with a 0.7 s colour crossfade (Apple `c5453eae`, `648091a7`; hold until
  Android TV follows, Bale ports its single-colour `AmbientBackdropTint`), personal ratings on a
  1–10 scale for any future rating UI and a `SILO_REF` bump with no functional diff expected
  (server `e17bfa1`), the Rust HLS reader on resume and `seek_reanchor` if
  `copy_keyframe_playlist` is turned on (server `92763e4`), ws-ticket 410 (stop) and 503 with
  `Retry-After` (wait, back off to 30 s) for the future control socket (server `4564351`,
  `a1c17ef`), Search's short and empty result states after the tighter keyword cut-off (server
  `651e55a`, console check).

- **Later, from the 2026-11-20 review:** subtitle search shown only when
  `GET /api/v2/subtitles/providers/status` says a provider exists, re-read per panel open, 5 s bound,
  failure keeps the last answer (Android `2ee32f414`; Bale has no subtitle search yet); the planner's
  E-AC-3/AC-3 5.1 conversion when the sink can't bitstream the source (server `d203b9f`; only with
  passthrough claims, which Bale doesn't make); `parallel_range_origin_v1` /
  `stream.parallel_range_url` (server `4eb4d4a`; withheld under `header_authenticated_media_v1`);
  catalog-event `library_id` as a string (server `9c2ec94`); the item's collections route
  (server `faa406d`; web only so far); Next Up timed from the file end by
  `playback.next_up_prompt_seconds` (server `7019d69`; Android TV still uses credits start, as Bale);
  a seek kept across a replan (server `d22e55f`, with `4a73f48`); HLS kept playing through a server
  outage (server `40c093c`, compare the Rust HLS reader).

- **Later, from the 2026-11-21 review:** the profile section baseline route and
  `home_layout_baseline` flag, and the section settings read listing a page's default rows (server
  `f18678f`; for a future Home row editor, Bale's layout is local today); request create dropping
  `poster_path` / `backdrop_path` values that aren't plain TMDB image paths, and the admin-only
  `MediaRequest.poster_url` (server `6633874`; Bale forwards the detail read's paths); a zero
  download estimate omitted from `download.estimated_completion_at` (server `28dd148`); hero
  backdrop banding on dark art (Android `c6dfd52a6`; console check of Bale's masked art).

- **Buffering quality, slow-link evidence (2026-11-22):** an Auto lowering sends
  `bandwidth_estimate_source: observed_rebuffer` and `observed_throughput_kbps` only when the plan's
  measured throughput is below its delivered bitrate, also after the attempt has played, and drops the
  source when the reading is gone at send time (network change); otherwise the estimate goes alone,
  which the server plans under but never learns. The Lower prompt pick follows the same rule (Android
  `878c972c4`, Apple `3709c5fb`, server `f503d7e`). Bale's `buffering.rs` sends the source on every
  lowering once played, and has no reading on HLS (`PlanThroughput`), where Apple times each segment
  from first to last byte (segments under 1 s left out, overlaps once). Add tests for a lowering with
  and without evidence; decide whether the Rust HLS reader times segment bodies as evidence.
- **Up Next timing (2026-11-22):** the card opens on crossing `playback.next_up_prompt_seconds` before
  the end of the file (default 30; 0 = only at the stream's end), not at the credits marker; credits
  drive only Skip Credits; a hand-opened card gets no countdown at the prompt point; the series-page
  return still records the closing-credits crossing (Android `e580e1811`, shared `NextUpPrompt.kt`,
  server `7019d69`). Bale's `PlayerPage.UpNext.cs` raises it at `Credits.Start` and reads no setting.
- **Collections on the title page (2026-11-22):** with `item_collections` in the search capabilities,
  movies and series read `listCatalogItemCollections`: one collection shows up to six of its other
  titles plus Explore all, several show an "Also in" row of collection posters; no books, no
  collections of libraries TV hides; Back returns to the card that opened it (Android `cb2c32d8a`,
  `TvCollectionRow.kt`; server `faa406d`). Needs the route in `scripts/generate-api.sh`.

- **Later, from the 2026-11-22 review:** a sidecar pick the server answers with burn-in, adopted and
  standing for the picked row (Android `c89bdf34b`; Bale remounts on burn-in already, console check of the
  menu and saved choice); episodes after same-titled movies and series in search (server `3e11909`); Date
  Viewed dating a series by its episodes (server `b1d07ad`); burned-in PGS fitted to cropped frames
  (server `2972dc6`); edition read from filename tags and the item's own folder (server `b3d25aa`).
- **Paused-session timeout (2026-11-24):** the server ends a native session paused longer than
  `playback.paused_session_timeout_minutes` (default 15) and answers progress and replans with 410
  (server `dde5ed2`). Send `paused_timeout_v1` in `client_features` on every start and keep it on each
  replan of the attempt; on 410 `playback_session_paused_timeout` or `playback_session_ended` from progress,
  a failure/quality/track replan or a seek reanchor, leave the player without a new attempt or a stop (the
  server already ended it), with "Playback stopped because it was paused for too long." for the former and
  the admin wording for the latter (Android `56e23561a` `PlaybackSessionLifecycle.playbackServerEnd`, Apple
  `80ae9437`, Roku `6389fcc`). Today `PlaybackProgressReporter.RunAsync` only stores the error.

## Reviews

### 2026-11-24

One act item, raised by all four projects: the server now ends a session left paused for 15 minutes
(default) and answers progress and replans with 410; the official apps advertise `paused_timeout_v1` and
leave the player with a "paused for too long" notice, and Bale handles no 410 there at all. Also: Android
labels sports episodes by air date (server side not merged yet), Apple adds star ratings on the title page,
and transcode playlists may now end in empty segments.

#### silo-android (`bbbfdeeb57f9..b7c0412af6b4`, 3 commits, 1 none)

- `56e23561a` act: a 410 `playback_session_paused_timeout` (the server ended a session left paused past an
  admin limit) on progress, failure-recovery or staged replans, seek reanchor or seek fallback leaves the
  player like an administrator's 410 `playback_session_ended`, with `player.notice.pausedTimeout` instead
  of the admin wording; video starts advertise `paused_timeout_v1`, repeated on every replan of the
  attempt (`PlaybackSessionLifecycle.playbackServerEnd`). Bale handles neither 410: the progress loop
  only stores `LastProgressError` (`PlaybackProgressReporter.RunAsync`) and the player stays.
- `b7c0412af` later: a series the server marks `episode_labels=air_date` (Sportarr leagues numbered
  S<year>E<n>) shows the episode's air date ("Nov 20, 2026") wherever the episode code went: Continue
  Watching and Home episode cards, the hero marquee, the season rail's metadata line, play labels, the
  player eyebrow and Info pane, Up Next; undated episodes keep their code, sorting keeps the numbers.
  The player reads the labels from `/api/v2/watch` only. Bale always prints `S01E01` (`EpisodeCode`). The
  server side (`episode_labels`, `air_date` on cards, detail and watch) is not on silo-server's main branch
  or in its contract yet; build it once it lands.

None for Bale also: `137fca310` (downloads: an offline stop in the end credits queues the full duration).

#### silo-apple (`07c4c0d4843b..d9bbdc77c102`, 4 commits, 2 none)

- `80ae9437` act: the same paused-timeout exit and `paused_timeout_v1` advertisement as Android
  `56e23561a`.
- `c4cbdeb4` later: movie and series pages get a star rating button between Watchlist and More (tvOS:
  Select opens a half-star editing pill, Left/Right change it, Select saves, Back cancels, holding Select
  removes; `PUT`/`DELETE /api/v2/ratings/{item_id}`, `user_rating` on detail). Apple-only; hold until
  Android TV follows, as with the More menu redesign.

None for Bale also: `82b9bfab` (the player's own watched checks count the end credits; Bale has no local
90% check and refreshes from the server), `d9bbdc77` (iPad/iPhone sidebar).

#### silo-server (`3b2b321688ac..64f9ee20ecee`, 46 commits, 42 none)

- `dde5ed2` act: the server ends a native session left paused longer than
  `playback.paused_session_timeout_minutes` (default 15, 0 off) while the client still reports. Progress
  and replan then answer 410 `playback_session_paused_timeout` when the start listed the attempt-sticky
  client feature `paused_timeout_v1`, else 410 `playback_session_ended`; the client leaves the player and
  starts nothing. On by default and Bale reports every 10 s while paused, so a Bale session paused 15
  minutes is ended while the player stays open. See the Open item.
- `4bee8f8` later: an encoded transcode's synthetic VOD playlist can list segments past the media's real
  end; once FFmpeg has read to the end they answer 200 with a segment holding no media (MPEG-TS PAT/PMT,
  or fMP4 `moof` with an empty `mdat`) instead of 404/500, and the stored duration is lowered. Console
  check that Bale's Rust HLS reader and FFmpeg step over such segments and end playback cleanly.
- `1da5b75` later: audio-only remuxes are written in 2-second fragments instead of one at the end; matters
  once Bale plays audio-only files (no audiobook player yet).
- `abef9b3` later: an admin message receipt reads `acknowledged` when the player answers on the control
  socket within 3 s. For Bale's control socket when it is built: answer a message frame on receipt.

None for Bale also: `6480841` (CPU decode fallback for WMV 7/8, MS-MPEG-4 and AV1 transcodes without a GPU
decoder), `708d199` (keyframe-planned copy handles open-GOP files), `835e488`, `97ace34` (transcode nodes),
`ebd0adf`, `d858c3c` (panics answer the `internal_error` problem), `c8d4d41`, `1c33d53`, `a62176b` (SVG
logos cached at full size), `fdb187a` (web hides format-only audio titles), `bbde112` (web player leaves when
the session ends while minimized), and jellycompat, recommendations, plugins, metadata, watchsync, blobstore,
task-manager, test, web and admin fixes.

Contract changes on generated paths: none. The OpenAPI diff touches only admin and node schemas
(`acknowledged` on `AdminPlaybackCommandReceipt.delivery`, `stream_end_seconds` / `stream_planned_seconds` on
the transcode-node session report); the new 410 problem type is documented in the protocol docs only.

#### silo-roku (`5778e4765def..6389fccb8ead`, 1 commit)

- `6389fcc` act: the same paused-timeout exit on Roku: every start and replan sends `paused_timeout_v1`,
  the 410 drops the queued stop job (the server already ended the session) and the notice draws over the
  screen the player returns to.

### 2026-11-23

No act items. The batch is quiet for Bale: all three projects raise the default cellular quality to Auto up
to 10 Mbps with a 2 Mbps Data Saver / Low Data Mode cap (settings manifest revision 30; Bale is never
metered), Apple redesigns its tvOS More menu, and the server marks an item watched at its end credits,
stops forcing a transcode for multi-PPS H.264 and lets a web player give up on a progressive remux that
keeps dropping at the same point. No contract change on a path Bale generates.

#### silo-android (`e8071640afc1..bbbfdeeb57f9`, 1 commit, 1 none)

- `bbbfdeeb5` none: cellular quality defaults to Auto up to 10 Mbps, Data Saver caps at 2 Mbps; the TV
  player only renames its unmetered quality pair (`PlaybackNetworkTier.UNMETERED`).

#### silo-apple (`3709c5fbefd1..07c4c0d4843b`, 2 commits, 1 none)

- `131efb8c` later: the tvOS detail More menu is redesigned: episode watched toggle, submenus, a
  popover anchored at the side, the episode rail's long-press menu aligned with it (no Play, no Watch
  Party; remote Play/Pause plays the focused episode), the Version selector focusable with a single
  version, and focus returning straight to the popover trigger. Apple-only; hold until Android TV
  follows, as with Not interested.

#### silo-server (`c90e406db71e..3b2b321688ac`, 23 commits, 19 none)

- `47a85e4` later: reaching the played file's end-credits marker now finishes the item (whichever comes
  first of the credits and the watched threshold), also below the resume floor, only for the file the
  session actually streams. Server-side; Bale's stop report is unchanged. Console check that a title
  stopped in its credits leaves Continue Watching on Bale's return refresh.
- `7a74859` later: the planner no longer skips stream-copy routes for multi-PPS H.264; a strict decoder
  fails the copy and the ordinary recovery walk ends at the encode. Bale's hardware H.264 route may now
  get a copy where it got a transcode; a decode failure takes Bale's single replan.
- `41ea93e` later: protocol §6 reconnect exception: when a reopened progressive remux drops again within
  30 s, with no seek, less than 1 s past where it dropped before, the client sends `failure_recovery`
  with `failure.classification: "progressive_drop_repeated"` instead of another reconnect cycle (web
  player; Chromium's media cache re-asking for dropped bytes). Bale reads the remux forward-only and
  already caps reconnects (3 per 120 s, then the network failure replan, `PlayerPage.Media.cs`); adopt
  the classification if the console ever shows a same-point drop loop.
- `6a13f8a` later: the library Collections tab and `GET /api/v2/library/{id}/user-collections` list a
  profile's own collections in its saved order, then shared ones by creator (was by name). Bale shows
  the server's order; console check when the tab's personal group is checked.

None for Bale also: `cf64c37` (MKV bitrates no longer count attached fonts, with a one-off backfill;
fansub releases may now direct-play where they transcoded), `113c97c` (cellular defaults, settings
revision 30), `b2bab50` (admin history subtitle order), `4d5040e`, the web, jellycompat, admin, reader,
metadata, task-manager and CI fixes.

Contract changes on generated paths: none. The OpenAPI diff is two summary lines on the library
collections routes (shared collections from other profiles on the same login).

### 2026-11-22

Three act items. All three official apps now send `observed_rebuffer` with a lowering only when there is
measured slow-transfer evidence (the server's design now says the same). Android TV opens Up Next at
the playback setting's point before the end of the file instead of at the credits marker, and shows
the collections a title is in on its page. The rest of the server batch (82 commits) is web UI,
right-to-left, notification, backup and node work, with no contract change on a path Bale generates.

#### silo-android (`c6dfd52a631c..e8071640afc1`, 7 commits, 3 none)

- `878c972c4` act: a stall's Auto lowering still goes out on every counted stall, but carries
  `bandwidth_estimate_source: observed_rebuffer` (and `observed_throughput_kbps`) only when the
  playback throughput meter shows the plan's media arrived below its delivered bitrate (transfer
  timed from each request's first byte, requests under 1 s ignored); otherwise the estimate goes
  unsourced, which the server plans under but never learns (24 h). A stall from a slow encoder,
  burn-in or a node starting no longer caps later playback on the connection. The source is also
  dropped when the meter has no reading at send time (network change). Bale's Rust controller
  (`buffering.rs` `send_replan`) attaches `observed_rebuffer` to every Auto lowering with an estimate,
  as Android did before.
- `e580e1811` act: Up Next opens `playback.next_up_prompt_seconds` before the end of the file
  (shared `NextUpPrompt.kt`, crossing only, so a resume or seek past it doesn't open it), or only at
  the stream's end for "At end" (0); credits markers drive only Skip Credits, and skipping credits
  that run to the end ends the stream. The TV settings default is now 30 s, as the server's. The
  series-page return still records the closing-credits crossing on its own, and a hand-opened Up
  Next isn't turned into a countdown at the prompt point. Bale's `PlayerPage.UpNext.cs` still raises
  the card at the version's `Credits.Start` and doesn't read the setting (follows server `7019d69`,
  tracked as later on 2026-11-20).
- `cb2c32d8a` act: the title page shows the collections a movie or series is in, read from
  `listCatalogItemCollections` only when the search capabilities advertise `item_collections`: one
  collection gives up to six of its other titles under its name plus Explore all; several give an
  "Also in" row of collection posters. Nothing while loading, empty or on a first-read failure; a
  failed refresh keeps the row. TV leaves out books and collections of libraries TV doesn't show;
  Back from a card it opened returns to that card, Up from a first-rail row returns to the hero.
  Bale's Detail has no such row and doesn't generate the item collections route (server `faa406d`).
- `c89bdf34b` later: a sidecar subtitle pick the server answers with burn-in (an ASS track on an HLS
  transcode) is now adopted and remounts at the current position, standing for the picked row
  (menu check, saved choice). Bale's `PlaybackReplan.TransportChange` already remounts on a burn-in
  answer; console check that the menu checks the picked row and an Auto burn-in isn't saved.
- `03972d479` none for Bale: the TV menu no longer lists an embedded track twice (server row plus
  Media3's own track); Bale lists only the server's rows.

#### silo-apple (`71a3beb9c0c4..3709c5fbefd1`, 2 commits, 1 none)

- `3709c5fb` act: the same slow-link rule as Android `878c972c4`, also for the Lower prompt pick; on
  server HLS the evidence is now the relay's own segment timing (first to last byte, segments under
  1 s left out, overlaps counted once), not AVPlayer's loopback access log, which counted encode time
  as transfer. Bale has no throughput reading on HLS (`PlanThroughput`).

#### silo-server (`a84b2551b236..c90e406db71e`, 82 commits, 77 none)

- `f503d7e` act: the buffering-quality design gains a "Slow-link evidence" rule, the server side of
  Android `878c972c4` / Apple `3709c5fb`: an Auto lowering carries `observed_rebuffer` and
  `observed_throughput_kbps` only when the playing plan's measured throughput (first body byte to last,
  at least 1 s receiving, at least 100 kbps, no network change while it played) is below its delivered
  bitrate, before and after the first frame; without evidence the estimate goes with no source. The
  server also declines to learn a limit when its transcode had no lead over the client (new node route
  `GET /transcode/{session_id}/output-bound`; unknown learns nothing). Bale requires evidence only
  before the attempt has played (`buffering.rs` `evaluate_trigger` / `send_replan`). The conformance
  vectors are unchanged; the spec asks each client to test a lowering without evidence itself.
- `3e11909` later: keyword search ranks episodes after movies and series with the same title (search
  index schema 5, servers rebuild their index). Bale shows the server's order; console check.
- `b1d07ad` later: the Date Viewed sort dates a series by its latest finished episode (before, a TV
  library fell back to title order). Bale offers the sort (`CatalogSort.cs`); console check of a TV
  library.
- `2972dc6` later: burned-in PGS is fitted to the video frame (aspect kept, centred), so subtitles on
  cropped encodes stay inside the picture. Server-side only.
- `b3d25aa` later: the scanner reads a file's edition from filename tags before folder names, and only
  from the item's own folder. Edition labels and keys can change after a rescan; Bale groups by key
  (`formatting.rs`), console check of the label.

Contract changes on generated paths: none. The contract diff (admin direct-connections routes, the
email unsubscribe link route, backup restore items and settings, `AdminPluginInstallation`, the
VideoToolbox node override value, the node-listener `output-bound` route) is on paths Bale doesn't
generate.

### 2026-11-21

No act items. The server batch (243 commits) is almost entirely web admin UI, accessibility,
right-to-left and translation fixes, backup, deployment configuration and CI; the only change on a
path Bale generates is one optional admin-only field. The TV apps dropped the older phones'
match-word footnote from device sign-in, which Bale never showed.

#### silo-android (`1b4732d90beb..c6dfd52a631c`, 2 commits, 1 none)

- `c6dfd52a6` later: the Home hero backdrop no longer bands on dark art: the art draws whole and the
  wash is painted over it through the corner fade in one draw, dithered before rounding (Android 9's
  GPU path doesn't dither gradients). The look is unchanged; Bale masks the art with
  `Assets/ArtMask.png` over its tint (`MaskedArt.cs`, `AmbientTint.cs`): console check for banding
  on dark art.
- `63c898084` none for Bale: device sign-in approval copy is device-neutral and the "Older phones
  show ..." match-word note is gone from `TvPairingPanel.kt`; Bale's sign-in never showed match
  words.

#### silo-apple (`f70e16aeba15..71a3beb9c0c4`, 1 commit, 1 none)

The same pairing change (`71a3beb9`): match-word note removed, a generic approval title when the
device name is unknown.

#### silo-server (`975bbf41b33c..a84b2551b236`, 243 commits, 240 none)

- `6633874` later: admin request reads return `poster_url`, a server path that fetches the TMDB
  poster (capability `request_posters` on the admin request capabilities); the field sits on the
  shared `MediaRequest` schema but is filled on admin reads only. Request create now keeps
  `poster_path` / `backdrop_path` only when they are plain TMDB image paths and drops any other
  shape without failing. Bale's create (`RequestsFeed.cs`) passes the detail read's paths through
  unchanged, so nothing changes.
- `28dd148` later: a request's `download.estimated_completion_at` is left out when the estimate is
  zero. Bale doesn't read the download estimate (`RequestPresentation.cs`).
- `f18678f` later: a profile section baseline route (per-profile tokens for the server rows a page
  shows, for the web's Home layout export/import) and `home_layout_baseline` on the profile section
  flags; the section settings read lists a page's default rows. Bale keeps its row layout locally
  (`HomeSectionLayout.cs`); for a future Home row editor.

Contract changes on generated paths, additive (regenerate and check at the next `SILO_REF` bump):
`MediaRequest.poster_url` (optional, admin reads only). The rest of the contract diff (the two new
routes, `home_layout_baseline`, `AdminRequestCounts.requests_enabled`, deployment-managed node and
AI provider fields, backup restore change lists, refresh-task samples) is on paths Bale doesn't
generate.

#### silo-roku (`c1841aeb896e..5778e4765def`, 1 commit, 1 none)

The same pairing change (`5778e47`): the companion code card loses the match-word line.

### 2026-11-20

Two act items: Home should follow watch-state changes from other devices (Apple and Roku joined
Android on the `user_state` events channel, and the server now announces every stop), and the
top-bar menu timing moved to 120 ms / immediate. The server batch touches no path Bale generates
except two optional fields.

#### silo-android (`e158795142f8..1b4732d90beb`, 5 commits, 2 none)

- `5d90cf32f` act: top-bar menus open 120 ms after focus reaches a tab (was 180) and swap at once
  between neighbouring menu tabs (was 80); Down after Back closed a menu opens it every time.
  Bale's `TopMenuPreviewTiming` keeps 180/80 ms (Open items, top-bar menu timing).
- `1b4732d90` act: Home re-reads when it returns (not on a new ViewModel's first load) and retries a
  transient section hydration failure up to three times with 1/2/4 s backoff. Bale refreshes once on
  return with no retry (Open items, Home watch state).
- `2ee32f414` later: subtitle search (Find more / Search online) only when
  `subtitles/providers/status` reports a provider; Bale has no subtitle search yet.

#### silo-apple (`9dabb6213812..f70e16aeba15`, 4 commits, 3 none)

- `0414ff74` act: Home subscribes to the `user_state` channel (`HomeWatchStateRealtime.swift`) and
  re-reads on `user_state.changed` for the active profile and on each subscribe. Bale has no events
  socket (Open items, Home watch state).

#### silo-server (`bd40bd9ebfa4..975bbf41b33c`, 50 commits, 34 none)

- `165ab2b` act: a stop that writes progress, history or watched state publishes
  `user_state.changed` with `playback_stopped: true` (`content_id`, `series_id`); stop finalisation
  survives the player dropping its connection (Open items, Home watch state).
- `f31842a` later: v2 sockets close with 4001 `access_changed` / 4003 `lifetime_ended` (new ticket,
  reconnect), 4002 `credential_invalid` (final), 4004 `authority_unavailable` (back off); an
  uncheckable ticket mint answers 503. For Bale's events socket and the future control socket.
- `9a180df` later: control-socket commands carry the administrator's reason as top-level `reason`.
- `d203b9f` later: `server_remux_hls` converts a surround track to E-AC-3 (else AC-3) 5.1 when the
  sink attests `layout_aware_passthrough` but can't bitstream the source
  (`surround_bitstream_conversion_v1`). Bale sends that feature only with passthrough entries
  (`PlaybackRequests.cs`) and claims none, so it never triggers.
- `4eb4d4a` later: `parallel_range_origin_v1` and `stream.parallel_range_url` (original_http,
  self-authorising); absent under `header_authenticated_media_v1`, which Bale sends.
- `0e9cf1f` later: spellings of one named edition (durations within 5 s) share an `edition_key`; files
  keep their `edition_raw`. Bale groups by key (`formatting.rs`); console check of the label.
- `faa406d` later: `GET /api/v2/catalog/items/{id}/collections` (capability `item_collections`); only
  the web shows it so far.
- `7019d69` later: the web opens Next Up `playback.next_up_prompt_seconds` before the file end;
  Android TV and Bale still use credits start.
- `d22e55f` later: the web keeps a seek made during a replan (with `4a73f48`, still to check).
- `40c093c` later: the web plays buffered HLS through a server outage and reconnects only when needed.
- `9c2ec94` later: `library_id` is a string in catalog events.
- `36640d6` later: video and episode rows list the same titles as their See All page; console check.
- `c7c1559`, `6415237`, `9df62cd`, `5a748e2` later: live session rows rename `content_id` →
  `media_item_id` and `series_name` → `series_title` and drop `episode_name` (breaking, on household
  sessions); session rows and history gain `subtitle`; admin history `end_position_seconds`; signed
  admin poster route. None on a path Bale generates.

Contract changes on generated paths, additive (regenerate and check at the next `SILO_REF` bump):
`StreamV3.parallel_range_url`, `CatalogSearchCapabilities.item_collections`. The breaking renames,
new routes and admin/plugin/download fields are on paths Bale doesn't generate.

#### silo-roku (`3e7ee3a566c1..c1841aeb896e`, 1 commit, 0 none)

- `c1841ae` act: Home follows `user_state` changes over the events socket, with a 120 s poll as
  fallback (`HomeWatchState.bs`, `HomeEventsTask.bs`) (Open items, Home watch state).

### 2026-11-19

One act item, on every client at once: the opening edition is chosen by name before quality. The
server batch is web, plugins, admin environment settings and nodes, with no change to a path Bale
generates.

#### silo-android (`db87f835ff90..e158795142f8`, 2 commits, 0 none)

- `e15879514` act: with no history the detail page opens the unnamed or standard edition, then
  theatrical, then any other cut; quality (fit, resolution, HDR, server order) competes only inside
  that best name class (`DetailVersionSelector.openingEdition`). Bale ranks quality first (Open
  items, opening edition).
- `bb59172f8` later: Media3 direct play gets a fresh retry window after sustained progress (enough
  bytes over a recovery interval, at a new file offset), so a later outage in a long film isn't
  counted against the first one's window; failures log Media3 codes and exception class names, never
  messages. Android-only player; playback follows Apple, but check Bale's original_http reconnect
  budget the same way if long films give up after an early outage.

#### silo-apple (`648091a7d9b7..9dabb6213812`, 2 commits, 0 none)

- `b54fefb1` act: the same opening-edition rule as Android (`DetailVersionSelection.swift`).
- `9dabb621` later: the tvOS Home/library corner wash drops the top-right colour beneath wide
  artwork (the art fades into the other three corners); framed covers keep all four with a glow.
  Extends the 2026-11-18 corner-wash later item; Bale keeps Android's single-colour `AmbientTint`.

#### silo-server (`651e55a32253..bd40bd9ebfa4`, 27 commits, 24 none)

- `66105aa` act: the web's opening-edition change, the source of the client commits above.
- `e78f889` later: Recently Released rows order by release date (a series' premiere when it has
  none), exactly as their See All page. Server-side; Bale shows the server's order, so rows and See
  all should now agree.
- `4a73f48` later: the web keeps a seek made before a direct-played original loads while a quality
  change is queued. Check Bale's quality-change replan keeps a pending seek target the same way.

Contract changes (admin settings `managed_by_env_vars`, node `env_managed` and job-cap refusals,
plugin schedules and notification destinations) are on paths Bale doesn't generate.

#### silo-roku (`d240f47964d9..3e7ee3a566c1`, 1 commit, 0 none)

- `3e7ee3a` act: the same opening-edition rule; a last played edition still wins.

### 2026-11-18

No new act items. Android TV took rows of collections (already built from it), Apple TV added Not
interested and a richer Home backdrop wash; the server batch is mostly jellycompat, web and admin.

#### silo-android (`0867eacb14b5..db87f835ff90`, 1 commit, 0 none)

- `db87f835f` act, done 2026-11-18: Home and library rows of collections on Android TV. Bale's
  version already follows this commit (Open items, Rows of collections).

#### silo-apple (`6eea7cf74eef..648091a7d9b7`, 5 commits, 2 none)

- `29c0dd43` later: Not interested on movie and series cards and title pages
  (`PUT`/`DELETE /api/v2/not-interested/{id}`, shown only when the server sends
  `user_state.not_interested`); Home and For You drop a marked title at once, and tvOS keeps focus
  when its card or row leaves. Android TV has no Not interested yet; hold until it does. Bale
  generates `CatalogItemUserState.NotInterested` but not the path.
- `c5453eae`, `648091a7` later: the tvOS Home/library page wash takes one colour per screen
  corner from the matching quarter of the art (kept dark, grey art stays grey) and crossfades
  over 0.7 s while the art still swaps in 0.24 s. Bale ports Android's single-colour
  `AmbientBackdropTint` (`AmbientTint`); take it if Android TV follows.

#### silo-server (`217ff25abc5b..651e55a32253`, 38 commits, 31 none)

- `e17bfa1` regenerate and check: personal ratings go from 1–5 to 1–10 (five stars with half
  stars); field names unchanged, `getPersonalRatingCapabilities` reports the scale. Bale generates
  only `CatalogItemDetail.user_rating` (range and description change) and shows no personal
  rating; a future rating UI must use 1–10. The other contract changes (admin catalog
  `include_file_paths`, ws-ticket 410/503, HEAD on the HLS master) are on paths Bale doesn't
  generate, so bumping `SILO_REF` should give no functional diff.
- `92763e4` later: with `copy_keyframe_playlist` on, a resumed copy-remux HLS session gets a
  complete VOD playlist starting at the resume keyframe; the stream origin is unchanged. Extends
  the 2026-11-10 later item: check the Rust HLS reader on resume and `seek_reanchor` if the setting
  is turned on.
- `4564351`, `a1c17ef` later: minting a control ws-ticket answers 410 `playback_session_ended`
  for a stopped session and 503 with `Retry-After: 5` while it is rebuilt (404 stays for unknown);
  the web backs off its retries to 30 s. For Bale's control socket when it is built.
- `951a018` later: the v2 HLS master playlist answers HEAD (for DLNA renderers). Bale sends GET.
- `651e55a` later: keyword search results are cut at the first weaker match, so searches can
  return far fewer results; no contract change. Search's short and empty result states should
  still look right.
- `75f7712` later: renaming an account also renames its primary profile while it still has the
  old username. Server-side; Bale shows what the profile list returns.

### 2026-11-17

One cross-client feature: Home and library rows of collections (server, Apple TV, Roku). Android
TV has not taken it yet.

#### silo-android (`f66681ed81e1..0867eacb14b5`, 1 commit, 1 none)

No TV or shared changes (the phone bottom bar collapses on scroll).

#### silo-apple (`dce56e3c5f0a..6eea7cf74eef`, 1 commit, 0 none)

- `6eea7cf7` act: a section of type `collection_posters` brings its cards in `collections`
  (no `items`); Apple TV draws them as collection poster cards in a Skyline row with title-row
  focus, the marquee previews the focused collection and fills the hero with its backdrop
  (thumbhash first, neighbours warmed); a card that can't be opened is dropped. A server
  collection opens in its library (keeping a library page's Movies/Shows filter), a personal one
  opens its own page; another profile's shared collection reads "by Name". No See all / Explore
  all, never the hero row. Bale drops the row: it has no items (BrowseFeed).

#### silo-server (`92ee051878c2..217ff25abc5b`, 11 commits, 7 none)

- `4a0ab33b`, `217ff25a` regenerate and check: section type `collection_posters` with optional
  `collections` (`SectionCollectionCard`: id, title, poster, collage flag, `item_count`,
  `library_id`, `creator_profile_id`, and `backdrop_url`/`backdrop_thumbhash` on server
  collections), `collection_count`, and `getHomeCapabilities.collection_rows`. A client that
  doesn't show collections leaves the row out, as Bale does today. act: see Apple `6eea7cf7`.
- `03489137` regenerate and check (breaking): `notify_favorites`, `notify_watchlist`,
  `notify_continue_watching` and the `reason` filter of `GET /api/v2/notifications/series` are
  removed; only the Next Up episode and watchlist arrivals notify. Bale uses none of them.
- `1bdea6eb` regenerate and check: accounts and profiles may create scoped API keys (admin
  switch, off by default; no playback scope). Nothing for Bale.

#### silo-roku (`e09a5e0a3efe..d240f47964d9`, 2 commits, 0 none)

- `8183af8`, `d240f47` act: the same rows of collections (Apple `6eea7cf7`): Collections-grid
  cards, every card the server sends with no Explore all, a card opens the collection page (on a
  library page, that library's view), never the hero; the focused card's backdrop fills the
  hero. An empty collection opened from a row says Back, not Back to collections.

### 2026-11-16

The TV apps (and Roku) shipped a batch of cross-client owner decisions: Edition-scoped Auto,
Specials first, the web's card overlays and sort list, and the count-based profile row. Each
**act** was checked against Bale's source.

#### silo-android (`48015d4ee3ba..f66681ed81e1`, 53 commits, 17 none)

- `8b9b3f017` act: the profile row follows the account: Switch Profile with several profiles,
  Add Profile with one and room for more (`max_profiles`), no row at a limit of one. Order is
  History, Requests, Watch Party, divider, Settings, profile row, Change Account, Sign Out,
  focus on History. Bale shows Switch Profile first and always, and Switch Server (ShellPage).
- `84ea05a6b` act: menu header reads server first, then role/username; no divider under it;
  Sign Out is destructive red at rest. Bale has subtitle then host, a divider and a plain
  Sign Out (ShellPage).
- `ce24a6b5c` act: with two or more saved accounts and Profile Selection asking, launch and
  return-from-background open Change Account. Part of the several-accounts task.
- `2d3620d50`, `cc4fd6662` act: poster overlays match the web: watch status top-right (check,
  or episodes left), badges in three corners plus two edge rails, seven presets, uploaded badge
  images, no 0.86 calibration on badge text. Bale draws the fixed Classic stack and a watched
  check (BrowseViews, BrowseFeed).
- `f65fe6917` act: watch progress is a keyline capsule (4dp, inset 10/8, black 55% outlined
  track, `#EDEDED` fill) on media, episode and shelf cards. Bale has the flush white-20% track
  (`SkylineProgressTrack`).
- `466c31968` act: Edition picks the cut and Version Auto stays inside it; the page opens on
  `last_edition_key`, then `last_file_id`; an Edition pick keeps Version on Auto. Bale's Edition
  pick pins a file (PlaybackSelection, PlaybackEditions). deviation: display resolution
  matching stays as the ranking inside the edition.
- `4686d6dd8` act: the Version selector icon is Material VideoLibrary. Bale draws the clapper.
- `a60dac2d0` act: sort lists mirror the web (up to 17 fields by library type, ratings split by
  source, Rotten Tomatoes gated by `GET /api/v2/capabilities/ratings`), the Sort panel scrolls.
  Bale has seven fixed options (LibraryBrowseModels).
- `3632eda4e`, `5613b278e`, `064b8d0b7`, `c1371178b` act: the Show tab leaves the season row;
  More › Show Series Info (after Shuffle) returns the hero to the series. Bale still has the
  Show tab (DetailViews).
- `69e39ad45` act: Specials first as season zero; the default season still prefers numbered
  seasons. Bale sorts Specials last (DetailFeed).
- `d1c36e7be` act: no Explore all on a Continue Watching / In Progress / Next Up row that holds
  its whole list (`total_count` ≤ cards, fewer cards than min(20, `item_limit`)). Bale offers it
  on every non-featured row with cards (BrowseModels).
- `62dc95035` act: detail draws the opened card's title, facts and art on the first frame; Play
  reads Loading until the fetch. Bale shows the skeleton.
- `98d077170` act: the requests capability is re-probed on foreground, and open Requests screens
  close when requests are off. Bale probes on shell load and menu open only.
- `34cef244f` act: Who's watching names wrap to two lines (one in Manage). Bale trims to one.
- `b2b0fb565` act: title counts group thousands. Bale prints them ungrouped (PersonModels).
- `b7cc29ec8` check: after the player stops, the series page re-reads seasons as well as
  episodes. Bale re-reads detail and episodes, not the season list.
- `3afd7006f` check: simultaneous text cues stack. libass should already; confirm on the console.
- Already matched: `40d34e6d9` server resume point after stop, `a4045779b` no local resume for
  streams, `fe3d9c8af` `pcm_*` claimed, `d902e961b` season zero labelled Specials.
- later: `69e142cd6` still-watching count resets on pause/seek (phase 5); `cc35bac84`
  Left/Right seek on a focused skip pill; `d9983125f`, `c24555c70` every marker band above the
  played fill in the TheIntroDB palette (phase 3); `e8c10f1db` end time ticks while paused,
  `a09cd3e66` Space toggles pause; `a9c8fa4cf`, `73e55b96b` invitation claim; `376446317`,
  `f66681ed8` audiobooks; `f410d220b` 13 more languages.

#### silo-apple (`e792abdcb523..dce56e3c5f0a`, 83 commits, 39 none)

Most tvOS changes have an Android TV twin, given in brackets. `6091fb9e6` and `df3b26177`
exist only on unmerged Android branches.

- `94a6095f`, `197b5c18` act: Edition-scoped Auto (Android `466c31968`); Auto ranks first parts
  by resolution, HDR, then size, no bitrate. Bale's `select_version` (bale-core formatting)
  takes `last_file_id` first and ranks across cuts by resolution, bitrate, size. deviation:
  display matching stays inside the edition.
- `c466777f` act: Specials first; the last special continues into S1E1 (Android `69e39ad45`).
- `8655b06b` act: no Explore all on a whole Continue Watching row (Android `d1c36e7be`; tvOS
  keeps it on Next Up, Android and Roku drop it there too).
- `55da563c` act: the web's sort list with the ratings capability, scrolling Sort panel
  (Android `a60dac2d0`). The endpoint is not in `scripts/generate-api.sh`.
- `fe7aa020`, `4be5334f` act: web overlay layout from `user_state.watched_count` /
  `unplayed_count`, Plate and Edge band presets, one overlay scale clamped 1.0–1.35 (Android
  `2d3620d50`).
- `1531620d` act: keyline progress bar, 4 pt on TV (Android `f65fe6917`).
- `3abeaf4f` act: profile row by profile count and the new menu order (Android `8b9b3f017`).
- `8d9b2395` act: Change Account wherever a profile choice is required with two or more
  accounts (Android `ce24a6b5c`).
- `7bb8fe3d` act: Back on the picker opened by Switch Profile reopens the same profile without
  its PIN on the same page; the launch picker still exits (Android `6091fb9e6`, unmerged).
  Bale's `Session.ShowProfilePicker` clears the profile and back stack, and the parity backlog
  ticks "Back from Switch Profile exits". Hold until Android merges.
- `d0e87752` act: two-line profile names (Android `34cef244f`).
- `710169c9` act: grouped library, collection and person counts (Android `b2b0fb565`).
- `a82ff603` act: Calendar DIGITAL/PHYSICAL badges (Android `64ae5f08c`, already open).
- check: `9718f17b` series cards show "2019 – 2023" / "2021 – Present" (no Android match; Bale
  shows one year); `b5c73e3a` profile list read without `X-Profile-Id`, 404 on Home after the
  active profile is deleted returns to Who's watching; `2d7a4011` cancellable connect, Back
  reads Cancel; `417508d9`, `e3a38bae` a subtitle pick follows to the new file's matching track
  on a Version/Edition change (Bale and Android reset to Auto); `b9446d5d` the 45 s start budget
  covers source opening (confirm a mount that never opens reaches the single replan);
  `3d4d7d1f` stacked text cues (Android `3afd7006f`); `933381d6` top-bar menus open after
  120 ms, Bale uses 180/80 ms (Android `df3b26177`, unmerged); `dce56e3c` focus after removing a
  Continue Watching card.
- Already matched: `4b2470ca` end time on the play bar, `1ed93647` "Version" and "Audio"
  labels, `9df69eda` Sort opens on the current sort.
- not doing: `026ac351` box sets in Search.
- later: phase 5 `1fb21c50` still-watching prompt (`player.passout_threshold`, default 3),
  `fc86b201` Up Next pill closes the player, `08ff656f` an early-stopping stream is not marked
  watched and Keep Watching remounts where it stopped; phase 3 `e11c5aa8`, `feb2682f`,
  `9ff49dca`, `5569b192`, `0de9be23`, `034d173e` skip pills, marker bands, scrub end; phase 4
  `b4f69c11` badges follow the selected audio and replanned file, `d5098c2b` stats grouped as
  Playback / Stream / Engine; `93488d3e` unplaced text subtitles in Fill; `1d4b6248`
  translation polling; `9b8b3528` remote-control profile hand-over; `c85526df`, `8ba9c514`,
  `e0337595` audiobooks.

#### silo-server (`5159df6dcf52..92ee051878c2`, 347 commits, 318 none)

The v3 capability rules Bale relies on (platform-attested entries, `progressive_range_refusal_v1`,
`embedded_bitmap`, `client_managed_dynamic_range_v1`, `client_dv7_to_hdr10`,
`client_video_transformations_v1`, `seek_reanchor`) are unchanged: the planner was only split
into files (`1ba89ab3`, `19088564`, `2e142f0f`), and the public playback routes changed in
descriptions only. No act items.

- `91611b42` regenerate and check: profiles gain `requests_allowed`, `listProfiles` gains
  `requests_allowed_supported`; a blocked profile gets `allowed: false` and 403 from discovery.
  Bale already gates Requests on `Allowed`. later: the editor toggle.
- `2c757832`, `ceaa1bc4` regenerate and check: cards, detail and seasons gain `availability`
  and `authors` (the poster badge is web-only so far).
- `8aae0261` regenerate and check: `VideoTrack.frame_color_range`. Not used by Bale's tone
  mapping.
- `4a0aba61` regenerate and check: title keys on Discover rows and recipe presets (later).
- `7bb66f9a` check: `/api/v1` answers 410 `client_upgrade_required` except health/ready, which
  is Bale's only v1 call.
- `4ac66bd6`, `a4e2ed2a` check on the console: file paths and names go only to admins and
  curators; other viewers get external subtitles labelled by title or numbered language.
- `85d1c166` check: `versions` now come in `sortFileVersions` order; Bale keeps server order.
- `0b579aa6`, `d8fe8eea`, `a672677a`, `588d0497`, `29445555`, `47b52e84` check on the console:
  episodes above a profile's quality ceiling are hidden from season lists and Home rows; Next
  Up stays full after dismissals; smart collection rows keep their limit; Because You Watched
  is per library.
- `7115ee70`, `3864d627`, `e12193d6` check: embedded subtitle windows wait at most 10 s for the
  index; demuxes are capped at four, a refusal answers 503 with `Retry-After`. Bale's fetches
  retry 503.
- `8dae40e6` check: a paused session frees its stream slot after two minutes without progress.
  Bale reports every 10 s while paused.
- `aa827602` check: the web player reanchors a dropped progressive remux, as Bale already does.
- later: `playback.copy_keyframe_playlist` (experimental, off) serves copy-remux fMP4 HLS as one
  VOD playlist from Matroska Cues (`186c24a2`, `0387303d`, `ac54052c`, `6bd11d96`; check Bale's
  HLS seek windows if it is turned on); plan `st` authorizes media for Cast (`6e78f5f1`); a
  replan keeps a stored subtitle by ID (`5d57326b`); font bundles serve what fits 32 MiB
  (`8f2611d3`); single-field validation errors name the field (`d7d6d970`).

#### silo-roku (`4b9b36f64601..e09a5e0a3efe`, 22 commits, 11 none)

Roku is not a reference for Bale's look, but its docs record cross-client owner decisions.

- `7bb02a9` act: Edition selector, Version Auto stays within the edition (the cross-client
  rule; Android `466c31968`).
- `a41d106` act: Specials first as season zero, owner decision 2026-11-15 for every client
  (Android `69e39ad45`).
- `cfda7be` act: no Explore all on a Continue Watching / Next Up row that shows its whole list,
  owner request 2026-11-13 for every client (Android `d1c36e7be`).
- `431c1f1` act: Switch Profile / Add Profile by profile count, saved with the profile list
  (Android `8b9b3f017`).
- `4a22785` act: Change Account at launch with several accounts (Android `ce24a6b5c`).
- Already matched or covered: `3e0a7d6` Version / Audio labels, `b5fe42b` Show Series Info
  (Android `3632eda4e`), `fdcd5db` inset progress capsule (Android `f65fe6917`),
  `d4303e5` web sort list (Android `a60dac2d0`), `ed824d3` grouped counts (Android `b2b0fb565`).
- later: `f5078a0` an early end (more than 8 s and 1.5% short of `source.duration_seconds`)
  resumes where the stream stopped instead of replaying the end (phase 5, as Apple
  `08ff656f`); `53aaa78` Up Next Back pill leaves the player (Apple `fc86b201`); `42249ca`
  marker bands in the shared palette (phase 3).
- Idea for constrained devices: `fdcd5db` draws the capsule from 9-patch art at two
  resolutions instead of per-card shapes.

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
