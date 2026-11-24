namespace Bale.Xbox.Services;

/// <summary>
/// shell/TvTopMenuBar.kt dwell-to-preview delays (Android 5d90cf32f, after tvOS TVTopMenuBar, silo-apple #1195): a menu
/// opens 120 ms after focus reaches its tab, and a showing menu swaps at once when focus reaches a neighbouring menu tab.
/// The profile menu always waits the first delay.
/// </summary>
internal static class TopMenuPreviewTiming
{
    // TopMenuInitialPreviewDelayMillis / TopMenuPanelSwitchDelayMillis.
    private const int InitialMilliseconds = 120;
    private const int SwitchMilliseconds = 0;

    public static TimeSpan Delay(bool hasOpenPanel, bool isProfile = false) =>
        TimeSpan.FromMilliseconds(hasOpenPanel && !isProfile ? SwitchMilliseconds : InitialMilliseconds);
}
