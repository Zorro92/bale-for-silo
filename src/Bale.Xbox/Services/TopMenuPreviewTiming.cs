namespace Bale.Xbox.Services;

/// <summary>shell/TvTopMenuBar.kt dwell-to-preview delays; profile always uses the initial delay.</summary>
internal static class TopMenuPreviewTiming
{
    public static TimeSpan Delay(bool hasOpenPanel, bool isProfile = false) =>
        TimeSpan.FromMilliseconds(hasOpenPanel && !isProfile ? 80 : 180);
}
