# FFmpegInteropX wrapper patches

The series: `0001` adds opt-in `bale_*` FFmpegOptions keys (decode-ahead frames, sws slice threads,
dav1d max frame delay), a demux lock for concurrent packet readers, and the decode-ahead worker.
`0002` adds `bale_decode_thread_priority` (0 off, 1 above normal, 2 highest): the decode-ahead
thread is raised directly, and threads created during libdav1d `avcodec_open2` or `sws_init_context`
are raised from a `DLL_THREAD_ATTACH` hook (`Source/BaleThreadBoost.h`).
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
