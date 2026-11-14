#if DEBUG_SERVER
namespace Bale.Xbox;

public sealed partial class ShellPage
{
    private bool _debugNavigationLoaded;
    // Shell is cached: an old loaded Home must not satisfy a new home command before Select runs.
    internal bool DebugHomeReady => _debugNavigationLoaded && !_searchSelected
        && _selected is { LibraryTab: null, Page: null }
        && DebugContentPage is BrowsePage { DebugHomeLoaded: true };
}
#endif
