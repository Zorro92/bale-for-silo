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
- [ ] Season/episode load errors keep the page; skeleton while loading, "No episodes available" when empty
- [ ] Page background: solid sampled tint × 0.42, default (0.04, 0.12, 0.14)
- [ ] Text title: 36 Inter Tight Black, uppercase, black@0.55 shadow, "A: B" split onto a 20sp line
- [x] Synopsis focusable, 13sp white@0.82, opens full text + tagline dialog
- [ ] Cast: 12/12 text, 2-line name, white@0.88 → white on focus, cap 24, header gap 14, placeholder icon 40
- [ ] Section header 18.1; Play icon 18; round action icons 19; episodes→Cast gap 32
- [ ] Episode cards: watched still dimmed black@0.35; focus = 3px white@0.9 inner border, scale 1.04
- [ ] Starring sorted by billing order; runtime ≥ 1h as "1h 5m"
- [x] More menu: titled "More Actions" with icons; Favorites, type-worded Watched, Mark Season Watched, Go to Season / Go to Series
- [ ] Version / Audio / Subtitles readout line and round selector menus in the action row (Up from season tabs lands on Version)

### Profiles and sign-in (`screens/profiles/*`, `screens/auth/*`, `navigation/TvAppNavigation.kt`)
- [x] Profile tiles: round 110 avatar in 128 column, 14 gap, 6 columns, focus scale 1.12 + 3px white ring 6 outside, name 12.9 SemiBold secondary → white
- [x] Header "Who's watching?" 27.5 ExtraBold + "Signed in as <user>"
- [ ] Lock badge bottom-right 30 #1C1C1E circle; "KIDS" pill
- [ ] PIN: full-screen avatar + 4 dots + round keypad, auto-submit at 4, "Checking…"
- [ ] Picker error screen with Retry; Sign out / Change server buttons
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
- [ ] Error: "Search is unavailable" + message + Try again
- [ ] Empty state sizes: title 18.9, body 15.5 @62%, icon 28 @34%
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
- [ ] "Explore all" end card; "See all" header
- [ ] Library: "Recommended" pill; Browse grid (6 cols, sort, filter, A–Z, paging); Collections; cascade level 2 + footer hint; "MOVIE LIBRARIES"
- [ ] Calendar
- [ ] Profile menu: Watchlist, Favorites, History, Settings, Switch Server; opens on dwell/Down
- [ ] For You Watchlist/Favorites dropdown; offline pill; drop key repeats while a rail animates
- [x] 500 ms fade page transitions

## Wave 3: new screens
- [ ] Watchlist / Favorites / History grids
- [ ] Settings (incl. Home sections editor)
- [ ] Requests tab + request detail
- [ ] Notifications inbox
- [ ] Multi-server list, password sign-in, server discovery, HTTP warning
- [ ] Profile create / edit / manage
