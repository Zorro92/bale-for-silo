using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox;

/// <summary>
/// The Options panel (TvPlayerHud.kt): opened from the control bar's Options button or the Menu button,
/// it replaces the controls and stays until Back. Info shows the title and how it plays; Chapters lists
/// the playing file's chapters and seeks to the one picked. Tabs follow focus, as on the TV apps.
/// </summary>
public sealed partial class PlayerPage
{
    private bool OptionsOpen => OptionsPanel.Visibility == Visibility.Visible;

    private void OnOptionsClick(object sender, RoutedEventArgs e) => OpenOptions();

    private void OpenOptions()
    {
        try
        {
            if (!_active || _leaving || _attempt is not { Plan: { } plan } attempt) return;
            if (_controls.Visible)
            {
                _scrub.Cancel();
                _controls.Hide();
                TransportControls.Visibility = Visibility.Collapsed;
            }
            FillInfo(attempt.Request.Detail, plan);
            var chapters = PlayingChapters();
            ChaptersTab.Visibility = chapters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FillChapters(chapters);
            UpdateAutoPlayRow();
            ShowOptionsTab(OptionsTab.Info);
            OptionsPanel.Visibility = Visibility.Visible;
            FocusRetry.Until(InfoTab, () => !OptionsOpen || InfoTab.Focus(FocusState.Keyboard));
        }
        catch (Exception ex) { LogFailure("options", ex); }
    }

    private void CloseOptions()
    {
        OptionsPanel.Visibility = Visibility.Collapsed;
        Focus(FocusState.Programmatic);
    }

    private IReadOnlyList<Bale.Core.Api.Models.VersionChapter> PlayingChapters()
    {
        if (_attempt is not { } attempt) return [];
        var fileId = attempt.Plan?.Source?.MediaFileId ?? attempt.Start?.FileId;
        return (attempt.Request.Detail.Versions.FirstOrDefault(version => version.FileId == fileId)?.Chapters ?? [])
            .Where(chapter => chapter.StartSeconds is { } start && double.IsFinite(start) && start >= 0)
            .OrderBy(chapter => chapter.StartSeconds).ToList();
    }

    private void FillInfo(ItemDetail detail, Bale.Core.Api.Models.PlaybackPlan plan)
    {
        var episode = PlaybackControlsText.Eyebrow(detail);
        OptionsEpisode.Text = episode ?? string.Empty;
        OptionsEpisode.Visibility = episode is null ? Visibility.Collapsed : Visibility.Visible;
        OptionsTitle.Text = PlaybackControlsText.Title(detail);
        OptionsDuration.Text = _duration is > 0 and { } duration && double.IsFinite(duration)
            ? PlaybackTransportFeedback.Clock(duration, forceHours: duration >= 3600) : string.Empty;
        OptionsBadges.Children.Clear();
        foreach (var badge in PlaybackOptionsInfo.Badges(plan))
        {
            OptionsBadges.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 2),
                Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock { Text = badge, FontSize = 10.3, FontWeight = Windows.UI.Text.FontWeights.SemiBold, Foreground = Paper() },
            });
        }
        OptionsStreamRows.Children.Clear();
        var position = _attempt is { } attempt ? SourcePosition(attempt) : 0;
        var subtitles = TrackOptions(PlaybackSelectorKind.Subtitles).FirstOrDefault(option => option.IsSelected)?.Title;
        foreach (var row in PlaybackOptionsInfo.StreamRows(plan, subtitles, PlaybackOptionsInfo.CurrentChapter(PlayingChapters(), position), PlayingAudioTrack()))
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock { Text = row.Label, FontSize = 12.9, Foreground = Paper() });
            var value = new TextBlock
            {
                Text = row.Value, FontSize = 12.04, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xED, 0xED, 0xED)),
            };
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            OptionsStreamRows.Children.Add(grid);
        }
    }

    /// <summary>The playing file's catalog audio track at the plan's selected index (an ordinal in audio_tracks).</summary>
    private Bale.Core.Api.Models.AudioTrack? PlayingAudioTrack()
    {
        if (PlayingSelection(out var detail) is not { AudioIndex: { } index } selection || detail is null) return null;
        var tracks = detail.Versions.FirstOrDefault(version => version.FileId == selection.FileId)?.AudioTracks;
        return tracks is not null && index >= 0 && index < tracks.Count ? tracks[index] : null;
    }

    private static SolidColorBrush Paper() => new(Color.FromArgb(0xFF, 0xED, 0xED, 0xED));

    private void FillChapters(IReadOnlyList<Bale.Core.Api.Models.VersionChapter> chapters)
    {
        ChapterRows.Children.Clear();
        var hours = _duration >= 3600;
        for (var i = 0; i < chapters.Count; i++)
        {
            var start = chapters[i].StartSeconds!.Value;
            var title = string.IsNullOrWhiteSpace(chapters[i].Title) ? $"Chapter {i + 1}" : chapters[i].Title!.Trim();
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var time = new TextBlock { Text = PlaybackTransportFeedback.Clock(start, forceHours: hours), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(time, 1);
            grid.Children.Add(time);
            var row = new Button { Style = (Style)Application.Current.Resources["SkylinePanelRowStyle"], Content = grid };
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(row, title);
            row.Click += (_, _) =>
            {
                // TvPlayerHud.kt: a chapter seeks and closes the panel.
                CloseOptions();
                QueueSeekTo(start);
            };
            ChapterRows.Children.Add(row);
        }
    }

    private enum OptionsTab { Info, Video, Chapters }

    private void OnOptionsTabFocus(object sender, RoutedEventArgs e) => ShowOptionsTab(
        ReferenceEquals(sender, VideoTab) ? OptionsTab.Video : ReferenceEquals(sender, ChaptersTab) ? OptionsTab.Chapters : OptionsTab.Info);

    private void ShowOptionsTab(OptionsTab tab)
    {
        InfoTab.IsChecked = tab == OptionsTab.Info;
        VideoTab.IsChecked = tab == OptionsTab.Video;
        ChaptersTab.IsChecked = tab == OptionsTab.Chapters;
        InfoPane.Visibility = tab == OptionsTab.Info ? Visibility.Visible : Visibility.Collapsed;
        VideoPane.Visibility = tab == OptionsTab.Video ? Visibility.Visible : Visibility.Collapsed;
        ChaptersPane.Visibility = tab == OptionsTab.Chapters ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAutoPlayRow() => AutoPlayRowValue.Text = _upNext.AutoPlay ? "On" : "Off";

    /// <summary>TvPlayerHud.kt "Auto-play next": flips the setting for this device and the card on screen.</summary>
    private async void OnAutoPlayRowClick(object sender, RoutedEventArgs e)
    {
        if (!_active || _leaving || _attempt is not { } attempt) return;
        var value = !_upNext.AutoPlay;
        ApplyAutoPlay(value);
        try { await attempt.Client.Settings.SetAutoPlayNextAsync(value); }
        catch (Exception ex)
        {
            LogFailure("auto-play write", ex);
            if (_active && ReferenceEquals(_attempt, attempt)) ApplyAutoPlay(!value);
        }
    }
}
