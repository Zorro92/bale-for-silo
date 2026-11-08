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
This diagnoses the early MF `E_INVALIDARG` seen with bale.6 P010 uploads; none of the variants is
runtime-verified. It ships in `2.1.0-bale.7`; debug `upload=on` aliases 1, and `upload=1..5` selects
an explicit variant. Only debug builds capture the safe `Bale ` log prefix; production logging stays off.
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
