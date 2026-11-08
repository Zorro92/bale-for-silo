# Bale stage 2: GPU AV1 film grain (design)

Status: 2026-11-08. Steps 1–2 implemented and verified: Rust CPU oracle/fallback, dav1d fixtures and
integer GPU passes. [Windows CI run 42505455358](https://github.com/Zorro92/bale-for-silo/actions/runs/42505455358)
passes real FXC compilation, strict Windows Clippy, all four create-time self-tests and all 44 WARP
fixtures / 88 frames, including padding and pipeline-state checks. Local formatting, strict Linux
and Windows cross-target Clippy, and Linux bit-exact release tests also pass. The Linux cross-check
uses placeholder shaders, not GPU execution. Step 3 adds a synthetic console probe; console results
are pending. Steps 4–6 remain future work; no wrapper or runtime-policy changes. Playback still uses
dav1d grain.
Reference algorithm: dav1d 1.5.4, commit `54706fc6bc0cdecab7e9593974a4039cc038fca7`,
`src/filmgrain_tmpl.c` + `src/fg_apply_tmpl.c` + the Gaussian sequence in `src/tables.c` (BSD-2).
Ported files retain the original copyright and full licence; `native/bale-gpu/NOTICE` records provenance.
The eventual GPU goal is byte-identical output at a fraction of dav1d's CPU cost; this step establishes
correctness, not a CPU performance improvement over dav1d.

## 1. Where it runs
- MediaPlayer + FFmpegInteropX MSS stay (MediaEngine was not faster; frame-server copies cost more).
- dav1d already exports grain instead of applying it when `AV_CODEC_EXPORT_DATA_FILM_GRAIN` is set
  (patch 0005); each grained frame then carries `AV_FRAME_DATA_FILM_GRAIN_PARAMS` (type AV1).
- New wrapper patch 0009 applies it at upload time, inside `BaleD3D11Upload::CreateSample`, in place of
  the plain `CopyResource(pooled, staging)`. Rust `bale_gpu.dll` does the work through a C ABI.
- The upload device is MF's own device; its immediate context is shared with the renderer.

## 2. Mode selection (decided at codec open, because dav1d reads apply_grain only at init)
| Condition at open                                       | Export | Grain applied by          |
|---------------------------------------------------------|--------|---------------------------|
| bale_gpu.dll missing, exports missing, grain ABI ≠ 1    | off    | dav1d (today's behaviour) |
| `grain=dav1d`, or upload off, or source not 4:2:0 8/10-bit | off | dav1d                     |
| otherwise                                               | on     | GPU; Rust CPU as fallback |
- New option `bale_film_grain_mode` (0 dav1d, 1 rust-cpu, 2 gpu) and `ffmpeg-tune grain=dav1d|cpu|gpu|default`.
  Default stays dav1d until console A/B passes, then flips to gpu (same path as stage 1 upload).
- Runtime fallback, permanent per stream, logged once (no params, no exception text):
  GPU create / self-test fails, upload device lost, or apply returns a device error → Rust CPU grain on
  the CPU output path. Reason: after export is on, dav1d can no longer apply grain without reopening.
- Upload disabled mid-stream (existing DisableUpload) → same CPU grain on the CPU buffer path.

## 3. C ABI (additive exports; bale_gpu ABI_VERSION stays 4; separate grain ABI = 1)
```c
#define BALE_GRAIN_ABI 1
typedef struct BaleGrainParams {         // 184 bytes, no implicit padding; static_assert both sides
    uint32_t struct_size;                // = sizeof(BaleGrainParams)
    uint32_t seed;                       // low 16 bits used
    uint32_t width, height;              // visible frame size (AVFrame width/height)
    uint8_t  bit_depth;                  // 8 or 10
    uint8_t  matrix_identity;            // AVFrame colorspace == AVCOL_SPC_RGB
    uint8_t  num_y_points;               // 0..14
    uint8_t  chroma_scaling_from_luma;   // 0/1
    uint8_t  num_uv_points[2];           // 0..10
    uint8_t  scaling_shift;              // 8..11
    uint8_t  ar_coeff_lag;               // 0..3
    uint8_t  ar_coeff_shift;             // 6..9
    uint8_t  grain_scale_shift;          // 0..3
    uint8_t  overlap_flag;               // 0/1
    uint8_t  clip_to_restricted_range;   // 0/1
    uint8_t  y_points[14][2];            // value, scaling; values strictly increasing
    uint8_t  uv_points[2][10][2];
    int8_t   ar_coeffs_y[24];
    int8_t   ar_coeffs_uv[2][25];
    uint8_t  reserved0[2];
    int16_t  uv_mult[2], uv_luma_mult[2], uv_offset[2];  // offset in [-256, 255]
} BaleGrainParams;                       // size, alignment and every field offset pinned by Rust tests
                                        // C++ assertions will be added with the wrapper in step 4

uint32_t bale_grain_abi_version(void);
HRESULT  bale_grain_validate(const BaleGrainParams*);  // S_OK / E_INVALIDARG / E_UNEXPECTED
HRESULT  bale_grain_cpu_apply(const BaleGrainParams*, uint32_t format /*1 NV12, 2 P010*/,
                              uint8_t* y, intptr_t y_pitch, uint8_t* uv, intptr_t uv_pitch,
                              uint32_t threads /*0 = auto, max 4*/);   // in place

// Step 2 (Windows): opaque, decode-thread-only GPU handle; create includes a self-test.
HRESULT  bale_grain_gpu_create(ID3D11Device*, ID3D11DeviceContext*, BaleGrainGpu** out);
HRESULT  bale_grain_gpu_apply(BaleGrainGpu*, const BaleGrainParams*,
                              ID3D11Texture2D* src_staging, ID3D11Texture2D* dst_pooled);
void     bale_grain_gpu_destroy(BaleGrainGpu*);

// Step 3 (Windows): synthetic hardware diagnostics; bytes written, UTF-8, no trailing NUL.
uint32_t bale_grain_gpu_probe(uint8_t* report, uint32_t capacity);
```
- `HRESULT` is `i32` in Rust. The three step-1 exports each use `catch_unwind`; validation/layout
  failures return E_INVALIDARG, and a panic returns E_UNEXPECTED. Release keeps `panic = "unwind"`.
  Neither raw nor validated parameter types implement Debug; the exports never log their contents.
- FFmpeg's struct never crosses the boundary; the future wrapper maps `AVFilmGrainAOMParams` field by
  field (`uv_mult_luma` → `uv_luma_mult`, `limit_output_range` → `clip_to_restricted_range`).
- Rust validates every field (ranges above, increasing active points, lag-dependent coeff counts) and
  builds a `ValidGrainParams` newtype; synthesis only accepts this validated snapshot.
  Dimensions are 1..65536 (AV1's 16-bit dimension-minus-one limit); multipliers are -128..127 and
  offsets -256..255. Boolean fields must be 0/1 and reserved0 must be zero. The fixed coefficient
  arrays have no explicit counts: callers zero unused tails after 2*lag*(lag+1) luma coefficients and
  2*lag*(lag+1)+(num_y_points != 0) chroma coefficients. Inactive planes have zero coefficients.
  Only the low 16 seed bits participate. The FFI checks struct_size before reading the full struct.
- CPU pitches are signed, nonzero byte strides with magnitude at least the visible row size;
  pointers address the first logical row, even for negative pitches. P010 words are little-endian,
  with ten coded bits in bits 15..6. Plane spans must not overlap. Padding is never modified;
  odd-width chroma averages clamp the second luma sample instead of writing dav1d's padding pixel.
  Threads above 4 are rejected; 0 chooses the available parallelism capped at 4. All span and address
  arithmetic is checked before constructing slices. The ABI has no buffer-length arguments: callers
  must supply exclusive, live allocations covering the checked spans, including negative strides.
  The safe Rust API additionally verifies slice lengths, with slices starting at each plane's lowest
  addressed byte rather than its first logical row. Validation errors leave both planes unchanged;
  callers must not assume rollback if synthesis panics.
- GPU handle contract (implemented): COM pointers are borrowed for the call; `create` AddRefs device
  and context and keeps them until `destroy`. One handle per stream, created, used and destroyed only
  on the decode thread (the wrapper already serializes Map / CreateSample / Flush). All three GPU
  exports use the same unwind boundary. Create nulls its output before fallible work; destroy(null)
  is a no-op. A panic returns E_UNEXPECTED and permanently poisons an existing handle. Device-loss
  errors also poison it; subsequent applies return that same failure without any D3D calls.
- Create requires Device1, an immediate Context1 on that device and ID3D10Multithread, checks the
  required texture/view-format support, and propagates D3D HRESULTs. Context state uses flags=0,
  the device's own feature level and the ID3D11Device emulated interface (never SINGLETHREADED).
  Unsupported formats/interfaces or a failed self-test fail closed; there is no partial handle.
- Apply rejects invalid parameters/pointers before submitting work: distinct source/destination,
  same device/format/size, NV12+8-bit or P010+10-bit, even dimensions covering the visible image,
  one mip/array slice/sample (quality zero), and destination BIND_RENDER_TARGET. Source/destination
  must be unmapped for the call. Argument failures return E_INVALIDARG without writes. Nonfatal D3D
  errors propagate; GetDeviceRemovedReason is checked after each submitted apply. Failed/panicking
  synthesis is not transactional, so callers must not consume a failed destination.
- Loading: `LoadPackagedLibrary(L"bale_gpu.dll")` + `GetProcAddress`, once per process; the version
  check fails closed (export off).

## 4. GPU pass (inside `bale_grain_gpu_apply`)
Order, all on MF's immediate context:
1. `ID3D10Multithread::Enter()`: the renderer can't interleave calls with our state setup.
2. `ID3D11DeviceContext1::SwapDeviceContextState(ours, &prev)`: our own clean pipeline state; MF's
   state is restored exactly afterwards. No ID3D11DeviceContext1 → create fails → CPU fallback.
3. `CopyResource(intermediate, staging)`. One intermediate per handle (DEFAULT, SRV, same format
   and size); in-order execution on one context makes reuse safe, so no fences are needed.
4. UpdateSubresource the per-frame inputs (≈ 24 KB at 1080p, driver-renamed):
   | Input             | Format / size                         |
   |-------------------|---------------------------------------|
   | luma template     | R16_SINT 82×73                        |
   | cb+cr templates   | R16G16_SINT 44×38                     |
   | scaling LUTs      | R8_UINT (1<<bitdepth)×3 (y, cb, cr)   |
   | block offsets     | R8_UINT ceil(w/32)×ceil(h/32)         |
   | constants         | cbuffer (dims, shifts, mults, clip ranges, plane flags) |
5. Draw 1, luma: fullscreen triangle (SV_VertexID, no vertex buffer), viewport = texture size,
   SRV intermediate plane 0 as R16_UINT / R8_UINT, RTV pooled plane 0 as R16_UINT / R8_UINT.
6. Draw 2, chroma: viewport = texture size / 2, SRV planes 0+1 (chroma reads un-grained luma
   from the intermediate), RTV plane 1 as R16G16_UINT / R8G8_UINT.
7. Unbind, swap the state back, `Leave()`. The wrapper then does its existing Flush + sample.
- UINT views: exact integers, no float rounding (P010 = value << 6). Plane creation uses the base
  CreateShaderResourceView/CreateRenderTargetView APIs: the format selects luma (R8_UINT/R16_UINT)
  or chroma (R8G8_UINT/R16G16_UINT). This is the D3D11.1 format-selected route documented by
  [DXGI_FORMAT](https://learn.microsoft.com/en-us/windows/win32/api/dxgiformat/ne-dxgiformat-dxgi_format),
  avoiding an additional Device3/PlaneSlice requirement. CheckFormatSupport and real planar
  create-time self-tests fail closed; WARP tests require the same views, without a planar fallback.
- P010/NV12 textures must have even sizes; pixels outside the visible w×h (or ceil(w/2)×ceil(h/2)
  chroma area) are written unchanged, including P010 low bits. Inactive planes also pass through
  without clipping. Intermediate/LUT/offset resources are reused until texture size or format
  changes. The offset texture has ceil(texture-width/32)×ceil(texture-height/32) capacity; only the
  visible grid is uploaded, so changes in visible dimensions alone never require reallocation.
- ContextGuard pairs Multithread Enter/Leave and state swap/restore on success, errors and panic
  unwinding. Both passes unbind SRVs/RTVs before restoring state. No Flush; the wrapper owns that.
- Shaders: `fullscreen_vs.hlsl`, `grain_luma_ps.hlsl`, `grain_chroma_ps.hlsl` and shared
  `grain_common.hlsli`, SM 5.0 (Series X is FL 11_0). Grain math is integer-only, Texture2D.Load only,
  with no samplers; only vertex clip-space geometry and SV_Position use floats. The pixel shaders
  retain dav1d's BSD-2 header. `gpu.rs` shares cpu.rs's preparation cache and clip-range helper.
- On a Windows host targeting Windows, build.rs uses the FXC environment override or the newest
  numerically sorted Windows Kits/10/bin/<version>/x64/fxc.exe. Each shader compiles with
  `/nologo /O3 /WX /Qstrip_debug /Qstrip_reflect`, `/E main`, and vs_5_0 or ps_5_0, into OUT_DIR.
  Missing/failed FXC or empty output fails the build. Shader/include files and FXC changes trigger
  rebuilds. include_bytes! embeds the blobs; there is no runtime compiler or compiler dependency.
- On a non-Windows host targeting Windows, build.rs writes empty placeholders and declares/enables
  bale_grain_no_shaders; create returns E_NOTIMPL before D3D work. This permits Linux cross-target
  lint/type checks only. Shipped DLLs must come from the Windows CI job with real FXC shaders.
- Every successful create runs four self-tests under the same multithread lock and state swap:
  64×64 NV12 and P010, each with visible 63×61, deterministic nontrivial source data, overlap,
  lag 3, nonzero signed UV multipliers/offsets, restricted clipping, explicit chroma scaling and
  a second csfl case (also exercising identity-matrix chroma clipping). STAGING/READ Map readback
  treats UV as starting at RowPitch*Height. All texture bytes, including visible samples and
  padding pixels, must equal cpu::apply; pitch-only driver padding is not image content. Mismatch
  returns E_FAIL with no handle. Map synchronizes the test without an explicit Flush.

### Per-pixel math (mirrors dav1d exactly)
- One offset grid serves all planes: dav1d draws one rand8 per 32-luma-px block column with the same
  row seeds for luma and chroma, and the "previous row/column" offsets are the neighbours in that grid.
- Luma at (x, y): b = (x>>5, y>>5), l = (x&31, y&31); `sample(r, dx, dy) = T[3+2*(3+(r&15)) + l.y + dy]
  [3+2*(3+(r>>4)) + l.x + dx]`. g = sample(off[b], 0, 0).
  If overlap and b.x > 0 and l.x < 2: g = clip(round2(sample(off[b.x-1], 32, 0)*w[l.x][0] + g*w[l.x][1], 5)).
  If overlap and b.y > 0 and l.y < 2: top = sample(off[b.y-1], 0, 32), blended with sample(off[b.y-1][b.x-1],
  32, 32) the same way when l.x < 2 and b.x > 0; then g = clip(round2(top*w[l.y][0] + g*w[l.y][1], 5)).
  w = {{27,17},{17,27}}. `out = clamp(src + round2(lutY[src]*g, scaling_shift), min, max)`.
- Chroma (4:2:0): blocks are 16×16, offsets 3+(3+…), dx/dy = 16, overlap only at l = 0 with w = {23,22}.
  avg = (L[2cx][2cy] + L[min(2cx+1, w-1)][2cy] + 1) >> 1 (un-grained luma).
  csfl: index = avg with lutY; else index = clip_pixel(((avg*luma_mult + src*mult) >> 6) +
  (offset << (bd-8))) with that plane's LUT. Planes with no points (and not csfl) pass through.
- Clip: restricted → luma 16..235, chroma 16..240 (235 if identity matrix), all << (bd-8); else 0..max.
  Grain clip ±(128 << (bd-8)). `>>` is arithmetic on negatives, as in C.

## 5. CPU side in Rust (implemented in step 1)
- Modules: `grain/{params,lfsr,gaussian,template,scaling,offsets,cpu,ffi}.rs` plus `mod.rs` and
  Windows-only `gpu.rs`. Synthesis denies unsafe code; only `ffi.rs` and `gpu.rs` are exempt,
  with SAFETY comments for raw-pointer/COM calls. No runtime dependencies were added.
- `template.rs`: direct port of generate_grain_y / generate_grain_uv (4:2:0), including the
  luma-average AR term (only when num_y_points > 0), the uv seed xor 0xb524 / 0x49d8, and the rule that
  chroma templates exist only if num_uv_points[pl] or csfl. The luma template is always generated.
  `gaussian.rs` preserves dav1d's complete 2048-entry Gaussian sequence.
- `scaling.rs`: port of generate_scaling, including 10-bit sub-step interpolation between already
  rounded 8-bit anchors. Empty scaling tables are zero-filled; inactive planes remain untouched.
- `offsets.rs`: one rand8 per 32×32 luma block, shared across all planes. Unit tests compare the grid
  with dav1d's current/previous-row draw loops, including row-seed wraparound.
- A thread-local cache holds one prepared entry (templates, three LUTs, offsets), keyed by an owned
  validated snapshot with full parameter equality. Changing seeds invalidate it; preparation is not
  claimed to meet the original unmeasured 0.1–0.3 ms estimate.
- `cpu.rs` (oracle and runtime fallback): in place on NV12/P010 with arbitrary signed byte pitches,
  including unaligned P010 and odd strides. Chroma runs before luma within each independent 32-row
  block row, because it reads un-grained luma. `std::thread::scope` workers own disjoint block-row
  groups (at most four threads). Arithmetic shifts, rounding, overlap order and intermediate clips
  mirror dav1d; no GPU or shader is involved.
- Manual release timing on 2026-11-08, Linux x86_64, Intel Core i7-10610U (8 logical CPUs),
  1920×1080 P010, 64 padding bytes per row, 30 frames per thread count with changing seeds:
  **29.192 ms/frame (1 thread)** and **12.373 ms/frame (4 threads)**. This includes templates, LUTs,
  offsets and full application, not only preparation. It is a host CPU baseline, not an Xbox result
  or a dav1d comparison. The ignored test is `timing_1080p_10bit`.

## 6. Wrapper patch 0009 (C++)
- Open: set export only per the table in section 2 (replaces the 0005 knob's meaning; 0005 stays).
- `UploadFrame`: after sws/av_image_copy, read the side data; if type AV1, map it to BaleGrainParams
  and validate; pass it to `CreateSample(target, pos, surface, grain)`.
  CreateSample: grain && gpu → `bale_grain_gpu_apply(staging, pooled)` instead of CopyResource.
  A device-level error reuses the existing "re-run this frame on the CPU path" route, plus CPU grain.
- Step 4 integration prerequisites: pooled upload textures currently use only BIND_SHADER_RESOURCE;
  the wrapper must also add BIND_RENDER_TARGET. The C++ parameter mapper must zero reserved0 and
  every unused coefficient tail (including all coefficients of inactive planes) before validation.
  Step 2 deliberately does not modify the wrapper or enable grain export in playback.
- CPU output path (`CreateBufferFromFrame`): if export is on and the frame has params → `bale_grain_cpu_apply`
  on the destination buffer after conversion. The decoder's AVFrame is never modified (it can still be
  a dav1d reference picture).
- Grain only runs when the output is the decoded size and the conversion is lossless (yuv420p →
  NV12, yuv420p10 → P010). Bale never scales in the wrapper today. If a stream would need scaling,
  export stays off at open and dav1d applies grain, because grain must be added before scaling.

## 7. Tests
### Step 1: committed dav1d oracles
- Run `scripts/grain-fixtures.sh` to regenerate with local ffmpeg/ffprobe 8.1.3, libaom 3.15.2 and
  dav1d 1.5.4. The shell entry point uses Python isolated mode (`-I`); no downloads are needed.
  libaom encodes only synthetic lavfi testsrc2, with `film-grain-test=1..16` and five explicit
  `film-grain-table` files. All-intra clips have two frames with different seeds per case.
- Every clip is decoded twice with libdav1d: default grain application, then `-export_side_data
  film_grain` to suppress application. ffprobe's exported JSON supplies the parameters. The committed
  data includes both gzip-compressed planar decodes, JSON, two little-endian 184-byte ABI records,
  the source tables, and a TSV manifest with decoded dimensions, branch tags and raw-frame SHA256s.
- `filmgrn1` cannot express restricted clipping. For the custom restricted cases the generator edits
  only that header bit using ffmpeg `trace_headers` positions, verifies the old bits, and re-probes
  before both oracle decodes. `restricted-identity` also sets matrix_coefficients to identity:
  deliberately nonconforming 4:2:0 metadata accepted by dav1d's default non-strict mode, solely to
  test the defensive chroma maximum of 235. No Rust output is used to make expected frames.
- Actual set: **44 cases / 88 frames / 2,230,165 bytes**, below the enforced 4,000,000-byte budget.
  Dimensions are 128×96, 130×98, 65×33, 129×97 and 333×197. True odd dimensions use `crop=...:exact=1`
  from a larger even testsrc2 canvas and are asserted from decoded frames. 1080p is a generated
  timing buffer, not a committed full-size oracle.

All named cases below exist with both `-8` and `-10` suffixes unless noted.

| Branch / condition | Fixture cases |
|---|---|
| All 16 libaom presets | `preset01`–`preset16` |
| Lag 0 / 1 / 2 / 3 | `lag0` / `csfl` / `restricted` / `lag3` |
| Chroma scaling from luma | `csfl`, `preset15` |
| No Y points; all planes pass through | `no-points` |
| Chroma pass-through with active Y | `preset06`, `preset13`, `preset14` |
| Restricted / full clipping | `restricted` / `lag0`, `lag3`, `csfl` |
| Identity-matrix chroma maximum | `restricted-identity` |
| Signed UV multipliers and offsets | `lag0`, `lag3`, `restricted` |
| Scaling shifts 8 / 9 / 10 / 11 | `lag0` / `csfl` / `preset12` / `lag3` |
| AR shifts 6 / 7 / 8 / 9 | `lag0` / `csfl` / `preset01` / `lag3` |
| Grain shifts 0 / 1 / 2 / 3 | `lag3` / `restricted` / `csfl` / `lag0` |
| Overlap off / on, including corners | `lag0`, `restricted` / `lag3`, `csfl` |
| Maximum 14 Y / 10 UV points | `lag3` |
| Nonconstant 10-bit sub-step interpolation | `lag0-10`, `lag3-10`, `csfl-10` |
| Partial even-sized blocks | `preset15`, `restricted` (130×98) |
| Truly odd width and height | `lag3` (333×197), `csfl` (129×97), `lag0` (65×33) |
| One-pixel edge blocks | `preset16`, `lag0`, `csfl` |
| Two changing seeds | Every case |

- `tests/grain_fixtures.rs` converts each plain frame into padded NV12/P010, applies Rust grain and
  compares every stored byte/word with dav1d. It tests threads 1, 4 and 0, plus negative Y, negative
  UV and both negative pitches with four threads: **528 frame/layout executions**. One-byte base
  guards and odd byte pitches force unaligned P010; sentinel checks protect all padding and guards.
  A mismatch reports case, frame, plane, x/y, expected/actual and thread/stride configuration.
- Unit tests pin all ABI offsets, validation and coefficient tails, signed rounding, LFSR draws,
  template presence, shared offsets, 10-bit interpolation, clipping, inactive-plane pass-through,
  FFI validation, signed unaligned P010 and panic containment. Timing is ignored by default.
- CI reads committed fixtures only: Linux runs formatting, Clippy and locked release tests; the
  existing Windows job runs locked release tests for `x86_64-pc-windows-msvc` before packaging.
  The only added dependency is development-only `flate2` with its pure-Rust backend. No ffmpeg,
  libaom, dav1d, C compiler or network access is needed by the fixture tests themselves.

### Step 2: real GPU vs dav1d (mandatory Windows WARP gate)
- `tests/common/mod.rs` shares fixture loading with the unchanged CPU comparison/layout checks.
- `tests/grain_gpu_warp.rs` creates a protected WARP FL11_0 device; create must succeed, including
  all four synthetic self-tests. It executes **all 44 cases / 88 frames** through the embedded real
  shaders, with even-rounded STAGING uploads and DEFAULT RENDER_TARGET|SHADER_RESOURCE outputs.
  Comparisons cover every NV12/P010 byte against dav1d, sentinel padding pixels and unchanged source
  textures. Output starts with a different sentinel so a missed padding write cannot pass unnoticed.
- State-preservation coverage installs a viewport, pixel shader and render target before create;
  checks them after create's self-tests, two applies, and destroy. Argument coverage checks nulls,
  source/destination aliasing, invalid params, wrong depth/format, unequal/insufficient dimensions,
  and a missing render-target bind, with no image writes on rejection. D3D cannot create odd planar
  textures: a creatable odd R8 texture exercises ABI rejection, and GPU unit tests directly check
  odd NV12 descriptors (plus mip, array and sample constraints) without D3D submission.
- Windows CI locates FXC, runs strict `cargo clippy --all-targets --locked --target
  x86_64-pc-windows-msvc -- -D warnings`, and release tests with `--nocapture` so the self-test and
  44-fixture success lines appear in logs, before building/packaging the DLL. WARP missing planar
  UINT support is a test failure, never a skip or a substitute nonplanar test.
- Local required checks: `cargo fmt --check`, native/Linux strict all-target Clippy, locked release
  tests, and strict Windows all-target cross-Clippy (placeholder blobs, not GPU execution).

### Step 3: console probe
- Debug-only `scripts/bale-debug.sh rust` / `GET /rust` calls the additive `bale_grain_gpu_probe`
  export (grain ABI 1, crate ABI 4 unchanged), on the existing background task with a 64 KiB report
  buffer and a 120 s curl timeout. It creates a private hardware VIDEO_SUPPORT device, enables
  multithread protection, reports feature level and runs the existing NV12/P010 create-time tests.
  A failed create also reports Device1/Context1 casts, required format-support bits, context-state
  creation and individual NV12/P010 UINT plane SRV/RTV results.
- Synthetic 1920×1080 P010 and NV12 frames are compared byte-for-byte with `cpu::apply` (threads 0),
  including the first differing plane/x/y if any. Timing uses changing seeds, five warm-up applies
  then 60 frames for 1080p P010, 4K P010 and 1080p NV12: CPU submission and GPU timestamp-query
  median/p95 in ms/frame. Disjoint samples are explicitly skipped; query polling has a bounded
  two-second wait and reports timeouts. P010 1080p CPU fallback reports ten-frame medians for
  threads 1 and 0 (auto, capped at four); input reset is outside the timed call.
- **Console results pending.** No console deployment or probe run is claimed. The probe does not
  enable GPU grain in playback. After wrapper integration, paused-frame `grain=dav1d` vs `grain=gpu`
  screenshots, CPU A/B (target −0.25 to −0.4 cores), late frames and Sample memory remain future
  gates, not results.

## 8. 4K memory cap
- 4K P010 ≈ 24.9 MB per texture vs 6.2 MB at 1080p. ~55 in flight would be ~1.3 GB of the 3.25 GB budget.
- Bale.Core picks decode-ahead from a texture budget: frames = clamp(budget / frame_bytes, 8, current),
  starting budget 640 MB (≈ 25 frames at 4K; 1080p unchanged). Tune from Device Portal numbers and
  GetExtendedMemoryInfo, since it is unclear whether GPU textures count in commit.
- Grain itself adds one intermediate texture plus ~24 KB per handle.

## 9. HDR / DV
- Grain works on coded values (PQ for HDR10), exactly as dav1d does; patch 0004 color attributes are
  unchanged. HDR10+ (T.35 dynamic metadata) is dropped → HDR10 base.
- Dolby Vision: no documented decoder or RPU path on Xbox (display LLDV mode only). 8.1 / 7 → HDR10
  base; profile 5 → server transcode. Possible later: our own GPU reshaping of profile 5. Not stage 2.

## 10. Build order (each step gated before the next)
1. **Implemented:** fixture script + Rust params/template/scaling/offsets/cpu/ffi + bit-exact tests
   in Linux and Windows CI. Local release oracles and Windows library/test cross-checks pass.
2. **Implemented:** HLSL + gpu.rs + additive GPU ABI + strict Windows lint and mandatory WARP tests.
   Real shader compilation and WARP results are gated by Windows CI; no console verification.
3. **Probe implemented:** hardware diagnostics, full-size correctness, GPU/CPU timing; console results pending.
4. Wrapper patch 0009 + `grain=` tune knob + packaging check; release with default dav1d.
5. Console screenshot identity, CPU A/B, memory; then default gpu.
6. 4K decode-ahead cap, measured with a 4K grain title.
