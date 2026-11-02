namespace Bale.Core.Detail;

/// <summary>Null = Auto; audio is an ordinal, subtitles are combined external-first indexes.</summary>
public sealed record PlaybackSelection(string? FileId = null, int? AudioIndex = null, int? SubtitleIndex = null)
{
    public static PlaybackSelection Auto { get; } = new();
}

/// <summary>
/// Page-owned choices, keyed to the playable item, never its series/season container.
/// TvItemDetailViewModel.kt clears file-specific tracks on a version change; its next-up
/// variants use exactly the same rules against the focused episode's identity.
/// </summary>
public sealed class PlaybackSelectionState
{
    private readonly Dictionary<string, (PlaybackSelection Selection, string? TrackFileId)> _items = new();

    public PlaybackSelection For(ItemDetail? detail)
    {
        if (detail is null) return PlaybackSelection.Auto;
        var (selection, trackFileId) = _items.GetValueOrDefault(detail.ContentId, (PlaybackSelection.Auto, null));
        if (selection.FileId is not null && !detail.Versions.Any(version => version.FileId == selection.FileId))
        {
            selection = PlaybackSelection.Auto;
        }

        var version = PlaybackFormatting.SelectDisplayVersion(detail.Versions, selection.FileId, detail.LastFileId);
        if (trackFileId != version?.FileId) selection = selection with { AudioIndex = null, SubtitleIndex = null };
        if (selection.AudioIndex is { } audio && (audio < 0 || audio >= (version?.AudioTracks?.Count ?? 0)))
        {
            selection = selection with { AudioIndex = null };
        }

        if (selection.SubtitleIndex is >= 0 and var subtitle
            && !PlaybackFormatting.CombinedSubtitleSelectionIndexes(version?.SubtitleTracks ?? []).Contains(subtitle))
        {
            selection = selection with { SubtitleIndex = null };
        }

        _items[detail.ContentId] = (selection, version?.FileId);
        return selection;
    }

    public PlaybackSelection Select(ItemDetail detail, PlaybackSelectorKind kind, PlaybackSelectorOption option)
    {
        var current = For(detail);
        var selected = kind switch
        {
            // TvPlaybackSelectorRow.kt routes Edition through the same onSelectVersion callback.
            PlaybackSelectorKind.Version or PlaybackSelectorKind.Edition => new PlaybackSelection(option.FileId),
            PlaybackSelectorKind.Audio => current with { AudioIndex = option.TrackIndex },
            _ => current with { SubtitleIndex = option.TrackIndex },
        };
        var version = PlaybackFormatting.SelectDisplayVersion(detail.Versions, selected.FileId, detail.LastFileId);
        _items[detail.ContentId] = (selected, version?.FileId);
        return For(detail);
    }
}

/// <summary>Explicit, boundary-stopping menu traversal from TvAnchoredSelectorMenu.kt.</summary>
public static class PlaybackSelectorNavigation
{
    public static int InitialIndex(IReadOnlyList<PlaybackSelectorOption> options)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].IsSelected) return i;
        }

        return options.Count > 0 ? 0 : -1;
    }

    public static int? NextIndex(int count, int from, bool forward) =>
        from + (forward ? 1 : -1) is var next && next >= 0 && next < count ? next : null;

    public static double ScrollTarget(double scroll, double rowTop, double rowHeight, double viewport, double maxValue)
    {
        if (viewport <= 0 || rowHeight <= 0) return scroll;
        var target = rowTop < scroll ? rowTop : rowTop + rowHeight > scroll + viewport ? rowTop + rowHeight - viewport : scroll;
        return Math.Clamp(target, 0, Math.Max(0, maxValue));
    }
}
