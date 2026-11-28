using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox;

/// <summary>
/// The Options panel (TvPlayerHud.kt, TVPlayerInfoHUD.swift): opened from the control bar's Options button or the
/// Menu button, it replaces the controls and stays until Back. Tabs Info · Stats · Video · Audio · Subtitles ·
/// Chapters, empty ones hidden (PlaybackOptionsTabs.Visible). Info shows the title and how it plays; Stats the
/// official playback readout; Video the automation rows; Audio and Subtitles list the tracks and switch on Select
/// (the panel stays open, the check moves); Chapters seeks to the one picked. Tabs follow focus, as on the TV apps;
/// inside a pane Up/Down walk the rows and never turn sideways.
/// </summary>
public sealed partial class PlayerPage
{
    private static readonly PlaybackOptionsTab[] AllOptionsTabs =
    [
        PlaybackOptionsTab.Info, PlaybackOptionsTab.Stats, PlaybackOptionsTab.Video,
        PlaybackOptionsTab.Audio, PlaybackOptionsTab.Subtitles, PlaybackOptionsTab.Chapters,
    ];

    private PlaybackOptionsTab _optionsTab = PlaybackOptionsTab.Info;
    private IReadOnlyList<PlaybackOptionsTab> _optionsTabs = [PlaybackOptionsTab.Info, PlaybackOptionsTab.Video];
    private IReadOnlyList<PlaybackTrackRow> _audioRows = [];
    private IReadOnlyList<PlaybackTrackRow> _subtitleRows = [];
    private IReadOnlyList<PlaybackStatsSection> _optionsStats = [];

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
            FillChapters(chapters);
            FillTracks();
            // The last once-a-second throughput sample stays; the rest is read now.
            FillOptionsStats(RefreshHudStats(attempt, sampleBandwidth: false).OfficialSections());
            _optionsTabs = PlaybackOptionsTabs.Visible(_optionsStats.Count > 0, PlaybackOptionsTabs.TrackCount(_audioRows),
                PlaybackOptionsTabs.TrackCount(_subtitleRows), chapters.Count);
            foreach (var tab in AllOptionsTabs)
                OptionsTabButton(tab).Visibility = _optionsTabs.Contains(tab) ? Visibility.Visible : Visibility.Collapsed;
            UpdateAutoPlayRow();
            ShowOptionsTab(PlaybackOptionsTab.Info);
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

    private ToggleButton OptionsTabButton(PlaybackOptionsTab tab) => tab switch
    {
        PlaybackOptionsTab.Stats => StatsTab,
        PlaybackOptionsTab.Video => VideoTab,
        PlaybackOptionsTab.Audio => AudioTab,
        PlaybackOptionsTab.Subtitles => SubtitlesTab,
        PlaybackOptionsTab.Chapters => ChaptersTab,
        _ => InfoTab,
    };

    private FrameworkElement OptionsPane(PlaybackOptionsTab tab) => tab switch
    {
        PlaybackOptionsTab.Stats => StatsPane,
        PlaybackOptionsTab.Video => VideoPane,
        PlaybackOptionsTab.Audio => AudioPane,
        PlaybackOptionsTab.Subtitles => SubtitlesPane,
        PlaybackOptionsTab.Chapters => ChaptersPane,
        _ => InfoPane,
    };

    /// <summary>The pane's focusable rows, top to bottom (Info and Stats are read-only).</summary>
    private List<Button> OptionsPaneRows(PlaybackOptionsTab tab) => tab switch
    {
        PlaybackOptionsTab.Video => [AutoPlayRow],
        PlaybackOptionsTab.Audio => AudioRows.Children.OfType<Button>().ToList(),
        PlaybackOptionsTab.Subtitles => SubtitleRows.Children.OfType<Button>().ToList(),
        PlaybackOptionsTab.Chapters => ChapterRows.Children.OfType<Button>().ToList(),
        _ => [],
    };

    private void OnOptionsTabFocus(object sender, RoutedEventArgs e)
    {
        foreach (var tab in AllOptionsTabs)
        {
            if (!ReferenceEquals(sender, OptionsTabButton(tab))) continue;
            ShowOptionsTab(tab);
            return;
        }
    }

    private void ShowOptionsTab(PlaybackOptionsTab tab)
    {
        _optionsTab = tab;
        foreach (var each in AllOptionsTabs)
        {
            OptionsTabButton(each).IsChecked = each == tab;
            OptionsPane(each).Visibility = each == tab ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// D-pad inside the panel, explicit rather than directional search (a full-width row is "below" every tab):
    /// Left/Right walk the tab bar and stop at its ends; Down from a tab enters its pane on the checked row; Up/Down
    /// walk a pane's rows, Up from the first returns to its tab, and the last row and Left/Right in a pane stay put.
    /// </summary>
    private void OnOptionsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            var up = e.Key is VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp;
            var down = e.Key is VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown;
            var left = e.Key is VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft;
            var right = e.Key is VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight;
            if (!up && !down && !left && !right) return;
            var focused = FocusManager.GetFocusedElement();
            if (AllOptionsTabs.Where(tab => ReferenceEquals(focused, OptionsTabButton(tab))).Select(tab => (PlaybackOptionsTab?)tab).FirstOrDefault() is { } focusedTab
                && focusedTab != _optionsTab) ShowOptionsTab(focusedTab);
            var rows = OptionsPaneRows(_optionsTab);
            if (ReferenceEquals(focused, OptionsTabButton(_optionsTab)))
            {
                e.Handled = true;
                if ((left || right) && PlaybackOptionsTabs.NextTab(_optionsTabs, _optionsTab, forward: right) is { } nextTab)
                    OptionsTabButton(nextTab).Focus(FocusState.Keyboard);
                else if (down && PlaybackOptionsTabs.EntryRow(RowSelection(_optionsTab, rows.Count), RowsEnabled(rows)) is >= 0 and var entry)
                    rows[entry].Focus(FocusState.Keyboard);
                return;
            }
            var index = rows.FindIndex(candidate => ReferenceEquals(candidate, focused));
            if (index < 0) return;
            e.Handled = true;
            if (left || right) return;
            var target = PlaybackOptionsTabs.MoveFromRow(index, rows.Count, down, RowsEnabled(rows));
            if (target == -1) OptionsTabButton(_optionsTab).Focus(FocusState.Keyboard);
            else if (target is { } targetRow) rows[targetRow].Focus(FocusState.Keyboard);
        }
        catch (Exception ex) { LogFailure("options input", ex); }
    }

    private static bool[] RowsEnabled(List<Button> rows) => rows.Select(row => row.IsEnabled).ToArray();

    /// <summary>Which of a pane's rows is checked: the playing track in Audio / Subtitles, none elsewhere.</summary>
    private bool[] RowSelection(PlaybackOptionsTab tab, int count)
    {
        var tracks = tab == PlaybackOptionsTab.Audio ? _audioRows : tab == PlaybackOptionsTab.Subtitles ? _subtitleRows : null;
        return Enumerable.Range(0, count).Select(i => tracks is not null && i < tracks.Count && tracks[i].IsSelected).ToArray();
    }

    /// <summary>
    /// Apple's tvOS Stats pane (PlaybackStatsPanel, usesTwoColumnLayout): Playback on the left; Stream and Engine on the right.
    /// Each section has an eyebrow header, then label/value rows (a value that does not fit ends in an ellipsis). The pane is
    /// read-only, so no row takes focus.
    /// </summary>
    private void FillOptionsStats(IReadOnlyList<PlaybackStatsSection> sections)
    {
        if (SameStats(_optionsStats, sections)) return;
        _optionsStats = sections;
        StatsPaneLeftRows.Children.Clear();
        StatsPaneRightRows.Children.Clear();
        foreach (var section in sections)
        {
            var column = section.Id == "playback" ? StatsPaneLeftRows : StatsPaneRightRows;
            column.Children.Add(new TextBlock
            {
                Text = section.Title.ToUpperInvariant(), Style = StatsSectionStyleSource.Style,
                Margin = column.Children.Count == 0 ? new Thickness(0) : new Thickness(0, 16, 0, 4),
            });
            foreach (var (label, value) in section.Rows)
            {
                var row = new Grid { Style = StatsRowStyleSource.Style };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock { Text = label, Style = StatsLabelStyleSource.Style });
                // HudReadOnlyRow: the value ends in an ellipsis rather than wrapping (the debug overlay wraps).
                var text = new TextBlock
                {
                    Text = value, Style = StatsValueStyleSource.Style,
                    TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
                };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                column.Children.Add(row);
            }
        }
    }

    private static bool SameStats(IReadOnlyList<PlaybackStatsSection> before, IReadOnlyList<PlaybackStatsSection> after) =>
        before.Count == after.Count && before.Zip(after).All(pair => pair.First.Id == pair.Second.Id
            && pair.First.Rows.SequenceEqual(pair.Second.Rows));

    /// <summary>OnTick, once a second: the open Stats tab follows playback.</summary>
    private void UpdateOptionsStats()
    {
        if (OptionsOpen && _optionsTab == PlaybackOptionsTab.Stats) FillOptionsStats(_hudStats.OfficialSections());
    }

    private IReadOnlyList<PlaybackTrackRow> PlayingTrackRows(PlaybackSelectorKind kind) =>
        PlaybackOptionsTabs.TrackRows(TrackOptions(kind));

    private void FillTracks()
    {
        _audioRows = PlayingTrackRows(PlaybackSelectorKind.Audio);
        _subtitleRows = PlayingTrackRows(PlaybackSelectorKind.Subtitles);
        FillTrackRows(AudioRows, _audioRows, PlaybackSelectorKind.Audio);
        FillTrackRows(SubtitleRows, _subtitleRows, PlaybackSelectorKind.Subtitles);
        // TvPlayerHud.kt HudSubtitlesPane: "Tracks · 3" (Off is not counted).
        var subtitles = PlaybackOptionsTabs.TrackCount(_subtitleRows);
        SubtitleTracksHeader.Text = subtitles > 0 ? $"TRACKS · {subtitles}" : "TRACKS";
    }

    private void FillTrackRows(Panel panel, IReadOnlyList<PlaybackTrackRow> rows, PlaybackSelectorKind kind)
    {
        panel.Children.Clear();
        foreach (var track in rows)
        {
            // HudSubtitleTrackRow: check slot, name, attributes on the right; the playing row keeps a soft fill.
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new FontIcon { Glyph = "\uE73E", FontSize = 15, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
            var name = new TextBlock { Text = track.Name, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);
            if (track.Attributes is { } attributes)
            {
                var detail = new TextBlock
                {
                    Text = attributes, FontSize = 12.04, Opacity = 0.6, MaxWidth = 300, MaxLines = 1, Margin = new Thickness(12, 0, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(detail, 2);
                grid.Children.Add(detail);
            }
            var row = new Button { Style = (Style)Application.Current.Resources["SkylinePanelRowStyle"], Content = grid };
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(row, track.Attributes is { } spoken ? $"{track.Name}, {spoken}" : track.Name);
            MarkTrackRow(row, track.IsSelected);
            // TvPlayerHud.kt (Android 461240c6d): a track no route can show is listed dimmed (the capsule's Disabled
            // state), out of focus and Select.
            if (!track.IsEnabled) row.IsEnabled = false;
            var index = track.TrackIndex;
            row.Click += (_, _) => OnTrackRowClick(kind, index);
            panel.Children.Add(row);
        }
    }

    private static void MarkTrackRow(Button row, bool selected)
    {
        // TvPlayerChrome.Selected (white 7.5%) under the playing track; focus still inverts the row.
        if (selected) row.Background = new SolidColorBrush(Color.FromArgb(0x13, 0xFF, 0xFF, 0xFF));
        else row.ClearValue(Control.BackgroundProperty);
        if (row.Content is Grid grid && grid.Children.OfType<FontIcon>().FirstOrDefault() is { } check)
            check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// TVPlayerInfoHUD.swift selectAudio / selectSubtitle: the same switch as the control bar's menus; the panel stays. The
    /// checked row goes through too: it switches nothing but saves the playing track as the viewer's choice.
    /// </summary>
    private void OnTrackRowClick(PlaybackSelectorKind kind, int trackIndex)
    {
        var rows = kind == PlaybackSelectorKind.Audio ? _audioRows : _subtitleRows;
        if (!rows.Any(row => row.TrackIndex == trackIndex && row.IsEnabled)) return;
        _ = SwitchTrackFromOptionsAsync(kind, trackIndex);
    }

    private async Task SwitchTrackFromOptionsAsync(PlaybackSelectorKind kind, int trackIndex)
    {
        try
        {
            // A subtitle row may be in-band closed captions, which the player draws itself (PlayerPage.Captions.cs).
            await (kind == PlaybackSelectorKind.Audio ? ChangeTracksAsync(trackIndex, null) : PickSubtitleAsync(trackIndex));
            if (OptionsOpen) MarkTrackRows();
        }
        catch (Exception ex) { LogFailure("options track", ex); }
    }

    /// <summary>Moves the checks to what plays now, in place so focus stays on the row; a changed list is rebuilt.</summary>
    private void MarkTrackRows()
    {
        var audio = PlayingTrackRows(PlaybackSelectorKind.Audio);
        var subtitles = PlayingTrackRows(PlaybackSelectorKind.Subtitles);
        if (!SameTracks(audio, _audioRows) || !SameTracks(subtitles, _subtitleRows))
        {
            FillTracks();
            if (_optionsTab is PlaybackOptionsTab.Audio or PlaybackOptionsTab.Subtitles)
                OptionsTabButton(_optionsTab).Focus(FocusState.Programmatic);
            return;
        }
        _audioRows = audio;
        _subtitleRows = subtitles;
        Mark(AudioRows, audio);
        Mark(SubtitleRows, subtitles);

        static bool SameTracks(IReadOnlyList<PlaybackTrackRow> a, IReadOnlyList<PlaybackTrackRow> b) =>
            a.Select(row => row.TrackIndex).SequenceEqual(b.Select(row => row.TrackIndex));

        static void Mark(Panel panel, IReadOnlyList<PlaybackTrackRow> rows)
        {
            foreach (var (row, track) in panel.Children.OfType<Button>().Zip(rows)) MarkTrackRow(row, track.IsSelected);
        }
    }

    private void UpdateAutoPlayRow() => AutoPlayRowValue.Text = _upNext.AutoPlay ? "On" : "Off";

    /// <summary>TvPlayerHud.kt "Auto-play next": flips the setting for this device and the card on screen.</summary>
    private async void OnAutoPlayRowClick(object sender, RoutedEventArgs e)
    {
        if (!_active || _leaving || _attempt is not { } attempt) return;
#if DEBUG_SERVER
        if (attempt.DebugUrl is not null) return;
#endif
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
