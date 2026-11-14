# UI artwork residency

**UI off screen → release decoded artwork; visible again → restore from disk, visible art first.**
This applies to EnteredBackground/Suspending and to the cached Browse/Detail pages underneath
PlayerPage. Pause does not change residency. There is no navigation reset, cache eviction timer,
periodic foreground GC, or foreground driver Trim.

The implementation is locally tested; push CI verifies the Windows build. The memory and foreground-app GPU
acceptance targets below still require the parent's console measurements. Object counts are not
freed-byte measurements, and Bale cannot explicitly trim the OS-owned XAML compositor device.

## Console evidence before this change

The owner measured another app's D3D12 work while Bale stayed **suspended**, at comparable
input-idle offsets. No playback is needed to reproduce the slowdown. App memory is MiB.

| Bale state before suspension | App memory | Other app GPU busy/frame | Other app texture-heavy pass |
|---|---:|---:|---:|
| Fresh, profile picker only | ~75 | 18.8 ms | 1.4 ms |
| Fresh, Browse/Home; no Detail or playback | ~303 | 50.6 ms (2.7×) | 19 ms |
| Fresh, one Detail; no playback | ~317 | Same slowdown | Same slowdown |
| After playback, including player release/trim from c98e28c; owned D3D11=0 | ~410–460 | Same slowdown | Same slowdown |

The ~300 MiB browsing footprint, rather than a surviving Bale-owned playback device or an infinite
animation, is enough to squeeze the other app. Xbox has a single ~3.25 GiB shared app pool.
During 4K VP9 playback the owner measured ~1358 MiB, with ~300–500 MiB attributable to the UI
underneath; releasing that art should give the player the same headroom.

Earlier navigation-only measurements also grew from ~70 MiB at profiles to ~260 MiB at Home,
~311 MiB on the first Detail, then ~550–650 MiB after repeated Detail/Back trips. Forced GC freed
almost none of it. Those were native/WinRT/Composition allocations, not proof of a managed leak.
The audit found both decoded model bitmaps and explicit surfaces/shadows with longer lifetimes.

## Ownership and release/restore

`Services/UiResidency.cs` coordinates a weak resource registry (`UiResidencyState`), weak page
tracking from **every** PageFrame (including the nested shell frame), and per-host recipes. It
preserves Frame caches, journal parameters/Detail return entries, view/model/container identity,
selected tabs, FocusRetry and RailScrollState. The registry cannot root a page by itself.

| Owner | Release | Restore |
|---|---|---|
| `Artwork.Load` BitmapImages in card/poster/episode/cast/trailer/avatar models, current and cached pages, and unrealized rail items | Cancel pending SetSourceAsync and set BitmapImage.UriSource=null. This resets the encoded source and decoded software/hardware surfaces even for a stream-backed bitmap. Leave the empty BitmapImage bound; clearing a visual Image.Source alone would leave the model's decoded bitmap resident. | The visible Image/ImageBrush requests the same bitmap's saved URI/priority/decode recipe. Per-bitmap serialization and request versions stop late decode callbacks from refilling a suppressed source. |
| XAML/package ImageBrushes (including profile backgrounds) | Discover them in the root, cached page trees and open popups; clear their bitmap URI too. Small recipes are anchored to the page so clearing the logo cache cannot lose a reload recipe whose managed wrapper was held only by native XAML. | Visible brush owners restore their sources; collapsed pages and offscreen scroll/rail content wait. Opacity-zero logos/incoming fade slots still load, since they wait for ImageOpened. |
| `Artwork.Kept` (up to 48 decoded logos) | Drop only the strong memory cache and reject late cache inserts. Browse/Detail logo handlers now belong to their Image controls, not cached bitmaps that could retain old pages. | Local decode through the existing artwork loader. |
| `MaskedArt` (Browse, Detail, Requests) | Dispose pending/current/retiring LoadedImageSurfaces, pending streams, image/mask brushes, mask surface and Composition visual graph. Retain only the requested URI/host recipe. Detail also disposes its backdrop on Unloaded. | Recreate the visible host's graph and decode from disk. The first returning image snaps in; subsequent art changes retain the existing 500 ms crossfade. Both StartLoadFromUri and StartLoadFromStream use desiredMaxSize from the measured host in DIPs, including the mask, rather than full source resolution. |
| `AmbientTint` | Cancel pending decoder/pixel-sampling work and clear the Color cache. It never cached sample bitmaps: each decoder/stream/pixel array was local to the async sample. Keep only the tiny applied Color in the host recipe. | Reapply the saved color, or resample an interrupted request from disk when that host becomes visible. Version checks reject stale results. |
| `UiShadow` (SurfaceShadow, Detail text, SkylinePanel, card-menu glow) | Detach each DropShadow.Mask and dispose the app-owned DropShadow/visual graph. GetAlphaMask returns a XAML-owned cached brush: drop our reference, never Close it (that would permanently break resumed shadows). XAML releases its mask backing on unload/platform suspend. Unloaded hosts also release immediately. | Recreate from host/mask/settings only when loaded and visible again; text sizing and themed focus-glow settings are preserved. |

**The disk art cache is unchanged.** `ApplicationData.LocalCacheFolder/art` still has its existing
512 MB cap / 400 MB trim target, keyed by the existing artwork identity. Background/suspend and
playback do not delete, evict, move, or clear those files. Resume and Back use `Artwork.FileAsync`
/ `ReadAsync`: a cached image is decoded locally without refetching it. A cache miss still uses
the normal downloader. Existing card/logo decode widths/heights remain in logical pixels;
Calendar continues to take its poster decode width from the measured themed layout. The newly
bounded backdrop decode also reduces foreground memory.

`App` closes the UI residency gate synchronously before awaiting the existing idempotent player
suspend shutdown. Then the existing off-thread WinRT-finalizer backstop runs (bounded wait), and
`PlaybackResources.ReleaseIdle` invokes the existing Bale-owned-device IDXGIDevice3::Trim path
**after** UI release. No compositor device is borrowed or trimmed. A generation check prevents
late shutdown/trim from affecting a quickly resumed foreground app. Resuming dispatches to the
UI thread; an app still off screen does not restore art. LeavingBackground restores visible art
and preserves the existing stopped-player Retry behavior.

PlayerPage waits for PageFrame's outgoing 500 ms fade, then releases the underlying UI before
allocating its attempt's player/decoder/presenter. It stays released through play, pause, seek,
in-page retry/replan and an in-player error/Retry screen. Root navigation away from PlayerPage
(Back, end-of-playback return, or another-page error/replan) opens the playback gate and restores
the incoming page. Background and player gates are independent: resuming to the stopped player's
Retry screen must not refill hidden Browse/Detail art. There is no pause event subscription.

## Diagnostics and acceptance (parent on console)

`scripts/bale-debug.sh mem` now includes:

```text
UI residency: background|playback|foreground releases=… resumes=… resident images=… surfaces=… shadows=… samples=…; released …; restored …
```

`images` counts registered active bitmap sources (decoding or decoded), `surfaces` counts explicitly
owned LoadedImageSurfaces (including masks/pending/retiring art), `shadows` counts DropShadows,
and `samples` counts pending tint samples. Released totals accumulate through overlapping gates;
restored counts are requests initiated for visible hosts, not bytes or a claim of decode completion.
The debug worker reads a UI-thread snapshot, so it does not dispatch into a suspended UI. Existing
App/Xbox memory and `Playback resources: … trimmed=… owned D3D11=…` lines remain.
Safe lifecycle logs are `ui resources: suspend released images=N surfaces=M shadows=… samples=…`,
`ui resources: resume restored …`, and the corresponding `playback released` /
`playback-return restored` triggers. They contain no artwork URLs or media names.

1. Install the **normal push-CI debug build**. Fresh launch at profiles, record `mem`. Open Browse/Home,
   wait at least 20 s after real input (the console throttles after ~10–15 s), then record `mem`.
   Foreground images should be present; bounded backdrops may already lower the former ~303 MiB.
2. Open the other app to background/suspend Bale. Record Bale's App/Xbox memory using the debug
   endpoint during EnteredBackground if available, or the platform's suspended-process accounting.
   A fully suspended process may not answer HTTP: do not relaunch it to obtain a suspended reading.
   Expect a release log, a `background` UI snapshot with **resident images/surfaces/shadows/samples=0**,
   and App memory moving toward **~75–120 MiB** without `mem gc`. Trim counts describe Bale-owned
   devices only and may stay unchanged when none exist; `owned D3D11=0` should remain.
3. While Bale **stays suspended**, measure the other app at the same input-idle offset. Its GPU work
   must return to the profile-picker baseline (~18.8 ms busy/frame and ~1.4 ms texture-heavy pass),
   rather than ~50.6 / ~19 ms. Freed object counts or a successful build alone do not pass this test.
4. Return to Browse: expect `resume restored`, visible art reloading from local disk, the same focus,
   selected tab and vertical/horizontal scroll offsets. Move through offscreen rails, switch cached
   tabs, open Detail and Back, checking posters, logos, ambient tint, crossfades and shadows. Repeat
   steps 1–3 on fresh Detail with no playback (previously ~317 MiB), then after several Detail/Back
   trips. Use screenshots and real key/press input; FocusRetry and RailScrollState must still work.
5. From Detail, record `mem`, then play a 4K VP9 item. After the player fade, expect `playback released`,
   a `playback` UI line with resident art/surface/shadow/sample counts zero, and App memory lower by
   the former UI share (roughly 300–500 MiB against the owner's ~1358 MiB baseline, content/cache
   dependent). At equal input-idle offsets compare memory while opening/playing. Pause/resume must
   be instant and must **not** change UI release/restore counters or refill hidden art. Back/end/
   navigation away must log `playback-return restored` and reload only the visible returning page.
6. Repeat with native MP4/HLS, FFmpeg/system HEVC/VP9, zero-copy AV1 8/10-bit, text and bitmap/PGS
   subtitles. Background during playback must still stop media, release display resources and trim;
   returning shows Retry, with hidden art still released. Retry must recreate playback/subtitles.
   Back from Retry must restore Detail correctly. Also try a quick app switch during shutdown and
   Back during opening: stale work must not erase resumed art, reset navigation/focus, or trim a new
   player. Verify debug server `where`, `navlog`, `mem`, and the existing player counters still work.

Local validation: the platform-independent registry tests cover idempotence, weak ownership,
late registration, both orders of background/player gating, lazy restore, stale generations and
cleanup failures. The changed services can also be C#-compiled on Linux against the real UWP
projections; full XAML/Native AOT packaging is covered by push CI. No console verification is
performed by the implementation agent.
