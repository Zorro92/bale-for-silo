# GPU HDR to SDR tone mapping and display-sized output

`bale_gpu.dll` exposes the independent tone-map **ABI 1** through
`native/bale-gpu/include/bale_tonemap.h`, plus the additive capability export
`bale_tonemap_output_formats()`. It is the production path for HDR originals on SDR displays
(see `docs/player-plan.md` "Console tone mapping by default"), and since 2026-11-17 also the
display-sized scaler for 4K SDR on smaller displays (see "4K SDR downscale on smaller displays").
The FFmpegInteropX fork (wrapper 2.1.0-bale.62 pinned) owns the stage around it.

## Capabilities (`bale_tonemap_output_formats`, 2026-11-17)

The ABI version stays **1**: the wrapper's loader requires `abiVersion() == 1`, so new behaviour is
announced by bits instead. Absent export = NV12 output only.

| Bit | Name | Meaning |
|---|---|---|
| 1 | `BALE_TONEMAP_OUTPUT_NV12` | NV12 output |
| 2 | `BALE_TONEMAP_OUTPUT_P010` | P010 output: 10-bit codes 64–940 / 64–960, `code << 6`, padding bits zero |
| 4 | `BALE_TONEMAP_OUTPUT_DOWNSCALE` | `config.reserved[0..2]` = visible source size; area downscale to `width`×`height` (≤4:1 per axis) |
| 8 | `BALE_TONEMAP_INPUT_NV12` | NV12 input, read as `code << 2` (exactly the 10-bit code) |
| 16 | `BALE_TONEMAP_SDR709` | BT.709 SDR frames (transfer 1/6/14/15, primaries/matrix 1): scale only |
| 32 | `BALE_TONEMAP_OUTPUT_NV12_DITHERED` | NV12 output is ordered-dithered to 8 bits (below); P010 unchanged |

Apply picks the output depth from the output texture's format and the input depth from the input
texture's format. The owner's rule is that processed video is always output in 10-bit, so the fork
asks for P010 whenever bit 2 is present (bale.36+). Phase 2 of the bit-depth rule (2026-11-19): on an 8-bit
SDR display Bale sets `bale_tonemap_output_depth=8` when bit 32 is present (not for AV1), and the fork's
stage pool and media type become NV12 from the first frame. The create self-test checks NV12 and P010
output at 1:1 (it never inherits a configured downscale source size).

## Dithered NV12 output (`BALE_TONEMAP_OUTPUT_NV12_DITHERED`, 2026-11-19)

NV12 output from the tone map and the area downscale is computed in fp32 exactly like P010 and only the
final 8-bit quantisation changes: plain rounding `floor(x + 0.5)` becomes `floor(x + t)`, with
`t = (bayer8(x, y) + 0.5) / 64` from a static 8x8 Bayer (ordered-dither) matrix at the output pixel, for
luma and chroma (Cr on the mirrored index `63 - bayer8`, so Cb and Cr do not step together; chroma uses
its own half-size plane position). The 64 thresholds are evenly spaced with mean 0.5, so a flat area keeps
its mean level, each code is under one code from the exact value, and a slow 10-bit ramp becomes a fine
fixed pattern instead of 8-bit bands.

Why this pattern: Bayer is computed from the pixel position alone (no noise texture or table), its
thresholds are exact in f32, so `cpu.rs` mirrors the shader exactly, and its period-8 structure sits at
the highest spatial frequencies, invisible at a one-code amplitude. There is **no per-frame offset**:
the pattern is tied to the output pixel, so a still picture produces identical frames (no crawl, no
temporal shimmer); only the content moves. P010 output stays plainly rounded, bit for bit as before.
Tests: `cpu.rs` (permutation, mean error ~0 and more local levels than plain rounding on a smooth
ramp) and WARP (`nv12_output_is_dithered_like_the_reference_and_p010_is_unchanged`, Windows CI only).

## Area downscale in display-linear light

Owner's choice (2026-11-17), "area downscaling in linear light". Each output pixel averages the
tone-mapped, display-linear BT.709 RGB of the source pixels its footprint covers, weighted by the
overlap of each source pixel's unit square, then encodes (inverse BT.1886) and quantizes **once**.
At 1:1 the footprint is exactly one pixel with weight 1, so unscaled output is unchanged. Chroma
uses the same left-sited [1 2 1]/4 × [1 1]/2 filter on the output grid. At 2:1 the cost is about the
same as unscaled output (four samples per output pixel, a quarter as many pixels); there is no
intermediate texture and no CPU work. `shaders/tonemap_common.hlsli` `output_pixel` and
`src/tonemap/cpu.rs` `reference_codes` implement it identically. CPU tests cover validation,
uniform pictures and linear-light blending; the WARP test compares GPU and CPU at 2:1, 2.4:1 and 3:1.

## BT.709 SDR scale-only pass

With `SDR709`, BT.709 SDR frames decode through a **sign-preserving** BT.1886 curve, skip the tone
curve and gamut conversion and are not clipped, so saturated Y'CbCr outside 0..1 RGB survives;
only the final quantization limits the range. A 1:1 pass returns the source codes within rounding
(`bt709_sdr_scale_only_round_trips_at_one_to_one`). The encode curve is sign-preserving for every
mode, which is identical to the old clamp inside 0..1, where every HDR and BT.2020 result lies.
BT.2020 SDR keeps its gamut conversion and clip.

## Ownership and GPU work

Create on the decode thread with the MF D3D11 device while holding its multithread protection.
Serialize create/apply/destroy and keep textures unmapped and available for all queued work.
Create retains the device and runs a fixed 16×8 PQ self-test containing black, 100/203/1000/4000
nits and saturated source colours. It checks both input ranges and direct/copy input paths
against the Rust reference, with a tolerance of three NV12 codes (twelve P010 codes). A mismatch returns
`BALE_TONEMAP_E_SELFTEST` (`0x8004BA01`, FACILITY_ITF) and a null handle.

Apply takes the caller's immediate context and a P010 (or, with INPUT_NV12, NV12) array subresource,
reads only the visible source extent, and writes a caller-owned, exactly output-sized NV12 or P010
texture with RT|SRV binds. A
non-SRV decoder texture is copied with CopySubresourceRegion into a cached SRV texture of the same format;
array SRVs select the requested slice directly. Two ps_5_0 draws use D3D11.1 R8_UNORM/R8G8_UNORM (NV12) or R16_UNORM/R16G16_UNORM (P010)
plane RTVs. All validation, allocations and view creation precede output writes. The context's
pipeline state is restored. Apply does not flush or perform readback. Input synchronization,
flushing and asynchronous device-loss handling belong to the caller. Destroy releases resources;
null is a no-op. No tone-map call logs anything.

## Colour and server parameter semantics

The binding-free `native/bale-gpu/shaders/tonemap_math.hlsli` contains the independently written
math: ST 2084 PQ, BT.2390's Hermite EETF, Hable's published curve, source range/matrix decoding,
BT.2020→709, linear gamut clipping, SDR transfer and limited-range quantization. It uses ordinary
fp32 scalar/vector operations supported by both SM5 and SM6. Resource sampling and bindings live
in `tonemap_common.hlsli`; neither is part of the reusable math include. Rust's
`src/tonemap/cpu.rs` mirrors the math function for function. CI compiles a resource-free harness
with FXC ps_5_0 and DXC ps_6_0, and WARP compares the live ABI output to the CPU reference.
No separate license is assigned to the math file by this change. WARP rejects P010 arrays
(including decoder-bound arrays), so its pixel comparisons exercise single-texture direct/copy
paths; descriptor tests cover decoder-array slice bounds. Real decoder-array execution still
needs hardware validation; no console was used for this implementation.

- Curve 1 uses the server's knee offset **1.0**, brightest **original source RGB** signal and
  `desat=0` (disabled), rather than mapping luminance or each channel independently. A scalar
  gain preserves channel ratios before gamut clipping. `source_peak_nits=1000` corresponds to
  tonemapx `peak=100`: that option is divided by ten, then scaled by 100/203. tonemapx uses an
  internal **203-nit** white. Consequently ABI `target_white_nits=100` represents that 203-nit
  EETF endpoint normalized to SDR 1; other target whites scale it by target/100. Positive desat
  uses that internal white as its unit, after primaries conversion, like tonemapx.
- Curve 2 follows `zscale=t=linear:npl=100,tonemap=hable`: scale by the brightest RGB component
  after desaturation, normalize Hable by the source peak, then convert primaries. Its units are
  target-white nits (100 by default). FFmpeg `tonemap=hable` defaults to **desat=2**; ABI
  **desat=0 explicitly disables it**, so the equivalent fallback includes `tonemap=hable:desat=0`.
- `zscale=t=bt709` defaults to display-referred conversion, the inverse BT.1886 EOTF (power 1/2.4).
  `sdr_encode` matches it. The include also provides the scene-referred `bt709_oetf` camera
  function for future consumers; the Bale pass does not use the camera OETF.
- P010 limited/full range is decoded explicitly. Both BT.2020 NCL (9/9) and BT.709 (1/1)
  primaries/matrix pairs work for PQ (16); HLG (18) needs BT.2020; SDR transfers 1/6/14/15 take
BT.2020 (gamut conversion) or, with SDR709, BT.709 (scale only). Frame HDR metadata is accepted
  but ignored; a positive fixed source peak is mandatory in v1.
- Chroma is left-sited, including unspecified/unknown locations: bilinear upsampling at luma
  coordinates (2x, 2y+0.5), then horizontal [1 2 1]/4 and vertical [1 1]/2 downsampling. Visible
  edges clamp without reading decoder padding. This deliberately specifies a compact filter;
  it does not reproduce zscale's default bicubic resampler or tonemapx's block chroma handling.

Differences from the server's implementation: fp32 math instead of its integer LUT/SIMD
quantization, ordered dithering for NV12 only (above), explicit linear gamut clipping, and the chroma filter above. Inputs
above the configured source peak clamp to the EETF endpoint rather than extrapolating its
cubic (which can roll highlights back). Fixed peak selection does not track frame metadata.
These are not claimed to be bit-identical server output.

Parameter semantics were inspected in Jellyfin FFmpeg commit
`64bf9db0fcc51454f129eb6f8d817ad43f476695`,
[tonemapx patch](https://github.com/jellyfin/jellyfin-ffmpeg/blob/64bf9db0fcc51454f129eb6f8d817ad43f476695/debian/patches/0053-add-simd-optimized-tonemapx-filter.patch),
[shared colour code / 203-nit reference](https://github.com/jellyfin/jellyfin-ffmpeg/blob/64bf9db0fcc51454f129eb6f8d817ad43f476695/debian/patches/0007-add-bt2390-eetf-and-code-refactor-to-opencl-tonemap.patch)
and [vf_tonemap](https://github.com/jellyfin/jellyfin-ffmpeg/blob/64bf9db0fcc51454f129eb6f8d817ad43f476695/libavfilter/vf_tonemap.c)
(LGPL-2.1+). The shader math is independently expressed from the standards and published curve,
not copied from those implementations. zimg's
[transfer selection](https://github.com/sekrit-twc/zimg/blob/master/src/zimg/colorspace/gamma.cpp)
explains the default display-referred BT.709 interpretation.

## Debug switches

- `scripts/bale-debug.sh tonemap-size W H` overrides the display-sized target for the next open
  (no args = automatic, from the HDMI mode).
- `scripts/bale-debug.sh tonemap-churn [N] [control]` runs bale_gpu's tone map alone N times on a
  fresh hardware device (create, five 4K applies, destroy; no MF or FFmpeg). It ran 40/40 on
  2026-11-17 and isolated the crash from bale_gpu.
- `scripts/bale-debug.sh tonemap-debug [noselftest,nostate]` skips the create self-test, or replaces
  the private ID3DDeviceContextState swap with explicit save/restore of what the draw touches.
- `bale-debug.sh mem` reports `D3D11 devices: watched N destroyed M alive K`: each engine device
  carries a private-data marker that only real destruction frees.

For the next FFmpeg player attempt, the curve can be overridden with one of:

```sh
scripts/bale-debug.sh ffmpeg-tune decoder=auto tonemap=bt2390
scripts/bale-debug.sh ffmpeg-tune decoder=auto tonemap=hable
scripts/bale-debug.sh ffmpeg-tune decoder=auto tonemap=off
scripts/bale-debug.sh ffmpeg-tune tonemap=default
```

The options map to `bale_tonemap=1`, `2`, `0`, or no key. Omitted keys retain their captured
values. Any explicit mode, including off, requires decoder=auto in the resulting tuning;
changing away from auto requires clearing tone mapping in the same update. Invalid updates
change nothing. The current package pin is read during compilation, so .25+ enables the switch
automatically for the pinned .26 wrapper. Earlier packages reject explicit modes with a clear
HTTP 400; default remains available. The pre-.25 wrapper's
[TryParseBaleOption](https://github.com/Zorro92/bale-ffmpeginteropx/blob/c3a9adbf2d81024a54063e7570ed9883afd5403c/Source/FFmpegMediaSource.cpp)
rejects unknown Bale keys with E_INVALIDARG rather than forwarding them to FFmpeg.
The tuning log and `FFmpeg tone map override` stats/where row report the captured mode.

## Memory ledger

`bale_gpu.dll` also exports the independent **memory ABI 1**, declared in
[`native/bale-gpu/include/bale_mem.h`](../native/bale-gpu/include/bale_mem.h) (C/cdecl).
It does not change the tone-map, grain, subtitle, zero-copy or engine ABI versions:

```c
typedef struct BaleMemEntry {
    uint32_t category;
    int32_t count;
    int64_t bytes;
    int64_t peak_bytes;
} BaleMemEntry;
uint32_t bale_mem_abi_version(void); /* 1 */
void bale_mem_account(uint32_t category, int64_t delta_bytes, int32_t delta_count);
uint32_t bale_mem_snapshot(BaleMemEntry* out, uint32_t capacity);
void bale_mem_reset_peaks(void);
uint64_t bale_mem_underflows(uint32_t category);
```

`BaleMemEntry` is 24 bytes, with fields at offsets 0, 4, 8, 16. The snapshot returns
the **total needed**, writes `min(capacity, total)` entries in ascending category order,
and accepts null `out` as a size query. Use capacity 64 to avoid a query/allocation race.
Every category ever accounted remains in snapshots, including zero rows after releases
and peak resets. Accounting uses only lock-free atomics, with no mutex/spin lock, allocation
or D3D calls, including when invoked from texture destruction under a D3D11 lock on any
thread. Snapshots use only atomic reads: counters are independently sampled during concurrent
updates, with peak floored at the observed bytes. Invalid IDs (>=64) are ignored. Bytes/count clamp independently at zero on
underflow and saturate at their signed MAX on overflow; the lifetime underflow counter
increments once per faulty call (saturating at UINT64_MAX), and is visible on `mem` rows.
Reset sets peak=current, preserving residency, history and underflow counters.

| ID | Component |
|---|---|
| 0 | Unassigned |
| 1 | bale_gpu tone map: constants, scratch, self-test textures, churn probe |
| 2 | bale_gpu film grain: GPU buffers/textures and probes |
| 3 | bale_gpu subtitle overlay: swap-chain buffers (including resize), atlas, bitmap/staging textures, buffers |
| 4 | bale_gpu engine target/staging textures (`engine.rs`) |
| 5 | bale_gpu zero-copy resources: D3D12 committed resources and private D3D11 landing/self-test textures |
| 6–15 | Reserved for bale_gpu |
| 16 | d3d11va decoder texture array (count = slices) |
| 17 | Stage output (tone map/downscale output) |
| 18 | dav1d upload pool + staging ring |
| 19 | rav1g output images |
| 20 | rav1g process device present (count 1, bytes 0) |
| 21 | d3d11va copy pool / rav1g direct-copy textures |
| 22 | rav1g decoder buffers (`device_size` per stream; count = streams) |
| 23–31 | Reserved for FFmpegInteropX |
| 32 | Bale real-player swap chain **(estimate)** |
| 33–63 | Reserved |

Count allocations at their allocator, once per underlying resource (swap-chain count is
its buffer count; category 16 counts slices and 22 counts streams), and pair additions
with final releases, including pooled residency.
Unknown-size residents can use bytes=0/count=1. The fork should resolve these exports at
runtime, check `bale_mem_abi_version()==1`, and skip accounting on older DLLs. This change
defines its ABI/IDs; fork instrumentation is maintained in the fork repository. Shared-resource opens,
views and imported caller textures must not add another charge. bale_gpu resources hold
a `Tracked` RAII guard in COM private data, so clones/views/context references keep their
charge until destruction. A marker on subtitle buffer zero covers all swap-chain buffers;
successful resize destroys the old marker before attaching the new one, while failed
resize leaves the old charge intact.

`scripts/bale-debug.sh mem [gc]` preserves the process totals and adds current/peak MiB,
counts and a counted total. `mem peaks-reset` sends `POST /mem?action=peaks-reset`.
Older DLLs print `GPU ledger: unavailable`. Category 32 is debug-only, follows successful
`EngineBackend` open/resize/close, uses the requested BGRA8/RGB10A2 (4 B/px) or RGBA16F
(8 B/px), and assumes **two buffers** because MF hides the count. MediaPlayer and standalone
debug presentation probes remain uninstrumented.

Sizes are **logical**: sum mip extents × bytes per pixel × array size (and MSAA samples),
NV12=1.5 B/px, P010/P016=3 B/px, buffers=ByteWidth; common DXGI formats are handled and
unknown ones use 4 B/px. Xbox tiling/alignment padding is not counted.

**One memory pool.** With the `hevcPlayback` capability (declared in Package.appxmanifest) the app gets
3328 MiB in a single pool that CPU heaps and D3D11/D3D12 resources both count against:
`MemoryManager.AppMemoryUsage` already includes every texture, and `GetExtendedMemoryInfo` reports the
graphics partition as 0 of 0 on the Series X (measured 2026-11-20; rav1g measured the same with its
test player, 1024 MiB without the capability). Microsoft's hevc-xbox page only says the partition exists
on Series X|S; games may get one, apps don't. So never add GPU memory to app memory, and the `mem`
remainder line is **app memory minus the counted total**: CPU heaps (managed and native), Media
Foundation, driver, rav1g internals, uninstrumented textures and padding. Estimates and snapshot timing
make it approximate.

**Measured limits (memprobe, Bale 0.1.708, Series X, hevcPlayback, 2026-11-20).** There is no ~1.2 GB
ceiling; that was only what the playback routes happened to use.

| Probe | Reached | Stopped by |
|---|---|---|
| `memprobe cpu` (touched native memory) | 3200 MiB probe, app 3251 of 3328 MiB | app limit (next step would pass 3328); level Low → Medium at ~1.7 GB, High at ~3.2 GB |
| `memprobe d3d11` (resident textures) | 2828 MiB GPU, app 2963 MiB | E_OUTOFMEMORY at the DXGI local budget, **2857 MiB** |
| `memprobe d3d12` (committed resources) | 2828 MiB GPU, app 2962 MiB | E_OUTOFMEMORY at the same 2857 MiB budget |
| `memprobe mixed cpu=1000` | 1000 MiB CPU + 1928 MiB GPU, app 3029 MiB | app limit: CPU and GPU share the 3328 MiB pool |

- The DXGI local budget with hevcPlayback is 2857 MiB (rav1g's ~1210 MiB was the package without it);
  the non-local segment group is empty (budget 0), and AvailableForReservation reads 1507.8 MiB.
- No paging before the failure: the fixed 64 MiB GPU copy stayed at 1.70–1.77 ms from 128 MiB to 2.8 GB.
- D3D11 and D3D12 report the same budget and fail at the same size.
- So the limits are: everything ≤ 3328 MiB of app memory, of which GPU-resident memory ≤ ~2857 MiB.
  Earlier symptoms near 1.2 GB (rav1g stutter, the tone-map route's failures) had other causes
  (memory bandwidth, the per-open msvproc leak), not a quota.
- Device Portal's "total" (~1.6 GB) is the system partition's physical pages, not the app's quota; 4K AV1
  on dav1d held ~2000 MiB (working set 1.5 GB, commit 2.0 GB) with 0 dropped frames.

**Debug memory limit probe.** Leave the player, then run one of:

```sh
scripts/bale-debug.sh memprobe cpu
scripts/bale-debug.sh memprobe d3d11 step=100 max=3400
scripts/bale-debug.sh memprobe d3d12 step=100 max=3400
scripts/bale-debug.sh memprobe mixed cpu=1000 step=100 max=2400
```

These call `GET /memprobe?mode=...&step=...&max=...[&cpu=...]` in a `DEBUG_SERVER` build. Sizes
are binary MiB: step defaults to 100, max to 3400, and mixed's held CPU allocation to 1000.
Step/max accept 1–4096 with step <= max; GPU modes require max >= 128. `cpu=0..4096` is mixed-only.
Duplicate/unknown parameters are rejected; an open player or another running memory/media probe
returns HTTP 409. The entire run stays on a dedicated worker, including native creation/destruction.
The script allows 300 s. Do not open a player or run other GPU diagnostics during a probe.

CPU allocates native blocks, writing one byte every 4 KiB. Mixed holds those touched blocks while
running D3D11. Each GPU run uses the engine/churn's default hardware adapter and its own device
references; D3D12 uses that exact adapter too (Windows can reuse an existing D3D12 device for an
adapter). DEFAULT RGBA8 textures are at most 4096×4096 (64 MiB), with shorter textures making exact
MiB steps. Each new texture is cleared and GPU completion is awaited. The first GPU row (`step=0`)
allocates the fixed 128 MiB source/destination pair, warms a copy, and establishes the baseline.
Subsequent rows add step MiB (the last can be partial), then time one fixed 64 MiB copy using GPU
timestamps, without another warmup that might mask paging. **GPU max includes that 128 MiB pair; mixed's CPU hold is additional.**
All probe resources bypass the memory ledger. `cpu_mib`/`gpu_mib` report owned logical bytes, not
alignment, query/device or driver costs; those costs appear in app usage and DXGI CurrentUsage.

The response and app log contain a header, one row per CPU/GPU step, a `STOP reason=...` row,
and a `freed cpu_mib=0 gpu_mib=0 app_mib=...` row after all owned resources/device references are
released. Every step includes app usage/limit/level, elapsed step time and memory-event counts
(limit changing and usage increased). GPU rows add `copy_ms`, HRESULT/stage, device-removed reason,
and `LOCAL[...] NON_LOCAL[...]`, each with budget, current usage, available reservation and current
reservation in MiB. DXGI query failures show their HRESULT rather than misleading zero budgets.
Stage 1 is texture creation, 2 is write/completion wait, 3 is timed copy; invalid/disjoint timestamps
stop at stage 3 with an error rather than treating an unavailable measurement as zero.

Stops are max, allocation/create failure, device removal, a copy taking **more than 5×** the baseline,
or app usage within one configured step of its current limit (also checked before GPU setup).
GPU query/fence waits are bounded at 15 s. `finally` releases allocations even on reported failure;
a process terminated by the OS cannot return a final report, so each completed row is logged as it
happens. The freed reading is immediate; driver accounting may settle later. A fall in LOCAL usage,
rising NON_LOCAL usage or a copy-time jump near the LOCAL budget is evidence of paging/demotion,
not proof of a hard allocation cap. Compare CPU vs GPU runs and mixed's shifted threshold with the
same app state and input-idle timing; the debug HTTP request itself injects no controller input.
The probe does not change `hevcPlayback` or the production allocator, and cannot establish a console
limit until run there by the owner.

Console readings, Bale 0.1.705 (wrapper bale.62), 1080p SDR display, 40 s into playback:

| Route | App memory | Counted | Largest lines |
|---|---|---|---|
| 4K HEVC HDR, console tone map (no rav1g device) | 1201.7 MiB | 532.7 MiB | 16 decoder array 20 slices 478.1 MiB; 1 tone map 23.9; 32 swap chain 15.8 (est.); 17 stage output 5× 14.8 |
| Same, rav1g device resident (after an AV1 play) | 1244.7 MiB | 532.7 MiB | same; 20 rav1g device present |
| 4K AV1 on rav1g | 1117.1 MiB | 687.6 MiB | 22 rav1g decoder buffers 487.8 MiB; 19 rav1g output images 4× 94.9; 17 stage output 12× 71.2; 3 subtitles 17.8 |

Every line returns to 0 after stop except 20, the rav1g process device, which stays once created
(idle app memory 240 → 280 MiB). That resident device is the ~43 MiB that takes the 4K tone-map route
over ~1.2 GB; the biggest single item on that route is the 20-slice d3d11va decoder array.
