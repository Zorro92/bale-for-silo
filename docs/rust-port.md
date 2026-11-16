# Porting Bale's C# logic to Rust

Direction (owner, 2026-11-10, confirmed 2026-11-15): new code in Rust where possible, existing C#
ported where it makes sense, GPU work in D3D11 by default (D3D12 only where D3D11 cannot do the job).

## Where rendering already is

Everything Bale draws itself already runs on the GPU, most of it from Rust (`native/bale-gpu`):
tone mapping, AV1 film grain, the AV1 zero-copy repack (D3D12, Series only) and the subtitle
overlay (D3D11 swap chain over the video; libass rasterises glyphs on the CPU, which is inherent).
The XAML UI is drawn by the system compositor on the GPU; only layout and C# logic use the CPU.
No page does hand-written CPU drawing.

What stays CPU-bound and is **not** part of this port:

- Software video decode (dav1d AV1, FFmpeg MPEG-4 Part 2). GPU AV1 decode is rav1g's job.
- Poster/backdrop JPEG decode by XAML. Memory, not CPU, is the issue there (`docs/ui-memory.md`, 0.2.x).
- The XAML UI itself. A Rust D3D11 UI would have to rebuild text, controller focus navigation, the
  on-screen keyboard and accessibility for little gain, since XAML already composites on the GPU.

## What gets ported

Pure decision logic in `src/Bale.Core`, in slices. Each slice:

1. Moves into a new pure-Rust crate, `native/bale-core` (no Windows dependencies), built as a
   cdylib for `x86_64-pc-windows-msvc` (packaged as `bale_core.dll`, static CRT like bale-gpu)
   and for Linux (`libbale_core.so`), so `tools/Bale.Cli` and the Linux test run keep working.
2. Keeps its C# public surface as a thin wrapper, so callers and the existing C# tests do not
   change. The existing tests become the parity suite: a slice is done when they pass against Rust.
3. Crosses the C ABI with JSON (serde on the Rust side, System.Text.Json source generation on the
   C# side). These calls run once per playback decision, not per frame, so the cost is negligible
   and the ABI stays two functions: `bale_core_call(name, json) -> json` and `bale_core_free`.
   Errors come back as a JSON error value; Rust panics are caught at the boundary.
4. Adds Rust unit tests for anything the C# tests do not cover.
5. Keeps the C# original in `tests/Bale.Core.Tests/Reference/` and compares it with the Rust port on
   thousands of random inputs (`RustPortDifferentialTests`). Delete a reference once its slice has
   shipped and been verified on the console for a while.

### Wire details

- Server models (plans, file versions) cross in the server's own JSON, written by Kiota's
  serializer; Rust declares only the fields it reads. Bale's records cross snake_case; Rust gives
  them no defaults and C# rejects unknown result keys, so a misnamed field fails loudly.
- Doubles may be NaN or infinite (tests and replans produce them): both sides use .NET's named
  literals ("NaN", "Infinity"), never `double.ToString()` ("∞").
- Results deserialized into C# records must carry every init-only property: System.Text.Json's
  generated code sets a missing one to null/false, not to its C# initializer (this broke
  `ConsoleEvidence.Default`'s empty lists until Rust wrote them all).
- serde_json needs `float_roundtrip`: its default float parser can be one ULP off, so a double
  arriving from C# could differ from the one sent (caught by the formatter test on large values).
- Text Rust formats for C# must follow .NET's rules: `"0.0"` rounds 15 significant digits half away
  from zero, and the invariant culture writes infinity as "Infinity" (`dotnet::format_fixed`).
- Never return a non-finite double through JSON: serde_json writes NaN and infinity as `null`.
- A C# `InvalidDataException` becomes `{"invalid_data": message}` and is rethrown unchanged.

### Slices, in order

1. **Infrastructure** (done, 60c2c82, verified on the console in 0.1.524). Crate, CI (fmt, clippy
   and tests on Linux; Windows build with static-CRT and export checks), packaging, C# loader, ABI
   version check, `SoftwareDecodePolicy.NormalizeCodec` end to end. The C# test
   `LowerCasesLikeToLowerInvariant` caught a casing difference (.NET leaves U+0130 unchanged), so
   each ported string helper gets a test against the .NET original.
2. **Decoder and route policy** (done, 8c9c4e2, verified on the console in 0.1.525): `SoftwareDecodePolicy`,
   `HardwareDecodeLimits`, `PlaybackRoutePolicy`, `ConsoleToneMapPolicy`, `DolbyVision7Policy`,
   `DisplayHdrPolicy`, `DisplayMatchPolicy`. `PlaybackFormatting.Decodes`/`IsDolbyVision` stay C#
   for now; `CanManage` passes their answers in.
3. **Capability claims** (done, d3050cb; routes verified on the console in 0.1.526, same six titles
   as slice 2). Open: the first launch after installing 0.1.526
   died silently (no log line, no dump) during the first debug `play`, after the debug server had
   timed out twice. Follow-up (same day): three cold stop/launch cycles with
   the same commands did not crash. Reinstalling the *same* version 0.1.526 then left debug port 8790
   silently dropped while Bale ran normally and logged "listening"; relaunches, further reinstalls
   and a full console restart did not clear it, and the PC side was ruled out. Installing a newer
   build (0.1.527, a CI re-run of the same commit) fixed it at once, and its first launch played
   normally. So same-version reinstalls break the package's inbound networking until the next real
   update; the Rust crates do no networking. The first-launch exit stays unexplained and has not
   recurred. `ConsoleEvidence` (baseline, probe-based
   claims, AV1/MPEG-4 software claims). `PlaybackClaims` stays C#: it is Xbox-app glue (async probe,
   UI thread, logging) around these calls.
4. **Plan handling, pure parts** (done, ab3d951; verified on the console in 0.1.529: six titles,
   embedded PGS and an SRT sidecar; owner's scope 2026-11-15):
   `PlaybackOriginalAudioSelection.ResolveIndex`, `PlaybackVersionNotice.Describe`,
   `PlaybackSubtitleResolver.Format`/`ResolveEmbedded`, and `PlaybackFormatting.SubtitleLanguageKey`
   with its alias table. `ResolutionRank` and `Decodes` stay C# (passed in) until a Detail slice.
   Kept in C#: `PlaybackMediaUrl` (a security check that must use the same `Uri` parser as the code
   that fetches), and `PlaybackStartBuilder`/`PlaybackReplan` (they reshape the C# request records
   the C# HTTP client sends; revisit only if requests move to Rust).
5. **Meters and timing** (done, 56f5e34; verified on the console in 0.1.530: AV1 frame meter and
   late-frame trace, a progressive seek re-anchor, a 190 Mbps speed test, live bandwidth): `PlaybackFrameDeliveryMeter`,
   `PlaybackLateFrameTrace`, `PlaybackFrameRecordProcessor`, `PlaybackThroughputMeter`,
   `ConnectionSpeedMeter` and `PlaybackSeekReanchor`. The per-frame and per-read meters use a second
   ABI (`abi_meters.rs`): Rust owns each meter behind a handle (a `SafeHandle` in C#, freed by the
   GC), calls take `#[repr(C)]` structs and numbers, no JSON. ABI version 2.

6. **Detail helpers** (done, 355a0e1; verified on the console in 0.1.531: six titles' Auto versions
   and routes, last-played precedence over display matching; owner 2026-11-15):
   `PlaybackFormatting.SelectVersion` (with Auto's display match), `Decodes`, `IsDolbyVision` and
   `ResolutionRank`. `CanManage` and the version notice now compute these in Rust instead of taking
   C#'s answers. The resolution regex follows .NET's Unicode `\d`/`\b`
   (`unicode-general-category`), and frame rates parse with .NET's `NumberStyles.Float` grammar.
   Detail label text (`VersionCompactLabel` and friends) stays C#.

7. **HTTP streams** (owner 2026-11-15: "skeptical, but try it"). Step 0 is a go/no-go spike, not a
   port: `bale-debug.sh rusthttp` downloads the speed-test probe with a Rust HTTP + TLS stack (ureq,
   rustls, bundled webpki roots) inside the app, with the exact headers the C# client would send, and
   reports throughput and the thread's CPU time. Compare with `speed test` (C# `SocketsHttpHandler`),
   running `threads` alongside each. Port only if Rust connects from the app container and is at least
   as fast for no more CPU; otherwise remove the spike (ureq, `http_probe.rs`, `RustHttpProbe`, the
   debug route) and keep the streams in C#.

   Spike results (console, 0.1.532, 2026-11-15, remote server over wired Ethernet, app idle on Browse):

   | Run | Throughput | CPU over a 12 s sample |
   |---|---|---|
   | C# `speed test` 1 / 2 | 149 / 229 Mbps (last 3 s) | 0.017 / 0.035 cores (process) |
   | Rust `rusthttp` 1 / 2 | 177 avg, 189–190 Mbps last 3 s | 0.012 cores (process); thread 140–250 ms per 10 s |

   Rust connects from the app container (TLS with bundled roots works) and matches C# throughput, for
   somewhat less CPU per byte. At real stream bitrates (10–80 Mbps) either costs well under 0.01 cores.
   A third Rust run got HTTP 429: the probe endpoint rate-limits repeated tests.

   During real playback (0.1.532, 20 s `threads` samples, start=600 s): 4K HEVC original at 18.6 Mbps
   (FFmpeg demux, D3D11 decode, console tone map) used 0.36 cores in all, 0.26 of it on the unnamed
   system threads (.NET thread pool, Media Foundation, FFmpeg's demuxer and the network together) and
   0.075 on the UI thread. 1080p AV1 at 2.3 Mbps used 0.52 cores, 0.42 of it dav1d. Scaling the spike's
   cost per byte, the network share at 18.6 Mbps is about 0.003 cores, around 1% of playback's CPU.

   Owner's goal is control and lower latency/jitter, not CPU, so the next measurement is the player's
   own fetch pattern: `bale-debug.sh fetchbench` replays HttpRangeStream's 1 MiB range requests, three
   in flight, through the player's C# transport and through Rust, in four separate regions of the
   playing file (C#, Rust, Rust, C#), and compares time-to-first-byte and block-time tails.

   First run (0.1.534, 4K HEVC original playing, two rounds of 48 × 1 MiB, 3 in flight): medians were
   equal (time to first byte ~153 ms for both: the round trip to the remote server), but Rust's tail was
   worse (TTFB p95 290–615 ms vs 170–214; blocks over 500 ms: 3–4 per Rust run vs 0–1 per C# run). Each
   Rust run built a fresh connection pool, so its first blocks paid TCP + TLS handshakes while C# reused
   the player's warm pool. The benchmark now keeps one Rust pool for the app's lifetime and warms one
   connection per worker before timing.

   Warm rerun (0.1.535, three rounds, 12 runs of 48 × 1 MiB during 4K playback):

   | | TTFB p50 | TTFB p95 | TTFB max | Block p95 | Blocks > 500 ms | Throughput |
   |---|---|---|---|---|---|---|
   | C# (6 runs) | 144–147 ms | 168–232 | 235–580 | 280–603 | 5 in total (3 in one run) | 98–109 Mbps |
   | Rust (6 runs) | 162–167 ms | 220–259 | 233–271 | 299–737 | 3 (all in the first run) | 87–104 Mbps |

   Mixed: Rust's median time to first byte is ~18 ms higher in every run, its worst first byte is
   steadier (C# has occasional 480–580 ms spikes), and block tails and throughput are the same within
   noise. No clear latency win for a port.

   Owner 2026-11-15: lean towards Rust to keep GC out of the playback read path, which only works if
   Rust owns the whole path (its own IRandomAccessStream handed to Media Foundation/FFmpegInteropX,
   block cache, HTTP); C# would only create it and push refreshed auth headers. Step 1 before that
   port: close the ~18 ms median. ureq already sets TCP no-delay, but resolves DNS before looking in its
   pool (a lookup, on a spawned thread, per request); the benchmark agent now uses a caching resolver.

   With the caching resolver (0.1.536, three rounds during 4K playback):

   | | TTFB p50 | TTFB max | Block max | Blocks > 500 ms | Throughput |
   |---|---|---|---|---|---|
   | C# (6 runs) | 150–153 ms | 170–546 | 343–1224 | 3 (in 2 runs) | 93–115 Mbps |
   | Rust (6 runs) | 152–163 ms | 217–457 (217–255 after the first run) | 287–1058 | 4 (all in the first run) | 88–113 Mbps |

   The median gap fell from ~18 ms to ~5 ms, inside run-to-run noise. After its first run Rust had no
   block over 500 ms and its worst first byte stayed at 217–255 ms; C# still had occasional 0.5–1.2 s
   blocks. Step 1 passes; the stream port (steps 2–6) may go ahead.

   Step 2, the Rust range stream (`range_stream.rs`, handle ABI `abi_stream.rs`, C# `RustRangeStream`
   via `PlaybackMedia.OpenNativeStreamAsync`): HttpRangeStream's 1 MiB blocks, 32-block LRU, 2–16
   prefetch, three fetch workers, one demand retry, a joined failed prefetch retried as demand, and
   the sequential fallback (reopen past 256 KiB forward or backward). Readers share blocks by `Arc`,
   so an evicted block lives until its last reader drops it (C#'s leases). `close` wakes blocked reads
   before the SafeHandle frees the handle. Headers come from `SiloClient.CaptureHeadersAsync` for the
   media's captured profile plus the plan's stream headers. Tests (`RustRangeStreamTests`) run both
   stacks against a real loopback HTTP server: byte-exact random reads, one fetch per block, paced
   prefetch hits (9 of 10 blocks for both), the no-range fallback, retries, invalid ranges, waking a
   stalled read, and identical identity/credential headers. Deliberately breaking prefetch, the demand
   retry or the Content-Range check fails them.

   Not yet matched (step 6): the C# client also sends Kiota's User-Agent, offers compressed encodings,
   and gets Kiota's automatic retries on 429/503 with Retry-After. (The 401 refresh is step 4, below.)
   Not used by playback yet.

   Step 3, the Rust IRandomAccessStream (`winrt_stream.rs`, windows-rs `#[implement]`): `ReadAsync`
   returns an `IAsyncOperationWithProgress` run on the Windows thread pool that reads the range stream
   straight into the caller's `IBuffer` memory (`IBufferByteAccess`), filling the request unless it is
   `Partial` (C# `StreamFill`). The buffer crosses to the pool thread as an `AgileReference`. Seek and
   Position share one atomic position; `Close` marks only the adapter closed, as the C# adapter does.
   The handle became a shared reference (`Arc`) so the WinRT object keeps the stream alive.
   `MediaRangeStream` is the common base of both streams, so the player and its stats take either.
   Debug switch: `bale-debug.sh stream-adapter rust` opens the Rust range stream and hands Media
   Foundation or FFmpegInteropX the Rust stream; default stays `pooled` (C#).

   First console run (0.1.538, 4K HEVC original, 25 s, same position, alternating): the Rust stream
   played, but with 24–34 stream waits totalling 9–10 s against C#'s 3 waits and 1–1.6 s, and a per-fetch
   rate of ~15 Mbps against 136–159. Cause: block fetches stopped reading at the last expected byte, so
   ureq never saw the body end and closed the connection instead of pooling it; every block paid a new
   TCP + TLS handshake and slow start (loopback test: 11 connections for 10 blocks). Fixed by reading to
   the end of each response; `BlockFetchesReuseConnections` guards it.

   Retest (0.1.539, `stream-adapter rust`): parity with C#.

   | Run (25 s or a seek sequence) | C# stream waits / total | Rust stream waits / total |
   |---|---|---|
   | 4K HEVC from 50:00, round 1 | 3 / 1.15 s | 3 / 1.51 s |
   | 4K HEVC from 50:00, round 2 | 5 / 0.94 s | 5 / 1.04 s |
   | 4K VP9, three seeks | 17 / 4.1 s | 20 / 3.8 s |

   Also on Rust: Xvid AVI and 1080p AV1 software decode (0 late frames), H.264 system decode, three
   seeks per title, four rapid stop/start cycles with no hang, no errors logged. Not yet exercised:
   the Media Foundation (non-FFmpeg) route, for want of a library original that takes it.

   Step 4, credentials (the C# `SiloRequestHandler` rules). A 401 makes Rust call the owner's callback
   (`bale_core_range_set_refresh`, an `[UnmanagedCallersOnly]` C# method) with the generation of the
   rejected headers, once per generation however many fetches met it, and wait up to 15 s for
   `set_headers`; the request then retries once, and a second 401 is `Unauthorized` (7, an
   `HttpRequestException` with status 401), never retried again by the demand-fetch retry. C# forces
   the refresh with the rejected bearer (`CaptureHeadersAsync(..., rejectedAccessToken)`, so a token
   another request already rotated is reused), and ignores callbacks for headers it already replaced.
   On a failed refresh it hands back the old headers so the read fails now rather than after the
   wait. A 401 at open (no callback yet) is refreshed and retried from C#. A one-minute timer also
   hands over a token the session refreshed ahead of expiry (two-minute margin), so playback should
   not normally meet a 401 at all. `close` clears the callback under its lock before returning, so
   the C# GCHandle (weak) is freed only after the last possible call. Loopback tests cover a
   mid-stream 401, a second 401, a 401 at open, the timer hand-over and a stale callback; removing the
   retry, the stale check or the hand-over fails them.

### Stays in C#

- The XAML app (`src/Bale.Xbox`) and anything that drives XAML objects.
- The Kiota-generated Silo API client (`src/Bale.Core/Api`) and the feeds built on it.
- `DebugServer` (debug-only, mostly drives C# objects).
- Logic that is only glue between C# objects.

## Rules

- A slice that changes behaviour is a bug, not a port. Behaviour changes go in their own commits.
- The software-decode allowlist and every other owner-set policy keep their comments in Rust.
- Never log tokens, URLs, headers or media titles from Rust either.
