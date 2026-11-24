# Resource budget

Status: **goal for 0.2.x tuning** (owner, 2026-11-20). Until then the priority is rav1g playing as smoothly as
possible (higher performance is better, not higher usage); its ceiling is in "Now: rav1g's envelope" below. Every number is checked against the measured
limits in [gpu-tone-map.md](gpu-tone-map.md) "Measured limits" (normal app with `hevcPlayback`, Series X):
3328 MiB app memory in one pool, GPU-resident ≤ 2857 MiB (DXGI budget), ~1.86 TFLOP/s GPU compute (about 1/6
of the console), 4 cores (8 logical CPUs) shared with the system, and the input-idle state that all real
playback runs in (CPU ~21-25% slower, CPU RAM copies 3.6-4x slower, GPU copies ~2.4x slower, GPU compute
unchanged).

Soft = the design point a part is built for. Hard = enforced; going over it starts the degradation ladder.

## Now: rav1g's envelope

The goal is the highest playback performance (smooth, real-time 4K with headroom); the envelope is the
ceiling rav1g must stay under, not a target to fill. At equal performance, less is better. This is what the
rest of Bale leaves for rav1g during 4K AV1 playback. Measured on
2026-11-20 (Bale 0.1.705, 1080p SDR display): app 1117 MiB, of which rav1g ~600 MiB (decoder buffers 488,
output images 4 × 23.7) and the rest of Bale ~520 MiB (stage output 71, subtitles 18, swap chain 16, UI,
buffers, Media Foundation).

| Resource | rav1g may use (soft / hard) | Why |
|---|---|---|
| Memory, everything rav1g owns (buffers, output images, device) | 1.6 GB / 2.0 GB | app hard cap 2.8 GB minus ~0.6 GB for the rest of Bale and ~0.2 GB headroom |
| GPU-resident memory | ~2.0 GB | DXGI budget 2857 MiB; the rest of Bale holds ~0.15 GB on the GPU at a 1080p display |
| GPU time per frame | ≥ 85% of the app's GPU slice | Bale's stage (downscale/tone map/dither), grain, subtitles and present ≤ 15% |
| GPU ms per frame (at 85%) | 24 fps ≤ 35 ms · 30 ≤ 28 · 50 ≤ 17 · 60 ≤ 14 | frame time × 0.85 |
| CPU | 3 cores (up to 6 threads, but throughput stops growing beyond 4 threads in total) | Bale's audio, network, demux, Media Foundation and present fit in ~1 core |
| CPU full-frame passes | 0 if possible | idle CPU RAM copies ~3.5 GB/s per thread; a 4K 10-bit frame is 25 MB |
| GPU full-frame copies | as few as possible | idle GPU copies ~40 GB/s (~0.6 ms per 4K frame); compute is not slowed |

- Measure in the idle state (no input for > 12 s), which is what a viewer gets.
- rav1g v0.6.0-alpha.1 in Bale (2026-11-22, `play-url` test clips, 4K60 AV1 10-bit, 30 s, 1080p SDR display):
  **not real time**: rav1g delivered 42-47 frames/s in every window at 40/60/100 Mbps. The engine dropped nothing
  (0 of 1797) but stalled instead (waiting/bufferingstarted about every 1-1.3 s, ~300 ms each; a 30 s clip took
  41 s), and the owner saw frequent pauses. Decode-ahead starved 237-240 of 240; GPU ~10 ms/frame, CPU ~1.5-2 cores,
  app 1.12-1.16 GB. Memory and GPU are inside the envelope; throughput is not. "Dropped frames" alone can't show
  this: judge smoothness by stalls and the delivery rate against the stream's fps.
- Cause found and fixed (2026-11-22): bale_gpu's 4K → 1080p stage cost ~10 ms of GPU because both planes re-evaluated
  every output pixel's area sum (~20.7 M full tone-map evaluations for 8.3 M source pixels). The stage now computes each
  output pixel once into a 1080p fp32 intermediate (31.6 MiB; output identical, 0 of 36 CI cases differ). 4K60 AV1 40 Mbps:
  47.1 → 57.6 fps from rav1g, missed frames 21.8% → 4.5%, image release waits 233 → 96 of 240. That equals the stage-off
  run (57.5 fps / 5.0%), so the remaining ~4.5% isn't the stage. `tonemap-debug nostaged` restores the old path.
- rav1g v0.6.0-alpha.2 in Bale (2026-11-23, fused output write; `play-url`, 45 s, two rounds, rav1g rate / missed
  frames): 40M 60.4, 60.1 / 1.3%, 0.8% (alpha.1: 57.6 / 4.5%); 60M 57.0, 57.5 / 5.5%, 4.9%; 100M 53.0, 53.3 / 12.2%, 12.0%;
  HDR10 60M 52.8, 53.1 / 12.4%, 12.2% (alpha.1: ~51.2 / 14.7-15.4%); HDR10 100M 51.2, 51.7 / 15.0%, 14.1%. 40 Mbps 4K60 is
  now about real time; above that the decoder is still the limit.
- rav1g v0.7.0-alpha.1 display output in Bale (2026-11-24, fork bale.74, debug trial `rav1g-tune display=on|off`;
  same build both ways, 1080p 24 bpp display, 45 s, two rounds, rate / missed / stalls). rav1g writes the 1080p
  tone-mapped P010 image itself (display pass 0.97-1.01 ms SDR, 1.18 ms HDR10 per 4K frame) instead of Bale's stage:

  | 4K60 AV1 | display output | Bale's stage |
|---|---|---|
  | 40M SDR | 60.0, 60.9 / 1.2%, 0% / 1, 0 | 60.8, 59.9 / 1.4%, 0.8% / 1, 1 |
  | 60M SDR | 60.9, 60.8 / 0%, 0% / 0, 0 | 58.3, 57.4 / 3.4%, 4.9% / 4, 6 |
  | HDR10 60M | 60.8, 60.9 / 0%, 0% / 0, 0 | 52.8, 53.0 / 12.2%, 13.0% / 15, 15 |

  rav1g's images (ledger 19): 8 x 5.9 = 47.5 MiB at display size vs 10 x 23.7 = 237.3 MiB at 4K (-190 MiB). The
  owner's bar (40 Mbps smooth, 60 Mbps headroom, SDR and HDR10) is met with the display output, not with the stage.
  Picture parity against Bale's stage (incl. the 11-22 linear-light chroma) is still to be checked by eye and test.
- The CPU stage that reads the compressed stream is the known limit and grows with bitrate; heavy 4K
  AV1 above ~40 Mbps is the risk case.
- Spend resources only where they buy measured performance: e.g. more slots or frames in flight are worth
  their memory if they raise idle-state fps or remove stalls; otherwise keep the smaller setting.

## 0.2.x goal: the whole app during 4K playback

| Resource | Soft | Hard |
|---|---|---|
| App memory (CPU + GPU) | ~2.0 GB | 2.8 GB |
| GPU-resident memory | ~1.5 GB | 2.2 GB |
| GPU time per frame | ≤ 70% of a frame | 85% |
| CPU | ≤ 3 cores busy | 4 threads total in heavy pools |
| CPU full-frame passes | 1 per frame | 2 |

| Part | Soft | Hard | Notes |
|---|---|---|---|
| UI, managed heap, images | 250 MiB browsing, ~150 MiB in playback | 400 MiB | drop image caches/backdrops when playback starts; ~0 CPU in playback |
| Network + demux buffer | 256 MiB | 512 MiB | sized by time (20-30 s), byte cap for remuxes (128 Mbps peak = 16 MB/s); ~0.25 core |
| HW decoder (fork, FFmpeg d3d11va) | ~190-290 MiB at 4K | 480 MiB | FFmpeg's default is 4 + 16 frames for HEVC/H.264 (20 = 478 MiB at 4K P010); size from the stream's DPB (4K HEVC levels: ≤ 6 refs) + current + frames in flight. Done for HEVC (wrapper bale.68 `bale_d3d11va_pool_from_stream`, DPB + 3): 4K clips 20 → 8 surfaces (478 → 191 MiB), same smoothness; debug default since 2026-11-22, release after a long HEVC play. VP9 and H.264 (bale.71, `ffmpeg-tune pool=stream`): 4K VP9 12 → 11 surfaces (261 MiB), 1080p H.264 with `decoder=auto` 20 → 8 (57.1 → 22.9 MiB), 0 missed frames on both (2026-11-23); 1080p H.264 takes the system decoder by default, so there it doesn't apply. Debug default for all three since 2026-11-23 (owner). Windows' own decoders (passthrough) can't be shrunk |
| rav1g (4K) | ~650 MiB | 900 MiB | budget passed in as a parameter; free the process device when the next mount is not AV1 (~43 MiB) |
| dav1d (1080p only) | ~600 MiB, ≤ 3 threads | — | one CPU pass per frame |
| Stage output (tone map/downscale/dither) | 6-8 frames at display size (~24 MiB at 1080p P010) | — | display depth (dithered 8-bit on SDR 8-bit); ≤ 10% of the frame's GPU time |
| Film grain (GPU) | ≤ 2 ms per frame | — | a few MiB |
| Subtitles | ~32 MiB | — | libass on one thread off the frame path; PGS composited on the GPU |
| Swap chain | 2-3 buffers at display size (16-24 MiB) | — | display depth; 10-bit only for HDR passthrough |
| Audio | < 0.25 core, ~20 MiB | — | passthrough is nearly free |

Sum of soft targets: 4K AV1 ~1.2-1.4 GB, 4K HEVC tone-mapped or a UHD remux ~1.0-1.5 GB.

Enforcement (0.2.x):
1. Per-category soft/hard numbers in the memory ledger; `mem` and the log flag a category over its line.
2. Pools read their caps from Bale at mount (fork `bale_*` options, a rav1g parameter), never fixed inside.
3. Degradation ladder when over a hard cap: drop UI caches → shorten decode-ahead and the network buffer →
   shrink output pools → last resort, a lower server version.
4. Per-stage GPU timestamps (decode, stage, grain, subtitles, present) in the stats pane, so the GPU split is
   measured rather than assumed.

Reading decoder arrays in place is retired (2026-11-24, owner): on the console it saved the ~24 MiB copy
but cost ~80-90 MiB the driver does not report (2026-11-21), so wrapper bale.76 never asks for
shader-readable decoder pools and bale_gpu copies every decoder array first
([gpu-tone-map.md](gpu-tone-map.md), "Capabilities").

Stage measurement is now available in debug packages: `bale-debug.sh gpu-timing on` before a new
playback, then **Stage GPU** / `Bale stage gpu:` (240-outcome avg/max windows for input copy, luma,
chroma and total). See [gpu-tone-map.md](gpu-tone-map.md#measure-the-stages-gpu-time-debug-packages).
Open: collect idle-state 4K60 windows, check disjoint/unavailable counts and Xbox timestamp validity,
and test the stage's ~2 ms share against the rav1g release-fence waits; no stage cost is measured yet.

Open (2026-11-22): the stage evaluates each output pixel's area sum once when downscaling
([gpu-tone-map.md](gpu-tone-map.md#pass-structure-each-output-pixel-once-2026-11-22)): 20.7 M → 8.3 M
full per-sample evaluations at 4K → 1080p, expected ~10 ms → ~4–4.5 ms of GPU per frame, for a 31.6 MiB
fp32 intermediate at 1080p. Measure on the console at 4K60 AV1 against `tonemap-debug nostaged`: delivered
fps (was ~48, stage off 57.5), missed frames/stalls, and rav1g image release waits. Still over the ~2 ms
budget if the estimate holds.

Open (2026-11-22): output chroma mixed in linear light, input siting from the stream and one late clip
([gpu-tone-map.md](gpu-tone-map.md#closer-to-true-area-scaling-in-linear-light-2026-11-22-part-1)). No
memory added (same 31.6 MiB intermediate at 1080p, now holding the linear average); per-pixel work about the
same (the encode moves from the area pass to the plane passes, +1 encode per chroma sample, a few sign/abs
ops per source pixel; DXC ps_6_0 static size of the area shader 631 → 678 instructions, including the
unused `gammachroma` branch). Console A/B to run at 4K60 AV1 and 4K HEVC HDR → 1080p: delivered fps,
missed frames/stalls and Stage GPU against `tonemap-debug gammachroma`; plus a visual check of fine
saturated detail and a TOPLEFT-sited stream.

Result (2026-11-23, 4K60 AV1 HDR10 60M → 1080p, rav1g 0.6, two 45 s runs each): new math 15.0 / 14.8% missed
frames, `gammachroma` 15.4 / 14.7%, 18-19 stalls each, rav1g rate ~51/s in all four. The new chroma path costs
nothing measurable; the misses come from the decoder (51 of 60 fps). The 11-22 single run (14.8 vs 12.3%) was noise.
The visual check is still open.

Open: the HEVC pool floor with the fork's stage and Media Foundation holding frames; the GPU cost of the
stages other than rav1g; the ladder needs a real low-memory test.
