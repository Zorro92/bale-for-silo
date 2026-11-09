# Native FFmpeg source patches

`build-ffmpeg-native.ps1` applies every `*.patch` here (sorted by name, `git apply` in
`Libs/ffmpeg`) before the unchanged upstream `Build-FFmpeg.ps1` runs.

- A patch may change only the files in `$allowedPatchFiles` in the build script (today
  `libavcodec/libdav1d.c`). No configure, build-flag, dependency or other library changes.
- Every change to this folder needs a new patch set name: pass it as the `patch_revision`
  workflow input (`p1`, `p2`, ...). It becomes part of the package version
  (`8.1.2-bale.dav1d.1.5.4.p1`) and release tag (`native-ffmpeg-8.1.2-dav1d-1.5.4-p1`), and
  `scripts/ffmpeg-native.json` records it as `patch_revision`.
- `SOURCES.txt` lists each patch with its SHA256, the patch files are release assets, and
  `ffmpeg-native-sources.tar.gz` is taken from the patched tree (LGPL corresponding source).
- Patches must keep default FFmpeg behavior unchanged for callers that don't opt in.

| Patch | Purpose |
|---|---|
| `0001-libdav1d-user-picture-buffers.patch` | When the caller replaces `get_buffer2`, libdav1d asks it for picture memory (validated against dav1d's allocator contract) and falls back to the internal pool otherwise. Used by the zero-copy path to decode into GPU-readable memory. |
