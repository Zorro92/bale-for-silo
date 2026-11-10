# UI memory growth (planned for 0.2.x)

App memory grows by tens of MiB every time a detail page is opened and left. It is native memory:
a forced full GC with finalizers (`bale-debug.sh mem gc`) frees almost none of it. Playback itself
does not leak. Investigated on 0.1.414–0.1.416 (commit 83ae467); no code has changed yet.

## Measurements

All readings were taken 30 s after the last key, so the console's input-idle throttle had kicked in
(see "Player diagnostics" in AGENTS.md). App memory in MiB.

Fresh launch, no playback until the last steps:

| Step | App |
|---|---|
| Launch (profiles) | 70 |
| Browse (Home) | 260 |
| Shrek detail | 311 |
| Back to Home | 445 |
| Brawl detail | 434 |
| Back to Home | 500 |
| Shrek detail | 530 |
| Playing 4K AV1, 60 s in | 2038 |
| Stopped (detail) | 375 |
| Back to Home | 552 |

The Movies tab behaves the same, so Home's refresh on return is not the cause. These steps opened
the same title three times in one process:

| Step | App |
|---|---|
| Movies tab | 467 |
| Detail 1 → back | 496 → 544 |
| Detail 2 → back | 593 → 634 |
| Detail 3 → back | 620 → 645 |

- The growth slows after a few trips and levels off around 550–650 MiB.
- Starting playback releases about 150–200 MiB of it (UI artwork). That memory comes back on the
  next return to Browse.
- Replaying the same title returns to the same figure, so the player itself doesn't leak.

## Suspects (not yet confirmed)

1. **DetailPage never disposes its backdrop.** `DetailPage.xaml.cs` creates a `MaskedArt` on
   `Loaded`, but nothing calls `Dispose()` on it (`OnNavigatedFrom` in `DetailPage.Sections.cs`
   doesn't touch it). BrowsePage does dispose its own in `OnUnloaded`.
   - `MaskedArt` loads through `LoadedImageSurface.StartLoadFromStream` without a size, so the
     backdrop is decoded at full source resolution.
2. **Old detail pages are kept alive.** Forced GC frees nothing, so something still references the
   old pages. One known path:
   - On a title's first visit, `DetailPage` attaches an `ImageOpened` handler that captures the page
     to the logo `BitmapImage` from `Artwork.LoadKept`.
   - That image lives in `Artwork.Kept`, a static cache holding up to 48 images.
   - So the cache references the handler, the handler references the page, and the page's whole
     visual tree and backdrop stay in memory.
   - `BrowsePage.ShowMarquee` does the same, which matters less because it is one long-lived page.
   - This doesn't explain growth on repeat visits to a title whose logo is already cached, so there
     is probably another reference.

## Plan

1. Add a debug-only count of live pages (Detail, Browse, Player) to `bale-debug.sh mem gc`, using
   weak references and compiled out of production builds. Confirm whether old detail pages pile up.
2. Dispose `MaskedArt` when the detail page is left. Remove the logo `ImageOpened`/`ImageFailed`
   handlers once they've run, or when the page is left. Remove any other reference the counter
   shows is keeping pages alive.
3. Decode backdrops no larger than they're drawn (pass a size to `LoadedImageSurface`) if that
   turns out to be a meaningful share.
4. Re-run the walks above to check the result.
