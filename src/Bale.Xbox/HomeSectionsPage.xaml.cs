using Bale.Core;
using Bale.Core.Browse;
using Bale.Core.Settings;
using Bale.Xbox.Controls;
using Bale.Xbox.Services;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>TvHomeSectionsEditor.kt: immediate local saves, same-row focus after moves, Done on entry.</summary>
public sealed partial class HomeSectionsPage : Page, IBackHandler, IRestoresFocus
{
    private readonly Rail _header;
    private readonly Dictionary<string, HomeSectionEditorRow> _rows = new(StringComparer.Ordinal);
    private IReadOnlyList<MediaRow> _available = [];
    private HomeSectionLayout _layout = new();
    private SiloClient? _client;
    private string? _profileId;
    private CancellationTokenSource? _read;
    private bool _editing;
    private bool _active;
    private bool _closing;
    private bool _backPressed;

    public HomeSectionsPage()
    {
        InitializeComponent();
        _header = Rail.Attach(HeaderActions);
        _header.Pin = false;
        _header.Remembered = DoneButton;
        Loaded += (_, _) => RestoreFocus();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _active = true;
        _client = Session.Client;
        _profileId = _client?.ProfileId;
        if (_client is not { } client || _profileId is null)
        {
            StatusMessage.Text = "Silo couldn’t refresh the Home rows. Try again when the server is reachable.";
            return;
        }
        _layout = HomeSectionSettings.Load(client);
        if (HomeSectionSettings.TryGetRows(client, out var rows) && rows.Count > 0)
        {
            _available = rows;
            UpdateRows();
        }
        else _ = ReadRowsAsync(client, _profileId);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false;
        _read?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private bool OwnsEditor => _active && !_closing && ReferenceEquals(Frame?.Content, this)
        && ReferenceEquals(_client, Session.Client) && _client?.ProfileId == _profileId;

    private async Task ReadRowsAsync(SiloClient client, string profileId)
    {
        using var read = new CancellationTokenSource();
        _read = read;
        try
        {
            var rows = await client.Browse.LoadHomeAsync(ArtworkWidths.Tv1080, read.Token);
            if (!OwnsEditor || read.IsCancellationRequested || client.ProfileId != profileId) return;
            _available = rows;
            HomeSectionSettings.Remember(client, profileId, rows);
            UpdateRows();
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!OwnsEditor || read.IsCancellationRequested) return;
            Log.Error("Home section editor load", error);
            StatusMessage.Text = "Silo couldn’t refresh the Home rows. Try again when the server is reachable.";
            StatusMessage.Visibility = Visibility.Visible;
        }
        finally { if (ReferenceEquals(_read, read)) _read = null; }
    }

    private void UpdateRows()
    {
        var sections = _layout.Arrange(_available, includingHidden: true);
        var ids = sections.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _rows.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            SectionRows.Children.Remove(_rows[id]);
            _rows.Remove(id);
        }
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            if (!_rows.TryGetValue(section.Id, out var row))
            {
                row = new HomeSectionEditorRow();
                row.Activated += OnRowActivated;
                row.GotFocus += OnRowFocused;
                _rows.Add(section.Id, row);
            }
            if (SectionRows.Children.IndexOf(row) != index)
            {
                SectionRows.Children.Remove(row);
                SectionRows.Children.Insert(index, row);
            }
            row.Update(section, _layout, _editing, index, sections.Count);
        }
        EditButton.IsEnabled = sections.Count > 1;
        StatusMessage.Text = "Home has no populated rows to arrange yet.";
        StatusMessage.Visibility = sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EditorFooter.Visibility = sections.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRowActivated(HomeSectionEditorRow row, int action)
    {
        if (!OwnsEditor || _client is null || _profileId is null) return;
        var next = action == 0 ? _layout.WithVisible(row.SectionId, _layout.HiddenSectionIds.Contains(row.SectionId))
            : _layout.Move(_available, row.SectionId, action);
        if (ReferenceEquals(next, _layout)) return;
        if (!HomeSectionSettings.Save(_client, _profileId, next))
        {
            // Do not make an unsaved preference look persisted, or lose the current control's focus.
            SaveError.Text = "Bale couldn’t save Home sections on this Xbox. Try again.";
            SaveError.Visibility = Visibility.Visible;
            return;
        }
        SaveError.Visibility = Visibility.Collapsed;
        _layout = next;
        UpdateRows();
        if (action != 0)
        {
            var arranged = _layout.Arrange(_available, includingHidden: true);
            var index = arranged.ToList().FindIndex(section => section.Id == row.SectionId);
            // TvHomeSectionsEditor.move: keep the same section; endpoints focus the enabled arrow.
            action = index == 0 ? 1 : index == arranged.Count - 1 ? -1 : action;
        }
        FocusRow(row, action);
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        _editing = !_editing;
        EditButton.Content = _editing ? "Done Editing" : "Edit";
        EditorDescription.Text = _editing ? "Move rows into your preferred order." : "Choose which rows appear on Home.";
        EditorFooter.Text = _editing ? "Use the arrow buttons to move rows. The new order saves immediately."
            : "Open eye: visible on Home. Closed eye: hidden. Hidden rows leave no gap.";
        UpdateRows();
        FocusHeader(EditButton);
    }

    private void OnDoneClick(object sender, RoutedEventArgs e) => OnBack();

    public bool OnBack()
    {
        if (_closing) return true;
        _closing = true;
        if (Frame.CanGoBack) Frame.GoBack();
        return true;
    }

    public void RestoreFocus()
    {
        // TvHomeSectionsEditor.kt always enters on Done, not the first visibility control.
        FocusHeader(DoneButton);
    }

    private void FocusHeader(Button button)
    {
        _header.Remembered = button;
        FocusRetry.Until(this, () => !OwnsEditor || (button.Focus(FocusState.Programmatic) && button.FocusState != FocusState.Unfocused));
    }

    private void FocusRow(HomeSectionEditorRow row, int action)
    {
        FocusRetry.Until(this, () => !OwnsEditor || row.FocusAction(action));
    }

    private void OnRowFocused(object sender, RoutedEventArgs e)
    {
        if (sender is not HomeSectionEditorRow row) return;
        // Rail intentionally suppresses bring-into-view. Reveal vertically, without moving sideways.
        var top = row.TransformToVisual(SectionRows).TransformPoint(new Point()).Y;
        var bottom = top + row.ActualHeight;
        var offset = RowsScroller.VerticalOffset;
        if (top < offset) RowsScroller.ChangeView(null, top, null, disableAnimation: true);
        else if (bottom > offset + RowsScroller.ViewportHeight)
            RowsScroller.ChangeView(null, bottom - RowsScroller.ViewportHeight, null, disableAnimation: true);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!OwnsEditor) return;
        if (SettingsInput.IsBack(e.Key))
        {
            _backPressed = true;
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Tab) { e.Handled = true; return; }
        var step = SettingsInput.VerticalStep(e.Key);
        var focused = FocusManager.GetFocusedElement() as DependencyObject;
        if (SettingsInput.IsWithin(focused, HeaderActions))
        {
            var horizontal = SettingsInput.HorizontalStep(e.Key);
            if (horizontal != 0)
            {
                var target = horizontal < 0 && EditButton.IsEnabled ? EditButton : DoneButton;
                FocusHeader(target);
                e.Handled = true;
            }
            if (step != 0)
            {
                if (step > 0 && SectionRows.Children.FirstOrDefault() is HomeSectionEditorRow first) FocusRow(first, 0);
                e.Handled = true;
            }
            return;
        }
        if (step == 0) return; // Each row's Rail/control contains Left/Right.
        var rows = SectionRows.Children.OfType<HomeSectionEditorRow>().ToList();
        var index = rows.FindIndex(row => SettingsInput.IsWithin(focused, row));
        if (index < 0) return;
        var action = rows[index].FocusedAction;
        var targetIndex = index + step;
        if (targetIndex < 0) FocusHeader(DoneButton);
        else if (targetIndex < rows.Count && rows[targetIndex].CanFocusAction(action)) FocusRow(rows[targetIndex], action);
        // No sideways fallback when that arrow is disabled, or Down reaches the last row.
        e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!SettingsInput.IsBack(e.Key)) return;
        e.Handled = true;
        if (!_backPressed) return;
        _backPressed = false;
        OnBack();
    }
}
