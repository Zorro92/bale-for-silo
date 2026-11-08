#if DEBUG_SERVER
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>Debug-only delivery diagnostics, independent of the managed/native observation backend.</summary>
internal interface IDebugFrameDelivery : IDisposable
{
    string? OutputPixelFormat { get; }
    string? OutputColour { get; }
    void SetSeeking(bool seeking);
    string DescribeLateFrameTrace();
    void ResetLateFrameTrace();
    PlaybackFrameDeliverySnapshot? Read();
}
#endif
