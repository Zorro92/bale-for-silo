#if DEBUG_SERVER
using Bale.Core.Api.Models;
using Bale.Xbox.Services;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    private (string Status, string Body)? _debugNavigationFailure;

    private void DebugRecordPlaybackDecision(PlaybackDecision decision) =>
        _debugNavigationFailure = DebugNavigationRequest.PlaybackFailure(decision);

    // Use the same shutdown as Back; do not leave a decoder/session alive behind another page.
    internal Task DebugStopForNavigationAsync() => ShutdownAsync();
    internal bool DebugNavigationReady => _active && !_leaving && !_suspended
        && _attempt is { Attached: true, Media: { Opened: true, Started: true, FailureQueued: false } }
        && _state is "playing" or "paused";
    internal bool DebugNavigationFailed => _state == "error";
    internal (string Status, string Body) DebugNavigationFailure => _debugNavigationFailure
        ?? ("500 Internal Server Error", "Playback failed\n");
    internal string DebugPlanDelivery => $"Plan: delivery={DebugNavigationHistory.Identity(_attempt?.Plan?.Delivery ?? _delivery ?? "none")}\n";
}
#endif
