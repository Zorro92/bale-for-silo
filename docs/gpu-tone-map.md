# GPU HDR to SDR tone mapping (debug only)

`bale_gpu.dll` exposes independent tone-map **ABI 1** through
`native/bale-gpu/include/bale_tonemap.h`. It accepts PQ P010 and produces limited BT.709 NV12.
Production continues to request server tone mapping. The wrapper stage requires
FFmpegInteropX 2.1.0-bale.25 or later; Bale now pins .25 and enables the debug switch. The
package version gate protects older builds.

## Ownership and GPU work

Create on the decode thread with the MF D3D11 device while holding its multithread protection.
Serialize create/apply/destroy and keep textures unmapped and available for all queued work.
Create retains the device and runs a fixed 16×8 PQ self-test containing black, 100/203/1000/4000
nits and saturated source colours. It checks both input ranges and direct/copy input paths
against the Rust reference, with a tolerance of three NV12 codes. A mismatch returns
`BALE_TONEMAP_E_SELFTEST` (`0x8004BA01`, FACILITY_ITF) and a null handle.

Apply takes the caller's immediate context and a P010 array subresource, reads only the visible
config extent, and writes a caller-owned, exactly sized NV12 texture with RT|SRV binds. A
non-SRV decoder texture is copied with CopySubresourceRegion into a cached P010 SRV texture;
array SRVs select the requested slice directly. Two ps_5_0 draws use D3D11.1 R8_UNORM/R8G8_UNORM
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
  primaries/matrix pairs work. Only transfer 16 (PQ) is supported. Frame HDR metadata is accepted
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

## Debug switch

For the next FFmpeg player attempt, use one of:

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
automatically for the pinned .25 wrapper. Earlier packages reject explicit modes with a clear
HTTP 400; default remains available. The pre-.25 wrapper's
[TryParseBaleOption](https://github.com/Zorro92/bale-ffmpeginteropx/blob/c3a9adbf2d81024a54063e7570ed9883afd5403c/Source/FFmpegMediaSource.cpp)
rejects unknown Bale keys with E_INVALIDARG rather than forwarding them to FFmpeg.
The tuning log and `FFmpeg tone map override` stats/where row report the captured mode.
