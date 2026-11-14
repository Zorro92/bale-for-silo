using Bale.Core;
using Bale.Core.Detail;
using Bale.Xbox.Services;

// Run the real DetailPage.DebugNavigation.cs orchestration on Linux. Only the XAML page state,
// focus and player navigation are stand-ins; inventory reads use the real authenticated client.
namespace Bale.Xbox
{
    public sealed partial class DetailPage
    {
        private bool _active = true;
        private ItemDetail? _detail;
        private ItemDetail? _nextUpPlaybackDetail;
        private string? _targetId;
        private int _playbackLoadVersion, _trackChoiceRevision;
        private readonly bool _seasonsLoading = false, _episodesLoading = false, _nextUpPlaybackLoading = false;
        private readonly object _audioSelectionCapabilities = new();
        private readonly PlaybackSelectionState _playbackSelections = new();
        private object PlayButton { get; } = new();
        private string? PlayTargetId => _targetId;
        private ItemDetail? PlaybackDetail => _detail?.PlaysEpisodes == true ? _nextUpPlaybackDetail : _detail;
        private sealed record DetailReturnEntry(ItemDetail Detail, string? PlayEpisodeId);

        internal DetailPage(ItemDetail owner, ItemDetail? episode = null)
        {
            _detail = owner;
            _nextUpPlaybackDetail = episode;
            _targetId = episode?.ContentId ?? owner.PlayTargetId;
            _debugLoadState = "loaded";
        }

        internal ItemDetail Inventory => PlaybackDetail!;
        internal PlaybackSelection Selection => _playbackSelections.For(PlaybackDetail);
        internal ItemDetail Owner => _detail!;
        internal PlaybackSelectionState Selections => _playbackSelections;
        internal (ItemDetail Detail, PlaybackSelection Selection, double? Start, bool Exact)? Handoff { get; private set; }
        internal bool FocusSucceeds { get; set; } = true;
        internal int Summaries { get; private set; }
        internal void Leave() => _active = false;
        internal void ChangeTarget(string target) => _targetId = target;
        internal void ReloadPlayback() => _playbackLoadVersion++;
        internal void ChangeTracks() => _trackChoiceRevision++;
        internal void ChangeFlags() => _detail = _detail! with { Flags = new(true, true, true) };

        private void ShowPlaybackSummary()
        {
            _ = _playbackSelections.For(PlaybackDetail);
            Summaries++;
        }

        private bool FocusAction(object target) => FocusSucceeds;

        private Task OpenPlayerAsync(string targetId, double? start, bool fromEpisode)
        {
            Handoff = (PlaybackDetail!, Selection, start, _debugExactStart);
            return Task.CompletedTask;
        }
    }
}

namespace Bale.Xbox.Services
{
    internal static class Session
    {
        // Only DebugNavigationFileTests uses this stand-in; xUnit serializes cases in that class.
        internal static SiloClient? Client { get; set; }
    }
}
