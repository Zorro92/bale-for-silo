using Bale.Core.Detail;
using Bale.Xbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox;

public sealed partial class DetailPage
{
    private readonly Rail _actionsRail;
    private Control? _actionEntryTarget;
    private PlaybackSelectionState _playbackSelections = new();
    private PlaybackSelectorKind _selectorKind;
    private Button? _selectorOpener;
    private ItemDetail? _selectorPlaybackDetail;
    private int _trackChoiceRevision;

    // A series/season container's own files are never its episode's selector inventory
    // (TvItemDetailScreen.kt / TvItemDetailViewModel.kt's next-up variants).
    private ItemDetail? PlaybackDetail => _detail is { } detail
        ? detail.PlaysEpisodes
            ? _nextUpPlaybackDetail is { } episode && episode.ContentId == _playEpisode?.ContentId ? episode : null
            : detail
        : null;

    // TvPlaybackSelectorRow.kt attaches the shared selectorFocus requester to Edition when shown.
    private Button PlaybackSelectorEntry => EditionSelector.Visibility == Visibility.Visible ? EditionSelector : VersionSelector;

    private void OnEditionSelectorClick(object sender, RoutedEventArgs e) => OpenSelectorMenu(PlaybackSelectorKind.Edition, EditionSelector, "Edition");
    private void OnVersionSelectorClick(object sender, RoutedEventArgs e) => OpenSelectorMenu(PlaybackSelectorKind.Version, VersionSelector, "Version");
    private void OnAudioSelectorClick(object sender, RoutedEventArgs e) => OpenSelectorMenu(PlaybackSelectorKind.Audio, AudioSelector, "Audio");
    private void OnSubtitleSelectorClick(object sender, RoutedEventArgs e) => OpenSelectorMenu(PlaybackSelectorKind.Subtitles, SubtitleSelector, "Subtitles");

    private void OpenSelectorMenu(PlaybackSelectorKind kind, Button opener, string label)
    {
        if (PlaybackDetail is not { } detail || SelectorMenu.IsOpen) return;
        var selection = _playbackSelections.For(detail);
        // Single-version and not-yet-loaded triggers remain focusable; only opening is gated.
        if (!PlaybackFormatting.SelectorIsInteractive(kind, detail, selection)) return;
        _selectorKind = kind;
        _selectorOpener = opener;
        _selectorPlaybackDetail = detail;
        PageScroller.IsEnabled = false;
        SelectorMenu.Show(label, PlaybackFormatting.SelectorOptions(kind, detail, selection), opener);
    }

    private async void OnPlaybackOptionChosen(PlaybackSelectorOption option)
    {
        if (_selectorPlaybackDetail is not { } detail || PlaybackDetail?.ContentId != detail.ContentId)
        {
            CloseSelectorMenu();
            return;
        }

        var kind = _selectorKind;
        var selection = _playbackSelections.Select(detail, kind, option);
        CloseSelectorMenu();
        ShowPlaybackSummary();
        // Edition uses the same version callback and resets file-specific indexes without deleting
        // durable preferences (TvPlaybackSelectorRow.kt / TvItemDetailViewModel.kt).
        if (kind is PlaybackSelectorKind.Edition or PlaybackSelectorKind.Version || Session.Client is not { } client
            || PlaybackFormatting.SelectDisplayVersion(detail.Versions, selection.FileId, detail.LastFileId) is not { } version) return;

        var revision = ++_trackChoiceRevision;
        ActionStatus.Text = string.Empty;
        try
        {
            await client.Detail.SaveTrackChoiceAsync(detail, version, kind, option.TrackIndex);
        }
        catch (Exception ex)
        {
            Log.Error("Detail playback choice", ex);
            if (_active && revision == _trackChoiceRevision && ReferenceEquals(client, Session.Client))
            {
                ActionStatus.Text = "Couldn't save that playback choice. Try again.";
            }

            return;
        }

        // A series pick also changes other episodes' Auto context. Refresh the current target,
        // not necessarily the episode that was focused when the write began. Older writes and
        // A -> B -> A loads must never replace a newer choice or a newer target's inventory.
        if (!_active || revision != _trackChoiceRevision || !ReferenceEquals(client, Session.Client)
            || _detail is not { } owner || PlaybackTrackChoices.OwnerId(owner) != PlaybackTrackChoices.OwnerId(detail)) return;
        await RefreshPlaybackChoicesAsync(client.Detail, revision);
    }

    private async Task RefreshPlaybackChoicesAsync(DetailFeed feed, int revision)
    {
        if (_detail is not { } owner) return;
        var targetId = owner.PlaysEpisodes ? _playEpisode?.ContentId : owner.ContentId;
        if (targetId is null) return;
        var generation = ++_playbackLoadVersion;
        try
        {
            feed.Forget(targetId);
            var refreshed = await feed.GetAsync(targetId);
            if (!_active || revision != _trackChoiceRevision || generation != _playbackLoadVersion
                || _detail?.ContentId != owner.ContentId || !ReferenceEquals(feed, Session.Client?.Detail)
                || (owner.PlaysEpisodes && _playEpisode?.ContentId != targetId)) return;
            if (owner.PlaysEpisodes)
            {
                _nextUpPlaybackTargetId = targetId;
                _nextUpPlaybackDetail = refreshed;
                _nextUpPlaybackLoading = false;
            }
            else if (_detail is { } current)
            {
                // A concurrent watched/favorite write owns its flags; refresh only playback data.
                _detail = current with
                {
                    Versions = refreshed.Versions,
                    PlaybackVariants = refreshed.PlaybackVariants,
                    LastFileId = refreshed.LastFileId,
                    EffectiveSubtitleLanguage = refreshed.EffectiveSubtitleLanguage,
                    EffectiveSubtitleMode = refreshed.EffectiveSubtitleMode,
                    EffectiveShowForcedSubtitles = refreshed.EffectiveShowForcedSubtitles,
                    EffectiveSubtitleTrackSignature = refreshed.EffectiveSubtitleTrackSignature,
                };
            }

            ShowPlaybackSummary();
        }
        catch (Exception ex)
        {
            // Do not roll back a successful server write because its following read failed.
            Log.Error("Detail playback choice refresh", ex);
            if (_active && revision == _trackChoiceRevision && generation == _playbackLoadVersion)
            {
                _nextUpPlaybackLoading = false;
                ActionStatus.Text = "Saved, but couldn't refresh playback choices. Try again.";
                ShowPlaybackSummary();
            }
        }
    }

    private void CloseSelectorMenu() => HideSelectorMenu(restoreFocus: true);

    private void HideSelectorMenu(bool restoreFocus)
    {
        var opener = _selectorOpener;
        SelectorMenu.Hide();
        _selectorOpener = null;
        _selectorPlaybackDetail = null;
        PageScroller.IsEnabled = DialogLayer.Visibility != Visibility.Visible;
        if (restoreFocus && opener is not null)
        {
            FocusRetry.Until(Actions, () => !_active || SelectorMenu.IsOpen || FocusAction(opener));
        }
    }

    private bool FocusAction(Control target, FocusState state = FocusState.Programmatic)
    {
        var previous = _actionEntryTarget;
        _actionEntryTarget = target;
        try
        {
            return target.Focus(state);
        }
        finally
        {
            _actionEntryTarget = previous;
        }
    }

    private void ShowSelectorDescriptions(PlaybackSummaryValues values)
    {
        string? editionValue = null;
        var versionValue = values.Version;
        if (PlaybackDetail is { } detail)
        {
            var selection = _playbackSelections.For(detail);
            var version = PlaybackFormatting.SelectDisplayVersion(detail.Versions, selection.FileId, detail.LastFileId);
            var editions = PlaybackEditions.For(detail.Versions, detail.PlaybackVariants);
            // TvPlaybackSelectorRow.kt: only distinct edition labels and a resolved playable file
            // show Edition. A series/season uses its identity-checked Play episode, never the container.
            if (version is not null && PlaybackEditions.HasChoices(editions))
            {
                editionValue = PlaybackEditions.Current(editions, version)?.Label;
                // The Version trigger omits global Auto with editions, unlike the quiet hero readout.
                versionValue = PlaybackFormatting.VersionCompactLabel(version);
            }
        }

        EditionSelector.Visibility = editionValue is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(EditionSelector, SelectorDescription("Edition", editionValue));
        AutomationProperties.SetName(VersionSelector, SelectorDescription("Version", versionValue));
        AutomationProperties.SetName(AudioSelector, SelectorDescription("Audio", values.Audio));
        AutomationProperties.SetName(SubtitleSelector, SelectorDescription("Subtitles", values.Subtitles));
    }

    private static string SelectorDescription(string label, string? value) =>
        string.IsNullOrWhiteSpace(value) ? label : label + ", " + value;
}
