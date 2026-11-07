namespace Bale.Core.Playback;

/// <summary>Content-resolution/refresh selection, independent of WinRT and HDMI requests.</summary>
public static class DisplayMatchPolicy
{
    /// <summary>Return an index into the supported modes, never a synthesized HDMI mode.</summary>
    public static int? PickMode(DisplayModeFacts current, IReadOnlyList<DisplayModeFacts> modes,
        long videoWidth, long videoHeight, double frameRate, bool requiresHdr10 = false)
    {
        if (videoWidth <= 0 || videoHeight <= 0 || !double.IsFinite(frameRate) || frameRate <= 0) return null;
        // HdrDisplayController.kt: exact resolution, otherwise holding resolutions, otherwise largest.
        // Xbox media modes are at most 60 Hz (Microsoft hevc-xbox); never enter stereo or lose required PQ.
        var eligible = modes.Select((mode, index) => (Mode: mode, Index: index))
            .Where(pair => !pair.Mode.Stereo && (!requiresHdr10 || pair.Mode.Smpte2084)
                && pair.Mode.Width > 0 && pair.Mode.Height > 0
                && double.IsFinite(pair.Mode.RefreshRate) && pair.Mode.RefreshRate is > 0 and <= 60).ToArray();
        if (eligible.Length == 0) return null;
        var exact = eligible.Where(pair => pair.Mode.Width == videoWidth && pair.Mode.Height == videoHeight).ToArray();
        var holding = eligible.Where(pair => pair.Mode.Width >= videoWidth && pair.Mode.Height >= videoHeight).ToArray();
        var largest = eligible.Max(pair => PixelArea(pair.Mode));
        var candidates = exact.Length > 0 ? exact : holding.Length > 0 ? holding
            : eligible.Where(pair => PixelArea(pair.Mode) == largest).ToArray();
        // RefreshRateSelector.kt: integer multiples always beat non-integer cadence, then minimize delta.
        // Compare class/delta separately so the 10000 penalty cannot obscure the 0.005 Hz tie tolerance.
        var scored = candidates.Select(pair => (pair.Mode, pair.Index,
            IntegerMultiple: IsIntegerMultiple(pair.Mode.RefreshRate, frameRate),
            Delta: Math.Abs(pair.Mode.RefreshRate - frameRate))).ToArray();
        var best = scored.OrderByDescending(pair => pair.IntegerMultiple).ThenBy(pair => pair.Delta).First();
        return scored.Where(pair => pair.IntegerMultiple == best.IntegerMultiple && pair.Delta - best.Delta <= 0.005)
            .OrderByDescending(pair => (ulong)pair.Mode.Width * 9 == (ulong)pair.Mode.Height * 16)
            .ThenByDescending(pair => PixelArea(pair.Mode))
            // HDMI can enumerate several encodings of the same transport; avoid a redundant handshake.
            .ThenByDescending(pair => pair.Mode == current)
            .Select(pair => (int?)pair.Index).FirstOrDefault();
    }

    private static ulong PixelArea(DisplayModeFacts mode) => (ulong)mode.Width * mode.Height;

    private static bool IsIntegerMultiple(double refreshRate, double frameRate)
    {
        var ratio = refreshRate / frameRate;
        return ratio >= 0.99 && Math.Abs(ratio - Math.Round(ratio)) < 0.05;
    }
}
