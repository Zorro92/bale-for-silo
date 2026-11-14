#if DEBUG_SERVER
using Bale.Xbox.Services;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    // Use the same shutdown as Back; do not leave a decoder/session alive behind another page.
    internal Task DebugStopForNavigationAsync() => ShutdownAsync();
    internal bool DebugNavigationReady => _active && !_leaving && !_suspended
        && _attempt is { Attached: true, Media: { Opened: true, Started: true, FailureQueued: false } }
        && _state is "playing" or "paused";
    internal bool DebugNavigationFailed => _state == "error";
    internal string DebugPlanDelivery => $"Plan: delivery={DebugNavigationHistory.Identity(_attempt?.Plan?.Delivery ?? _delivery ?? "none")}\n";
}
#endif
