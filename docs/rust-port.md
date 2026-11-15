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

### Stays in C#

- The XAML app (`src/Bale.Xbox`) and anything that drives XAML objects.
- The Kiota-generated Silo API client (`src/Bale.Core/Api`) and the feeds built on it.
- `DebugServer` (debug-only, mostly drives C# objects).
- Logic that is only glue between C# objects.

## Rules

- A slice that changes behaviour is a bug, not a port. Behaviour changes go in their own commits.
- The software-decode allowlist and every other owner-set policy keep their comments in Rust.
- Never log tokens, URLs, headers or media titles from Rust either.
