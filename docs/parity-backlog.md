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
  - [ ] Selector list top/bottom alpha edge fades (26dp; Bale shows only the chevrons)
  - [ ] Edition selector and edition-scoped Version options
  - [ ] AUDIO readout resolves the device track ("Auto · Spanish · AC3 · 5.1" on the APK); Bale has no audio-capability resolver yet

### Profiles and sign-in (`screens/profiles/*`, `screens/auth/*`, `navigation/TvAppNavigation.kt`)
- [x] Profile tiles: round 110 avatar in 128 column, 14 gap, 6 columns, focus scale 1.12 + 3px white ring 6 outside, name 12.9 SemiBold secondary → white
- [x] Header "Who's watching?" 27.5 ExtraBold + "Signed in as <user>"
- [x] Lock badge bottom-right 30 #1C1C1E circle; "KIDS" pill (per MarqueeProfileAvatar.kt, scaled by 110/92; verified on the console with a test PIN and a kids profile)
- [x] PIN: full-screen avatar + 4 dots + round keypad, auto-submit at 4, "Checking…" (already in place per TvPinEntryDialog.kt; verified on a test PIN: wrong PIN → red dots + "Wrong PIN. Try again.", right PIN → "Checking…" → Home)
- [x] Picker error screen with Retry; Sign out / Change server buttons (StatusViews.kt TvErrorScreen, TvMarqueeButton Glass, TvSignOutDialog.kt; error via `bale-debug.sh fault profiles` → Retry reloads; sign-out dialog opened and cancelled on the console)
  - [ ] Picker backdrop: the APK draws the colored Aurora light, Bale a dark gradient
  - [ ] Avatar fill for profiles without an image (APK: Kids is salmon, Bale slate)
- [x] Back from Switch Profile exits (picker is the root)
- [ ] Login: two columns, "Sign in to <server>", QR + code card + status ("Continue on your phone"), actions row; declined stays with retry; Back on the code screen exits
- [ ] Server entry copy: "Enter your server address", Connect / Back
- [ ] Session expiry → Login with "You were signed out of <server>. Sign in again."; all pages, not only Home
- [ ] Startup with saved tokens goes straight to Main (no network gate)
- [x] Startup profile rule: ask Every Time by default; offer "Open X automatically?" after first pick

### Search (`screens/search/TvSearchScreen.kt`, `TvSearchViewModel.kt`)
- [x] Field: leading 20 search icon @72%, radius 14, 17.2 text, 5.5% white fill / 12% border, focused ElevatedSurface / 94% border; placeholder from media types
- [x] Any non-blank query after 300 ms
- [x] Paging 40 at a time; status line 13.8 with Searching… / No results / N+ / About N
- [x] Error: "Search is unavailable" + message + Try again (source-matched; Xbox verification pending)
- [x] Empty state sizes: title 18.9, body 15.5 @62%, icon 28 @34% (source-matched; Xbox verification pending)
- [x] People row; dynamic chips (Audiobooks)

### Shell and Home (`shell/TvTopMenuBar.kt`, `shell/TvMainShell.kt`, `components/TvMediaRow.kt`, `components/TvSkylineSectionFeed.kt`)
- [x] Avatar focus: keep 18% white fill, ring 1dp white 18% → 2dp OnSurface
- [ ] Tabs Medium at rest, SemiBold selected/focused; selected border 1; resting alpha 0.62; search glyph 19
- [ ] Panel dwell 180 ms first, 80 ms switching
- [x] Profile panel: no scrim, traps focus; header uppercase "subtitle · server" 11.2 @0.38; icons 16; divider 1 @10%
- [ ] Row header→rail 12, progress icon 22, row gap / card padding per TvSkylineSectionFeed
- [x] Ambient tint animates, art crossfades
- [ ] Empty state "Nothing to watch yet" + Refresh; separate loading/error
- [x] Continue Watching opens the series with season/episode selected
- [ ] Home refreshes when returned to

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
  - [ ] Settings row (Bale has no Settings page yet); Watch Party row
  - [x] Dropdown drop shadows: one shared panel chrome (TvSkylinePanelChrome.kt) on the cascade, flyout, For You, profile, Sort and Filter panels
- [x] For You Watchlist/Favorites dropdown; offline pill; drop key repeats while a rail animates
  - [x] Dropdown, Recommendations, offline pill and rail repeat gate (pill not seen on console: needs the server down)
  - [x] Watchlist/Favorites inline in the For You tab (TvRecommendationsScreen.kt), not a pushed screen; empty feed falls back to Watchlist
- [x] 500 ms fade page transitions

## Wave 3: new screens
- [x] Watchlist / Favorites / History grids (landed with the wave 2 profile menu)
- [ ] Settings (incl. Home sections editor)
- [ ] Requests tab + request detail
- [ ] Notifications inbox
- [ ] Multi-server list, password sign-in, server discovery, HTTP warning
- [ ] Profile create / edit / manage
