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
   as slice 2). Open: the first launch after installing 0.1.526 died silently (no log line, no
   dump) during the first debug `play`, after the debug server had timed out twice; it did not
   recur in that session, and a clean restart could not be retried (Device Portal stop/launch
   timed out). Watch for it on the next fresh installs. `ConsoleEvidence` (baseline, probe-based
   claims, AV1/MPEG-4 software claims). `PlaybackClaims` stays C#: it is Xbox-app glue (async probe,
   UI thread, logging) around these calls.
4. **Plan handling:** `PlaybackStartBuilder`, `PlaybackReplan`, `PlaybackVersionNotice`,
   `PlaybackOriginalAudioSelection`, `PlaybackSubtitles`, `PlaybackMediaUrl`.
5. **Meters and timing:** `ConnectionSpeedMeter`, `PlaybackThroughputMeter`,
   `PlaybackFrameDeliveryMeter`, `PlaybackLateFrameTrace`, `PlaybackSeekReanchor`.

After slice 5, decide with the owner whether the HTTP streams (`HttpRangeStream`,
`ProgressiveHttpStream`) are worth moving: they are tied to .NET streams and the Media Foundation
byte-stream adapter, so a port means a Rust byte stream handed to MF directly.

### Stays in C#

- The XAML app (`src/Bale.Xbox`) and anything that drives XAML objects.
- The Kiota-generated Silo API client (`src/Bale.Core/Api`) and the feeds built on it.
- `DebugServer` (debug-only, mostly drives C# objects).
- Logic that is only glue between C# objects.

## Rules

- A slice that changes behaviour is a bug, not a port. Behaviour changes go in their own commits.
- The software-decode allowlist and every other owner-set policy keep their comments in Rust.
- Never log tokens, URLs, headers or media titles from Rust either.
