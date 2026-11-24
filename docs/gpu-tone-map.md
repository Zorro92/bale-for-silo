# GPU HDR to SDR tone mapping and display-sized output

`bale_gpu.dll` exposes the independent tone-map **ABI 1** through
`native/bale-gpu/include/bale_tonemap.h`, plus the additive capability export
`bale_tonemap_output_formats()`. It is the production path for HDR originals on SDR displays
(see `docs/player-plan.md` "Console tone mapping by default"), and since 2026-11-17 also the
display-sized scaler for 4K SDR on smaller displays (see "4K SDR downscale on smaller displays").
The FFmpegInteropX fork (wrapper 2.1.0-bale.77 pinned) owns the stage around it.

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

**Bit 64 is retired (2026-11-24, owner).** It let apply read shader-readable d3d11va decoder arrays in
place instead of copying them; on the console (Bale 0.1.712, 4K HDR HEVC tone map) that saved the 23.9 MiB
scratch copy, but app memory still rose from 1185/1191 to 1255/1247 MiB, so the shader reads cost ~80-90 MiB
the driver does not report (24 fps, 0 drops either way). Wrapper bale.76 no longer asks for such pools and
bale_gpu copies every decoder array first again. The bit stays unused, since wrappers bale.64 to bale.75
read it as permission for shader-readable decoder pools.

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
At 1:1 the footprint is exactly one pixel with weight 1. Output chroma (left-sited) mixes the output
pixels' linear RGB over a [1 2 1]/4 × [1 1]/2 footprint and encodes that once (below). There is no CPU
work. `shaders/tonemap_common.hlsli` `area_linear` and `src/tonemap/cpu.rs` `reference_codes` implement
it identically. CPU tests cover validation, uniform pictures and linear-light blending; the WARP test
compares GPU and CPU at 2:1, 2.4:1 and 3:1.

### Closer to true area scaling in linear light (2026-11-22, part 1)

The xav encoder's guide documents a downscale method it calls "true area scaling in linear light"
(xav `src/guide.txt`, section "Perfect/True Area Scaling in Linear Light"; MIT non-AI licence, so only
the documented method was read, none of its code). Bale already did its core: exact fractional area
weights over display-linear light, encoded once. Part 1 moves three cheap points of that method in:

1. **Output chroma in linear light.** Before, the chroma pass filtered the output pixels' *encoded*
   Cb/Cr. Now it mixes their linear RGB over the 2-luma-pixel footprint centred on the left-sited
   chroma position (1/4, 1/2, 1/4 across columns 2i−1..2i+1, 1/2, 1/2 over rows 2j, 2j+1), then clips
   and encodes once and keeps `.yz`. Averaging encoded Cb/Cr darkens and desaturates saturated fine
   detail (red/green texture turns brownish); linear mixing keeps its hue and brightness. The staged
   intermediate now holds the unclipped linear average (`area_value`), the luma pass encodes it and the
   chroma pass mixes six of them, so the encode moves rather than multiplies (one extra encode per
   chroma sample, a quarter of the output pixels). Luma is the same operations on the same value in
   both paths.
2. **Input chroma siting from the stream.** `BaleToneMapFrameInfo.chroma_location` (FFmpeg's
   AVChromaLocation, filled by the fork) now sets where the bilinear chroma upsampling reads: LEFT (1)
   and UNSPECIFIED (0) as before (co-sited horizontally, halfway between rows); CENTER (2) halfway both
   ways; TOPLEFT (3) co-sited both ways; TOP (4), BOTTOMLEFT (5) and BOTTOM (6) their positions too;
   unknown values read as left. Most HEVC/AVC is left or unspecified; UHD Blu-ray HDR10 HEVC is
   specified as TOPLEFT (chroma_sample_loc_type 2) and AV1 can signal colocated (TOPLEFT), where left
   siting shifted the chroma half a luma row down. Which values real library files carry is not yet
   checked (the fork passes what FFmpeg reports).
   The cbuffer carries it in `output.w` bits 0-7.
3. **Clip once, late.** Per-source-pixel values are no longer clipped: PQ, HLG and BT.2020 SDR decode
   sign-preserving (`sign(x)·f(|x|)`, magnitude capped at 1, 1 and 1.5), the tone curve's result and the
   BT.2020→709 conversion stay unclipped (negative or >1 channels for out-of-gamut colours), and
   `finish_linear` clips to 0..1 once per output pixel and once per chroma footprint, just before the
   encode. A sub-black or out-of-gamut pixel thus pulls its neighbourhood's average the way its light
   does instead of counting as 0/1. BT.709 SDR keeps its scale-only, never-clipped pass. The curves
   clamp their own inputs at 0 and the peak, the scale divides by `max(sig, 1e-6)` and `desaturate`
   guards its divisions, so negative channels cannot produce NaN (`extended_math_stays_finite_...`).

Expected visual differences: finer saturated colour detail (red/green, coloured text, foliage) stays
brighter and more saturated after a downscale; TOPLEFT/CENTER-sited streams lose a half-row chroma
offset (colour edges line up with luma edges); near out-of-gamut edges a few codes of difference. Flat
in-gamut content is unchanged (`methods_agree_on_a_flat_in_gamut_picture`). Luma changes only where a
source value left 0..1 before the old clip (PQ/HLG/BT.2020 SDR); BT.709 SDR luma is bit-identical.

Not in part 1: cubic (or exact-area) chroma *upsampling* of the input (Catmull-Rom since 2026-11-24,
see "Cubic input chroma" below), and averaging HDR light before the tone curve. The old math stays
available on the console as `tonemap-debug gammachroma` (`cpu::Method::Legacy`): per-pixel clip, left
siting, encoded-chroma filter; the WARP tests compare both methods against the CPU reference.

### Cubic input chroma (2026-11-24, part 2, first half)

The input chroma is now upsampled with a separable 4x4 **Catmull-Rom** cubic (Keys, a = −0.5) instead of
bilinear: the same encoded Cb/Cr samples at the same siting (part 1's `chroma_offset`), only the filter
changes (`chroma_cubic` in `tonemap_common.hlsli`, `ChromaPlane::cubic` in `cpu.rs`; rows first, then the
column). Bilinear softens chroma detail; Catmull-Rom passes through the samples and reproduces linear
and quadratic chroma exactly, so fine colour detail and colour edges (coloured text, saturated edges,
foliage) stay sharper and line up better with luma. Its negative lobes overshoot at a sharp chroma edge
(about 7% of the step per axis), so the result is clamped to the valid code range (64-960 limited,
0-1023 full, in 10-bit codes) and overshoot never leaves the legal chroma range. There is no
neighbourhood (anti-ringing) clamp, so a faint fringe at hard colour edges stays possible. The "average
HDR light before the tone curve" half of part 2 is not done.

Exactness: a luma pixel's chroma position has the fraction 0, 1/4, 1/2 or 3/4 per axis, where every
weight is a multiple of 1/128 (exact in fp32), and with integer codes every sum is exact too unless a
partial sum passes 1024 (near-white chroma, which the clamp then cuts), so the GPU and `cpu.rs` agree
on the upsampled chroma itself, not just within the tolerance. NV12 input is filtered in 8-bit codes and
scaled by 4 once, which is the same value.

Fast path: on an axis where the visible source is exactly 2 or 4 times the output (4K → 1080p, both
axes), every lane of a wave reads the same chroma phase in each step of the area loop, so a uniform
branch can skip the zero-weight taps of a co-sited phase (fraction 0, weights 0 1 0 0): 4 loads when
one axis is co-sited, 1 when both are, 16 otherwise. `cpu::cosited_bits` sets it per axis (cbuffer
`output.w` bits 10/11); at 1:1, 3:1 or fractional ratios lanes disagree, so the bits stay off and the
full filter runs without the branch's divergence. Same codes either way
(`cubic_chroma_fast_path_is_exact_and_both_filters_match_the_reference`, WARP).

Cost (per evaluated source pixel; FXC ps_5_0 DXBC of `tonemap_area_ps`: 337 → 511 static instruction
slots): bilinear read 4 chroma loads and ~50 instructions; the cubic 16 loads and ~110 on the full path,
~52 (4 loads) with one axis co-sited, ~39 (1 load) with both. Against the ~220-instruction loop
body (plus its ~15 transcendentals) that is about **+27%** of the per-sample work on the full path,
**+14%** for left-sited (or unspecified) streams at 2:1 and **+6%** for top-left-sited ones (UHD Blu-ray
HDR10) at 2:1. Chroma texture reads per output pixel: 4K → 1080p 16 → 40 (left), 25 (top-left), 64
(center); 4K → 720p (3:1, 9 source pixels per output pixel) 36 → 144; 1:1 (2.5 evaluations per output
pixel, see "Pass structure") 10 → 40. No new memory. Expected: roughly +0.3–0.6 ms of the stage at 4K →
1080p for left-sited HEVC (if the stage is ~4–4.5 ms as estimated there), and up to +1 ms on the full
path; more if the area pass turns out texture-bound rather than ALU-bound on the console (11 instead of 5
loads per source pixel at 2:1 left, 17 on the full path). Not measured on the console yet. Limits it is
sized for: the ~1.86 TFLOP/s app GPU slice and the ~2–2.5 ms budget for non-decode GPU work at 60 fps
(GPU compute is not slowed in the input-idle state); it adds no memory (3328 MiB app, 2857 MiB DXGI
budget) and no CPU work. The co-sited fast path exists to keep the main case (4K → 1080p) close to
that budget.

`tonemap-debug bilinearchroma` keeps the bilinear read for A/B (cbuffer `output.w` bit 9,
`cpu::Options::bilinear_chroma`). Tests: CPU (`catmull_rom_weights_are_exact_at_the_quarter_phases`,
`cubic_chroma_is_exact_on_curves_and_clamped_at_sharp_edges`,
`reference_upsamples_chroma_with_catmull_rom_unless_bilinear_chroma`); WARP: both filters against the
reference at 1:1, 2:1, 4:1 and 2.4:1/3:1, every siting class, P010/NV12 in and out, the fast-path
identity and `cubic chroma:` lines counting the codes the cubic changes; the staged-identity test and
the create self-test run the cubic.

### Pass structure: each output pixel once (2026-11-22)

`output_pixel(o)` is the expensive part: for every covered source pixel a full `sample_linear` (input
chroma, bilinear then and Catmull-Rom since 2026-11-24; decode, tone curve, gamut; several `pow` per
channel). Before, both plane passes evaluated it
from scratch: luma once per output pixel and chroma six times per chroma sample (the [1 2 1] × [1 1]
taps), so 4K → 1080p cost 4 × 2.07 M (luma) + 24 × 0.52 M (chroma) = **20.7 M** `sample_linear` for
8.3 M source pixels. On the console that was ~10 ms of GPU per 4K60 frame and made 4K60 AV1 GPU-bound
at ~48 fps (stage off: 57.5 fps).

When the output is smaller than the visible source the stage now runs three passes:

1. `tonemap_area_ps` writes `area_value(o)` for every output pixel into an output-sized
   `R32G32B32A32_FLOAT` intermediate: exact fp32, nothing rounded. Since part 1 above that is the
   unclipped linear average (`gammachroma`: its encoded Y'CbCr, as before).
2. The luma pass (`tonemap_luma_ps` built with `BALE_STAGED`) loads it once, clips and encodes.
3. The chroma pass (`tonemap_chroma_ps` with `BALE_STAGED`) loads the same six neighbours with the
   same weights and summation order, then clips and encodes the mix; `staged_value` clamps the
   neighbour exactly like `area_linear`'s own `o = clamp(...)` (edge pixel, never `Load`'s zero).

Dithering and quantization are unchanged. 4K → 1080p now costs **8.3 M** `sample_linear` (one per
source pixel, −60%); 3:1 (4K → 720p) the same; 1.5:1 (1080p → 720p) 3.7 M instead of 9.2 M. The price is
one write and about one read of the intermediate per frame (~33 MB each at 1080p, the chroma pass's
overlapping loads mostly hit the cache). Expected: the stage's ~10 ms at 4K → 1080p falls to roughly
4–4.5 ms (ALU share scaled by 0.4, plus well under 1 ms of traffic); still above the ~2 ms budget, so
cheaper per-sample math is the next step. Not measured on the console yet.

At 1:1 the old two-pass path stays: each output pixel is already evaluated once by luma, and chroma's
six taps reuse no work, but a 4K fp32 intermediate would cost ~127 MiB.

Intermediate memory (ledger category 1, created with the stage and released with it; only when
downscaling): 1920×1080 31.6 MiB, 1280×720 14.1 MiB (a 4K output from an 8K source would be 126.6 MiB).
It fits the measured limits (app 3328 MiB, DXGI budget 2857 MiB; the rest of Bale holds ~0.15 GB on the
GPU at a 1080p display).

Identity: the WARP test `staged_downscale_matches_the_per_plane_path` renders PQ, BT.709 SDR and HLG
frames (P010 and NV12 input, NV12 and P010 output) at 3840×2160 → 1920×1080, 3840×2160 → 1280×720 and
1920×1080 → 1280×720 through both paths on one handle and prints `staged identity:` lines with the
count of differing codes and the largest difference (target 0; fxc may compile `area_linear`
differently in the area shader than inside the plane shaders, so at most one code is allowed), for the
current math and for `gammachroma`. `method luma:` lines count the luma codes the current math changes
against `gammachroma` (asserted 0 for BT.709 SDR). The per-plane path stays available on the console as
the debug switch `nostaged`.

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
decoder array (with or without an SRV bind), or any input without an SRV bind, is copied with
CopySubresourceRegion into a cached SRV texture of the same format; other SRV-bound inputs are read in
place, array SRVs selecting the requested slice directly. Two ps_5_0 draws use D3D11.1 R8_UNORM/R8G8_UNORM (NV12) or R16_UNORM/R16G16_UNORM (P010)
plane RTVs. All validation, allocations and view creation precede output writes. The context's
pipeline state is restored. Apply does not flush or perform texture readback (debug timestamp queries
use asynchronous metadata readback only). Input synchronization, flushing and asynchronous device-loss
handling belong to the caller. Destroy releases resources;
null is a no-op. No tone-map call logs anything.

## Colour and server parameter semantics

The binding-free `native/bale-gpu/shaders/tonemap_math.hlsli` contains the independently written
math: ST 2084 PQ, BT.2390's Hermite EETF, Hable's published curve, source range/matrix decoding,
BT.2020→709, linear gamut clipping, SDR transfer and limited-range quantization. It uses ordinary
fp32 scalar/vector operations supported by both SM5 and SM6. Resource sampling and bindings live
in `tonemap_common.hlsli`; neither is part of the reusable math include. Rust's
`src/tonemap/cpu.rs` mirrors the math function for function. CI compiles a resource-free harness
with FXC ps_5_0 and DXC ps_6_0, and WARP compares the live ABI output to the CPU reference.
Before a push, `scripts/check-fxc.sh` compiles every shader (the `SHADERS` list in `build.rs`, same
flags) plus that FXC harness with Microsoft's fxc.exe under Wine on Linux, so FXC-only `/WX` errors such
as X3571 (`pow` of a base FXC cannot prove non-negative: write `abs()` outermost in the `pow` call) show up in
about 30 s instead of failing CI. The pre-push hook runs it when shaders change.
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
BT.2020 (gamut conversion) or, with SDR709, BT.709 (scale only). Frame HDR metadata sets the PQ
  source peak (see "Source peak from the stream's HDR metadata"); the config peak is the fallback, and
  0 means 1000 nits (older DLLs reject 0).
- Input chroma is upsampled with a 4x4 Catmull-Rom cubic, clamped to the valid code range, from the
  stream's chroma location (left for unspecified and unknown values; see "Closer to true area scaling"
  and "Cubic input chroma" above; debug `bilinearchroma`: bilinear). Output chroma is left-sited: the output
  pixels' linear RGB mixed by horizontal [1 2 1]/4 and vertical [1 1]/2 around (2x, 2y+0.5), then
  clipped and encoded once. Visible edges clamp without reading decoder padding. This deliberately specifies a compact filter;
  it does not reproduce zscale's default bicubic resampler or tonemapx's block chroma handling.

Differences from the server's implementation: fp32 math instead of its integer LUT/SIMD
quantization, ordered dithering for NV12 only (above), explicit linear gamut clipping, and the chroma filter above. Inputs
above the source peak clamp to the EETF endpoint rather than extrapolating its
cubic (which can roll highlights back). The PQ source peak follows the stream's MaxCLL / mastering
metadata (next section), where the server uses its fixed `peak`; streams without metadata get the same
fixed 1000 nits. These are not claimed to be bit-identical server output.

Parameter semantics were inspected in Jellyfin FFmpeg commit
`64bf9db0fcc51454f129eb6f8d817ad43f476695`,
[tonemapx patch](https://github.com/jellyfin/jellyfin-ffmpeg/blob/64bf9db0fcc51454f129eb6f8d817ad43f476695/debian/patches/0053-add-simd-optimized-tonemapx-filter.patch),
[shared colour code / 203-nit reference](https://github.com/jellyfin/jellyfin-ffmpeg/blob/64bf9db0fcc51454f129eb6f8d817ad43f476695/debian/patches/0007-add-bt2390-eetf-and-code-refactor-to-opencl-tonemap.patch)
and [vf_tonemap](https://github.com/jellyfin/jellyfin-ffmpeg/blob/64bf9db0fcc51454f129eb6f8d817ad43f476695/libavfilter/vf_tonemap.c)
(LGPL-2.1+). The shader math is independently expressed from the standards and published curve,
not copied from those implementations. zimg's
[transfer selection](https://github.com/sekrit-twc/zimg/blob/master/src/zimg/colorspace/gamma.cpp)
explains the default display-referred BT.709 interpretation.

## Source peak from the stream's HDR metadata (2026-11-24)

Before, every PQ stream was tone-mapped from the config's fixed `source_peak_nits` (the fork sets 1000,
tonemapx `peak=100`): a 4000-nit grade clipped everything above 1000 nits, and a 600-nit grade was mapped
as if it reached 1000 (its highlights dimmer than needed). Apply now picks the peak per frame from the
`BaleToneMapFrameInfo` metadata the fork already fills (`cpu::source_peak`):

1. MaxCLL (`has_content_light` and `max_cll > 0`), else
2. the mastering display maximum (`has_mastering`, `mastering_max_nits` finite and above 0), else
3. the config peak (`source_peak_nits`; 0, accepted since this change, means 1000).

A MaxCLL above the mastering maximum is broken metadata (no pixel is brighter than the display it was
graded on), so the mastering maximum is used then. A metadata peak is clamped to 400-10000 nits (lower is
broken metadata, 10000 is PQ's ceiling); the config peak is used as configured. PQ only: HLG keeps the
config peak (its OOTF already renders for the 1000-nit nominal display) and SDR has no tone curve.

Cost: no GPU work (the peak is the cbuffer's `params.x`). The metadata is per stream in practice, so the
peak changes only when the stream's metadata does, and apply now uploads the constant buffer only when a
constant changes (normally once per stream, after the self-test's), no longer on every frame. The CPU
reference applies the same rule, so the self-test and every WARP comparison cover it;
`pq_peak_follows_the_stream_metadata_and_constants_upload_only_on_change` (WARP, Windows CI) checks each
source against the reference, `fixedpeak`, the report and the upload count, and CPU tests cover every
branch and clamp.

Log: no tone-map call logs (see "Ownership and GPU work"), so the additive export `bale_tonemap_peak_read`
(every build, `BaleToneMapPeakInfo` in the header) reports the peak the most recently created stage
applies, its source and the metadata it saw. Debug builds read it in the one-second stats refresh: the
**Tone-map peak** stats row, and one log line per stream (again only when the peak changes), for example
`Bale tone map peak: 1500 nits from maxcll (PQ · MaxCLL 1500 · mastering 4000 · config 1000) · stage 3`.
The sources are `maxcll`, `mastering` and `config`. The record sits behind a mutex that apply takes only
on a change; the read only tries it (S_FALSE when busy), so it never waits, and neither side touches D3D
or the wrapper's source lock. The export's presence also tells a caller that the metadata peak exists.

Expected visual difference: streams mastered above 1000 nits keep highlight detail between 1000 nits and
their peak instead of clipping it, with slightly darker mid-tones (the curve fits a larger range into
SDR); streams peaking below 1000 nits get brighter highlights; streams without metadata look as before.
`tonemap-debug fixedpeak` restores the config peak for A/B. Not verified on the console yet, and which
metadata real library files carry is not checked either.

## Backlog (from a feature comparison with MPC Video Renderer, 2026-11-24)

Compared from MPC-VR's README, wiki, history.txt and release notes only (GPL-3.0: its code and shaders
were not read). Bale's stage is already ahead on downscaling (linear-light area average; MPC-VR documents
only "bicubic sharp") and level on dithering (both reduce 10-bit to 8-bit; Bale's ordered pattern is fixed
to the output pixel, so stills don't shimmer). Owner asked for these to be on the backlog:

- [x] **Peak from the stream's HDR metadata.** Use MaxCLL / mastering display max luminance (with sane
  clamps) for `source_peak_nits` instead of the fixed 1000 nits; one value per stream, no GPU cost. MPC-VR
  added metadata-based conversion and a per-frame "Measured" peak in 2026; a measured peak needs an extra
  GPU reduction pass and is a later option only if the HDR GPU budget allows. Implemented 2026-11-24, not
  console-verified: see "Source peak from the stream's HDR metadata".
- [x] **Cubic chroma upsampling** (Catmull-Rom class) of the input chroma: xav part 2 (see part 1 above).
  MPC-VR offers nearest / bilinear / Catmull-Rom with chroma siting. Implemented 2026-11-24 (Catmull-Rom
  at the stream's siting; the "average HDR light before the tone curve" half is not), not
  console-verified: see "Cubic input chroma".
- [x] **Check deinterlacing on the FFmpeg routes** (2026-11-24, console). 20 s 1080i (top field first) H.264 and
  MPEG-2 clips made from the open test set, played on a 1080p display with `ffmpeg-tune decoder=default|auto|software`
  (system decoder, FFmpeg D3D11, FFmpeg software): no combing on moving edges in any console capture, while the raw
  woven frame shows it. The fork marks interlaced samples (MFSampleExtension_Interlaced, field order) and Media
  Foundation's processor deinterlaces them. Not covered: interlaced sources through the stage (HDR, or larger than
  the display), which practically don't exist.
- [x] **SDR display brightness setting** (`target_white_nits`, 2026-11-24; console: `ffmpeg-tune white=200` logged `tone map SDR white=200 nits` on the HEVC stage route and the rav1g display-pass route, playback normal; the brightness itself still needs the owner's eyes). Settings ›
  Playback › HDR ON SDR TVS › **SDR Brightness** (an owner-requested deviation: the official apps never tone-map on the
  device) picks the SDR white the curve maps to: **Default (100 nits)**, as before, or 80, 120, 150, 200, 250 or 300 nits.
  BT.2390 ends at 2.03 times that white, so a higher value maps more of the source into the SDR range and the picture
  looks brighter (for brighter SDR TVs or a bright room); 80 is dimmer. Device-local and persisted
  (`AppSettings.SdrWhiteNits`: LocalSettings int nits, Default stored as no value, unknown or wrongly typed values read
  as Default); each playback attempt reads it once and keeps it through replans and remounts. `FfmpegSource.ApplySdrWhite`
  sends it as the fork key `bale_tonemap_white_nits` (wrapper bale.77, the same value for Bale's stage and rav1g's display
  pass) in release and debug builds, only on mounts that configure the tone map (`bale_tonemap`: HDR10/HLG/DV base layer
  on an SDR display, the SDR downscale, where the scale-only pass applies no curve, and the rav1g route when it
  tone-maps), after the debug overrides that add or remove the stage. Default leaves the key unset; an open that sends
  a white logs `player: tone map SDR white=N nits`. Debug `ffmpeg-tune white=80..400|default` overrides it for the next
  attempt (see "Debug switches"). The setting changes only the white, never the source peak the curve starts from.
- [ ] **Possibility: Dolby Vision tone mapping** (MPC-VR maps profiles 5, 8.1 and 8.4 with the DV dynamic
  metadata, ST 2094-10 L1/L2). Today's owner rule is DV passthrough only, base layer on SDR; this would be
  the owner's decision to change that rule, and it would make profile 5 playable on SDR displays.

Not planned: more tone curves (ACES, Reinhard, Mobius, spline; BT.2390 stays the standard) and GPU-heavy
upscalers (Jinc, vendor Super Resolution); 1080p to 4K stays with the console's scaler.

### Console check of the 2026-11-24 changes (Bale 0.1.749)

- **Peak from metadata:** a 1080p HDR HEVC film with MaxCLL 4057 and mastering max 1000 logged `peak: 1000 nits from
  mastering`; a 4K HDR VP9 film with MaxCLL 1184 and mastering max 4000 logged `1184 nits from maxcll`. The open
  HDR test clips carry no HDR metadata and log `from config` (1000).
- **Catmull-Rom chroma cost:** 4K60 HEVC HDR10 60M, two pairs, engine 0 load 61.0 / 57.5% (cubic) vs 53.8 / 51.0%
  (`bilinearchroma`): about +6.5-7 points, roughly 1.1 ms per 4K frame at 60 fps (estimate was 0.3-0.6 ms); 0 missed
  frames either way. The stage runs on hardware-decoded HEVC/VP9 here, where engine 0 has room.
- **Picture:** not judged yet (Device Portal's screenshot doesn't capture the HDR video plane); needs the owner's eyes.

## Debug switches

- `scripts/bale-debug.sh tonemap-size W H` overrides the display-sized target for the next open
  (no args = automatic, from the HDMI mode).
- `scripts/bale-debug.sh tonemap-churn [N] [control]` runs bale_gpu's tone map alone N times on a
  fresh hardware device (create, five 4K applies, destroy; no MF or FFmpeg). It ran 40/40 on
  2026-11-17 and isolated the crash from bale_gpu.
- `scripts/bale-debug.sh tonemap-debug [noselftest,nostate,nostaged,gammachroma,fixedpeak,bilinearchroma]`
  skips the create self-test, replaces the private ID3DDeviceContextState swap with explicit save/restore
  of what the draw touches, (`nostaged`) skips the downscale intermediate so each plane pass evaluates the
  area sum itself (the pre-2026-11-22 path, for GPU-cost A/B), (`gammachroma`) runs the pre-2026-11-22
  math: encoded chroma filter, left input siting, per-pixel clip (quality/cost A/B; the self-test checks
  that math), (`fixedpeak`) tone-maps PQ from the config peak (1000 nits) instead of the stream's MaxCLL /
  mastering metadata (the pre-2026-11-24 peak; the Tone-map peak row then says `debug fixedpeak`), or
  (`bilinearchroma`) upsamples the input chroma bilinearly instead of with Catmull-Rom (the pre-2026-11-24
  filter, quality/cost A/B). Words combine (`gammachroma,bilinearchroma,fixedpeak,nostaged` is the fully
  old stage). Each applies from the next tone-map create.
- `bale-debug.sh mem` reports `D3D11 devices: watched N destroyed M alive K`: each engine device
  carries a private-data marker that only real destruction frees.
- `scripts/bale-debug.sh ffmpeg-tune white=80..400` sets the SDR white (nits) of the next attempt's tone map, the
  stage's and rav1g's display pass alike, over Settings › SDR Brightness (memory only; `white=default` returns to the
  setting; 100 sends no key). It applies only where the tone map is configured, like the setting.

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
| 1 | bale_gpu tone map: constants, scratch, downscale intermediate, self-test textures, churn probe |
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

Other console limits (shared list: the bale-projects skill, "Measured console limits"):
- **Input-idle state** (~10 s after the last input, console-wide, normal apps; xbox-console-profiler
  2026-11-20): CPU ALU ~21-25% slower, CPU RAM copies 3.6-4x slower (1 thread ~13 -> ~3.5 GB/s, 4 threads
  ~23.5 -> ~6.5 GB/s), cache-resident copies ~20% slower, **GPU copies ~2.4x slower** (97 -> 40 GB/s
  DEFAULT->DEFAULT, 68 -> 37 GB/s upload->default), GPU compute unchanged. Real playback runs in this state.
  This corrects the 2026-11-10 note that GPU copies were not slowed (one small copy).
- **GPU features** (D3D12, measured by rav1g): shader model 6.4 at most, waves of 64 lanes only, no native
  16-bit arithmetic. WARP in CI uses small waves, so wave-dependent shader paths are only tested on the console.
- **GPU slow state** (rav1g, 2026-11-18): after the first 10-bit video output following a boot, the GPU ran
  ~2.6x slower; it wore off within ~2 min in some cases, in others only a restart cleared it. Plain Windows
  10-bit playback did not trigger it. Bale avoids it by capping output at the display's depth (see "First
  10-bit video after a boot slows the console GPU" in docs/player-plan.md).
- **D3D12 pipeline caches** (rav1g, 2026-11-18): ID3D12PipelineLibrary and GetCachedBlob remove the device
  (DXGI_ERROR_DRIVER_INTERNAL_ERROR); don't use them on the console.
- **Dependent compute steps** (rav1g intra wavefront; per-step GPU timestamps, 34,900 steps, 2 rounds,
  2026-11-21): each step of a chain of dependent dispatches costs about 18 us fixed (dispatch + barrier) plus
  the latency of its slowest thread group; extra groups in the same step add only ~0.1 us each. So the number
  of serial steps and the slowest group's latency set the cost, not the amount of work (replaces the
  2026-11-19 average of ~30 us).
- **Profiler results** (Zorro92/xbox-console-profiler docs/results.md, 9d6dad1, one run per variant):

  | | plain | hevcPlayback (Bale) | expandedResources |
  |---|---|---|---|
  | App memory limit | 1024 MiB | 3328 MiB | 5120 MiB |
  | DXGI local budget | 1210 MiB (app limit stops GPU first: D3D11 792, D3D12 928) | 2857 MiB | 4147 MiB |
  | CPUs | 8 logical (4 cores, shared) | 8 logical (4 cores, shared) | 6 logical (3 dedicated cores) |
  | GPU compute | 1.86 TFLOP/s | 1.85 TFLOP/s | 11.25 TFLOP/s |
  | GPU copy, active / idle | 97 / 40 GB/s | 89 / 38 GB/s | 144 / 144 GB/s |
  | Idle slowdown | yes | yes | none |

  A normal app's GPU queue is never preempted (busy 99.8%) but runs on ~1/6 of the GPU. CPU throughput stops
  growing beyond 4 threads. `expandedResources` is the deprecated manifest route to Game resources and Store
  submissions declaring it are rejected: it shows what Game mode gets, it is not an option for Bale.

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

## Measure the stage's GPU time (debug packages)

Development packages opt into the native Cargo feature `stage-timing`, even though the DLL is built
with `--release`. Production (`BaleDebugServer=false` in CI) omits the feature, query code and debug
exports entirely; feature-dependent DLL caches and the MSIX export check enforce the separation.
Timing is **off after app launch**. Before starting a new playback:

```sh
./scripts/bale-debug.sh gpu-timing on
# Start playback normally, or with the existing play/play-url command.
# Leave controller/keyboard input idle for at least 15 seconds before comparing windows.
./scripts/bale-debug.sh stats
./scripts/bale-debug.sh log
# Disable for the next playback (the current stage retains its create-time setting):
./scripts/bale-debug.sh gpu-timing off
```

`gpu-timing` without an argument reports the switch. Read **Stage GPU** in the debug overlay (I / View)
or `stats` / `where`. The one-second stats refresh logs each new window once, even with the overlay
hidden. The line begins `Bale stage gpu: frames=240 total avg/max ... ms` and includes `input-copy`,
`luma`, `chroma` avg/max milliseconds, `valid`, `disjoint`, `unavailable`, `pending`, `session` and
`window`. The first window needs about 241 apply calls (roughly four seconds at 60 fps); until then the
row says collecting. A route without a Bale stage stays waiting, never displays another playback's
result. This measures actual stage submissions, including decode preroll; use later idle-state windows
for the resource budget. Self-test draws are excluded. The asynchronous timing hardening described
below has not been validated on the console.

The shaders are **two plane passes** at 1:1 and **three** when downscaling (area pass into the fp32
intermediate, then the plane passes; see "Pass structure"), never separate horizontal/vertical filters.
Timing keeps its four intervals; the area pass is counted in **luma**. Timing covers:

- **total**: GPU timestamps enclosing the optional input copy, bindings and both draws, inside the
  stage's private context state (excludes state isolation/restore);
- **input-copy**: only `CopySubresourceRegion` into the SRV scratch texture (zero for in-place input);
- **luma**: constant update/bindings, the area draw when downscaling, and the luma draw;
- **chroma**: chroma bindings and the chroma draw.

Total can exceed the sum of passes because it includes surrounding commands within the stage. This is elapsed
GPU time on the decoder's D3D11 immediate context, **not whole-device busy time**. It excludes CPU view
creation/allocation, decoder work before apply, the wrapper's output pool/release waits, the engine's
conversion/present and other queues; scheduling/interleaved GPU work can inflate elapsed intervals.
Compare total with the ~2–2.5 ms budget for all non-decode GPU work at 60 fps (~1.86 TFLOP/s app slice),
and compare like-for-like dimensions, output depth, content and idle state. This does not establish
that the stage causes rav1g release-fence waits; it supplies the missing measurement.

There is one query set (one disjoint plus six timestamps), no additional frame textures. At most one
disjoint interval may be unresolved: a busy set skips that apply's measurement, counted as unavailable.
This reduces the original 56 query objects to seven, without a frame copy or extra thread against the
console's shared memory/CPU budget. Results are polled no sooner than four apply calls later, once per apply using
[`GetData`](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-getdata)
with `D3D11_ASYNC_GETDATA_DONOTFLUSH`. Raw `S_FALSE` leaves the slot pending; the CPU never spins,
flushes or waits for results, and an occupied set is never overwritten. Even with immediately ready
queries, at most one in four applies is measured; skipped applies are unavailable, not zero-ms samples.
Query creation validates the returned interface, descriptor and `GetDataSize`; every read verifies its
exact typed buffer size again before passing a pointer to the driver. Only `S_OK` consumes data;
`S_FALSE` keeps the set pending, including when only some timestamps are ready. All timestamps must be
ready before reuse, even for a disjoint interval. Query calls run under the stage's existing context
lock, after state isolation and before restoration. `Begin`/`End` return no HRESULT, so device status
is checked around timing operations; every fallible call's HRESULT is checked.

A timing error or Rust panic disables that stage's measurement permanently, accounts pending samples
as unavailable, and lets the draw continue. The debug row shows the reason/HRESULT and logs
`Bale stage gpu: disabled (<reason>; query HRESULT 0x...)` once per measured session, even for creation
failure before the first window. The ABI's reserved word now carries a fixed failure reason code (see
the header); layout and ABI 1 are unchanged. Teardown performs no query reads/waits and preserves a
disabled state, publishing any partial final window for readers still attached. An export-level panic
catch remains a final guard, but timing has its own catch so an instrumentation panic does not become
an apply error. Rust unwind handling does not catch native driver faults or repair a removed device.

The original asynchronous path terminated playback on the console with timing on, after stage create
and before the first published window. The exact driver instruction is unproven without a crash dump.
Leading candidates are several unresolved disjoint intervals on MF's shared device (unlike the older
successful private-device probe), or a query-data layout difference. Crossing context-state swaps was
also avoidable driver exposure, though desktop D3D11 documents that queries survive swaps. The WARP
test originally synchronized every apply, masking unresolved-query behavior; a P010 in-place-input
burst now covers submission without per-apply readback. Serialization, size validation and containment
address these risks without claiming that desktop tests reproduce the console failure.

Each 240 accounted outcomes makes a window (valid result, disjoint result or unavailable measurement);
results completing late may fall into a later window. `frames = valid + disjoint + unavailable`;
averages/maxima use **valid only**, never zero-fill missing results. Pending is the unresolved set count
at publication. A zero frequency or reversed timestamps disables timing instead of producing bogus
milliseconds. Disjoint intervals remain excluded samples. All-invalid windows say
unavailable rather than reporting 0 ms. The
[disjoint frequency](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ns-d3d11-d3d11_query_data_timestamp_disjoint)
is used separately for every frame and discontinuous intervals are excluded. Whether Xbox app-mode
D3D11 timestamps have a stable frequency, useful precision or frequent disjoint results under load is
**unmeasured**; check those health counts before interpreting timing. No whole-device busy estimate or
second API is used.

The two optional exports `bale_tonemap_debug_timing_enable` / `bale_tonemap_debug_timing_read` and
`BaleToneMapTimingStats` are documented in `include/bale_tonemap.h`; absence means measurement is
unavailable. They do not change create/apply/config or the wrapper's required **ABI 1**. The UI reads a
small native snapshot only, with no D3D or FFmpegInteropX source lock. The snapshot belongs to the
most recently created measured stage in the process: run one playback/probe at a time when comparing
windows. A mount whose initial snapshot is busy fails closed as unavailable rather than showing an
older playback's results.
