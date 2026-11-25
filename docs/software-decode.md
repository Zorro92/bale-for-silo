# Software decode fallback for H.264, HEVC and VP9

Status (2026-11-24): research, proposal and debug plumbing. **Production is unchanged**: release builds still
refuse software H.264/HEVC/VP9, and the claims are the same. Console measurements are pending (steps in
[Measure it on the console](#measure-it-on-the-console)). Backlog item: [player-plan.md](player-plan.md), "Later".

Answer first:

- **Today Bale over-claims.** Since 2026-11-17 the claims carry an unbounded software `video_decode` entry for every
  FFmpeg codec, H.264/HEVC/VP9 included, and the route sends what the console's hardware decoder can't take to
  software. But `FfmpegSource.Configuration` refuses a software route for a hardware codec ("The video codec cannot use
  the local fallback"). So every such file (4K H.264, H.264 High 10 ...) is planned direct, fails its first open, and
  only the failure replan (which withdraws every software entry) has the server transcode it. HEVC RExt, VP9 profile 1/3
  and 8-bit H.264 4:2:2/4:4:4 likely fail outright: the profile-blind hardware entry still admits them after the replan.
- **The official clients:** Apple TV 4K declares everything its engine decodes, unbounded, and lets the engine pick
  hardware or software per file; slower Apple devices claim small, fixture-measured software envelopes (H.264 High 10
  1080p30 10 Mbps, VP9 profile 0 1080p30 3 Mbps). Android claims hardware decoders only.
- **The console** (host measurements scaled to it; estimates until the console runs below): 1080p 4:2:0 fits the
  ~2.5 cores a software decode may take (H.264 High 10 at 24 fps ~1 core, at 60 fps / 20 Mbps ~2.3; VP9 profile 1 and
  12-bit HEVC ~1.9). 4:2:2 does not fit at 1080p30 (3.3-3.9 cores), largely because the fork's single-threaded swscale
  conversion to P010 costs a third of it. 4K H.264 is at or over the budget: ~2.3 cores at 4K24 / 25 Mbps, ~3.7 at the
  4K30 / 50 Mbps a phone records; 4K60 is out of reach.
- **Proposal:** two small production fixes now (drop the unbounded entries of the hardware codecs; let a refused
  hardware-codec route withdraw that codec on the replan so the server transcodes). Then phase A (Bale only): bounded
  1080p entries for H.264 High 10, VP9 profile 1 and 12-bit HEVC, with a Bale-side envelope check. Phase B (fork):
  threaded or GPU chroma conversion for 4:2:2/4:4:4, and software frames through the staged D3D11 upload and Bale's
  stage; only then could 4K H.264 at ~24 fps be claimed, and the estimates say it isn't worth much. Without the stage,
  4K software would hand Media Foundation 4K SDR to scale on a smaller display, which crashes the app after ~23 opens.
- **Debug plumbing (done):** `bale-debug.sh ffmpeg-tune swfallback=on` decodes those routes in FFmpeg software;
  `threads=N` sets the software decoder's thread count; `decoder=software` (existing) forces FFmpeg software on files
  the hardware could take, to measure the software cost of 4K HEVC/VP9.

## Bale today

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

**Now (owner's call, no measurement needed), two small production fixes:**

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

**Phase A, Bale only (no fork change).** After the console measurements, add bounded software entries
(`decoder_name` "FFmpeg", `hardware: false`) and let `FfmpegSource` take a software route for H.264/HEVC/VP9 when no
console stage is involved **and a bounded software entry admits the delivered file**. That last check matters because
the server can't apply the bounds when the profile-blind hardware entry admits the file (a 4K HEVC RExt original is
planned direct whatever the software entries say): run `formatting::decodes` (Bale's mirror of the server's matching:
codec, profile, depth, size, fps, bitrate) over the software entries for the plan's file, also refuse 4:2:2/4:4:4 pixel
formats for HEVC (`rext` covers both 4:2:0 12-bit and 4:2:2, and the claims can't tell them apart), and refuse
otherwise (fix 2 then has the server transcode). Starting values, each inside the host estimate's ~2.5-core budget,
to confirm or lower with the console runs:

| Codec | Profiles (ffprobe, lower-case) | Depths | Max size | Max fps | Max kbps | Estimate |
|---|---|---|---|---|---|---|
| h264 | `high 10` | 10 | 1920x1080 | 30 (60 if the 1080p60 clip passes) | 20 000 | ~1.0 core at 24 fps / 8 Mbps; ~2.3 at 60 fps / 20 Mbps |
| vp9 | `profile 1` | 8 | 1920x1080 | 30 | 10 000 | ~1.9 cores (4:4:4 8-bit, 9.7 Mbps) |
| hevc | `rext` | 12 | 1920x1080 | 24 | 10 000 | ~1.8 cores (4:2:0 12-bit); 4:2:2/4:4:4 refused by Bale (above) |

- Profiles keep these entries off files the hardware takes (a profile-less 8-bit H.264 entry would also admit 4K) and
  off unknown profiles (the server fails those, which is the safe side). Intra variants (`high 10 intra`) stay out
  until measured.
- 1920x1080 keeps Media Foundation from ever scaling 4K SDR, and matches the hardware entries' 1080p.
- Not yet: H.264 `high 4:2:2` / `high 4:4:4 predictive`, HEVC 4:2:2/4:4:4, VP9 `profile 3` (3.3-3.9 cores at 1080p30,
  and the single-threaded conversion alone runs out of time per frame), until phase B's conversion work. Fix 2 has
  the server transcode them.
- These entries are bounded, so Auto's display matching counts them (`match_display`). On a 1080p display that changes
  nothing; if a title has both a software and a hardware version of the same height, a later refinement could prefer
  the hardware one.
- Value: Hi10P anime encodes are the common case; VP9 profile 1 and 12-bit HEVC are rare.

**Phase B, with the fork.** Two pieces, in this order:

1. **Chroma conversion off the single swscale call** (4:2:2/4:4:4 and 10/12-bit to NV12/P010): either swscale's own
   slice threads (`sws_scale_frame` on a context with the `threads` option instead of one `sws_scale` call per frame),
   or the GPU (upload the planar planes and let a bale_gpu pass subsample and pack). Decode-only estimates then: H.264
   4:2:2 10-bit 1080p30 / 50 Mbps ~2.2 cores, HEVC 4:2:2 10-bit 1080p30 / 30 Mbps ~2.7, VP9 4:4:4 ~1.4, which brings
   1080p24-30 4:2:2 into reach.
2. **Software frames through the stage**, needed for anything larger than the display (and for HDR on an SDR display).
   Only then could 4K be claimed, and the estimates leave little: H.264 8-bit 4K at 24 fps up to ~25 Mbps (~2.3 cores)
   is the only 4K case inside the budget; 4K30 at phone bitrates (~3.7 cores) and 4K60 are not. Claim it only if the
   console beats the estimate:

| Codec | Profiles | Depths | Max size | Max fps | Max kbps |
|---|---|---|---|---|---|
| h264 | `constrained baseline`, `baseline`, `main`, `high` | 8 | 3840x2160 | 24 (30 only if 4K30 / 13 Mbps passes) | 25 000 |

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
| Claims | `native/bale-core/src/evidence.rs` (`video_decode`) | bounded entries above instead of unbounded ones for the hardware codecs (fix 1 first) |
| Which hardware codecs may decode in software | `native/bale-core/src/software_decode.rs` | a `SOFTWARE_FALLBACK_VIDEO_CODECS` list (h264, hevc, vp9), exposed through `SoftwareDecodePolicy` |
| Gate | `src/Bale.Xbox/Services/FfmpegSource.cs` | accept `route.SoftwareVideo` for that list when `!route.ConsoleStage` (today's debug `SoftwareFallbackAdmits`) and a bounded software entry admits the file |
| Envelope check | `route.rs` (new `software_admits`, beside `hardware_admits`) | `formatting::decodes` of the plan's file (versions are already a route input) over the software entries, plus the HEVC pixel-format refusal; the route then marks the mount refused before it opens |
| Route | `route.rs` | `hardware_admits` unchanged; phase B drops `!software_video` from `console_downscale` |
| Tone map promise | `tone_map.rs` (`can_manage`) | unchanged until phase B: hardware entries only |
| Failure | `software_decode.rs` (`without_failed_fallbacks`) | a failed software route withdraws every software entry (as today) and, for a refused hardware codec, the codec itself (fix 2) |
| Tuning | `SoftwareDecodeTuning` | decode-ahead/BufferTime already apply to any software video; thread count per the `threads=` A/B |
| Device gate | `software_video_supported` | unchanged: Series consoles with AVX2 only |
| Version choice | `formatting.rs` (`match_display`) | counts bounded entries already; optional hardware-first tie-break |
| Packages | `PackagedVideoCodecs` | lists unchanged; the decode entries are per request, so a remux of such a file (read by FFmpeg, same route) takes the same software decoder |

## Debug switches

Implemented 2026-11-24, debug builds only (`DEBUG_SERVER`), memory only, captured per playback attempt like every
`ffmpeg-tune` key; release builds and claims are unchanged.

- `bale-debug.sh ffmpeg-tune swfallback=on|off|default`: a software route for H.264/HEVC/VP9 (the route's own decision:
  the console's decoder can't take the file) decodes in FFmpeg software instead of being refused. Off and default are
  the release behaviour. Never with a console stage (an HDR file on an SDR display stays refused: the stage reads
  d3d11va textures). Logs `player: debug software fallback codec=h264 3840x2160`; `play-url` adds
  `debug software fallback` to its route line. VC-1 and MPEG-2 stay refused.
- `ffmpeg-tune decoder=software` (existing) forces FFmpeg software on every mount, now including those routes. Use it to
  measure the software cost of files the hardware takes (4K HEVC Main 10, 4K VP9). It drops the stage, so 4K on a 1080p
  display is scaled by Media Foundation (see the caveat below).
- `ffmpeg-tune threads=0..16|default`: FFmpeg's software decoder threads (`MaxDecoderThreads`; 0/default = the logical
  CPU count, 8). Software video only, AV1 included.
- Existing and useful here: `ffmpeg-tune ahead=`, `buffer=`, `prio=`; `stats`, `mem`, `threads [seconds]`,
  `frame-log on`; the standalone probe `playtest-ffmpeg <url> 30 software threads=N` (no route gate, muted, a 160x90
  surface).

## Measure it on the console

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

- **The calibration.** The console estimates scale host CPU time by one dav1d anchor with different content and
  bitrate; run 0 above measures the anchor clip itself on the console. 4K estimates are the least certain: the idle
  state slows RAM 3.6-4x, and 4K decoding reads far more reference data than 1080p.
- **Thread count.** FFmpeg runs one frame thread per logical CPU (8) on 4 shared cores. On the host 4 threads used
  ~10 % less CPU per frame and 120 MB less memory at 4K, at a lower peak rate; which wins on the console is the
  `threads=4` A/B.
- **Series S** (3.4 GHz with SMT, ~7 % below the X) and its memory limits are unmeasured; size claims with that margin
  or measure there.
- **720p displays.** Media Foundation's leak was found scaling 4K SDR; whether a 1080p software frame scaled to 720p
  is safe is untested (the hardware path has the same question).
- **HDR passthrough** of software-decoded 10-bit video (HEVC RExt with PQ/HLG) is plumbed in the fork but unverified;
  the owner's TV is SDR.
- **Intra-only and 4:4:4 H.264** (AVC-Intra, `high 4:4:4 predictive` screen recordings) have no test clip yet; keep them
  out of the claims until measured.
- **Interlaced 4:2:2** (1080i broadcast masters) would also need deinterlacing of software frames (Aether deinterlaces
  in software; Bale relies on Media Foundation's video processor).
- **VC-1 and MPEG-2 software entries** are as unbounded and as refused as H.264's; the "now" step drops them too.
