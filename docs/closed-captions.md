# In-band closed captions (CEA-608)

Status (2026-11-26): **wired into the player** (`fe2dc00`, on wrapper bale.84 with the fork's caption tap, pinned in
`e9fc6ba`); console check pending. The backlog items are "In-band closed captions (CEA-608)" and "In-band CEA-708" in
docs/parity-backlog.md. CEA-708 is not decoded yet.

CEA-608 ("line 21") captions reach digital video as ATSC A/53 `cc_data`: in H.264 and HEVC SEI
(`user_data_registered_itu_t_t35`, "GA94", type 3; SCTE 128 for cable) and in MPEG-2 picture user data.
Broadcast recordings, US TV episodes and many WEB-DLs carry them without a separate subtitle track.

## What the official clients do

- **Android TV** (Media3): `SeiReader`/`UserDataReader` find the `cc_data`, `ReorderingBufferQueue` puts it in
  presentation order (sized from the SPS), `Cea608Decoder` decodes it into cues with a row-based line and a
  column-based position. `SubtitleManager.kt` `applyAppearance` draws them in the subtitle appearance and sets
  `setApplyEmbeddedStyles(false)`, so 608 colours, italics and underline are dropped; the Position preset does
  not move a cue with an explicit line (Media3 1.11 `SubtitlePainter`). The track is one the player found itself, never a
  server row (`TvSubtitleIdentity.kt`: `SubtitleIdentity.LocalMedia3`), listed after the server's rows and picked locally
  ("In the player" below).
- **Apple TV** (AetherEngine, read for behaviour only): CC1 only, plain text (`CEA608Decoder.swift`), reordered by
  a DTS watermark (`A53ReorderBuffer.swift`), a synthetic "Closed Captions" track that appears with the first real
  caption data (`AetherEngine+ClosedCaptions.swift`), open captions given a 12 s provisional end.
- **Bale**: CC1 by default and CC2-CC4 selectable; rows and columns on the 608 grid as on Android TV; the
  subtitle appearance for font, size, colours and box; 608 colours, italics and underline kept as inline
  overrides (the brief, and what FFmpeg, mpv and VLC do), with a plain flag for Android TV's look
  (`FLAG_PLAIN`, `SubtitleOverlay.AttachCaptions(..., plain: true)`). Deliberate deviation (owner, 2026-11-25):
  the default stays styled. Captions keep their own colours, italics and underline, where Android TV and AetherEngine
  draw plain text; the player never passes `FLAG_PLAIN`. In the player: one CC row on CC1 once captions are found, picked on
  the client and kept across remounts ("In the player").

## How it runs

```
fork tap (sample or GPU feeder thread, decode order)
  bale_cc_push(token, packet, pts_100ns, codec, nal_length_size)      bale_cc_flush(token): seek, switch, close
    → registry: token → Captions (bale_cc_register)
      → extract: cc_data triplets (SEI or MPEG-2 user data)
      → reorder: presentation order by PTS
      → cea608: the selected channel's memories
      → cue: what is displayed, from when to when (kept 30 s)
      → ass: events on the 608 grid in the appearance (only while attached)
        → overlay's live track (LiveSink: queued, render thread adds them to libass)
```

All of it is `native/bale-gpu/src/subs/cc` (platform independent) plus the overlay's live track
(`subs/overlay.rs`, `ass.rs`, Windows). C#: `Services/ClosedCaptions.cs` (the decoder) and
`SubtitleOverlay.AttachCaptions` (the attach, which takes the subtitle appearance).

### The fork's tap (caption ABI 1)

From the fork's `bale/README.md`, "In-band caption packet tap": the app passes FFmpeg option
`bale_cc_sink=<token>` (decimal, 1..999999999) per media source; a remount is a new source with a new token.

| Export | Called by | Meaning |
|---|---|---|
| `uint32_t bale_cc_abi_version(void)` | fork, app | 1 |
| `int32_t bale_cc_push(uint64_t token, const uint8_t *data, size_t len, int64_t pts_100ns, uint32_t codec, uint32_t nal_length_size)` | fork | one packet of the playing video stream as a provider takes it for decoding (a buffered seek hands kept packets again); `codec` 1 H.264, 2 HEVC, 3 MPEG-2; `nal_length_size` 1, 2 or 4 from avcC/hvcC, 0 for Annex B and MPEG-2; `pts_100ns` on the `MediaStreamSample` timeline, `INT64_MIN` without one; `data` valid during the call only |
| `int32_t bale_cc_flush(uint64_t token)` | fork | every flush of the stream: seek, stream switch, close |

Both return 0, `E_NOT_FOUND` (0x80070490) when no decoder holds the token, `E_INVALIDARG` for an unknown codec
or size. They never block and never call back into the player.

### The app's exports

| Export | |
|---|---|
| `bale_cc_create(const Captions **out)` | a decoder on CC1, no token, no overlay |
| `bale_cc_register(captions, uint64_t token)` | take the tap's packets for `token` as a new stream (0 takes none; a token moves from another decoder) |
| `bale_cc_select(captions, uint32_t channel, uint32_t service)` | CC1..CC4; `service` is the CEA-708 service number, 0 for none (`E_NOTIMPL` for 1..63 for now) |
| `uint32_t bale_cc_seen(captions)` | bits 0-3 CC1..CC4 carried caption commands, bit 8 any `cc_data`, bit 9 CEA-708 data |
| `bale_cc_stats(captions, BaleCcStats *out)` | packets, packets with `cc_data`, pairs, cues, late packets, reorder depth, flushes, seen (48 bytes) |
| `bale_cc_attach(captions, overlay, const TextStyle *style, const uint8_t *font_name, size_t len, uint32_t flags)` | draw on the overlay from now on, in the style `bale_sub_overlay_load_text_styled` takes; flags 1 = plain |
| `bale_cc_detach(captions)` | stop drawing, drop the overlay's caption track if it is still this one |
| `bale_cc_destroy(captions)` | unregister, detach, release (a push already running finishes on its own reference) |

`bale_cc_abi_version` covers all of them; Bale only ever adds exports under 1, since the fork requires exactly 1.
CI's packaged-DLL check lists them all.

## The pieces

**extract.rs.** H.264 SEI (type 6) and HEVC prefix and suffix SEI (39, 40), in Annex B or with 1-4 byte length
prefixes, emulation prevention removed; payload type 4 with country 0xB5 and ATSC's provider 0x0031, "GA94",
type 3, or DirecTV's 0x002F (Media3 `CeaUtil`); MPEG-2 user data (00 00 01 B2) "GA94" type 3 before the first
slice. `cc_data()` with `process_cc_data_flag`; a count larger than the data takes what is there. The Annex B start
code scan steps three bytes where it can, so slice data is skimmed. Nothing panics on malformed input.

**reorder.rs.** The tap does not pass the SPS, so the depth is learnt: frames are held sorted by PTS (one entry
per PTS, field pairs in arrival order) and the earliest leaves when more than the depth (4 at first) are held, or
at once when it is the next frame after the last one released (within a quarter step of the smallest recent PTS
step, so nothing can still come in between). Streams without B-frames therefore flow with no delay after the
first five packets, B-frame streams as soon as the frame they wait for arrives. A packet whose PTS was already
passed goes out at once (Media3) and grows the depth by the frames that overtook it, up to 16. A packet without a
PTS joins the frame before it (a second field). A flush drops everything held and the step estimate.

**cea608.rs.** The decoder of one channel, after the rules of the former 47 CFR 15.119 (CEA-608-E has the same):
two 15×32 memories, a cursor and a pen.
- Pop-on (RCL into non-displayed memory, EOC flips without erasing, EOC alone forces pop-on), roll-up (RU2/RU3/
  RU4 on a base row, CR scrolls, a PAC moves the window intact, a smaller window erases the rows it drops, a
  roll-up command erases a pop-on or paint-on caption), paint-on (RDC writes into displayed memory).
- PACs (15 rows, indents 0-28, colours, italics, underline; 10 60-7F is unassigned), mid-row codes (colour turns
  italics off, italics keeps the colour, a space in the old style), Tab Offsets (non-destructive), Backspace, DER,
  EDM, ENM, Flash On (a spacing attribute, drawn steady), background attributes and black text (non-spacing, as
  Media3, FFmpeg and AetherEngine treat them), the special and extended sets (an extended character replaces the
  standard one sent before it), the basic set's 608 replacements; column 32 is overwritten past the end.
- Media3's handling of the byte stream: odd parity and `cc_valid` (a channel turning invalid drops what is being
  built), the repeated copy of a control code dropped (a third copy acts), the data channel bit, Text mode (TR,
  RTD) until a caption command, XDS packets in field 2 (a caption control code ends one); field 2 accepts 15/1D for
  its miscellaneous codes.
- Annex C.9: a roll-up or paint-on caption without data for 16 s is erased.

**cue.rs and mod.rs.** After each frame, a change of displayed memory ends the shown cue and starts the next at the
frame's PTS (ms, rounded). Cues carry rows (row, first and last column, styled runs). The decoder keeps ended cues
for 30 s (at most 1024) and the shown one: an overlay attached mid-playback gets those first, since the tap runs
ahead of the clock. While attached, each cue becomes ASS events added with a 30 s provisional end that is extended
while the caption stays and set when it ends, so a stream that stops cannot leave a caption up.

**ass.rs.** The live track's header is the SubRip/WebVTT converters' (`ConvertOptions::styled`, the appearance on a
1920×1080 script). Rows sit on 608's safe caption area (80% of the picture, centred: column `c` at
`0.1 + 0.8 × c / 32` of the width, row `r` ending at `0.1 + 0.8 × r / 15` of the height, as FFmpeg's
`ccaption_dec` places them), each event anchored at the bottom of its last row (`\an1`-`\an3`, `\pos`).
Adjacent rows that line up become one event, so a box (border style 4) is one box behind them and libass stacks
the lines at the font's height. Pop-on captions are aligned by their padding as Media3 does (centred, right or
left; all rows of a caption take the first alignment any row needs); roll-up and paint-on start at their column.
Colours (white is the appearance's colour), italics and underline are inline overrides. With the box style, a row's
608 background other than opaque black colours its box (`\4c`/`\4a`, semi-transparent is half), and only then is
black text drawn black; otherwise black text takes the text colour, so it stays readable. The appearance's
position (bottom, lower third, top) does not apply, as on Android TV.

**The overlay's live track.** `Overlay::begin_live` replaces the track with an empty one (`ass_new_track` +
`ass_process_codec_private`, duplicate check off) and returns its generation and a `LiveSink`. The decoder hands
it `Add` / `SetEnd` / `Clear` commands; they are queued in the overlay's state (an end folds into a queued add, a
clear drops what is queued; past 4096 the queue restarts with a clear) and the render thread applies them:
`ass_process_chunk` (ReadOrder = the event id), the event's `Duration` set in place (libass reads every event's
times again each render), `ass_flush_events`, then `ass_prune_events` 60 s behind the clock. libass's own change
detection decides whether a frame is presented. Another load, a clear or another attach ends the generation; the
decoder then sees `E_NOT_FOUND` and detaches.

## Threads and limits

Checked against docs/resource-budget.md and docs/gpu-tone-map.md "Measured limits" (4 shared cores, the idle state
that slows the CPU about a quarter, about 1.2 GB of usable memory):
- **Tap thread:** a push takes the registry and decoder locks briefly and the overlay's state lock to queue (the
  render thread holds it only briefly too); it never waits for a render. Measured on this host in release: 0.4-0.7
  µs per packet length-prefixed, 2.5 µs for 9 KiB Annex B packets (the start code scan, about 3.5 GB/s), so a
  30 fps stream costs well under 0.1% of a core and a 25 Mbit/s 4K MPEG-TS stream about 0.1% (the console's
  cores are slower, and slower again when idle, which leaves it far below anything that matters).
- **Render thread:** one libass event per caption block; a roll-up caption changes every frame it types, which is
  one re-render per change (the converters' rendering cost, no animation).
- **Memory:** two 15×32 memories (about 12 KiB) per decoder, at most 1024 kept cues, the overlay's queue at most
  4096 commands, libass events pruned a minute after they end. Nothing on the GPU besides the overlay's own.
- **Player (UI thread):** one decoder per FFmpeg mount of a tapped codec, released with the mount, so at most two
  exist while a replaced mount closes; the 100 ms tick asks the mount's decoder for its seen bits (a lock and a read)
  about once a second until the attempt has found captions, never afterwards; an attach is one call that replays at
  most 30 s of kept cues. No FFmpegInteropX call and nothing per frame.

## Timeline

Cue times are `pts_100ns / 10000` ms on the tap's `MediaStreamSample` timeline (the fork applies the start offset
and stream delay). That is the player clock (MediaPlayer's or IMFMediaEngine's position on the FFmpeg source), and
the overlay's clock is the source position less `_subtitleTimingOrigin`, so the attach sets the origin to the mount's
timeline offset (`PlaybackMedia.Timeline.TimelineOffsetSeconds`; source = player + offset):
- `original_http`: 0, the source position itself (as for the fork's bitmap subtitles).
- Progressive remux: the plan's offset, the keyframe origin at player 0. Every seek is a reanchor: a new mount, a new
  decoder and a new origin.
- HLS through FFmpeg: the plan's offset plus the first segment's player time (`StartPlayerClockAt`, which runs before
  the captions attach), since FFmpeg's clock reads 0 there. A seek inside the server's window remounts at the target's
  segment: a new origin again.

A seek inside a mount (direct play): the fork calls `bale_cc_flush`, which drops what is held and shown and queues a
Clear for the overlay's live track, and the player pushes the clock when the seek lands. Captions come back with the
caption data from the keyframe the seek decodes from, so a pop-on caption loaded before that keyframe shows only from
the next caption (Media3 also reads captions from that keyframe on). A new mount clears the overlay (`ClearSubtitles`)
before its decoder is attached. The attach waits for `_subtitleLoads` (one overlay track change at a time), so a
sidecar load still parsing for an earlier plan cannot clear the captions' track after them; the decoder replays the
cues it kept, so a late attach loses nothing. All of this is still to check on the console ("Left").

## In the player

`PlayerPage.Captions.cs`, with the decisions in Bale.Core's `PlaybackClosedCaptions` (one per playback attempt: a new
attempt, Retry or the next episode, starts with CC off).

**Which mounts.** Every FFmpeg mount of an H.264, HEVC or MPEG-2 stream (`PlaybackClosedCaptions.Taps`, the route's
codec) gets its own `ClosedCaptions` and a fresh `bale_cc_sink` token (a process-wide counter over 1..999999999), made
as the source is configured, after the transport opened, and disposed with the mount (`DisposeMount`, on the UI
thread, before the native sources close on the pool, whose last pushes then find no decoder). A remount (seek
reanchor, HLS remount, recovery replan, quality change, the engine handing a stream to MediaPlayer) gets new ones.
FFmpeg routes (`route.rs`): every original (`original_http`; MP4 too since 2026-11-16), the progressive remux and
every HLS mount (the Rust HLS reader). Only debug `ffmpeg off` and a video codec FFmpeg cannot hand to an allowed
decoder stay on Windows' own source, which has no tap. VP9, AV1, VC-1 and MPEG-4 mounts get no decoder.

**What the server does with captions.** silo-server neither strips nor asks for them: nothing in
`internal/playback/transcode.go` or `remux.go` names a53 or closed captions (checked at silo-server `3f7985e7e`), and
`-sn -dn` drop only subtitle and data streams. A remux copies the video (`-c:v copy`), so the SEI and user data,
captions included, arrive untouched (the DV7 filter removes only NAL type 63). A transcode re-encodes: captions then
survive only if the server's FFmpeg carries the decoder's A53 side data through its filters to an encoder that writes
it (libx264's and NVENC's `a53cc` default to on; the hardware encoders differ), and a frame rate change would drop
or repeat caption pairs. Not checked on a server: the HLS console check below says what a transcode delivers.

**The CC row.** Once a mount's decoder reports CC1 captions (`bale_cc_seen` bit 0: caption commands, not padding,
CC2-CC4 or CEA-708 data; asked about once a second from the player's tick until found), the control bar's Subtitles
menu and the Options panel's Subtitles pane list a row "CC" after the catalog's tracks (`TrackIndex` −2, which nothing
sends to the server; it counts as a track, so a file with only captions shows the Subtitles tab). It stays for the
attempt, since a remount's decoder starts unseen. A menu or panel already open lists it the next time it opens.
- Android TV lists in-band CEA-608 as a track the player found itself (`TvSubtitleIdentity.kt`
  tvMountedSubtitleIdentity: `SubtitleIdentity.LocalMedia3`), after the server's rows (`TvSubtitleHudState.kt`
  buildTvSubtitleHudOptions). Media3 exposes it where its extractor reads caption SEI, which Silo's player sets up
  for MPEG-TS (progressive TS through `TsExtractor`'s default caption format, HLS TS through
  `exposeCea608WhenMissingDeclarations` in `SiloPlayerFactory.kt`), as one track that decodes CC1 unless the PMT
  declares caption services, which then give one track each with its language. The fork's tap carries no such
  declaration, so Bale offers one row, decoding CC1 (the test clips are all CC1); the tap reads every container FFmpeg
  demuxes.
- The label: Android's row for an undeclared track has no language and falls back to a numbered "Subtitle N"
  (`formatSubtitleTrackDisplayLabel`, from `extractTrackEntries` in `TvPlayerScreen.kt`, whose number runs one past
  the track's place), and to the language's name when one is declared. Bale calls the row "CC", Android's name for
  CEA-608 everywhere else (`TvSubtitleChoiceLabels.kt` subtitleFormatShortName, the HUD's CC chip); one constant,
  `PlaybackClosedCaptions.Title`. AetherEngine names its synthetic track "Closed Captions".

**Picking it** is the client's business, as Android commits a client-owned identity locally (`SubtitleTransition.kt`
select, `TvSubtitleTransactionAdapter.kt`): no track_change replan. If the plan plays a server subtitle (its
selected_tracks name one: a sidecar, a burn-in or a native track), the player's Off path runs first
(`ChangeTracksAsync(null, -1, remember: false)`), so the server stops its burn-in or sidecar, and the plan that Off
brings (adopted in place or remounted) draws CC; a refused or busy Off leaves the server subtitle and CC off. Android
leaves the server's subtitle as it is when a player track is picked; turning it off first is the brief's rule
(2026-11-26), so the picture never carries both. CC is drawn in the subtitle appearance (`SubtitleOverlay.AttachCaptions`
with the same `SubtitleStyle` as text subtitles), never plain.
- Off ends CC at once and then takes the ordinary Off path (saved as the viewer's choice, as before). A server
  subtitle ends CC once a plan plays it, so a change the server refuses leaves CC on, as Android rolls a failed pick
  back to the committed track.
- Nothing is remembered: no series or movie subtitle preference is written for CC, and the Off a CC pick sends first
  is not one either (Bale's rule for a track the playing version does not describe, `PlayerTrackPreferences.WriteFor`).
  Android TV's HUD does stage `SeriesSubtitleChoice.of` for a LocalMedia3 pick, which for an undeclared track is
  subtitle_language "", subtitle_track_index −1, subtitle_mode "always" and a signature of codec cea608 with its
  "Subtitle N" label (plus a per-item track fingerprint), which the next episode's Auto resolves to Off
  (`AutoSubtitleResolver.kt` resolveAutoSubtitle: an empty preferred language disables subtitles). Bale writes nothing
  (brief, 2026-11-26), so the next episode
  starts from the viewer's server subtitle preference.
- Auto never picks CC: Bale's starts come from Detail or Up Next, whose Auto the server resolves over the catalog's
  tracks, and in-band captions are never in the catalog. Android's only automatic pick of the Media3 track is a start
  that carried no decision (deep link, cast, remote start) of a file without a server subtitle inventory, with subtitle
  mode "always" and no preferred language (`TvPlayerViewModel.kt` resolveTvAutoSubtitleIdentity); Bale has no such
  start.
- A recovery replan, seek reanchor, HLS remount and quality change keep CC on: Android keeps a player-discovered
  CEA-608 track as keep-current, so a recovery keeps the viewer's subtitles (`TvPlayerViewModel.kt`
  selectedSubtitleTrackIndex). Bale's replans already ask for no subtitle after the Off (the plan's selected tracks
  and `PlaybackReplan.WithTracks`), and every mount draws its new decoder while CC is on, unless its plan plays a
  server subtitle (which ends CC, as a pick of it does).

**Debug.** `bale-debug.sh where` has a `Captions:` line: `offered`, `picked`, and the mount's decoder (`attached`,
`token`, `seen` channels, `packets`, `captioned`, `pairs`, `cues`, `late`, `depth`, `flushes`, the timing `origin`), or
`decoder=none (route=… codec=…)`. `bale-debug.sh captions` prints it; `captions cc` picks the CC row through the
menu's path (409 until captions were found) and `captions off` the Off row (which also turns a server subtitle off and,
on a Silo session, saves Off as the row does). The log says `player: closed captions found channel=cc1`, `player: closed
captions picked`, `player: subtitles mode=render format=cea608 channel=cc1` and `player: closed captions off`.

## Tests and the debug path

- `cargo test` in native/bale-gpu: `subs::cc` unit tests (SEI in Annex B and every length prefix, HEVC prefix and
  suffix, MPEG-2 user data, DirecTV, escaping, malformed input; reorder with B-frames, pyramids, late frames, flushes,
  PTS-less packets, 3:2 steps; every 608 mode and code; the ASS layout; the decoder end to end with a recording sink).
- `tests/cc_clips.rs`: the bale-subtitle-clips 10-15 (`BALE_SUBTITLE_CLIPS`, default `~/Videos/bale-subtitle-clips`)
  read with `ffprobe -show_data` and pushed as the tap pushes them, against the captions their generator wrote
  (text, rows, columns, colours, italics, underline, backgrounds, pop-on times within 40 ms, roll-up and paint-on
  samples). With `ffmpeg` the rich clip is re-encoded with B-frames as H.264 (MP4, TS), HEVC (MKV, TS) and MPEG-2
  (TS) and must match too. Skipped without the clips or `ffprobe` (CI). 2026-11-25: all 11 match, none late.
- `tests/cc_warp.rs` (Windows CI): the caption ABI on a WARP overlay: the caption drawn at its time, gone after its
  erase, before its start, after a flush and after a detach.
- `tests/Bale.Core.Tests/ClosedCaptionsTests.cs`: the C# imports against the Rust exports and CI's list, the
  counters' layout, the sink option; and the wrapper run against a Linux build of bale_gpu when one is in
  native/bale-gpu/target/release (skipped otherwise, as on CI).
- `tests/Bale.Core.Tests/PlaybackClosedCaptionsTests.cs`: the decisions (which mounts, when CC is offered, a pick with
  and without a server subtitle, Off and a server pick, a recovery and a seek reanchor keeping CC, Auto, the Options
  rows); `PlayerClosedCaptionsWiringTests.cs` pins the player's wiring by source (a decoder and token per mount and its
  disposal, the row in both menus, the client-only pick that remembers nothing, the in-place answer, the timing origin,
  the flush clearing the overlay, the debug readout and command).
- `cargo run --release --example cc_dump -- <clip> [cc1|cc2|cc3|cc4] [--ass]` prints a local clip's captions as Bale
  decodes them (or the ASS script the overlay would draw).

## Left

- **Console checks** of the wiring (pending), with the clips 10–15 (bale-subtitle-clips, all CC1, 30 s of 1080p30
  H.264; serve them with `scripts/serve-media.py` and play each with `bale-debug.sh play-url`):
  1. `where` shows the mount's decoder counting packets and, a second or two after the first caption, `seen=cc1` and
     `offered=yes`; the Subtitles menu (driven by keys once) lists Off and CC; `captions cc` picks it.
  2. The captions at their times against the clock burned into the picture (10–12: "608 1/6" at 1–5 s, two lines
     6–10 s, italics 11–15 s, yellow 16–20 s, the top row 21–25 s, the last 26–29 s), in the appearance's font, size
     and box; the rich clips' colours, italics, underline and backgrounds (13, 14) and the split clip (15). Screenshots.
  3. A seek on each container (`seek 17`, `key l`, `key j`): the old caption goes at once, the next caption loaded after
     the keyframe the seek decodes from shows at its time, `flushes` counts up; Off from the menu ends them
     (`picked=no attached=no`).
  4. A Silo title with captions: direct play, then the remux (`delivery progressive`) and HLS (`delivery hls`, a
     non-zero `origin`; whether a transcode keeps captions), with seeks; a server subtitle on, then CC (the subtitle
     goes, CC shows); `fault playback` (the recovery keeps CC); the next episode starts with CC off and the series'
     subtitle preference unchanged; `mem` around a few remounts with CC on.
- **CEA-708** (DTVCC): `bale_cc_select`'s service number and `SEEN_DTVCC` are there; the decoder is not.
- Not handled: 608 Text mode (T1-T4) and XDS content are skipped, flash is drawn steady, a background change
  inside a row (the box takes the row's first background), MPEG-2's other caption formats (SCTE 20, DVD) and
  QuickTime `c608` caption tracks (a subtitle stream, not video; AetherEngine reads them). In the player: a menu or
  panel already open when captions are found lists CC only when it next opens, and nothing re-attaches CC on an
  appearance change (each attach reads the current appearance, and Settings › Subtitles is not reachable while the
  player is open).
