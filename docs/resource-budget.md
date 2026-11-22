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
  40/60/100 Mbps all 0 dropped of 1797; GPU ~10 ms/frame (of 14 ms), CPU ~1.5 cores at 40-60 Mbps and ~2 at 100;
  app 1.12-1.16 GB (rav1g ~710 MiB with the player's output textures). Well inside the envelope.
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
| HW decoder (fork, FFmpeg d3d11va) | ~290 MiB at 4K | 480 MiB | FFmpeg's default is 4 + 16 frames for HEVC/H.264 (20 = 478 MiB at 4K P010); size from the stream's DPB (4K HEVC levels: ≤ 6 refs) + current + frames in flight, ~10-12. Windows' own decoders (passthrough) can't be shrunk |
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

Parked with the per-stage GPU timestamps: whether reading decoder arrays in place (`ffmpeg-tune srv=on`)
saves GPU time. On the console it adds ~80-90 MiB the driver does not report (2026-11-21). The fork's
tests to pin it down: does the extra stay flat or grow over a long play; does reading one fixed slice add
~24 MiB; and do GPU timestamps around apply show any saving. bale_gpu creates its views per apply and
caches none.

Open: the HEVC pool floor with the fork's stage and Media Foundation holding frames; the GPU cost of the
stages other than rav1g; the ladder needs a real low-memory test.
