#if DEBUG_SERVER
using System.Globalization;

namespace Bale.Xbox.Services;

internal static class DebugSampleMemory
{
    internal const string Unavailable = "unavailable (use frame-delivery managed)";

    internal static bool ShouldCheck(long videoSample) => videoSample == 1 || videoSample > 0 && videoSample % 24 == 0;

    internal static string? Describe(long textureSamples, long bufferSamples)
    {
        if (textureSamples == 0 && bufferSamples == 0) return null;
        if (bufferSamples == 0)
            return string.Create(CultureInfo.InvariantCulture, $"GPU texture ({textureSamples}/{textureSamples} checked)");
        if (textureSamples == 0)
            return string.Create(CultureInfo.InvariantCulture, $"CPU buffer ({bufferSamples}/{bufferSamples} checked)");
        return string.Create(CultureInfo.InvariantCulture, $"mixed: texture {textureSamples} · buffer {bufferSamples}");
    }
}
#endif
