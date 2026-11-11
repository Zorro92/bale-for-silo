# AV1 GPU test player experiment

This is an **experiment-only**, manually triggered build of upstream `uwp_dx12_player`,
not a Bale decoder integration. Its purpose is to measure upstream AV1 D3D12 **compute**
decode speed against dav1d on an Xbox Series X in **app mode**. It is not a hardware
video-decoder test, and a successful build does not establish console compatibility or
performance. Nothing in Bale's app, normal CI workflow, dependency pins or packages changes.

## Upstream and patch

- Source: <https://aomedia.googlesource.com/av1-xbox-one>
- Pinned commit: `b5c61563b48625c261a8d627cfa928896fd10b13`
- License: BSD-2-Clause plus Alliance for Open Media Patent License 1.0. The artifact
  includes upstream's `LICENSE.txt`, `libav1/LICENSE`, `libav1/PATENTS`, and the
  libwebm license/patent notices under `licenses/`. Retain these when redistributing.
- `0001-use-localstate-paths.patch` changes only the UWP branch of
  `Decoders::GetAvailablePaths()` in `testing/player_common/decoder_creator.cpp`.
  Read and write paths both become
  `Windows::Storage::ApplicationData::Current->LocalFolder->Path`, converted with
  upstream's existing `pstos()` narrow-string helper. The x64 `uwp_common` project
  already compiles this file with `/ZW`, so no entry-point shim is needed.
- `0002-gpu-stage-timing-and-nopresent.patch` adds two player flags, `-gputime` and
  `-nopresent`, for the timing run described under
  [Timing run](#timing-run--gputime-and--nopresent). It touches the test player
  (`testing/common`, `testing/player_common`) and the libav1 D3D12 host (`libav1/dx`,
  plus two timers in `libav1/av1/decoder/decodeframe.c`). No shader, no shader maths
  and no decoded output changes; without the flags the decoder runs as before.
- `0003-cdef-v2-shader.patch` adds a second CDEF compute shader,
  `libav1/dx/shaders/cdef_filter_v2.hlsl`, and the player flag `-cdefv2` that selects
  it; see [CDEF v2 run](#cdef-v2-run--cdefv2). The upstream shader `cdef_filter.hlsl`
  is not modified and stays the default. The new shader is written to produce the same
  bytes as the upstream one, including where upstream differs from the AV1
  specification. The patch also adds a host-side comparison of the two shaders
  (`testing/cdef_check`), which is not part of the player build.

The workflow fetches the pinned commit directly from Googlesource, checks its hash,
and applies the patches with `git apply`. It builds `av1xbox.sln` (the player and its
`uwp_common`/`libav1` dependencies) as Release x64 with the same MSBuild action,
`v145` toolset and `10.0.26100.0` Windows SDK used by the wrapper build. The minimum
Windows version is overridden to `10.0.17763.0`. Upstream FXC/Shader Model 5.0 compute
shader settings remain unchanged, every upstream shader is built unmodified, and
without `-cdefv2` the decoder runs exactly the upstream shaders. An appended compiler option
`/wd4996` suppresses the newer SDK's deprecated `GetDesktopDpi` diagnostic (promoted
to an error by upstream's `/sdl`), without changing the upstream player source.

Before building, the workflow sets the package identity to `Av1GpuTestPlayer`, its
publisher to `CN=Bale Development`, and both display names to **AV1 GPU Test Player**.
A throwaway certificate is created per run; the upstream committed temporary PFX is
not used. Only the public `.cer` is exported. The package version is `0.1.<run number>.0`.

## Build and download

In GitHub Actions, select **AV1 GPU test player**, then **Run workflow** on `main`.
There are no push, pull-request or scheduled triggers. Equivalently, from this repo:

```sh
gh workflow run av1-gpu-testplayer.yml --ref main
gh run list --workflow av1-gpu-testplayer.yml --event workflow_dispatch --limit 5
gh run watch <run-id> --exit-status
gh run download <run-id> --name av1-gpu-testplayer-x64-<run-id> --dir av1-gpu-testplayer
```

The artifact `av1-gpu-testplayer-x64-<run-id>` contains:

- `packages/`: the signed x64 sideload package (MSIX requested; legacy tooling may
  emit APPX), any emitted `Dependencies/x64/` framework packages and sideload helpers.
- `Av1GpuTestPlayer.cer`: this run's public signing certificate.
- `msbuild.log`: normal-verbosity build/package diagnostics, also uploaded on failure.
- `cdef-shaders/`: FXC listings of both CDEF shaders (`cdef_filter.asm`,
  `cdef_filter_v2.asm`) and `stats.txt`, a side-by-side count of what the listings
  declare; see [CDEF v2 run](#cdef-v2-run--cdefv2).
- `SOURCES.txt`: upstream/build recipe commits, run URL, toolset/SDK and patch hashes.
- `licenses/`: the upstream license and patent notices.

A failed run can still upload diagnostics; only use a package from a successful run.

The run has a second job, `cdef-check`, on Linux. It applies the same patches and runs
the host-side comparison of the two CDEF shaders. The run is green only if both jobs pass.

## Install with Xbox Device Portal

Do **not** use `scripts/deploy-xbox.sh` for this experiment. Although it accepts a
package directory and finds a certificate/dependencies there, its post-install checks
and launch target are hardcoded to `Bale.ClientForSilo_`, and it only finds `.msix`
application packages. It cannot correctly verify or launch `Av1GpuTestPlayer`.

1. Put the console in Developer Mode, open its Windows Device Portal, and use the
   Apps installation page to add the downloaded `.msix` (or `.appx`/bundle if emitted).
2. Supply `Av1GpuTestPlayer.cer` as the certificate and every required x64 framework
   package from the emitted `Dependencies/x64/` directory. Do not select another
   architecture's dependencies. If no dependencies were emitted, inspect the package
   dependencies/build log rather than assuming all required frameworks are installed.
3. Install **AV1 GPU Test Player**, keep its Dev Home app type set to **App**, not Game,
   and launch it once to initialize LocalState. It is a separate package, not a Bale update.
4. Stop the player before editing its inputs. A new workflow run uses a new throwaway
   certificate; if an update is rejected because signing trust changed, remove only
   this experiment's old package and install the new package/certificate. Back up its
   LocalState files before uninstalling. Do not remove Bale.

No workflow or script here deploys to, launches on, or benchmarks a console.

## Inputs and logs in LocalState

Use Device Portal's file explorer, choose **LocalAppData** as the known folder, and
select the installed `Av1GpuTestPlayer` package. Under its **LocalState** folder,
create `args/` and `streams/`, upload your local test streams, and write a plain-text
`LocalState\args\args_av1.txt`. No stream or server location is embedded in the build.

Each non-comment line is one upstream task, for example:

```text
-i <stream path> -t 6 -frate -1 -limit 600 -progress
```

For a file uploaded as `LocalState\streams\sample.ivf`, use this concrete line:

```text
-i /streams/sample.ivf -t 6 -frate -1 -limit 600 -progress
```

`CParameters::Parse()` in `testing/player_common/cmd_parser.cpp` prefixes **both**
`-i` and `-dir` values with `read_path`; it concatenates the strings without adding
a separator. Therefore start the stream/directory value with one `/` or `\`, as in
the example. A plain `streams/sample.ivf` would be concatenated incorrectly, and an
absolute drive path would also be prefixed incorrectly. Quote values that contain
spaces. For multiple files, a line such as `-dir /streams -t 6 -frate -1 -limit 600 -progress`
uses the same LocalState root. The parser itself is not patched.

`-t 6` selects six decoder threads, `-frate -1` requests maximum speed rather than
source-rate pacing, `-limit 600` bounds the task, and `-progress` logs per-frame timings.
Use the same streams, bit depth, resolution, frame count and comparable pacing/thread
settings for any dav1d comparison; this is an upstream player, not Bale's playback pipeline.

Relaunch the player after uploading the args file. Upstream reads it at startup,
processes the task lines, and normally exits after completing them. Logs are written
to **`LocalState\logs`**; download them through the same Device Portal file explorer.
The log directory is created by upstream's existing logging initialization. An absent
or invalid args file produces diagnostics there, not a file-picker UI. App-mode GPU
availability and performance remain to be measured; upstream's old error text about
Game mode is not evidence that app mode was tested or a reason to change the app type
for this experiment.

## Timing run (`-gputime` and `-nopresent`)

The question for this run: **where does the time per 4K frame go**, and can the GPU
side alone ever fit in 41.67 ms (24 fps)?

| Flag | Effect |
| --- | --- |
| `-gputime` | Per-stage GPU timestamps and CPU timers for every decoded frame, a summary at the end of the task, and a live log line each time the decoder blocks on the player's output buffers. |
| `-nopresent` | Decoded frames go straight back to the buffer pool. Nothing is queued for display and nothing is presented, so the picture stays blank. The render loop sleeps 10 ms per pass instead of spinning. |

Both are off by default, and the player then behaves as before. `-progress` keeps its
existing per-frame lines.

Put both lines in `args_av1.txt`, the `-nopresent` one first (it cannot stall on the
display, so its summary is written even if the second task hangs):

```text
-i /streams/<clip>.ivf -t 6 -frate -1 -limit 300 -queue_size 4 -progress -gputime -nopresent
-i /streams/<clip>.ivf -t 6 -frate -1 -limit 300 -queue_size 4 -progress -gputime
```

For a single timing run with presentation, use the second line alone.

### What is measured

GPU: a D3D12 timestamp query after every stage of the frame's command list, on the
queue the decoder submits to (the player passes no queue, so the decoder creates its
own direct queue; the frequency comes from that queue). The queries are resolved into
a readback buffer as the last command of the list and read only after the frame's
existing fence has completed, so the run adds no wait. A stage's time is its timestamp
minus the previous one, which means **the stage times add up to the first-to-last
span exactly**, and any gap between two stages is counted in the later one.

| GPU stage | What it covers |
| --- | --- |
| `gen_blocks` | prediction block generation |
| `residual_clear` | residual buffer fill (the whole buffer, every frame) |
| `idct_sort`, `idct` | transform block sort, inverse transform (all types) |
| `inter`, `recon` | inter prediction (all passes), inter residual reconstruction |
| `intra` | intra prediction, the whole wavefront loop |
| `lf_gen`, `lf_vert`, `lf_horz` | loop filter: block generation, vertical edges (two passes), horizontal edges |
| `cdef`, `superres`, `loop_rest` | CDEF, super-resolution, loop restoration |
| `ext_borders` | reference border extension |
| `film_grain` | film grain |
| `copy_plane`, `output_copy` | copy shader into the staging area, then the copy into the output buffer or textures |

A timestamp records when the GPU reaches that point of the list. Stages that end in a
UAV barrier are cleanly separated. `residual_clear` has no barrier before `idct_sort`,
so read those two together. Stages a clip never uses are shown as `(not dispatched)`.

CPU timers (QueryPerformanceCounter, all in ms per frame):

| Timer | Meaning |
| --- | --- |
| `entropy_wall`, `entropy_sum` | tile (entropy) decode: wall time on the decode thread, and the sum over the tile threads. `sum / wall` is the parallelism actually reached. |
| `record` | recording the frame's command list, waits excluded. `rec_gen_blocks`, `rec_idct`, `rec_pred`, `rec_loopfilter`, `rec_cdef_lr`, `rec_borders`, `rec_copy` split it by host function (`rec_copy` still contains `wait_output` and `wait_image`). |
| `submit` | `Close`, `ExecuteCommandLists`, `Signal` |
| `memset`, `memset_tile` | the per-frame and per-tile memsets; the summary also gives the MB cleared per frame |
| `setup` | `av1_setup_frame`, including its waits and memsets |
| `frame` | decode thread, frame setup to submit done |
| `wait_slot`, `wait_sec_slot` | blocked waiting for one of the two pipeline slots. High values mean the GPU is behind the CPU. |
| `wait_output` | blocked in the player's output buffer callback (pool of `-queue_size`) |
| `wait_fb`, `wait_image`, `wait_item` | blocked on the reference frame pool, the output image pool, the GPU work item pool |
| `fence_wait`, `gpu_latency` | completion thread: time blocked on the frame's fence, and submit-to-fence time |
| `notify` | time inside the frame-ready callback. With `-progress` this includes its two log writes per frame. |

### Log lines

All new lines start with `GPUTIME`, in the usual `LocalState\logs` file.

Live, during decoding:

```text
GPUTIME output-buffer wait #3 BLOCKED: pool 4, free 0, held by decoder 3, queued or on display 1
GPUTIME output-buffer wait #3 resumed after 41.7 ms
```

The number is a running count. "Held by decoder" are buffers rented for frames in
flight and hidden frames not shown yet. A `BLOCKED` line with no `resumed` line at
the end of a log means the task hung right there; if "held by decoder" equals the
pool size, nothing outside the decoder could free a buffer.

After the last frame, the summary (first 10 decoded frames and first 10 output frames
are left out as warm-up when the task has more than 20):

```text
GPUTIME ==== summary: 300 frames decoded, 240 output; warm-up 10 skipped, 290 measured (...), GPU timestamps on 290 ====
GPUTIME target: 24 fps needs <= 41.67 ms per output frame
GPUTIME -- per frame, ms --                          n     mean      p50      p95      max
GPUTIME wall between output frames                ...                                        NN.N fps output
GPUTIME wall between decoded frames               ...                                        NN.N fps decoded
GPUTIME gpu busy (first to last timestamp)        ...                                        NN.N % of wall
GPUTIME gpu stage sum                             ...
GPUTIME gpu idle gap before the frame             ...
GPUTIME cpu decode thread, setup to submit        ...                                        NN.N % of wall
GPUTIME cpu entropy/tile decode, wall             ...
GPUTIME blocked, decode thread, all waits         ...                                        NN.N % of wall
GPUTIME per OUTPUT frame (1.25 decoded frames each): wall .. ms, gpu busy .. ms, cpu decode thread .. ms, ...
GPUTIME stage                    frames     mean      p50      p95      max      avg   share
GPUTIME gpu intra                   290    ...
GPUTIME cpu wait_output             290    ...
GPUTIME output-buffer waits: 3, total 120.0 ms, longest 60.0 ms (whole run, warm-up included)
GPUTIME ==== end of summary ====
```

In the stage table, `mean`/`p50`/`p95`/`max` are over the frames that ran the stage,
`avg` spreads the stage over all measured frames (the `avg` column adds up to the
"gpu busy" mean), and `share` is the stage's part of all GPU time.

With `-progress` as well, one line per decoded frame follows the summary. They are
kept in memory during the run and written at the end, so they add no log I/O to the
decode loop:

```text
GPUTIME frame 57 no=57 show=1 sef=0 intra=0 iters=62 tiles=4 sec=0 wall=61.20 busy=38.10 gap=22.40 | gen_blocks=0.31 ... | entropy_wall=18.20 ...
```

`wall` is the time since the previous frame completed, `busy` the first-to-last GPU
span, `gap` the time the decoder's queue sat idle before this frame, `iters` the intra
wavefront iterations, `sec` whether the frame also took the second pipeline slot.
Timers at zero are left out.

`-nopresent` adds one line of its own: frames released and the fps between the first
and the last. If the queue refuses timestamp queries the summary says
`NO GPU TIMESTAMPS` and the CPU timers are still valid.

### How to read it

- **"per OUTPUT frame" is the line to compare with 41.67 ms.** Hidden frames are
  decoded but not output, so an output frame costs more than a decoded one.
- **GPU busy per output frame near or above 41.67 ms**: the shaders themselves are too
  slow; the stage table says which. No host change gets a port to 24 fps.
- **GPU busy well under the wall time, large idle gap, `wait_slot` near zero**: the GPU
  is waiting for the CPU. Look at `entropy_wall`, `record`, `memset` and the blocked
  waits.
- **`wait_slot` high**: the CPU is waiting for the GPU (only two frames can be in the
  pipeline).
- **`wait_output` high, or output-buffer wait lines**: the player's output pool is the
  limit, which is what `-queue_size 4` in app mode makes likely.
- **`-nopresent` faster than the presenting task**: the difference is presentation
  coupling (buffers held by the display queue, GPU time taken by the swap chain), since
  the decoder does not share a command queue with the swap chain in this player.

### Host-side check of the summary maths

The summary code is a plain header (`testing/player_common/perf_stats.h`). The patch
also adds a small test that is not part of the player build. In a tree with the
patches applied:

```sh
g++ -std=c++14 -Wall -Wextra -I libav1 -o perf_stats_test testing/player_common/perf_stats_test.cpp
./perf_stats_test
```

## CDEF v2 run (`-cdefv2`)

In the first timing run, upstream's CDEF shader was the largest GPU stage on 4K
10-bit streams. Patch 0003 adds a replacement to measure against it.

| Flag | Effect |
| --- | --- |
| `-cdefv2` | CDEF runs through `cdef_filter_v2.hlsl` instead of `cdef_filter.hlsl`. Decoded output is meant to be identical. |

Off by default: without the flag the decoder creates the same pipeline state and
issues the same dispatch as before. The task's log says which shader is in use:

```text
CDEF shader: cdef_filter (original)
CDEF shader: cdef_filter_v2 (-cdefv2)
```

### What changed in the shader

| | `cdef_filter.hlsl` (upstream) | `cdef_filter_v2.hlsl` |
| --- | --- | --- |
| Thread group | 4 x 4 x 8, one group per 32x32 luma | 8 x 8 x 1, one group per 64x64 luma (one CDEF strength index) |
| A thread's work | one 8-sample row of an 8x8 luma block, then one 4-sample row of a 4x4 chroma block (U or V) | one 8x8 luma block and its two 4x4 chroma blocks |
| 64x64 with zero strengths or no index | full load, direction search, filter with zero strengths (a copy) and store | the group copies its samples and does nothing else |
| 8x8 block with zero primary strengths | direction search still runs | no direction search |
| Direction search | on 16 of the 128 threads, a loop over 64 samples into an indexed array, while the other threads wait at a barrier | on every thread for its own block, fully unrolled |
| Group shared memory | 32-bit per sample, an input window and an output array; results are written to shared memory, read back and then stored | two 16-bit samples per word, input window only; each thread stores its own result |
| Barriers per group | six in the source, five after FXC | one |
| Primary tap weights | constant table (immediate constant buffer) | computed |

Bindings, the constant buffer and the frame buffers are the same, so the host change
is the choice of pipeline state and the dispatch size (`(w + 63) >> 6` by
`(h + 63) >> 6` groups instead of `(w + 31) >> 5` by `(h + 31) >> 5`).

### Timing: old against new

Both lines in `args_av1.txt`, same clip, same settings; the only difference is the flag:

```text
-i /streams/<clip>.ivf -t 6 -frate -1 -limit 300 -queue_size 4 -progress -gputime -nopresent
-i /streams/<clip>.ivf -t 6 -frate -1 -limit 300 -queue_size 4 -progress -gputime -nopresent -cdefv2
```

Compare the `gpu cdef` row of the two summaries' stage tables, then `gpu busy` and the
"per OUTPUT frame" line. Every other GPU stage should be unchanged between the two tasks.

### Same output: md5 of every frame

Upstream's player can write one md5 per decoded frame and check a later run against
that file. First task: the original shader writes the reference. Second task: the new
shader is checked against it.

```text
-i /streams/<clip>.ivf -t 6 -frate -1 -limit 300 -queue_size 4 -progress -nopresent -save_md5 /streams/<clip>.ivf.md5
-i /streams/<clip>.ivf -t 6 -frate -1 -limit 300 -queue_size 4 -progress -nopresent -cdefv2 -md5_frame_check
```

- `-save_md5` takes a path below LocalState, like `-i`. `-md5_frame_check` takes no
  value and always reads `<stream path>.md5`, so the saved file must have exactly the
  stream's name plus `.md5`, next to the stream.
- Use the same `-limit` in both tasks. The checker does not guard against a reference
  file with fewer frames than it decodes.
- The result is in the log when the second task ends: `MD5 is OK`, or
  `MD5 checksum errors in N frames` with one `MD5 checksum error in <n> frame` line
  per bad frame. Later frames are predicted from earlier ones, so one wrong CDEF
  output shows up as errors from that frame on.
- Both flags read every frame back from the GPU. Do not combine them with `-gputime`
  for timing numbers.
- Do not add `-try10x3`: the saver and the checker hash that output format differently.

The md5 covers the visible picture after all later stages (loop restoration, film
grain). Run it on at least one 8-bit and one 10-bit clip.

### Host-side comparison of the two shaders

`testing/cdef_check` compiles both `.hlsl` files as C++ (attributes and semantics
stripped by `sed`, nothing else changed) and runs them the way a dispatch does, with
all threads of a group stopping at each barrier. It builds frame buffers with the
decoder's layout, fills them and every input with random and edge-case data, runs both
shaders and compares the **whole buffer**, padding included. It covers 8-bit and high
bit depth, frame sizes that are not multiples of 8 or 64, missing strength indices,
skipped blocks and all strength and damping values. A second pass changes one tap
weight in a copy of the new shader and requires the comparison to fail.

In a tree with the patches applied (needs `g++`, no GPU):

```sh
sh testing/cdef_check/run.sh --mutate
```

It ends with `cdef_check: OK, cdef_filter_v2.hlsl output is byte-identical to
cdef_filter.hlsl`. This proves the HLSL logic; the md5 run above is the proof for the
compiled shader on the console.

### Shader listings in the artifact

The workflow runs both shaders through the same FXC, profile and flags as the Release
build (`/T cs_5_0 /E main`) and stores the listings under `cdef-shaders/`. After the
build it checks that each listing's bytecode is the one compiled into the package.
`stats.txt` gives, for each shader: instruction slots, temp registers, indexable
temps (arrays the compiler could not keep in registers), immediate constant buffer
use, group shared memory size, barriers, and load/store counts. The counts are static
(instructions in the listing), not instructions executed, and are not a timing.

With the pinned upstream and FXC from Windows SDK `10.0.26100.0`:

| | `cdef_filter.hlsl` | `cdef_filter_v2.hlsl` |
| --- | --- | --- |
| Thread group | 4, 4, 8 | 8, 8, 1 |
| Instruction slots | 1160 | 3223 |
| Temp registers | 10 | 29 |
| Indexable temps | 1: `x0[120]` (`partial[8][15]` of the direction search), 227 instructions use it | none |
| Immediate constant buffer | yes, read at 4 places | no |
| Group shared memory | 12,224 bytes per 32x32 | 15,552 bytes per 64x64 |
| Barriers | 5 | 1 |
| Loops | 22 | 9 |

The new shader is longer because its direction search and its four-sample filter are
unrolled; what a thread executes is shorter. In the upstream listing the direction
search loop reads and writes the indexable temp 16 times per sample with a computed
index, 64 samples per block.
