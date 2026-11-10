# FFmpegInteropX wrapper patches

The series: `0001` adds opt-in `bale_*` FFmpegOptions keys (decode-ahead frames, sws slice threads,
dav1d max frame delay), a demux lock for concurrent packet readers, and the decode-ahead worker.
`0002` adds `bale_decode_thread_priority` (0 off, 1 above normal, 2 highest): the decode-ahead
thread is raised directly, and threads created during libdav1d `avcodec_open2` or `sws_init_context`
are raised from a `DLL_THREAD_ATTACH` hook (`Source/BaleThreadBoost.h`).
`0003` adds opt-in `bale_decoder_use_max_resolution` (0/1) and `bale_decoder_max_dpb_count`
(0–16) hints to compressed/passthrough video encoding properties only, never FFmpeg-decoded output.
Both default to 0 (attributes unset); positive values insert the SDK `mfapi.h` GUIDs as UINT32s.
The debug `maxres` / `dpb` controls are live from `2.1.0-bale.3` onward.
Microsoft's Xbox HEVC recommendation (`maxres=1 dpb=3`) showed no benefit in console A/B; it stays unset.
`0004` backports upstream PR #482 (upstream `master` 959404b, merged as 3145014) unchanged: FFmpeg-decoded
output (`UncompressedVideoSampleProvider`, also the D3D11 path) now sets `MF_MT_YUV_MATRIX` from
`color_space` (previously never set), maps BT.709/BT.601 transfer to `MFVideoTransFunc_709` instead
of gamma 2.2, adds sRGB, BT.1361, SMPTE 428, BT.2020 constant-luminance, EBU 3213, XYZ and DCI-P3
mappings, and drops the reserved-primaries mapping. It is not a `bale_*` key and is always on;
compressed/passthrough output is unchanged. It first shipped in `2.1.0-bale.4`.
`0005` adds opt-in `bale_dav1d_export_film_grain` (0/1, default 0). When 1 and the video codec is
`libdav1d`, `AV_CODEC_EXPORT_DATA_FILM_GRAIN` is set before `avcodec_open2`: dav1d then skips grain
synthesis and attaches `AV_FRAME_DATA_FILM_GRAIN_PARAMS` instead, so frames are output grain-free.
It is a debug measurement of grain CPU cost (and groundwork for GPU grain); production never sets it.
It first shipped in `2.1.0-bale.5`; earlier releases reject the key as unknown.
`0006` adds opt-in `bale_d3d11_upload` (0/1, default 0) for software-decoded NV12/P010 output.
On first MSS start it acquires a separate device-manager handle, protects the shared immediate
context with `ID3D10Multithread`, and hands the device to the software provider (no AVHWDeviceContext).
The same copy/sws step writes directly into a three-texture STAGING ring; CopyResource + Flush
uploads to pooled DEFAULT/SHADER_RESOURCE textures returned as Direct3D11Surface samples.
Processed events return textures; seek/size changes detach tracking and discard the old pool so
in-flight MF textures are never overwritten. Any upload failure permanently restores CPU buffers
for that stream, including the current frame. No shader or grain change; the decoder stays software.
It first shipped in `2.1.0-bale.6`; Bale only sets it from debug `ffmpeg-tune`.
`0007` widens `bale_d3d11_upload` to 0–5 (default 0, CPU buffers). DEFAULT texture variants:
1 = SHADER_RESOURCE, 2 = DECODER | SHADER_RESOURCE, 3 = DECODER,
4 = RENDER_TARGET | SHADER_RESOURCE, 5 = SHADER_RESOURCE with RESOURCE_MISC_SHARED.
All other variants use MiscFlags 0; staging, decode, grain and failure-to-CPU behavior are unchanged.
Device/protection, declared output and pool descriptors, first three MSS handoffs/Processed callbacks
and failure HRESULTs use `av_log`, always prefixed `Bale D3D11 upload`, with no URLs or paths.
It first shipped in `2.1.0-bale.7`; only debug builds capture the safe `Bale ` log prefix;
production logging stays off. The owner's 1080p AV1 → P010 console tests found that MF accepts
variants 2/3/4 but rejects 1/5 with `E_INVALIDARG`. All five then fell back to CPU after 1–2 samples:
Processed tried to reopen MF's already-closed surface and got `RO_E_CLOSED` (`0x80000013`).
`0008` retains each tracked sample's texture, event token and diagnostic timestamp, returning that
stored texture without touching closed surface/timestamp properties. Unknown callbacks after Flush
are ignored; Flush drops tracked textures and the pool, never recycling across seek/size generations.
Upload returns check the full texture descriptor; hardware-decoder pool behavior is unchanged.
Callback/detach exceptions are logged once without disabling upload; synchronous D3D/MF failures
still permanently fall back to CPU. First-three diagnostics remain, plus one steady snapshot after
240 texture samples are issued by the uploader (including decoded lookahead):
`Bale D3D11 upload steady: issued=N processed=M pooled=P` (P is the free texture pool size).
It ships in `2.1.0-bale.8` and renumbers variants: 1 = RENDER_TARGET | SHADER_RESOURCE (`upload=on`,
ready for stage-2 grain rendering), 2 = DECODER | SHADER_RESOURCE, 3 = DECODER,
4 = SHADER_RESOURCE only (known MF failure), 5 = SHADER_RESOURCE + RESOURCE_MISC_SHARED (known MF failure).
`upload=on` aliases 1; `upload=1..5` selects explicitly; off/default remain CPU buffers, not variant 1.
The corrected recycling path still needs the owner's console verification.
`0009` adds opt-in `bale_film_grain_mode` (0 = dav1d default, 1 = Rust CPU, 2 = GPU).
It loads `bale_gpu.dll` once per process with `LoadPackagedLibrary`, requiring grain ABI 1 and all six
exports; a missing/incompatible DLL keeps dav1d synthesis. Selection happens before `avcodec_open2`:
only software `libdav1d`, 4:2:0 8/10-bit input and matching NV12/P010 output can export for Rust grain.
GPU mode requires upload variant 1 (RENDER_TARGET | SHADER_RESOURCE, Bale's production upload default);
upload off or another variant selects CPU grain instead. The decoder remains software.
Per-frame AV1 parameters are mapped and validated; grain touches only Bale-owned output, never the
decoder AVFrame (dav1d may retain it as a reference picture). GPU grain replaces CopyResource into the
pooled texture; CPU grain runs in mapped staging when upload is on, or in a copied/repacked CPU buffer
when off. Grained frames skip direct-buffer output. Scaled or unsupported output shows ungrained.
GPU create/apply failure overwrites the failed target with the clean staging copy for that sample and
permanently selects CPU grain for the stream; upload failure retries the same frame in a CPU buffer.
CPU grain failure refills clean output. Each runtime fallback is logged once per stream with fixed
diagnostics and HRESULT only when available, never parameters, paths, URLs or exception text.
`bale_dav1d_export_film_grain` still wins as export-only, suppressing both dav1d and Rust synthesis.
It ships in `2.1.0-bale.9`; that release kept production grain on dav1d pending console integration verification.
`0010` changes the GPU fallback policy after console step 5 measured dav1d grain at ~1.1 cores versus
Rust CPU grain with upload at ~1.9 cores (1080p). At open, GPU requests without upload variant 1
select mode 0 and leave export off, keeping dav1d synthesis; explicit CPU requests stay CPU.
After a stream starts in GPU mode, GPU create/apply failure or upload loss permanently selects
Rust CPU grain at 1080p and below, or mode 0 (ungrained exported frames) above 1080p
(width > 1920 or height > 1080, captured from the codec context when creating the provider).
The current CPU-buffer retry also clears cached grain after a mode-0 downgrade; subsequent frames
skip `Prepare`. Dav1d cannot resume synthesis without reopening once export is enabled. Existing
once-per-stream guards and fixed HRESULT-only diagnostics distinguish CPU from ungrained fallback;
seeks do not reset the downgrade. Explicit CPU streams are unaffected. It ships in `2.1.0-bale.10`.
`0011` adds a debug-only fault: `bale_d3d11_upload_fault_frames=N` (0..999, default 0 = off) makes
the upload after N successful uploads throw `E_OUTOFMEMORY` at the same point as a real pool texture
failure (staging mapped and filled), so the existing fallback disables upload and retries that frame
in a CPU buffer. It exists to test the mid-stream surface-to-buffer switch without real memory
pressure; Bale sets it only from the DEBUG_SERVER `uploadfault` tuning. It ships in `2.1.0-bale.11`.
`0012` adds debug-only `bale_d3d11_repack` (0/1, default 0). When 1, upload variant 1 is active, the
`bale_gpu.dll` repack ABI 1 loads (independently of the grain exports), the input is `yuv420p` to NV12
or `yuv420p10le` to P010 at output size, and CPU grain isn't selected, the decoder planes are copied
row by row into the unchanged staging ring instead of running swscale: Y as decoded, and each chroma
row holding U from byte 0 and V from half the texture row. Two `bale_gpu` pixel shaders then shift
(P010) and interleave into the pooled render-target texture. GPU grain repacks into a Bale-owned
scratch texture first; a failed grain pass repacks the target clean. YUVJ input is excluded because
swscale converts its range. Any failure in a planar attempt (including a non-repack upload failure)
permanently disables the repack for that stream and retries the same frame through swscale; the existing
upload fallback still applies after that. The handle is created on the first planar frame (readback
self-test) and survives seeks. Logs are fixed `Bale D3D11 repack ...` lines with HRESULTs only.
Bale sets it only from the DEBUG_SERVER `repack` tuning. It ships in `2.1.0-bale.12`.
`0013` adds diagnostic timing only, with no option and no behavior change. The upload producer sums
QueryPerformanceCounter wall time per stage (gap since the previous upload, staging Map, fill or
swscale, Unmap, GPU copy/repack/grain, Flush, sample creation) and logs average/max ms every 240
frames as `Bale D3D11 upload timing: ...`. The decode-ahead worker logs `Bale decode-ahead timing: ...`
every 240 samples: GetNextSample time, idle time while the queue is full, and how often and how long
the MF request thread waited on an empty queue. Lines hold numbers only. It ships in `2.1.0-bale.13`.
`0014` speeds up the debug-only repack fill. 0013 showed the single-threaded planar copy into the
staging ring took 23-40 ms per 4K frame on the console (swscale on 4 threads: 9-16 ms), which
starved decode-ahead. `FillPlanar` now splits each plane's rows into `bale_sws_threads` slices
(no new option) run by `BaleCopyPool`, a small fork/join pool owned by the video provider: the
caller copies slice 0, workers (started on the first planar frame, `bale_decode_thread_priority`
boost, joined on destruction) copy the rest. If a worker cannot start, the remaining slices are
merged. Output bytes are unchanged. It ships in `2.1.0-bale.14`.
`0015` adds debug-only `bale_zero_copy` (0/1, default 0), honored only with `bale_d3d11_upload` > 0
outside frame grabbers. It needs the native FFmpeg `p1` patch (`../ffmpeg-native-patches`), which
lets libdav1d take picture memory from a replaced `get_buffer2`. For software `libdav1d`
`yuv420p10le` to P010 output the provider installs such a hook before the first decode. Once
`SetUploadDevice` has created a `bale_gpu.dll` zero-copy ABI 1 handle (the app must first register
`d3d12.dll` with `bale_zc_register_d3d12`; create runs a readback self-test), each picture is one
GPU-visible buffer from `bale_zc_alloc`, freed through `bale_zc_release` by the AVBuffer callback.
The hook declines (dav1d then uses its own pool for that picture) without a handle, for other
formats, or when the bounded pool is full. Upload then calls `bale_zc_repack`, which converts the
buffer on the GPU into a borrowed P010 landing texture; it is copied (or GPU-grained) into the
pooled texture with no staging Map or CPU fill. A picture from dav1d's own pool or a refused layout
takes the existing staging paths for that frame; any other repack failure, and every upload
failure, destroys the handle for the stream (buffers dav1d still holds stay valid). CPU grain
frames use staging. Timing lines report the path as `repack=2`; fixed `Bale zero-copy ...` lines
log create/disable/HRESULTs and pool counters after 240 frames and at teardown (`landing_copies`
counts frames that needed bale_gpu's extra GPU copy because the device cannot share a UAV P010
texture). It ships in `2.1.0-bale.15`.
`0016` widens `bale_d3d11_upload` to 0–6. Debug variant 6 is variant 1 (RENDER_TARGET |
SHADER_RESOURCE) plus UNORDERED_ACCESS, with `MISC_SHARED_NTHANDLE | MISC_SHARED`: the flags
D3D12 needs to open and write a pooled texture directly, without the landing-texture copy. It
tests whether MF accepts such samples. GPU grain and repack accept variant 6 like variant 1;
zero-copy already runs with any upload variant. Production kept variant 1 then (6 since 2026-11-09,
after `0018`). It ships in
`2.1.0-bale.16`.
`0017` lets zero-copy write the pooled texture itself when the upload variant makes it a
D3D12-shareable UAV (variant 6) and `bale_gpu.dll` exports `bale_zc_repack_into` (optional, same
ABI 1): the D3D12 repack targets the pooled texture and the landing texture and D3D11 copy are
skipped. GPU-grain frames still land, since grain reads the clean frame. A decline (unfit texture,
D3D12 cannot open it, too many open) returns the texture to the pool and lands the frame instead,
logged once; frame properties are read once, after the write succeeded. Timing lines report
`repack=3`; the stats line adds `direct=`. It ships in `2.1.0-bale.17`.
`0018` sends GPU-grain frames (grain mode 2) through that direct write too, passing the grain
parameters to `bale_zc_repack_into`, which fuses grain into the same D3D12 dispatch (bit-exact
with the CPU oracle; `bale_gpu.dll` self-tests it at create). A `bale_gpu.dll` that cannot fuse
grain (older, or its self-test failed) declines, and the frame lands and gets the D3D11 grain pass
as before; that decline is logged once, separately from the plain one. CPU grain (mode 1) still
skips zero-copy. The stats line adds `direct_grain=`. It ships in `2.1.0-bale.18`.
`0019` takes 8-bit 4:2:0 libdav1d pictures through zero-copy too. With zero-copy on, upload
variant 6, 10-bit output allowed and a `bale_gpu.dll` exporting `bale_zc_repack_into_bits`, the
pre-open check selects P010 output for such a stream (instead of NV12). The allocator then hands
dav1d byte-stride buffers, and `bale_zc_repack_into_bits(..., 8, ...)` writes each sample as
`v << 8`, which maps limited range 16..235 exactly onto P010's 64..940. GPU grain (8-bit
parameters) is fused into that write; CPU grain keeps dav1d's synthesis. 8-bit pictures never
land (`bale_zc_repack` reads 10-bit samples): a declined write uses staging (swscale to P010),
logged once, and shows that frame ungrained, since staging grain only matches NV12/P010 by depth.
YUVJ, frame grabbers and other upload variants keep NV12. The stats line adds `eight_bit=`; the
active line reads `direct write, 8-bit`. It ships in `2.1.0-bale.19`.
`0020` makes software libdav1d with zero-copy requested (`bale_d3d11_upload` and `bale_zero_copy`
on, not a frame grabber) zero-copy only. It removes threaded swscale, the staging upload, upload
variants 1-5, upload fault injection, the debug D3D11 repack (`BaleCopyPool.h` is deleted) and CPU
film grain: any nonzero `bale_d3d11_upload` now means the D3D12-writable pool texture (former
variant 6), and the grain loader no longer needs `bale_grain_cpu_apply` and accepts grain ABI 1 or 2
(a requested CPU grain mode selects GPU grain). Streams zero-copy cannot take (no `bale_gpu.dll`
zero-copy ABI, a pixel format other than 10-bit or 8-bit-as-P010 4:2:0, requested GPU grain
unavailable) are refused at open; a refused default stream, or no video stream left, fails the open
with `MF_E_UNSUPPORTED_FORMAT`. Runtime failures (upload device or zero-copy setup, a declined or
refused write, GPU grain, device loss) latch a fatal HRESULT in the provider, and
`FFmpegMediaSource` raises `MediaStreamSource.NotifyError` once; the app then replans to a server
transcode. Pictures outside the zero-copy buffers are dropped without a sample (`BaleFrameDropped`);
more than 8 in a row are fatal (`ZeroCopyMissed`). 8-bit frames never stage or land. The upload
device is initialised for zero-copy streams even when another stream opened a hardware context.
The stats line adds `dropped=`. The `bale_sws_threads`, `bale_d3d11_upload_fault_frames` and
`bale_d3d11_repack` keys are now rejected. Other streams keep the upstream CPU output path. It ships
in `2.1.0-bale.20`.
`ffmpeginteropx-wrapper.yml` applies every `*.patch` in ordinal
filename order with `git apply --index`, failing on any rejected patch. Use numbered filenames
(e.g. `0001-threaded-swscale.patch`); names and SHA256 hashes are recorded in `SOURCES.txt`.

Patches may touch only `.cpp` and `.h` files under upstream `Source/`. Never change the IDL,
project files, build flags or dependencies: the original 2.1.0 winmd and managed projection are
retained. The pipeline rejects other paths and compares the built and original public type names.
An API/ABI change needs a separate projection/package design, not an exception to this check.

The upstream commit and original package hash are pinned in `../ffmpeginteropx-wrapper.json`.
Native FFmpeg is restored using `../ffmpeg-native.json` and is never rebuilt by this pipeline.
See `docs/player-plan.md` for the manual build/release procedure.
