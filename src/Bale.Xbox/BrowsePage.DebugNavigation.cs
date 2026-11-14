#if DEBUG_SERVER
namespace Bale.Xbox;

public sealed partial class BrowsePage
{
    // An empty/error Home is still a settled, deterministic starting page for debug navigation.
    internal bool DebugHomeLoaded => IsHome && !_homeState.IsLoading;
}
#endif
