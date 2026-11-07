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

The workflow fetches the pinned commit directly from Googlesource, checks its hash,
and applies the patches with `git apply`. It builds `av1xbox.sln` (the player and its
`uwp_common`/`libav1` dependencies) as Release x64 with the same MSBuild action,
`v145` toolset and `10.0.26100.0` Windows SDK used by the wrapper build. The minimum
Windows version is overridden to `10.0.17763.0`. Upstream FXC/Shader Model 5.0 compute
shader settings and decoder behavior remain unchanged.

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
- `SOURCES.txt`: upstream/build recipe commits, run URL, toolset/SDK and patch hashes.
- `licenses/`: the upstream license and patent notices.

A failed run can still upload diagnostics; only use a package from a successful run.

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
