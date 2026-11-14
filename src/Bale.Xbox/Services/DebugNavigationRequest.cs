#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Detail;

namespace Bale.Xbox.Services;

internal sealed record DebugNavigationRequest(string ContentId, string? FileId, double? StartSeconds)
{
    internal const string Usage = "open/play requires id=<content-id>, optional file=<file-id>; only play accepts start=<finite non-negative seconds>";

    internal static bool IsId(string? value) => value is { Length: > 0 and <= 256 }
        && value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.');

    internal static bool TryParse(IReadOnlyDictionary<string, string> query, bool play, out DebugNavigationRequest request)
    {
        request = null!;
        if (query.Keys.Any(key => key is not ("id" or "file" or "start"))
            || !query.TryGetValue("id", out var id) || !IsId(id)
            || query.TryGetValue("file", out var file) && !IsId(file)) return false;
        double? seconds = null;
        if (query.TryGetValue("start", out var start))
        {
            if (!play || start.Length == 0 || !start.All(c => c is >= '0' and <= '9' or '.')
                || !double.TryParse(start, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
                || !double.IsFinite(parsed) || parsed < 0 || parsed > TimeSpan.MaxValue.TotalSeconds) return false;
            seconds = parsed;
        }
        request = new(id, file, seconds);
        return true;
    }

    // A redirected episode must still be the exact Play target. Never silently play next-up.
    internal bool MatchesTarget(string? pageId, string? targetId) => ContentId == pageId || ContentId == targetId;

    internal bool SelectFile(ItemDetail playable, PlaybackSelectionState selections)
    {
        if (FileId is null) return true;
        if (!playable.Versions.Any(version => version.FileId == FileId)) return false;
        // TvPlaybackSelectorRow.kt's Version callback also clears file-specific track picks.
        selections.Select(playable, PlaybackSelectorKind.Version, new("", "", false, FileId: FileId));
        return selections.For(playable).FileId == FileId;
    }

    internal static bool TryCount(string? input, out int count)
    {
        count = 50;
        return input is null || int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out count)
            && count is >= 1 and <= DebugNavigationHistory.Capacity;
    }
}
#endif
