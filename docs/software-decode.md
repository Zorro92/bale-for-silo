# Software decode fallback for H.264, HEVC and VP9

Status (2026-11-24): **phase A is in release** ([Phase A in release](#phase-a-in-release-2026-11-24)): H.264, HEVC and
VP9 the console's hardware decoder can't take decode in FFmpeg software inside the console-measured 1080p envelopes.
Phase B (4K, and HDR tone mapping of software frames) waits for the fork. Backlog item: [player-plan.md](player-plan.md),
"Later".

Answer first:

- **Bale over-claimed until 2026-11-24 (fixed, see "Now" below).** From 2026-11-17 the claims carried an unbounded
  software `video_decode` entry for every FFmpeg codec, H.264/HEVC/VP9 included, and the route sends what the
  console's hardware decoder can't take to software. But `FfmpegSource.Configuration` refused a software route for a
  hardware codec ("The video codec cannot use the local fallback"). So every such file (4K H.264, H.264 High 10 ...)
  was planned direct, failed its first open, and only the failure replan (which withdraws every software entry) had
  the server transcode it. HEVC RExt, VP9 profile 1/3 and 8-bit H.264 4:2:2/4:4:4 could fail outright: the
  profile-blind hardware entry still admitted them after the replan.
- **The official clients:** Apple TV 4K declares everything its engine decodes, unbounded, and lets the engine pick
  hardware or software per file; slower Apple devices claim small, fixture-measured software envelopes (H.264 High 10
  1080p30 10 Mbps, VP9 profile 0 1080p30 3 Mbps). Android claims hardware decoders only.
- **The console** ([measured](#measured-on-the-console-2026-11-24-bale-01767-series-x-1080p-sdr-tv), beating the
  host-scaled estimates by 20-50 %): every 1080p case took 0.8-2.1 cores with 0 missed frames (H.264 High 10 1080p24
  and 1080p60, H.264 4:2:2 10-bit 1080p30 50 Mbps, HEVC 4:2:2 1080p30, HEVC 12-bit 1080p24, VP9 4:4:4 1080p30); 4K30
  H.264 fits too (2.87 cores at 50 Mbps), only 4K60 fails.
- **Done:** the two production fixes (no unbounded software entries for the hardware codecs; a refused hardware-codec
  route withdraws that codec on the replan, so the server transcodes), then phase A: bounded 1080p entries (4:2:0
  H.264 High 10 and 12-bit HEVC up to 60 fps, 4:2:2/4:4:4 up to 30 fps) with a player-side envelope check. Left for
  phase B (fork): software frames through the staged D3D11 upload and Bale's stage, without which 4K software would
  hand Media Foundation 4K SDR to scale on a smaller display (the app crashes after ~23 such opens), and HDR on SDR
  TVs can't be tone-mapped.
- **Debug plumbing:** `bale-debug.sh ffmpeg-tune swfallback=on` decodes those routes in FFmpeg software also past the
  release envelope (4K, 4:2:2 at 60 fps); `threads=N` sets the software decoder's thread count; `decoder=software`
  forces FFmpeg software on files the hardware could take, to measure the software cost of 4K HEVC/VP9.

## Phase A in release (2026-11-24)

Approved by the owner after the console measurements. **Claims** (`software_decode::FALLBACK_ENVELOPES`, sent by
`evidence::video_decode` whatever the probe found): decoder "FFmpeg", `hardware: false`, at most 1920x1080, no level or
bitrate bound, `software_video_decode_v1` on.

| Codec | Profiles (ffprobe, lower-cased) | Bit depths | Max fps | Measured (cores, 0 missed) |
|---|---|---|---|---|
| h264 | `high 10` | 8, 10 | 60 | 1080p24 8 Mbps 0.78; 1080p60 20 Mbps 1.90 |
| hevc | `rext` | 12 | 60 | 12-bit 4:2:0 1080p24 10 Mbps 1.06 |
| h264 | `high 4:2:2`, `high 4:4:4 predictive` | 8, 10 | 30 | 4:2:2 10-bit 1080p30 50 Mbps 1.80 |
| hevc | `rext` | 8, 10, 12 | 30 | 4:2:2 10-bit 1080p30 30 Mbps 2.08 |
| vp9 | `profile 1`, `profile 3` | 8, 10, 12 | 30 | profile 1 4:4:4 1080p30 10 Mbps 0.90 |

silo-server applies codec, profile (literal; a file without a recorded profile never matches), bit depth, size and the
absolute frame rate to them. **The player decides each mount itself** (`route::decide` → `software_fallback`, read by
`FfmpegSource`): a hardware codec's software route decodes in FFmpeg software only when

1. a software entry the attempt claims now (its start request, after any replan) admits the delivered file's catalog
   version (`software_decode::fallback_admits` over `formatting::decodes`, Bale's mirror of the server's matching: codec,
   profile, depth, size, frame rate, bitrate);
2. a file not known to be 4:2:0 (pixel format, else a 4:2:0-only profile such as `high 10`) has a known frame rate of at
   most 30: the claims can't say chroma, and `rext` 12-bit is one profile for 4:2:0 and 4:2:2;
3. no console stage (tone map, SDR downscale) or Dolby Vision base layer is involved: the stage reads d3d11va textures.

Otherwise `FfmpegSource` refuses as before and the replan withdraws the codec (fix 2). The player's check is what keeps
the envelope: Bale's hardware entries name no profiles and the server skips profiles on platform-attested hardware
entries, so it plans HEVC RExt 8/10-bit, VP9 profile 1/3 and 8-bit H.264 4:2:2 direct at any size and rate the hardware
entry allows (4K, 60 fps). **Display matching** (`match_display`) counts a version only where the console plays it: a
hardware entry only for a profile the hardware decodes (`route::hardware_profile`), a bounded software entry, or
`fallback_admits`; so a 1080p Hi10P version beats a 720p hardware one, and a 1080p60 HEVC 4:2:2 version loses to one
the console plays. **Logs:** `player: software fallback codec=hevc 1920x1080 (past the hardware decoder)` on every such
mount; with `swfallback=on` past the envelope, `player: debug software fallback ... (past the release bounds)`; the
`play-url` route line adds `software fallback (past the hardware decoder, inside the release envelope)`.

What the server plans now (Series X claims, Bale's mirror of its matching; `SoftwareFallbackTests` pins every row):

| File | Server | Player |
|---|---|---|
| H.264 High 10 1080p24 or 1080p60; H.264 High 4:2:2 10-bit 1080p30; H.264 High 4:4:4 Predictive 720p30 | direct (fallback entry) | FFmpeg software |
| HEVC RExt 4:2:2 10-bit 1080p30; HEVC RExt 12-bit 4:2:0 1080p24 or 1080p60 | direct | FFmpeg software |
| VP9 profile 1 (4:4:4) 1080p30; VP9 profile 3 1080p25 | direct | FFmpeg software |
| H.264 8-bit 4K30; H.264 High 10 4K24; H.264 4:2:2 10-bit 1080p60; H.264 High 10 1080p120 | transcode (no entry admits it) | |
| H.264 4:2:2 8-bit 1080p60; HEVC RExt 10-bit 1080p60 or 4K30; VP9 profile 1/3 at 60 fps or 4K | direct (the profile-blind hardware entry) | refused; the replan withdraws the codec; transcode |
| HEVC RExt 12-bit 4:2:2 1080p60 | direct (the 12-bit entry; the server can't see chroma) | refused (30 fps chroma rule); transcode |
| HDR HEVC RExt 4:2:2 on an SDR TV | tone-mapped by the server (no console tone map promise for 4:2:2) | (a direct plan would be refused: stage) |
| HDR HEVC RExt 4:2:2 1080p30 on an HDR TV | direct | FFmpeg software with the stream's HDR10 colour (unverified, the owner's TV is SDR) |
| HEVC Main 10 4K60; H.264 High 1080p60; VP9 profile 2 4K60 | direct | hardware, as before |

A refused route costs one failed open before the transcode, as before phase A. A failed software mount (decode error)
withdraws every software entry and the codec; a start that runs out of time keeps the claims, as for software AV1.

## Bale before phase A (until 2026-11-24)

**The hardware.** The console's D3D11 video device reports H.264 VLD_NOFGT at 1080p (`H264_VLD_NOFGT NV12 1080p`),
HEVC Main and Main10 up to 8K, VP9 profile 0 and 2 up to 4K, VC-1 and MPEG-2 at 1080p ([player-plan.md](player-plan.md),
"GPU decode"). `HardwareDecodeProbe` claims exactly that, 60 fps, no bitrate bound.

**The route.** `route::hardware_admits` (native/bale-core/src/route.rs) keeps a file on the hardware decoder when a
probed entry takes its bit depth and size and its profile is one the decoders take (H.264 4:2:0 up to High, HEVC
Main/Main 10, VP9 profile 0/2). Everything else in `FFMPEG_VIDEO_CODECS` gets `software_video = true`. For the hardware
codecs that means:

| Codec | Software route today | Typical files |
|---|---|---|
| H.264 | above 1920x1080; High 10; High 4:2:2 / 4:4:4 (8 or 10-bit) | 4K phone, action-cam and drone footage (4K30-60, 50-100 Mbps), web 4K; "Hi10P" anime encodes (1080p24, 2-10 Mbps); camcorder/broadcast 4:2:2 (XAVC, AVC-Intra: 50-400 Mbps); screen recordings (4:4:4) |
| HEVC | RExt: 4:2:2, 4:4:4, 12-bit (ffprobe profile "Rext") | camera 4:2:2 10-bit footage (1080p and 4K, 50-150 Mbps); rare 12-bit masters |
| VP9 | profile 1/3 (4:2:2/4:4:4/4:4:0); above 3840x2160 | rare (YouTube uses profiles 0 and 2); YouTube 8K |
| VC-1, MPEG-2 | above 1920x1080 | practically none |

**The claims.** `evidence.rs::video_decode` adds an unbounded software entry (`decoder_name` "FFmpeg", no size, rate,
depth, profile or bitrate) for every FFmpeg codec but AV1 (owner, 2026-11-17, "as AetherEngine declares its build"),
plus `software_video_decode_v1`. silo-server therefore admits any H.264/HEVC/VP9 original
([below](#silo-server)).

**The player.** `FfmpegSource.Configuration` accepts a software route only for `SoftwareDecodePolicy.VideoCodecs`, which
since 2026-11-17 means the software-only codecs (AV1, MPEG-4 Part 2, VP8, WMV ...). A software route for H.264/HEVC/VP9
throws `InvalidDataException("The video codec cannot use the local fallback.")`; the open fails, the single failure
replan (`SoftwareDecodePolicy.WithoutFailedFallbacks`) withdraws every software entry, and the server transcodes or picks
another version. `play-url` says "h264 at this size/rate exceeds the console's hardware decoder ..." (8ed6c50). The end
result is right, but each such title pays a failed open plus a replan round trip, and the server was promised a
decoder the player does not use. Auto's display matching was not affected: it counts only hardware and bounded
entries (`formatting.rs::match_display`).

**Worse for the profiles a hardware entry can't exclude.** Bale's hardware entries name no profiles (evidence.rs: the
server skips profile lists on `platform_attested` hardware entries anyway), so after the replan the hardware entry
alone still admits HEVC RExt 8/10-bit, VP9 profile 1/3 and 8-bit H.264 4:2:2/4:4:4: the server may offer a copy route
again (the progressive or HLS remux; only the failed original is excluded by its attempted key), the route sends it to
software again, it is refused again, and the attempt ends in "Playback failed". Checked with Bale's own mirror of the
server's matching (`PlaybackFormatting.Decodes` before and after `PlaybackReplan.ForFailure`, 2026-11-24): admitted for
HEVC `rext` 10-bit, VP9 `profile 1` 8-bit and H.264 `high 4:2:2` 8-bit; H.264 High 10 and 12-bit HEVC are excluded by
bit depth, so those transcode. Not console-verified. Only a software decode (or a replan that withdraws the codec) can
play these; that was the intent of the 2026-11-17 claims, and the `FfmpegSource` gate is the missing piece.

Measured software video so far (owner's Series X, whole-app process CPU, idle state):

| Decoder, clip | CPU | Notes |
|---|---|---|
| dav1d, AV1 1080p23.976 10-bit (0.1.230) | ~1.3 cores | real time |
| dav1d, AV1 1080p59.94 10-bit 6.4 Mbps (0.1.439, then zero-copy) | ~3 cores | 0 late frames over ~4.5 min |
| dav1d, AV1 1080p23.97 10-bit with grain (0.1.695) | 1.23 (grain off), 1.39-1.51 (CPU grain), 0.95 (D3D12 zero-copy GPU grain) | 0 dropped |
| FFmpeg MPEG-4 Part 2, 512x384 / 720x480 / 1920x1080 | 0.06 / 0.08 / 0.30 cores | 30 s each, 0 late frames |

Both dav1d rows cost about **50 ms of process CPU per 1080p 10-bit frame**, the anchor used below (the 0.1.695
grain-off run took today's CPU-buffer path, P010 conversion included, like the host anchor).

## What the official clients do

### silo-apple and AetherEngine

AetherEngine (LGPL-3, read for behaviour only) routes **by format, not by size**: a file goes to its software host when
VideoToolbox can't decode it (`VTCapabilityProbe.canHardwareDecode`, VOD) or AVPlayer can't present it. Software:
H.264 High 4:2:2 / 4:4:4 / High 10 and HEVC RExt where VideoToolbox lacks the profile, interlaced H.264 (deinterlace),
H.264 MVC carriage, VP9 (VideoToolbox for profiles 0/2, libavcodec otherwise), VP8, MPEG-2/4, VC-1 and the long tail
(`docs/formats.md` "Software decode", `Sources/AetherEngine/Decoder/VideoRoutingPolicy.swift`). The engine has no
resolution, frame-rate or bitrate gate of its own; the bounds live in silo-apple's claims
(`iosApp/iosApp/Shared/AppleDecodeCapabilities.swift`):

| Device class | Evidence | Video claims |
|---|---|---|
| Apple TV 4K (every generation) | `declared` | the flat list of every codec the pinned build decodes, **no decode entries** (unbounded); Aether's load-time probe picks native or software, a typed load failure takes the bounded V3 replan |
| iPhone, iPad | `declared` | the same list, with bounded entries only for codecs decoded solely in software: MPEG-2 always, AV1 without hardware AV1 |
| Apple TV HD, macOS, downloads | `platform_attested` | VideoToolbox hardware entries (H.264 8-bit, HEVC 8/10-bit; 3840x2160 60 fps 120 Mbps, or 1920x1080 60 fps 25 Mbps) **plus the software envelopes "proven with Aether fixtures"**: H.264 `high 10` 10-bit 1920x1080 30 fps 10 Mbps (libavcodec); AV1 `main` 10-bit 1080p30 3 Mbps (dav1d); VP9 `profile 0` 8-bit 1080p30 3 Mbps (libavcodec); MPEG-2 `main` 8-bit 1080p 31 fps 40 Mbps; VC-1 `advanced` 8-bit 1080p30 32 Mbps |

Apple TV HD stays off the unbounded declaration because it "has much less software-decode headroom than every Apple TV
4K generation and no online performance signal exists yet that can trigger a typed replan". That is the case Bale is
in: four shared Zen 2 cores, slowed in the input-idle state, and no performance replan yet. Apple's bounded envelopes are
1080p30 with small bitrates; nothing above 1080p is claimed for software anywhere.

### silo-android

Android claims hardware decoders only: `MediaCodecCapabilitiesProbe` keeps a decoder when
`isHardwareAccelerated && !isSoftwareOnly` (API 29+) or its name is not a known software one (`omx.google.`, `c2.android.`,
`omx.ffmpeg.` ...), and never sends `software_video_decode_v1`. Anything the hardware can't take is transcoded.

### silo-server

From `docs/architecture/playback-protocol-v3.md` §3 and `internal/playback/capabilities_v3.go`
(`videoEligibleV3`, `videoDecodeTakesSourceV3`, `routeVideoMetadataCompleteV3`):

- At `exact` and `platform_attested` (Bale) a `hardware: false` entry counts only with `software_video_decode_v1`.
- Any one entry that admits the source is enough. Each entry checks bit depths, max width/height, frame rate and
  `max_bitrate_kbps` against the source's video bitrate (`SourceVideoKbps`: the video track's, else the file's).
- Profiles and levels are skipped for **hardware** entries at `platform_attested` only. A software entry with a profile
  list is compared literally with ffprobe's lower-cased name (`high 10`, `high 4:2:2`, `high 4:4:4 predictive`, `rext`,
  `profile 1`), and a file with no recorded profile fails it.
- `max_frame_rate` is absolute at `platform_attested` (the size/rate trade is `exact` only), so 4K30 and 1080p60 are two
  entries.
- An omitted bound is unconstrained: "An entry that names a codec and nothing else therefore claims that decoder handles
  every variant of it. That is a strong claim."
- No direct route at all without complete metadata (codec, bit depth, size, frame rate, bitrate > 0).
- `ConsoleToneMapPolicy.CanManage` (Bale) promises the console tone map only with a **hardware** HEVC/VP9 entry, so
  software-decoded HDR on an SDR display is tone-mapped by the server. Keep it so (the stage reads d3d11va frames).

## What the console can do

### Limits (docs/gpu-tone-map.md "Measured limits", docs/resource-budget.md)

- Xbox Series X CPU: 8 Zen 2 cores at 3.8 GHz, 3.66 GHz with SMT (Series S: 3.6 / 3.4 GHz). A normal app such as Bale
  gets 8 logical CPUs (4 cores with SMT), shared with the system; throughput stops growing beyond 4 threads (8 threads =
  4.1x one thread). Xbox One consoles (Jaguar, no AVX2) claim no software video at all (`software_video_supported`).
- Input-idle state (all real playback): CPU ALU 21-25 % slower, CPU RAM copies 3.6-4x slower (one thread ~3.5 GB/s,
  four ~6.5 GB/s).
- Budget: about 3 cores for decode, the rest of Bale (audio, network, demux, Media Foundation, present) in ~1 core; CPU
  full-frame passes: 1 per frame soft, 2 hard (0.2.x goal).
- Memory: 3328 MiB for CPU and GPU together; 0.2.x soft goal ~2.0 GB for the whole app.

### FFmpeg's decoders, host-measured and scaled to the console (estimates)

Measured on the development PC (2026-11-24): Intel Core i7-10610U, 4 cores / 8 threads with AVX2, the same shape as
Bale's share of the console; Fedora's FFmpeg 9.0.2 (the native h264/hevc/vp9 decoders and libdav1d, the same family as
Bale's 8.1.2 build). Each run: `ffmpeg -benchmark -threads 8 -c:v <decoder> -i <clip> -map 0:v:0 -an -pix_fmt nv12|p010le
-f null -`, so 8 decoder threads (the wrapper's default on the console) and the fork's swscale conversion to its output
format; CPU = user + system time per frame, in the laptop's warm steady state, with a dav1d anchor run between groups.

**Scaling to the console (estimate).** The console's dav1d costs ~50 ms of process CPU per 1080p 10-bit frame (the
table above). The same decoder on the host's anchor clip (`jellyfin/1080p-AV1-10bit-10M.mp4`, P010 out) took
38.4-39.2 ms per frame (five runs), so console ≈ host × 1.29. The factor carries the idle-state slowdown, Bale's own
per-frame work (Media Foundation, present, audio, network) and the clock difference, but the anchors' content and
bitrate differ, so read every estimate as ±25 %; step 0 of the measurement plan replaces the factor with the console's
own number for the anchor clip. 4K estimates are the most optimistic: the idle state slows RAM 3.6-4x and a 4K decode
moves about four times the data of a 1080p one.

| Clip (derived/sw-decode unless named) | Host CPU ms / frame | Host fps (8 threads) | Console cores at the clip's rate (estimate) |
|---|---|---|---|
| H.264 High 8-bit 4K 23.976 fps 25 Mbps (`2160p24-H264-High-25M-30s.mp4`) | 74 | 67 | **2.3** |
| H.264 High 8-bit 4K 30 fps 13 Mbps (`derived/2160p30-H264-SDR-20s.mp4`) | 71 | 72 | **2.7** |
| H.264 High 8-bit 4K 30 fps 50 Mbps (`2160p30-H264-High-50M-30s.mp4`, phone class) | 95 | 53 | **3.7** |
| H.264 High 8-bit 4K 60 fps 80 Mbps (`2160p60-H264-High-80M-30s.mp4`) | 86 | 58 | **6.6** |
| H.264 High 8-bit 4K 60 fps ~8 Mbps animation (the kodi-bbb clip) | 59 | 85 | **4.5** |
| H.264 High 10 1080p 23.976 fps 8 Mbps (`1080p24-H264-High10-8M-30s.mkv`) | 31 | 160 | **1.0** |
| H.264 High 10 1080p 60 fps 20 Mbps (`1080p60-H264-High10-20M-30s.mkv`) | 30 | 162 | **2.3** |
| H.264 High 4:2:2 10-bit 1080p 29.97 fps 50 Mbps (`1080p30-H264-High422-10bit-50M-30s.mkv`) | 86 | 58 | **3.3** |
| HEVC RExt 4:2:2 10-bit 1080p 29.97 fps 30 Mbps (`1080p30-HEVC-Main422-10-30M-30s.mkv`) | 101 | 49 | **3.9** |
| HEVC RExt 4:2:0 12-bit 1080p 23.976 fps 10 Mbps (`1080p24-HEVC-Main12-10M-30s.mkv`) | 59 | 72 | **1.8** |
| VP9 profile 1 (4:4:4 8-bit) 1080p 29.97 fps 9.7 Mbps (`1080p30-VP9-Profile1-444-10M-30s.webm`) | 50 | 98 | **1.9** |
| VP9 profile 0 1080p 60 fps 7.6 Mbps (hardware takes it; `1080p60-VP9-Profile0-8M-30s.webm`) | 19 | 207 | 1.5 |
| VP9 profile 0 4K 30 fps 17.5 Mbps (hardware takes it; `2160p30-VP9-Profile0-18M-30s.webm`) | 62 | 57 | 2.4 |
| HEVC Main 10 1080p 60 fps 7.7 Mbps (hardware takes it; `derived/1080p-HEVC-10bit-90s.mp4`) | 35 | 131 | 2.7 |
| HEVC Main 10 4K 60 fps 59 Mbps (hardware takes it; `jellyfin/4K-HEVC-HDR10-60M.mp4`) | 119 | 45 | 9.2 (3.7 at 24 fps) |

Reading, against the ~2.5 cores a software decode may take:

- **1080p 4:2:0 fits**: H.264 High 10 at 24 fps costs about one core, at 60 fps / 20 Mbps about 2.3; VP9 profile 1 and
  12-bit HEVC at 24-30 fps ~1.8-1.9. VP9 (FFmpeg's own decoder) is the cheapest per pixel.
- **4:2:2 does not fit at 1080p30**, and the conversion is a large part of why. Splitting decode from conversion (same
  runs without `-pix_fmt`): HEVC 4:2:2 10-bit 69 ms decode + 32 ms to P010; H.264 4:2:2 10-bit 56 + 26; VP9 4:4:4 36
  + 15; H.264 High 10 4:2:0 16 + 8 (10-bit to P010 is never free); 4K 4:2:0 8-bit H.264 90 + 5 (NV12 is cheap). The fork
  calls `sws_scale` once per frame on one thread (`UncompressedVideoSampleProvider`, no swscale threads), so a
  4:2:2 10-bit 1080p frame's ~35-40 ms of console conversion alone caps it below 30 fps, however many cores are free.
- **4K H.264 is at or over the budget**: ~2.3 cores at 4K24 / 25 Mbps, 2.7 at 4K30 / 13 Mbps, 3.7 at the 50 Mbps a phone
  records, 4.5-6.6 at 60 fps. The cost grows with bitrate as much as with pixels (CABAC: 4K30 13 → 50 Mbps adds 35 %).
- **4K HEVC/VP9 in software** (only relevant if a hardware decoder were missing): VP9 4K30 ~2.4, HEVC Main 10 4K far
  over.
- **Threads**: with 4 decoder threads instead of 8 the host spent 7-12 % less CPU per frame (less SMT sharing) at a lower
  peak rate (4K30 H.264 50 Mbps: 84 ms and 41 fps instead of 95 ms and 53 fps) and 120 MB less memory (306 vs 427 MB
  peak RSS). On a console app that has four cores and must leave one for Bale, `threads=4` may be the better default;
  measure both.

### Measured on the console (2026-11-24, Bale 0.1.767, Series X, 1080p SDR TV)

Debug `play-url` of the local clips, `ffmpeg-tune swfallback=on` (run 7: `decoder=software`), whole-process CPU
over 10 s starting ~8 s into playback (`threads 10`), overlay hidden, no input. Every run below except the last
played with **0 missed frames and 0 stalls**; one round each.

| Clip | Cores | App memory | Estimate before |
|---|---|---|---|
| calibration: AV1 1080p60 10-bit 10 Mbps, dav1d | 1.71 | 1065 MiB | (anchor) |
| H.264 High 10 1080p24 8 Mbps | 0.78 | 827 MiB | ~1.0 |
| H.264 High 10 1080p60 20 Mbps | 1.90 | 1055 MiB | ~2.3 |
| H.264 High 4:2:2 10-bit 1080p30 50 Mbps | 1.80 | 905 MiB | 3.3 |
| HEVC Main 4:2:2 10 1080p30 30 Mbps | 2.08 | 913 MiB | 3.9 |
| HEVC Main 12 1080p24 10 Mbps | 1.06 | 834 MiB | ~1.8 |
| VP9 profile 1 4:4:4 1080p30 10 Mbps | 0.90 | 664 MiB | ~1.9 |
| H.264 High 4K24 25 Mbps | 1.68 | 1168 MiB | ~2.3 |
| H.264 4K30 13 Mbps | 1.97 | 1228 MiB | |
| H.264 High 4K30 50 Mbps (8 threads / `threads=4`) | 2.87 / 2.85 | 1230 MiB | ~3.7 |
| VP9 profile 0 1080p60 8 Mbps (forced software) | 0.95 | | |
| VP9 profile 0 4K30 18 Mbps (forced software) | 2.04 | | |
| HEVC Main 10 1080p60 (forced software) | 2.24 | | |
| H.264 High 4K60 80 Mbps | 6.58 | 1434 MiB | 4.5–6.6: **fails**, 12.7% missed, 6 decoder stalls (2.4 s) |

So the console beats the host-scaled estimates by 20–50%, and the 4:2:2 conversion is not the limit at 1080p30.
Everything up to 4K30 fits in about 3 cores; only 4K60 does not. `threads=4` changes nothing at 4K30.
**Caveat:** the 4K clips on this 1080p TV were scaled by Media Foundation, the route that leaks per open
(AGENTS.md, ~23 opens); Bale was restarted after six. 4K software claims therefore still wait for software frames
through Bale's stage (phase B), whatever the CPU says. Not measured: run 8 (a library title end to end), HDR
software frames, Series S.

### Costs besides the decoder

- **Frame memory.** A 4K frame is 12.4 MB (8-bit 4:2:0) or 24.9 MB (10/12-bit, 16-bit samples); 1080p is a quarter.
  FFmpeg holds the DPB (H.264 and HEVC at 4K: 5-6 frames by level limits) plus one frame per frame thread (8 by
  default); the fork adds decode-ahead (8 frames above 1080p, else 16) and Media Foundation the 500 ms (above 1080p) or
  1 s BufferTime of decoded samples. 4K30 8-bit therefore holds roughly 200 + 100 + 190 MB of CPU frames before Media
  Foundation's own textures (host peak RSS of the decoder alone: 0.42-0.47 GB for 4K H.264, 0.64-0.66 GB for 4K HEVC
  10-bit). Inside the 3328 MiB pool and close to the measured 4K AV1 envelope (~1.1 GB app). `threads=4` would save
  four frames and their latency.
- **CPU full-frame passes.** The fork converts every software frame with swscale (yuv420p to NV12, 4:2:2/4:4:4 to
  NV12/P010, 12-bit to P010), and Media Foundation copies the CPU sample into a texture: two passes, the 0.2.x hard
  limit. At 4K30 8-bit that is ~1.5 GB/s of copy traffic in the idle state, a few tenths of a core at ~3.5 GB/s per
  thread.
- **4K SDR on a smaller display.** Software frames skip Bale's stage, so Media Foundation's video processor would scale
  4K SDR to the 1080p display itself, the leak that crashes Bale after ~23 opens ([player-plan.md](player-plan.md),
  "4K SDR downscale on smaller displays": "If a new route produces SDR larger than the display, give it the same
  display-sized stage"). A 4K software claim needs the stage first (phase B). 1080p software on a 1080p display is safe
  (1080p SDR survived 40/40 opens).
- **HDR.** On an SDR display the server tone-maps software-decoded HDR (no `client_managed_dynamic_range` without a
  hardware entry). On an HDR display the fork's software output carries the stream's colour and HDR10 metadata
  (`VideoColorAttributes::Apply`, as for dav1d), but HDR passthrough is unverified here (the owner's TV is SDR).

## Proposal

The rule, as Apple's bounded classes: claim software only inside envelopes measured on the console with real files;
keep the hardware decoder first; never claim what the player refuses.

**Now: two small production fixes, both done 2026-11-24.** The owner approved fix 1 with a 60 fps cap: the
console's app-mode output is at most 60 Hz, so files above 60 fps transcode, and the remaining software entries
(software-only codecs) carry `max_frame_rate` 60 too.

1. Drop the unbounded software entries of the hardware codecs (H.264, HEVC, VP9, VC-1, MPEG-2) from
   `evidence.rs::video_decode`, keeping those of the software-only codecs. The server then transcodes 4K H.264, H.264
   High 10 and 12-bit HEVC up front instead of after a failed open, and the measured entries below are not OR'ed with
   an entry that admits everything. One side effect: files above 60 fps (120 fps phone footage) now pass the server
   only through that software entry and then play on the hardware decoder (the route ignores the frame rate); without it
   they transcode.
2. When the failed route was a refused software route of a hardware codec, let the replan
   (`without_failed_fallbacks`) withdraw that codec for the attempt (flat lists and its hardware entries), so the server
   transcodes to another codec instead of offering a remux that fails the same way (HEVC RExt, VP9 profile 1/3, 8-bit
   H.264 4:2:2/4:4:4). Still needed after phase A for files outside the software envelopes (4K RExt).

**Phase A: done 2026-11-24**, as approved ([Phase A in release](#phase-a-in-release-2026-11-24)). Differences from the
proposal above the measurements: the console beat the estimates, so the envelopes take 4:2:2/4:4:4 at 30 fps and 4:2:0
H.264 High 10 and 12-bit HEVC at 60 fps, without bitrate bounds; intra profiles (`high 10 intra`, `high 4:2:2 intra`)
stay out. Possible refinements: a hardware-first tie-break in display matching between versions of the same height;
bitrate bounds if a heavy camera file (100 Mbps+ 4:2:2) stalls.

**Phase B, with the fork.** What is left, in this order:

1. **Software frames through the stage**, needed for anything larger than the display (and for HDR on an SDR display).
   The CPU no longer stands in the way: H.264 4K30 took 1.97 cores at 13 Mbps and 2.87 at 50 Mbps (4K60 80 Mbps fails
   at 6.6). Candidate claim once the stage takes software frames, inside the ~3-core budget:

| Codec | Profiles | Depths | Max size | Max fps | Max kbps |
|---|---|---|---|---|---|
| h264 | `constrained baseline`, `baseline`, `main`, `high` | 8 | 3840x2160 | 30 | per a long-play check (50 000 measured) |

2. **Chroma conversion off the single swscale call**, now an optimisation only: at 1080p30 the console decoded 4:2:2
   10-bit in 1.8-2.1 cores, so the conversion is not the limit there; it may be at 4K 4:2:2 or 1080p60 4:2:2.

What to ask the fork thread for (the two pieces above):

- Conversion: in `UncompressedVideoSampleProvider::InitializeScalerIfRequired`/`GetFrameBuffer`, a threaded swscale
  context (`sws_alloc_context` + the `threads` option + `sws_scale_frame`) or a GPU pack/subsample pass, for every
  software stream whose input is not 4:2:0 8-bit. Measure with the clips above (fork log or `frame-log`).
- Stage: in `UncompressedVideoSampleProvider`, honour `bale_tonemap` (and `bale_tonemap_output_width/height/depth`,
  `bale_tonemap_white_nits`) for software streams of any codec, not only `D3D11VideoSampleProvider`: reuse the staged
  D3D11 upload of `bale_film_grain_mode=3` (swscale into a mapped STAGING texture, NV12 or P010, ring of three), copy to
  a shader-readable texture, run the existing `VideoFrameProcessor` (bale_gpu's tone map / scale-only downscale /
  dither) into its pooled output, and present that as processed output (`HasProcessedOutput`, the fixed BT.709 contract
  and the tone-map mark `{b41e6a52-3c9d-4f0b-8e7a-5d2c1f9a0b37}` that `FfmpegVideoOutputGuard` checks).
- Same failure rule as the d3d11va stage: a device, upload or stage failure latches `NotifyError`, never an unprocessed
  frame.
- Bonus: swscale then writes straight into the staging texture, so Media Foundation's CPU copy goes away (two CPU
  full-frame passes become one).
- Bale side: drop `!software_video` from `route.rs`'s `console_downscale`, let `FfmpegSource` keep `bale_tonemap` on a
  software route, set the output size keys as for d3d11va, and only then let `can_manage` count software entries.

**Phase C, a performance signal.** Apple keeps slow devices off broad claims because nothing can replan on
"too slow". Bale already measures stalls and tags decoder stalls (`PlaybackSmoothnessMeter`). A software-video mount
whose decoder stalls repeatedly in its first ~30 s could use the single failure replan with the software entries
withdrawn (the server transcodes), keeping position and tracks. That safety net would allow less conservative bounds.

**Where each piece goes:**

| Piece | File | Change |
|---|---|---|
| Claims | `native/bale-core/src/evidence.rs` (`video_decode`) | done: the bounded envelopes (`software_decode::FALLBACK_ENVELOPES`); fix 1 dropped the unbounded ones |
| Which hardware codecs may decode in software | `native/bale-core/src/software_decode.rs` | done: `SOFTWARE_FALLBACK_VIDEO_CODECS` (h264, hevc, vp9), `SoftwareDecodePolicy.SoftwareFallbackVideoCodecs` |
| Gate | `src/Bale.Xbox/Services/FfmpegSource.cs` | done: a software route of those codecs needs `route.SoftwareFallback`; the debug `SoftwareFallbackAdmits` bypasses it |
| Envelope check | `route.rs` (`software_fallback`) + `software_decode::fallback_admits` | done: the delivered file's version against the attempt's current software entries, the 30 fps chroma rule, no stage |
| Route | `route.rs` | `hardware_admits` unchanged; phase B drops `!software_video` from `console_downscale` |
| Tone map promise | `tone_map.rs` (`can_manage`) | unchanged until phase B: hardware entries only |
| Failure | `software_decode.rs` (`without_failed_fallbacks`) | done (fix 2): a failed software route withdraws every software entry and, for a hardware codec, the codec itself |
| Tuning | `SoftwareDecodeTuning` | decode-ahead/BufferTime apply to any software video; `threads=4` changed nothing at 4K30 |
| Device gate | `software_video_supported` | unchanged: Series consoles with AVX2 only (no software entries otherwise, so no fallback) |
| Version choice | `formatting.rs` (`match_display`) | done: hardware entries count only for profiles the hardware decodes; the fallback through `fallback_admits` |
| Packages | `PackagedVideoCodecs` | lists unchanged; the decode entries are per request, so a remux of such a file (read by FFmpeg, same route) takes the same software decoder |

## Debug switches

Debug builds only (`DEBUG_SERVER`), memory only, captured per playback attempt like every `ffmpeg-tune` key; they
never change the claims.

- `bale-debug.sh ffmpeg-tune swfallback=on|off|default`: a software route for H.264/HEVC/VP9 (the route's own decision:
  the console's decoder can't take the file) decodes in FFmpeg software even outside the release envelope (4K, 4:2:2 at
  60 fps), for measurements. Off and default are the release behaviour (phase A: the envelope decodes, the rest is
  refused). Never with a console stage (an HDR file on an SDR display stays refused: the stage reads d3d11va textures).
  Logs `player: debug software fallback codec=h264 3840x2160 (past the release bounds)`; `play-url` adds `debug software
  fallback` to its route line. VC-1 and MPEG-2 stay refused.
- `ffmpeg-tune decoder=software` (existing) forces FFmpeg software on every mount, now including those routes. Use it to
  measure the software cost of files the hardware takes (4K HEVC Main 10, 4K VP9). It drops the stage, so 4K on a 1080p
  display is scaled by Media Foundation (see the caveat below).
- `ffmpeg-tune threads=0..16|default`: FFmpeg's software decoder threads (`MaxDecoderThreads`; 0/default = the logical
  CPU count, 8). Software video only, AV1 included.
- Existing and useful here: `ffmpeg-tune ahead=`, `buffer=`, `prio=`; `stats`, `mem`, `threads [seconds]`,
  `frame-log on`; the standalone probe `playtest-ffmpeg <url> 30 software threads=N` (no route gate, muted, a 160x90
  surface).

## Verify phase A on the console

No Silo title is needed: `play-url` takes the same route decision (the probe's version and the attempt's claims).
Reset the tuning first (`scripts/bale-debug.sh ffmpeg-tune decoder=default swfallback=default threads=default`), serve
the clips as below, and keep the input-idle rule for readings.

1. **Inside the envelope, no switch** (each of `derived/sw-decode/1080p24-H264-High10-8M-30s.mkv`,
   `1080p60-H264-High10-20M-30s.mkv`, `1080p30-H264-High422-10bit-50M-30s.mkv`, `1080p30-HEVC-Main422-10-30M-30s.mkv`,
   `1080p24-HEVC-Main12-10M-30s.mkv`, `1080p30-VP9-Profile1-444-10M-30s.webm`): `play-url` answers with
   `route=software codec=... stage=False; software fallback (past the hardware decoder, inside the release envelope)`,
   the log shows `player: software fallback codec=... 1920x1080 (past the hardware decoder)` and no `debug software
   fallback`, and `stats` matches the measured run (CPU, Missed frames 0, Stalls 0; Video media type decoder
   ffmpeg-software, P010 for the 10/12-bit clips, NV12 for VP9 4:4:4).
2. **Outside it, no switch** (`derived/sw-decode/2160p30-H264-High-50M-30s.mp4`, or `2160p24-H264-High-25M-30s.mp4`):
   `play-url` fails with 409 `... is past the console's hardware decoder and outside Bale's software envelope ...`,
   before the player opens it.
3. **Outside it with `ffmpeg-tune swfallback=on`**: the same 4K clip plays, the log says `debug software fallback ...
   (past the release bounds)`. Mind the 4K caveat below (restart Bale after a few 4K opens).
4. **Hardware files unchanged**: `jellyfin/4K-HEVC-HDR10-60M.mp4` and `derived/1080p-HEVC-10bit-90s.mp4` keep
   `route=system`, with no software fallback line.
5. **Through Silo, if the library has such a file** (Hi10P anime, camera 4:2:2): `bale-debug.sh play <id>` shows
   `Plan: delivery=original_http`, `player: software fallback ...` and no `player: replan`; a 1080p60 4:2:2 or 4K RExt
   file instead shows one failed open (`The video codec cannot use the local fallback`) and `player: replan outcome=`
   with a transcode.

## Measure it on the console

The measurement runs behind phase A (results in [Measured on the console](#measured-on-the-console-2026-11-24-bale-01767-series-x-1080p-sdr-tv)).

Clips: the local test-media folder (`$TEST_MEDIA`; not in the repo, see its MANIFEST.md). The software-decode set is
`derived/sw-decode/` (made 2026-11-24 from the Jellyfin 4K60 HEVC clip, 30 s each, no audio); the rest is the existing
Jellyfin and derived set. Serve the folder from the development PC:

```sh
python3 -I scripts/serve-media.py "$TEST_MEDIA" --bind "$MEDIA_HOST" --port 8765
M="http://${MEDIA_HOST}:8765"
```

**Setup.** A debug build with these switches, the console claimed (bale-projects skill), profile "Bale Test", the
1080p SDR TV. Start from release settings:

```sh
scripts/bale-debug.sh ffmpeg-tune decoder=default swfallback=default threads=default ahead=default buffer=default prio=default
scripts/bale-debug.sh caps            # record the decoder report and the display mode once
```

**One run** = one clip with one setting:

1. `scripts/bale-debug.sh play-url "$M/<clip>"`. The response's route line must say `route=software` and, with the
   switch, `debug software fallback`; the log shows `player: debug software fallback codec=...`.
2. No controller or keyboard input from here on (input-idle state; debug-server calls are not input).
3. After ~25 s of playback: `scripts/bale-debug.sh stats` (record **CPU**, **Missed frames**, **Stalls**, **Late
   frames**, **Delivered ahead**, **Playback time**, **Video media type (mss)** for the NV12/P010 subtype and decoder),
   `scripts/bale-debug.sh mem` (app memory) and, once per clip, `scripts/bale-debug.sh threads 10` (per-thread CPU).
4. Two rounds per clip and setting. Then the next clip (play-url replaces the player).

A clip passes when both rounds show no decoder stalls, Missed frames under ~0.5 %, CPU at most ~2.5 cores (Bale's
other work needs the rest of the 3-core budget) and app memory inside the 0.2.x soft goal (~2.0 GB).

**Caveat for every 4K clip on the 1080p TV:** without the stage, Media Foundation scales the 4K SDR frames and leaks
per open; Bale dies after ~23 such opens. Restart Bale (Device Portal) after at most 10 opens of 4K clips.

**Runs, in order:**

| # | Setting | Clips | Question |
|---|---|---|---|
| 0 | `av1-decoder dav1d` (then back to `av1-decoder rav1g`) | `jellyfin/1080p-AV1-10bit-10M.mp4` | the calibration: console CPU for the host's anchor clip |
| 1 | `ffmpeg-tune swfallback=on` | `derived/sw-decode/1080p24-H264-High10-8M-30s.mkv`, `1080p60-H264-High10-20M-30s.mkv` | phase A: H.264 High 10 at 24 and 60 fps |
| 2 | same | `1080p30-H264-High422-10bit-50M-30s.mkv` | H.264 4:2:2 10-bit (not proposed yet: checks the 3.3-core estimate and the conversion ceiling) |
| 3 | same | `1080p30-HEVC-Main422-10-30M-30s.mkv`, `1080p24-HEVC-Main12-10M-30s.mkv` | HEVC RExt: 4:2:2 (not proposed yet) and 12-bit (phase A) |
| 4 | same | `1080p30-VP9-Profile1-444-10M-30s.webm` | phase A: VP9 profile 1 |
| 5 | same, then `ffmpeg-tune threads=4` | `derived/sw-decode/2160p24-H264-High-25M-30s.mp4`, `derived/2160p30-H264-SDR-20s.mp4` (13 Mbps), `derived/sw-decode/2160p30-H264-High-50M-30s.mp4` | phase B: does 4K H.264 fit, and at which rate; 4 vs 8 threads |
| 6 | same | `derived/sw-decode/2160p60-H264-High-80M-30s.mp4`, the kodi-bbb 4K60 H.264 clip (2 Mbps) | the 4K60 ceiling (expected to fail at 80 Mbps) |
| 7 | `ffmpeg-tune decoder=software swfallback=default` | `derived/sw-decode/1080p60-VP9-Profile0-8M-30s.webm`, `2160p30-VP9-Profile0-18M-30s.webm`, `derived/1080p-HEVC-10bit-90s.mp4` | software cost of files the hardware takes (VP9 without a hardware entry, HEVC Main 10) |
| 8 | `ffmpeg-tune swfallback=on`, Silo playback | a library title past the hardware decoder (`bale-cli search`, then `bale-debug.sh play <id>`) | end to end through the server: `Plan: delivery=original_http`, software route, no replan; with the switch off, `player: replan outcome=` shows today's failed open |

Cross-check for any run: `scripts/bale-debug.sh playtest-ffmpeg "$M/<clip>" 30 software` (and `threads=4`), the standalone
probe with no route gate (muted MediaPlayer, ratio and cores). Afterwards reset:
`scripts/bale-debug.sh ffmpeg-tune decoder=default swfallback=default threads=default` and `scripts/bale-debug.sh
av1-decoder rav1g` (the debug default).

## Open questions

- **Bitrate.** The envelopes have no bitrate bound (none approved). The heaviest measured files ran at 50 Mbps (H.264
  4:2:2 10-bit, 1.80 cores) and 30 Mbps (HEVC 4:2:2, 2.08); camera 4:2:2 at 100 Mbps and above is unmeasured. A stall
  there would be the case for a bound, or for phase C.
- **High 4:4:4 Predictive** has no test clip yet (claimed with the 4:2:2 envelope as approved; screen recordings are
  usually low motion). Intra-only profiles (AVC-Intra, `high 10 intra`, `high 4:2:2 intra`) stay out of the claims.
- **Unknown facts.** A 4:2:2-capable profile (`rext`, VP9 profile 1/3, H.264 4:2:2/4:4:4) without a recorded pixel
  format falls back to the 30 fps limit, and such a file without a recorded frame rate is refused (the server plans no
  direct route without one anyway).
- **Series S** (3.4 GHz with SMT, ~7 % below the X) and its memory limits are unmeasured; the envelopes keep ~1 core of
  margin at their worst measured case (2.08 cores).
- **720p displays.** Media Foundation's leak was found scaling 4K SDR; whether a 1080p software frame scaled to 720p
  is safe is untested (the hardware path has the same question).
- **HDR passthrough** of software-decoded 10-bit video (HEVC RExt with PQ/HLG) on an HDR TV is plumbed in the fork but
  unverified; the owner's TV is SDR.
- **Interlaced 4:2:2** (1080i broadcast masters) would also need deinterlacing of software frames (Aether deinterlaces
  in software; Bale relies on Media Foundation's video processor).
- **Thread count.** `threads=4` changed nothing at 4K30 on the console; the default (8) stays.
