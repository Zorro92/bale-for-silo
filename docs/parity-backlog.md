# Parity backlog

Differences from the official Android TV app, found by reading its source next to Bale's
(2026-10-30). Official paths are relative to
`silo-android/androidTvApp/src/androidMain/kotlin/org/siloserver/silo/tv/ui/`. Tick items off as
they land and are verified on the console. Anything that needs the video player (playback itself,
Watch Party, Shuffle, theme music, audiobook player, Cast standby) waits for the player.

## Wave 1: existing screens

### Detail (`screens/detail/TvItemDetailScreen.kt`, `TvDetailHero.kt`, `TvDetailEpisodeRail.kt`)
- [x] Series Play/Resume and Start Over follow the focused episode (target, label, resume point); series resume comes from next-up, not the series
- [x] Episode and season items open the series page with that season/episode selected
- [x] Season/episode load errors keep the page; skeleton while loading, "No episodes available" when empty (season switches verified; skeleton, empty and failure states load too fast / need the server down to see)
- [x] Page background: solid sampled tint × 0.42, default (0.04, 0.12, 0.14) (already in place; normalization fixed to match AmbientBackdropTint.kt. Seen on The Lion King and dark-art titles)
- [x] Text title: 36 Inter Tight Black, uppercase, black@0.55 shadow, "A: B" split onto a 20sp line (verified on Coyote (2017), 0.1.140; matches TvDetailHero.kt heroDisplayHero)
- [x] Synopsis focusable, 13sp white@0.82, opens full text + tagline dialog
- [x] Cast: 12/12 text, 2-line name, white@0.88 → white on focus, cap 24, header gap 14, placeholder icon 40 (already in place per TvCastCrewSection.kt; verified on Coyote (2017))
- [x] Section header 18.1; Play icon 18; round action icons 19; episodes→Cast gap 32 (already in place per TvDetailSectionHeader.kt / TvItemDetailScreen.kt TvDetailSectionGap; verified on Coyote and The Lion Guard)
- [x] Episode cards: watched still dimmed black@0.35; focus = 3px white@0.9 inner border, scale 1.04 (already in place per TvDetailEpisodeRail.kt; focus verified on The Lion Guard, watched dim not seen on screen)
- [x] Starring sorted by billing order; runtime ≥ 1h as "1h 5m" (already in place per seriesStarringCredit / TvDetailMetadata.kt; seen on The Lion Guard and Coyote)
- [x] More menu: titled "More Actions" with icons; Favorites, type-worded Watched, Mark Season Watched, Go to Season / Go to Series
- [x] Version / Audio / Subtitles readout line and round selector menus in the action row (Up from season tabs lands on Version) (TvPlaybackSelectorRow.kt / TvAnchoredSelectorMenu.kt; menus compared side by side with the APK on The Lion King, Up from Season 2 verified on The Lion Guard)
  - [x] Selector list top/bottom alpha edge fades (26dp, TvAnchoredSelectorMenu.kt; painted in the panel fill, shown with each chevron; verified on The Lion King subtitles, 0.1.160)
  - [ ] Edition selector and edition-scoped Version options (PlaybackEditions.kt / TvPlaybackSelectorRow.kt; console 0.1.164: single-edition The Lion King hides Edition, Version stays closed with one file as the official selectorIsInteractive does; a multi-edition title still to find, bale-cli 422 blocks a library scan)
  - [ ] AUDIO readout resolves the device track ("Auto · Spanish · AC3 · 5.1" on the APK); Bale has no audio-capability resolver yet

### Profiles and sign-in (`screens/profiles/*`, `screens/auth/*`, `navigation/TvAppNavigation.kt`)
- [x] Profile tiles: round 110 avatar in 128 column, 14 gap, 6 columns, focus scale 1.12 + 3px white ring 6 outside, name 12.9 SemiBold secondary → white
- [x] Header "Who's watching?" 27.5 ExtraBold + "Signed in as <user>"
- [x] Lock badge bottom-right 30 #1C1C1E circle; "KIDS" pill (per MarqueeProfileAvatar.kt, scaled by 110/92; verified on the console with a test PIN and a kids profile)
- [x] PIN: full-screen avatar + 4 dots + round keypad, auto-submit at 4, "Checking…" (already in place per TvPinEntryDialog.kt; verified on a test PIN: wrong PIN → red dots + "Wrong PIN. Try again.", right PIN → "Checking…" → Home)
- [x] Picker error screen with Retry; Sign out / Change server buttons (StatusViews.kt TvErrorScreen, TvMarqueeButton Glass, TvSignOutDialog.kt; error via `bale-debug.sh fault profiles` → Retry reloads; sign-out dialog opened and cancelled on the console)
  - [ ] Picker backdrop: the APK draws the colored Aurora light, Bale a dark gradient (official picker draws MarqueeBackdrop stage 2 + Ambient scrim, not TvAuroraBackdrop; static phase-zero pose ported and seen on the console, 0.1.162; side-by-side with the official picker waits for the emulator re-sign-in)
  - [x] Avatar fill for profiles without an image (APK: Kids is salmon, Bale slate) (MarqueeProfileAvatar.kt ProfileTilePalette + radial shading; verified on the console, 0.1.162: Kids is salmon)
- [x] Back from Switch Profile exits (picker is the root)
- [ ] Login: two columns, "Sign in to <server>", QR + code card + status ("Continue on your phone"), actions row; declined stays with retry; Back on the code screen exits (layout, copy, QR/code card and serif-initial mark verified on the console, 0.1.155; decline → retry and Back-exits need a real signed-out session, still to check with the owner)
- [x] Server entry copy: "Enter your server address", Connect / Back (source-matched field centering, select-to-open keyboard, Language icon and TV-name chip landed) (verified on the console, 0.1.157: Down→Connect, Right→Back, Up→field, rounded corners when focused)
  - [ ] Advanced options: Auto / HTTPS / HTTP and port with matching URL-candidate probing (deliberately absent rather than exposing a partial control)
- [ ] Session expiry → Login with "You were signed out of <server>. Sign in again."; all pages, not only Home (single root-owned transition after rejected refresh; Core coverage passes; notice verified via show login expired, 0.1.155; a real revoked session still to check with the owner)
- [x] Startup with saved tokens goes straight to Main (no network gate) (MainTvActivity.kt resolveStartDestination; verified: 0.1.155 launched straight to the picker; offline/expired covered in Core tests)
- [x] Startup profile rule: ask Every Time by default; offer "Open X automatically?" after first pick

### Search (`screens/search/TvSearchScreen.kt`, `TvSearchViewModel.kt`)
- [x] Field: leading 20 search icon @72%, radius 14, 17.2 text, 5.5% white fill / 12% border, focused ElevatedSurface / 94% border; placeholder from media types
- [x] Any non-blank query after 300 ms
- [x] Paging 40 at a time; status line 13.8 with Searching… / No results / N+ / About N
- [x] Error: "Search is unavailable" + message + Try again (TvSearchScreen.kt; verified on the console with fault search, 0.1.155: copy, Down field → chip → Try again, retry → no-match state)
- [x] Empty state sizes: title 18.9, body 15.5 @62%, icon 28 @34% (verified on the console, 0.1.155: blank, unavailable and no-match states)
- [x] People row; dynamic chips (Audiobooks)

### Shell and Home (`shell/TvTopMenuBar.kt`, `shell/TvMainShell.kt`, `components/TvMediaRow.kt`, `components/TvSkylineSectionFeed.kt`)
- [x] Avatar focus: keep 18% white fill, ring 1dp white 18% → 2dp OnSurface
- [x] Tabs Medium at rest, SemiBold selected/focused; selected border 1; resting alpha 0.62; search glyph 19 (TvTopMenuBar.kt; verified on the console, 0.1.157: dim resting tabs, bordered selected Home, white focused Movies, inverted search circle)
- [x] Panel dwell 180 ms first, 80 ms switching (TvTopMenuBar.kt TopMenuInitialPreviewDelayMillis/PanelSwitchDelayMillis; panel opened after the dwell on the console, 0.1.157)
- [x] Profile panel: no scrim, traps focus; header uppercase "subtitle · server" 11.2 @0.38; icons 16; divider 1 @10%
- [x] Row header→rail 12, progress icon 22, row gap / card padding per TvSkylineSectionFeed (TvSkylineSectionFeed.kt; checked on the console, 0.1.157)
- [x] Ambient tint animates, art crossfades
- [x] Empty state "Nothing to watch yet" + Refresh; separate loading/error (StatusViews.kt; verified with fault home-empty / fault home on the console, 0.1.157: Refresh / Retry focused, Retry reloads 11 rows)
- [x] Continue Watching opens the series with season/episode selected
- [x] Home refreshes when returned to (verified on the console, 0.1.157: back from Lupin keeps the card and offsets, log shows a quiet refresh)

## Wave 2: missing pieces of existing screens
- [x] Detail: Trailers & More, Recommended/Related, Details facts table
- [x] Cast clickable → Person page
- [x] Long-press / Menu card actions (Play, watched, favorite, watchlist, remove from Home); episode card menu
- [x] "Explore all" end card; "See all" header
- [x] Library: "Recommended" pill; Browse grid (6 cols, sort, filter, A–Z, paging); cascade level 2 + footer hint; "MOVIE LIBRARIES"
  - [x] Collections pill (only when the library has collections), grouped grid, collection page with Collection Order
  - [ ] Filtered-error "Clear filters" (built; not seen on the console, needs a failing filtered load)
  - [ ] Shuffle (waits for the player); remote audiobook facet search (no audiobook library to test)
  - [x] Polish: toolbar icons/badges, alphabet selected/All treatment, 180 ms rail expansion; Recommended scope refill; Filter keeps focus across filter reloads
- [x] Calendar (focus follows the selected pill/day, first card, per-row memory: a deliberate deviation)
- [x] Profile menu: Watchlist, Favorites, History, Settings, Switch Server; opens on dwell/Down
  - [x] Settings row (verified on the console, 0.1.166: Down from History, Enter opens Settings): full-screen Settings, returning to Home; Switch Server now uses the shared sign-out confirmation
  - [ ] Watch Party row (deferred until the player); unsupported Settings controls remain deferred as listed in wave 3
  - [x] Dropdown drop shadows: one shared panel chrome (TvSkylinePanelChrome.kt) on the cascade, flyout, For You, profile, Sort and Filter panels
- [x] For You Watchlist/Favorites dropdown; offline pill; drop key repeats while a rail animates
  - [x] Dropdown, Recommendations, offline pill and rail repeat gate (pill not seen on console: needs the server down)
  - [x] Watchlist/Favorites inline in the For You tab (TvRecommendationsScreen.kt), not a pushed screen; empty feed falls back to Watchlist
- [x] 500 ms fade page transitions

## Wave 3: new screens
- [x] Watchlist / Favorites / History grids (landed with the wave 2 profile menu)
- [ ] Settings (incl. Home sections editor) (first milestone verified on the console, 0.1.166; the deferred panes below keep it open)
  - Matched `TvSettingsScreen.kt`, `TvHomeSectionsEditor.kt`, `TvHomeSectionPreferences.kt`, `TvMainShell.kt`, and `TvHomeScreen.kt`: black full-screen category rail/detail pane, focus-selected General/Server, detail → category → Home Back chain, profile identity/switching, server information/version, and shared confirmed Sign Out/Switch Server
  - General offers only the honored launch choices (Automatic / Every Time) and a populated Home rows editor: immediate device-local visibility/order saves scoped to server/profile, same-row move focus, and no sideways Up/Down fallback. Hidden Home offers Open Settings; preferences reapply before Home focus returns, including offline cached rows
  - Deferred: Playback/audio/subtitles (quality/bitrate, autoplay/skip, appearance), connection speed test/learned bandwidth, theme music, Diagnostics, timed-away profile policies (1h/12h) and warm-return profile prompting, unsupported card/poster/title-art/top-menu controls, full multi-server management, and Privacy Policy/QR surfaces. No nonfunctional placeholders are shown
  - Home preferences intentionally follow the TV device-local model; profile-wide server Home overrides are not used
  - Console 0.1.166: copy matches TvSettingsScreen.kt; rail/detail Up/Down/Left/Right and Back chain; Profile Selection picker opens on the saved choice and Escape cancels; hiding Continue Watching removes it from Home (10 rows) and showing it restores 11; Edit mode move Down/Up keeps focus on the same row, lands on the enabled arrow at the top, order restored. Not yet checked: all-hidden Open Settings, Sign Out/Switch Server dialogs (left for the owner)
  - Seen once, not reproduced in three tries: after hiding the focused row, focus landed on an off-screen card (Trending Today #11) after the quiet refresh, with the rail unscrolled
- [ ] Requests tab + request detail (implemented; Xbox verification pending)
  - Matched `TvMainShell.kt` / `RequestsFeatureStore.kt`: Requests is a profile-menu row immediately after History, not a root tab; it starts hidden and requires `requests_enabled`, `allowed`, and `state == available`. Capability and cached own requests reset with the acting account/profile
  - Matched `TvRequestsPage.kt`, `TvRequestComponents.kt`, `TvRequestPresentation.kt`, and `TvRequestDetailScreen.kt`: own-request groups then ordered discovery shelves, dense 88×132 posters, delayed preview, status colors/four-stage track, loading/empty/error/Retry states, Close-first long-press cancellation, full-screen movie/series detail, stable Request/status/Open in Library and Follow pills, synopsis, and More like this
  - Focus uses shared rails, explicit vertical boundaries, first-card menu entry, remembered hub cards, and per-navigation detail/recommendation focus and scroll restoration. Back closes an overlay, pops a detail entry, then returns the hub to its previous root. All Xbox rendering, Native AOT compilation and keyboard/focus checks remain unverified
  - Viewer writes require explicit Request/Request Again, Cancel Request, or notification-toggle activation. Create/cancel disable transport retries; uncertain outcomes are held rather than automatically resent. Approve/decline/administrative retry/limits remain web-admin-only, matching the official TV app
  - Deferred: global Search's Available to request results/entry (the endpoint is generated but not wired to Search; the matched empty-state Search hint therefore does not yet lead to requestable results), audiobook request detail/create beyond the pinned movie/series contract (library-backed cards still open the library; other audiobook cards show an explanatory notice), and card-density preferences beyond the default dense layout
  - Development-only read faults: `fault requests`, `fault requests-empty`, and `fault request-detail` each affect one read and never mutate Silo records. Local Core/declarative tests, rail checks and XML/resource validation pass
  - 0.1.171 console: Windows build passes; the capability probe logs `requests_enabled=false, allowed=false, state=disabled` for this server, and the Requests row is correctly hidden (Down from History reaches Settings). Hub/detail rendering and focus checks wait for the owner to enable Requests on the server
- [ ] Notifications inbox (not reachable in the official TV app; parked for the owner's call)
  - silo-android main 65664d7: `TvInboxScreen.kt` / `TvInboxFormatters.kt` exist, but `TvMainShell.kt`, `TvRoute.kt` and `TvAppNavigation.kt` have no inbox route, menu row or gate, and `TvTopMenuBar.kt` draws no unread badge (only comments mention one). Matching the official app means Bale adds no inbox for now
  - If the official app wires it up later, the screen is a flat newest-first list with Mark all read first when unread > 0; activating an item marks it read and opens series → episode → library target; opening the inbox does not mark anything read; no dismiss/clear-all
- [ ] Multi-server list, password sign-in, server discovery, HTTP warning
- [ ] Profile create / edit / manage
