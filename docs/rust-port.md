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

   Step 5, the forward-only stream (`progressive_stream.rs`, `abi_progressive.rs`): one response with
   no Range, only a 200 accepted (409 `RangeRefused` = 8, 401 refreshed and retried once at open). A
   pump thread reads 64 KiB chunks into a 256-chunk queue (16 MiB, as the C# channel); reads take the
   front chunk and never fail, ending the data at the body's end, after a fault or once closed, with
   `faulted` and `completed` as in `ProgressiveHttpStream`. One request carries the whole title, so it
   has no overall deadline, only a 30 s stall limit per body read (`timeout_per_read`); the C# stream
   waits for the server's stall reap instead. `close` frees the queue and wakes the reader; a pump
   inside a body read leaves when that read returns. The WinRT `IInputStream` (`winrt_stream.rs`)
   never faults, reading the buffer's capacity before touching its length, as `ForwardInputStream`
   does. C# shares the contract through `MediaProgressiveStream`; `stream-adapter rust` opens it.
   Loopback tests run both stacks for a full read, a truncated body and a 409, plus a 401 at open,
   close waking a blocked read, and the read-ahead bound; making truncation look complete, unbounding
   the queue or not waking readers on close fails them.

   Console A/B (4K HEVC, `delivery progressive`, `stream-adapter pooled|rust`, two rounds each): both
   played 40 s plus three reanchor seeks and a 20 s pause with no stall (position kept pace with the
   clock in every sample) and no errors; CPU was the same. Rust seeks were first ~0.4 s slower: opening
   the remux took 0.72–0.99 s against C#'s 0.33–0.42 s. A seek drops the remux's unfinished
   connection, and the Rust pool then had none, while the C# pool had just been warmed by the replan
   request, so every Rust open paid TCP and TLS handshakes. `RustTransport.WarmConnection` (a small GET
   at the public `/api/v1/health` route, read to its end) now runs when a Rust playback attempt starts,
   when a reanchor starts, and after each Rust progressive open, and idle connections live 60 s as in
   SocketsHttpHandler (ureq's default is 15 s, which expired spares between seeks). On 0.1.544 the
   open after a seek takes 0.30–0.38 s (C# 0.33–0.35 s); a start right after launch can still pay one
   cold connection (0.75 s, then 0.36 s).

   Step 6, the wire gaps, measured on the C# stream over the loopback server first. Every C# media
   request carries `User-Agent: kiota-dotnet/<version>` and `Accept-Encoding: gzip, deflate, br`, and
   Kiota's RetryHandler retries 429/503/504 three times, after Retry-After (seconds or an HTTP date)
   or else 3, 6 and 12 s, adding `Retry-Attempt: n`; when the retries run out the read fails without
   the stream's own demand retry (four requests in all). Rust now does the same (`http::retry_delay`,
   `call_with_retry`, error `Busy` = 9 mapped to a 503 HttpRequestException) for block fetches, the
   open probe, the sequential reopen and the progressive open; a range stream's `close` cuts a retry
   wait short. The User-Agent comes from capturing headers through Kiota's default handler chain.
   Deliberately not copied: Accept-Encoding, since Silo serves media identity-encoded whatever the
   request asks (`apiv2/encoding.go`) and the Rust agent decodes nothing; and a C# quirk where a retry
   repeats the Silo identity headers (`X-Silo-Client: Bale, Bale`), because the retry passes through
   `SiloRequestHandler` again. Tests run both stacks for a retried 429/503 and for four busy answers;
   ignoring Retry-After or demand-retrying a busy block fails them.

   Final console A/B (0.1.546, `stream-adapter pooled|rust`; 30 s of play, then three seeks; stream
   waits as count / total wait):

   | Title | C# play | Rust play | C# after seeks | Rust after seeks | Seek time C# / Rust |
   |---|---|---|---|---|---|
   | 4K HEVC direct, round 1 | 4 / 1.31 s | 5 / 1.17 s | 12 / 2.61 s | 10 / 2.86 s | 0.54–0.68 / 0.69–0.96 s |
   | 4K HEVC direct, round 2 | 3 / 1.12 s | 5 / 1.56 s | 8 / 2.04 s | 9 / 2.56 s | 0.58–0.79 / 0.55–0.86 s |
   | 4K VP9 direct | 8 / 1.65 s | 8 / 1.54 s | 29 / 3.94 s | 25 / 3.31 s | 0.64–1.07 / 0.72–0.84 s |
   | 1080p AV1 software | 3 / 0.97 s | 3 / 1.57 s | 7 / 1.91 s | 6 / 2.29 s | 0.78–1.20 / 0.80–0.89 s |
   | 4K HEVC progressive | no stall | no stall | no stall | no stall | 2.70–3.03 / 2.88–3.25 s |

   AV1 had 0 late frames on both; pause and resume worked on both; CPU was within noise; no errors in
   the log over 47 Rust mounts. Verdict: parity, no measurable win or loss on this network.

   Decision (owner, 2026-11-15): the Rust streams are the default (f899b54), in release builds and as
   the debug switch's default; `stream-adapter pooled|builtin` still selects the C# streams. Checked on
   0.1.547: with no switch set, direct play logs `stream adapter=rust` and the forward-only delivery
   `rust-forward`. bale_core already carries route policy and the other ported slices, so there is no
   separate C# fallback if it fails to load. API and JSON calls stay on the C# HttpClient (with its
   gzip/deflate/br decompression); media GETs have no body to compress.

   Also from this decision: the console tone map now covers `server_remux_progressive` (af495b0), whose
   video is the source's own stream copy. On 0.1.547 the HLG title through the forced forward-only
   delivery plays via FFmpeg d3d11va with `Bale tonemap active` and a correct picture. Since 2026-11-16
   the HLS remux takes it too, through FFmpeg (see "HLS through FFmpeg" below).

   **HLS through FFmpeg** (owner, 2026-11-15; the default for every build since the console A/B
   below, owner 2026-11-16; Windows' HLS and its `hls-engine` debug switch were removed 2026-11-17). Silo's HLS is simple (checked in silo-server): one rendition, never an
   adaptive ladder (a multivariant playlist only attaches subtitle groups to its single variant);
   media playlists are complete VOD lists (`copy_keyframe_plan.go`, `GenerateFullManifest`) or
   FFmpeg's own append-only EVENT playlist while a copy remux is produced
   (`stabilizeCopyHLSRemountTimeline`); segments are MPEG-TS, or fMP4 after one EXT-X-MAP init, and
   either concatenates into one stream; long queries ride on EXT-X-DEFINE `{$silo_query}`. So no HLS
   client logic is needed beyond reading the playlist:

   - `hls.rs` parses it (single variant, EXT-X-DEFINE NAME/VALUE, VOD/EVENT, EXT-X-MAP) and refuses
     what it can't play (keys, byte ranges, discontinuities, gaps, parts, ladders, separate audio,
     QUERYPARAM/IMPORT) with `HlsUnsupported` (10): the player then remounts on Windows' source for
     that attempt. Every child URI is checked like `PlaybackMediaUrl.IsAllowedChild` (same scheme,
     host, port, media routes, no traversal; never escaped, so stricter); a differential test runs
     193 cases against the C# rule.
   - `HlsSource` feeds the init and segments, from the one holding the start time, into the
     forward-only pump (`BodySource`), re-reading a growing playlist every half target duration (up
     to 60 s per segment). Requests go through `AuthClient`, the range stream's headers, 401 refresh
     and busy retries, now shared; `NativeHeaderRefresh` is the C# side for both streams.
   - Route: `hls_through_ffmpeg` sends HLS to FFmpeg's forward-only input (audio ordinal 0) and lets
     the console tone map cover `server_remux_hls`. The mount's clock starts at the opened segment
     (`PlaybackTimeline.StartingAt`, since FFmpegInteropX zeroes the first timestamp). Seeks inside
     the server's window remount locally at the target's segment, so playback lands at that segment's
     start (at most one segment early); outside it the server reanchors as before. A stream that ends
     early remounts locally.
   - Console A/B on 0.1.551/0.1.552 (2026-11-16, forced HLS, `server_remux_hls`, start at 900 s,
     then seeks to 2000, 3000 and 1500 s; two rounds each):

     | | Windows HLS | FFmpeg HLS |
     |---|---|---|
     | 4K HEVC HLG remux, start to playing | 6.2 / 7.8 s | 7.3 / 7.8 s |
     | Seek to playing (6 seeks) | 2.7–5.9 s, mean 4.3 s | 2.9–3.8 s, mean 3.2 s |
     | Landing error | −1.8 to +1.4 s | −3.0 to −0.2 s (segment start) |
     | Not-playing samples after start/seeks | 0 | 0 |

     FFmpeg HLS decoded the HLG remux with d3d11va and the console tone map (correct picture on the SDR
     display). A 1080p HEVC 10-bit remux with several original audio tracks first failed: the mount
     compared FFmpeg's one packaged audio stream with the original's track count (909a498 now
     expects one stream on every Silo package). After the fix it played (system HEVC, E-AC-3), and
     PGS subtitles rendered in sync, also after a seek. Not covered: a growing EVENT playlist read
     far ahead of the remux, and text subtitles. Windows' HLS was the fallback for a playlist the
     Rust reader refused until 2026-11-17 (owner: removed so the engine player can take every route);
     such a playlist now fails to a replan.

8. **Buffering quality** (lowering quality when playback keeps buffering; not yet verified on the
   console). `BufferingQualityController` and `BufferingSignals` moved to `buffering.rs`, ported line
   for line from the C# port of silo-apple's `BufferingQualityController.swift` /
   `BufferingSignalMapper.swift` and the server's design (`docs/design/2026-10-17-buffering-quality.md`),
   comments and citations kept. The C# classes are thin wrappers with the same public surface, so the
   player page and the tests did not change. Stays C#: the records and enums the page reads, the
   wire spellings (`BufferingQualityWire`), `BufferingQualityPlans.From` (it reads Kiota models),
   `BufferingSignals.Apply`, the clock (`PolledBufferingQualityClock`), and the two meters keyed on
   the identity of C# stream objects, `PlanThroughput` and `MediaSilence`.

   - **ABI** (`abi_buffering.rs`, a handle per controller, freed by its `SafeHandle`):
     `bale_core_buffering_create` / `_destroy` / `_call`. A call takes one JSON event (`type` plus its
     fields) with `now` (the clock's `ElapsedMs`), `mode`, `sends_fields` and `measured_kbps`, and
     returns `steps`, the `state` the page reads between calls (`pill`, `pill_pending`,
     `kept_estimate_kbps`, `current_plan_id`), and the event's answer (`returned` for `Back` and
     `StartDeadlinePassed`, `replan` for `ClientReplan`, `summary` for `RunningSummary`). The wrapper
     caches the state, so the per-tick reads (`Pill`, `CurrentPlanId`) never cross, and
     `SetCompetingPillVisible`, which the page reports every tick, crosses only when it changes (Rust
     ignores a repeat too). `LowerRung` and `StepDownEstimate` are plain `bale_core_call` functions
     (`buffering.lower_rung`, `buffering.step_down_estimate`). The signal mapper runs on every tick, so
     it takes the snapshot as six bits and returns at most two calls packed one per byte
     (`bale_core_signals_create` / `_destroy` / `_update` / `_plan_adopted` / `_invalidate` /
     `_note_request_failed` / `_note_data_arrived` / `_plan_has_run`); a tick with nothing to report
     allocates nothing. ABI version stays 2 (exports were only added).
   - **Measured throughput.** `MeasuredThroughputKbps` stays a C# callback; the wrapper reads it once
     before every call and passes the value in. The original read it only when a decision needed it,
     but nothing a call does changes what the page's callback reads (the plan's meter and the network
     generation), so the value is the same; the callback has no side effects.
   - **Timers.** Rust decides every timer (long stall, suppression, rearm, pill) and its deadline and
     reports it as a step: `schedule` (timer, deadline) or `cancel` (timer). The wrapper mirrors each one
     onto the injected `IBufferingQualityClock`, one clock timer per Rust timer, and a firing comes back
     as a `timer` event. So the polled model is unchanged (the page fires the clock from its UI tick,
     on the UI thread), firing order is the clock's own (deadline, then scheduling order, interleaved
     with the page's debug stall timer on the same clock), and the test clock's `Pending` and
     `NextDueMs` still count the controller's timers. A timer scheduled over a waiting one of the same
     kind leaves the old one waiting, as the original's overwritten field did.
   - **Output order.** Steps come back in the order the original performed them; the wrapper takes the
     new state, then applies the steps in order, raising outputs through `Output` between the timer
     steps. An output handler may call back in (the page calls `ReplanRefused` from inside a replan it
     cannot send); that nested call applies its own steps and state before the rest. In the original
     every replan is the last thing a call does, so the nested call sees the same state.
   - **Not identical, deliberately:** an output handler sees the state as of the end of the call. The
     only difference this makes is during `PlanAdopted`: the outgoing plan's summary handler now sees
     the new plan's `CurrentPlanId` and the original saw null; the page's summary handler does not read
     it. Within one call Rust uses one `now`, where the original read the clock several times (the
     same value on the test clocks; at most a millisecond apart on `Environment.TickCount64`).
   - **Tests.** The C# suite runs unchanged against Rust: the 66 conformance vectors with and without
     buffering_quality_v1, `BufferingQualityControllerTests`, `BufferingSignalsTests`. Rust replays the
     same vectors file itself (`buffering/tests.rs`, `include_str!`) through a host harness that mirrors
     the wrapper and the polled clock, plus the signal cases and the JSON shape (`abi_buffering.rs`).
     `RustPortBufferingDifferentialTests` runs the originals (`Reference/`) and the port side by side on
     400 random streams of 200 events (plans, waits, pills and answers, mode and measurement changes,
     re-entrant refusals, debug stall timers on the same clock), comparing every output, its time, and
     the state and pending timers after each event, and requires every action, notice, pill and replan
     field to occur. Allowing a negative `started_ago_ms`, which no vector uses, fails it.

### Stays in C#

- The XAML app (`src/Bale.Xbox`) and anything that drives XAML objects.
- The Kiota-generated Silo API client (`src/Bale.Core/Api`) and the feeds built on it.
- `DebugServer` (debug-only, mostly drives C# objects).
- Logic that is only glue between C# objects.

## Rules

- A slice that changes behaviour is a bug, not a port. Behaviour changes go in their own commits.
- The software-decode allowlist and every other owner-set policy keep their comments in Rust.
- Never log tokens, URLs, headers or media titles from Rust either.

## Rust 1.100.0 (2026-11-16)

Local stable moved 1.99.1 -> 1.100.0. Both crates pass CI's checks unchanged on it (`cargo fmt
--check`, `cargo clippy --all-targets -D warnings`, `cargo test`; bale-gpu for
`x86_64-pc-windows-msvc`); Cargo's new `unused_dependencies` lint finds nothing. CI is not pinned, so
it picks 1.100 up when the runners do; pin a `rust-toolchain.toml` only once Bale uses a 1.100 API.

What applies to Bale (release notes: rust-lang/rust RELEASES.md, "Version 1.100.0"):
- **Stable custom allocators** (`std::alloc::Allocator`, `Vec::new_in`, `Box::new_in`): a per-playback
  arena or pool for the range stream's 1 MiB blocks, subtitle payloads and upload staging, so a
  stopped player hands memory back in one piece. Fits the 0.2 memory work (docs/ui-memory.md), not
  before.
- **`core::mem::DropGuard`**: scoped cleanup on every exit path in bale-gpu's D3D11/D3D12 and FFI code;
  use when touching that code.
- **Small library additions**: `str::trim_prefix`/`trim_suffix`, `Vec::from_fn`, `bool::toggle`,
  `Box`/`Arc::map`; use as code is touched, no sweep.
- **Cargo dependency cooldowns** (minimum publish age, RFC 3923) and `[lints.cargo]`: worth turning on
  for supply-chain hygiene when the manifests next change.
- **Not relevant**: `-Cprofile-sample-use` (Bale's hot native code is C: dav1d/FFmpeg), the 32-bit x86
  ABI change, ARM/WASM target changes, the build-directory layout change (no script reads `target/*/deps`).
