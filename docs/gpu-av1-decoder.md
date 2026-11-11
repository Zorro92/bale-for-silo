# GPU AV1 decoder

The GPU AV1 decoder work moved to its own repository, **rav1g**:
<https://github.com/Zorro92/rav1g>. It holds the code (a fork of AOMedia's av1-xbox-one with
upstream history), the CI build of the test player, and the full history, measurements and
reasoning (`docs/gpu-av1-decoder.md` there). This page keeps only the summary that matters for Bale.

## Why

Bale decodes AV1 in software with dav1d. Heavy 4K AV1 scenes stutter with dav1d on the console
(~6–7 cores busy), and the console has no AV1 hardware decoder under D3D11 or D3D12
(player-plan.md). A GPU (D3D12 compute) decoder is the only remaining route to smooth heavy 4K AV1.
Until it proves out, Bale keeps dav1d with the 1080p claim and the server transcodes 4K AV1.

## Status (2026-11-11)

Console, app mode, 4K 10-bit, 1 tile, 40 Mbps, 300 frames; 24 fps needs at most 41.7 ms per frame.

- D3D12 compute works in app mode.
- Upstream as shipped: 10–13 fps. CDEF alone took ~52 ms per frame.
- A rewritten CDEF shader takes 5.5–7.2 ms and is md5-exact. With it: 21–23 fps on an SVT-AV1
  low-delay clip (GPU busy ~31 ms, now limited by single-tile CPU entropy decoding at 34–38 ms) and
  16.5 fps on the Jellyfin test clip (GPU busy 50 ms: intra 21 ms, inter 19 ms).
- A restructured intra wavefront was bit-exact but 2.3–2.4x slower (negative result).
- 1080p24/30 decodes in real time.
- Some encodes with hidden frames deadlock on output-buffer starvation; not fixed yet.

## Gate for Bale

A Rust port and Bale integration come only once 4K GPU and CPU time per frame are well under
41.7 ms on heavy content. The integration plan from the scoping (Rust host with a C ABI next to
`bale_gpu`, frames delivered into the existing D3D11 sample path, debug opt-in first) is kept in
the rav1g history document.
