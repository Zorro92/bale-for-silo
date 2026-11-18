# GPU HDR to SDR tone mapping and display-sized output

`bale_gpu.dll` exposes the independent tone-map **ABI 1** through
`native/bale-gpu/include/bale_tonemap.h`, plus the additive capability export
`bale_tonemap_output_formats()`. It is the production path for HDR originals on SDR displays
(see `docs/player-plan.md` "Console tone mapping by default"), and since 2026-11-17 also the
display-sized scaler for 4K SDR on smaller displays (see "4K SDR downscale on smaller displays").
The FFmpegInteropX fork (wrapper 2.1.0-bale.40 pinned) owns the stage around it.

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

Apply picks the output depth from the output texture's format and the input depth from the input
texture's format. The owner's rule is that processed video is always output in 10-bit, so the fork
asks for P010 whenever bit 2 is present (bale.36+). The create self-test checks NV12 and P010
output at 1:1 (it never inherits a configured downscale source size).

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
quantization, no dithering, explicit linear gamut clipping, and the chroma filter above. Inputs
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
