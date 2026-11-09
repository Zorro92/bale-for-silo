# GPU AV1 decoder (experiment, scoping 2026-11-08)

Goal: make 4K AV1 play smoothly on Xbox Series in app mode. Software dav1d can't: it reaches only
~89% of real time in heavy 4K scenes on ~7 of 8 cores (see gpu-film-grain.md section 8). The console
has no AV1 hardware decoder under D3D11 or D3D12 (player-plan.md), so the only remaining path is
decoding on the GPU with compute shaders. This is a "for science" experiment; production keeps the
1080p AV1 claim until it proves out.

## Reference: av1-xbox-one
Google/AOMedia GPU AV1 decoder for Xbox One (2020–21, last commit 2021-08-25, never upstreamed).
BSD-2-Clause plus the AOMedia Patent License 1.0, so porting is fine if both notices ship with Bale.

- **Split:** CPU does entropy decoding (a modified libaom decoder, ~46.6k lines of C including unused
  libaom recon code). The GPU does everything after it: inverse transforms, intra and inter
  prediction (incl. warp, OBMC, compound, scaled references), loop filter, CDEF, loop restoration,
  super-res upscaling and film grain. 87 HLSL compute shaders, ~15.7k lines; ~5.3k lines of C++ D3D12
  host code drive them.
- **Pipelining:** while the GPU reconstructs frame N, the CPU entropy-decodes frame N+1. The player
  keeps a ~16-frame queue and runs decoder threads above normal priority.
- **No published performance numbers.** It targeted Xbox One in UWP game mode, because D3D12 compute
  wasn't available to apps there. On Series, Bale already creates a hardware D3D12 device in app mode.
- **Shaders use nothing beyond Shader Model 5:** ByteAddressBuffer, groupshared memory, root
  SRV/UAV/constant bindings, no wave intrinsics. So they could run on either D3D11 `cs_5_0` or D3D12.

## Language
The host layer is Rust: a new module or crate next to `bale_gpu`, with a C ABI like the grain and
repack code. The heavy work runs in the HLSL compute shaders, reused from the reference. The CPU
entropy decoder starts as the reference's C, built by the Rust crate via `cc`. Replacing it with
Rust (for example rav1d's parser feeding the same GPU buffers) is a later, optional phase, only
worth doing once the GPU side is proven.

## Main questions (answered by phase 0)
1. **D3D11 or D3D12.** D3D11 compute on Bale's shared device writes straight into the MF texture pool,
   with no interop. But the GPU repack showed that work on the shared immediate context stalls the
   decode thread. A D3D12 compute queue runs independently of the renderer, but its frames must
   reach D3D11 (shared NT-handle textures plus a fence, or D3D11On12). The D3D12 video query also
   removed the device on this console, so D3D12 compute stability needs its own check.
2. **GPU time per 4K frame** in app mode, where the GPU is shared with the shell. Needs a console run
   of the heaviest shaders (inter prediction, inverse transforms, CDEF, loop restoration).
3. **CPU entropy cost at 4K.** libaom's entropy decoder is slower than dav1d's. It must stay well under
   one frame time per frame across the available threads, using tile parallelism. Measure on the
   desktop first, then on the console.
4. **Feature gaps** in a 2021 decoder that was never upstreamed: intra block copy, palette, odd sizes,
   10-bit edge cases. The AOM conformance vectors (8-bit and 10-bit) give md5 checks against dav1d.

## Phases
0. **Spikes.** (a) Build the reference CPU decoder plus shaders in Windows CI and decode AOM
   conformance vectors on WARP, checking md5s. (b) Console timing of representative shaders on D3D11
   and D3D12 with a 4K workload. (c) Desktop measurement of entropy-only decode time on a 4K stream.
   Stop here if the GPU or entropy time can't fit 24 fps with headroom.
1. **Rust host.** Port the D3D12 host code (resources, root bindings, dispatch order, barriers,
   fences) to Rust on whichever API phase 0 picks. md5-exact on the conformance vectors on WARP in CI,
   in the style of the grain and repack WARP tests.
2. **Console decode.** Debug-only path that decodes an AV1 file on the console and reports per-frame
   CPU/GPU time, with no playback yet.
3. **Playback integration.** A wrapper patch that routes AV1 to the GPU decoder instead of dav1d and
   delivers textures into the existing D3D11 sample path. Seeking, flush, resolution changes, HDR10
   metadata and film grain (theirs or Bale's) all need handling. Debug opt-in first, measured with
   the same 120 s Buffering run as the dav1d and repack results.
4. **Optional.** Rust entropy decoder, 10x3 packing for memory, production enablement.

Each phase is gated on the previous one. Phase 0 is the cheap part that decides whether the rest is
worth it.

## Phase 0 results (2026-11-08)
The upstream test player (`uwp_dx12_player`, built by `.github/workflows/av1-gpu-testplayer.yml`) was
sideloaded and run on the Series console **in app mode**. It decoded 4K 10-bit AV1 unlimited-rate
with `-queue_size 4`. The default 16-frame queue fails to allocate output buffers in app mode.

- **D3D12 compute works in app mode:** the device and all PSOs are created, with shader creation
  taking ~5–6 s. The DEVICE_REMOVED seen with the D3D12 *video* query does not affect compute.
- **Hidden frames hang it:** SVT-AV1 random-access encodes (hidden alt-refs plus
  `show_existing_frame`) stop after the first frame, for both 1 and 8 tiles. Low-delay encodes with
  every frame shown decode fine. Most real AV1 encodes use hidden frames, so this would need fixing
  in any port.

| Stream (3840x2160, 10-bit, 300–600 frames) | GPU decoder fps | Desktop 1-thread dav1d, relative |
|---|---|---|
| Jellyfin 40 Mbps, 1 tile | 10.3 | 1.28 |
| SVT low-delay 40 Mbps, 1 tile | 13.3 | 1.00 |
| SVT low-delay 40 Mbps, 8 tiles | 14.1 | 1.02 |
| SVT low-delay 12.6 Mbps, 1 tile | 15.9 | 0.68 |

For comparison, console dav1d decodes the Jellyfin clip at ~32–35 fps on ~6–7 cores. The GPU
decoder is about 3x slower.

**Reading:**
- Eight tiles at the same bitrate gain only 6%, so tile-parallel entropy decoding is not what limits it.
- Cutting the bitrate 3.2x gains only 19%. Fitting time = fixed + per-bit gives a fixed cost of
  **~57 ms per 4K frame** (≤17.5 fps) even at zero bitrate. 24 fps needs ≤41.7 ms in total.
- The fixed part is likely GPU reconstruction. That is unconfirmed, because the player does not log
  GPU timestamps. It could also be per-frame host overhead such as fence waits or the small queue.

**Gate: not met.** On the reference as built, the per-frame floor alone is ~40% over the 24 fps
budget before any entropy cost, and correctness (spike a) was not checked. Continuing would first
need a GPU timestamp breakdown, which means patching the test player. Only a shader-side fix (a
rewrite for RDNA2 waves, not a port) could close a gap of that size.
