using System.Collections.ObjectModel;
using Bale.Core;
using Bale.Core.Requests;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox;

/// <summary>TvRequestsPage.kt: a secondary shell route with passive preview and row-snapped poster shelves.</summary>
public sealed partial class RequestsPage : Page, IRestoresFocus, IBackHandler
{
    private readonly ObservableCollection<RequestRowView> _rows = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _load;
    private SiloClient? _client;
    private string? _profile;
    private int _version;
    private int _focusVersion;
    private bool _active;
    private bool _updating;
    private bool _leaving;
    private bool _entryPending = true;
    private bool _loading;
    private RequestRowView? _lastRow;
    private RequestCardView? _preview;
    private RequestCardView? _pendingPreview;
    private RequestCardView? _actionTarget;
    private ContentControl? _pressed;
    private VirtualKey? _heldKey;
    private bool _longPress;
    private MaskedArt? _art;
    private AmbientTint.WashAnimation? _wash;

    public RequestsPage()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        RowsScroller.BringIntoViewRequested += (_, args) => args.Handled = true;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); ShowPreview(_pendingPreview); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            _longPress = true;
            if (ReferenceEquals(FocusedCard(), _pressed) && _pressed?.Content is RequestCardView card) ShowActions(card);
        };
        ActionDialog.Dismissed += () => { _actionTarget = null; FocusContent(); };
        ActionDialog.Confirmed += OnCancelConfirmed;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _active = true;
        _leaving = false;
        try
        {
            _art = new MaskedArt(ArtBox);
            _wash = new AmbientTint.WashAnimation(Wash);
            if (_preview is { } preview) ShowPreview(preview);
        }
        catch (Exception ex) { Log.Error("Requests artwork", ex); }
        await LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _active = false;
        _version++;
        _focusVersion++;
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
        _previewTimer.Stop();
        _toastTimer.Stop();
        CancelPress();
        ActionDialog.Hide();
        _art?.Dispose();
        _art = null;
        _wash?.Dispose();
        _wash = null;
    }

    private async Task LoadAsync()
    {
        if (Session.Client is not { } client) return;
        if (!ReferenceEquals(_client, client) || _profile != client.ProfileId)
        {
            _rows.Clear();
            _lastRow = null;
            _preview = _pendingPreview = null;
            _previewTimer.Stop();
            _art?.Hide();
            _wash?.Show(null);
            _entryPending = true;
        }
        _client = client;
        _profile = client.ProfileId;
        _load?.Cancel();
        _load?.Dispose();
        var cancellation = new CancellationTokenSource();
        _load = cancellation;
        var version = ++_version;
        _loading = true;
        if (_rows.Count == 0) ShowState("Loading");
        try
        {
#if DEBUG_SERVER
            var empty = DebugServer.TakeRequestsEmptyOrThrow();
#else
            var empty = false;
#endif
            var rows = empty ? Array.Empty<RequestRow>() : await client.Requests.LoadAsync(cancellation.Token);
            if (!IsCurrent()) return;
            UpdateRows(rows);
            ShowState(_rows.Count == 0 ? "Empty" : "Content");
            if (_rows.Count > 0) ShowPreview((_lastRow ?? _rows[0]).LastFocused ?? _rows[0].Cards[0]);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrent()) return;
            Log.Error("Requests load", ex);
            if (_rows.Count == 0)
            {
                ErrorText.Text = RequestsFeed.ErrorMessage(ex);
                ShowState("Error");
            }
            else ShowToast(RequestsFeed.ErrorMessage(ex));
        }
        finally
        {
            if (IsCurrent())
            {
                _loading = false;
                if (_entryPending || StateOwner.FocusState != FocusState.Unfocused || RetryButton.FocusState != FocusState.Unfocused)
                    FocusContent();
            }
        }

        bool IsCurrent() => _active && version == _version && ReferenceEquals(client, Session.Client) && client.ProfileId == _profile;
    }

    private void UpdateRows(IReadOnlyList<RequestRow> rows)
    {
        _updating = true;
        try
        {
            var index = 0;
            foreach (var row in rows)
            {
                var existing = _rows.Skip(index).FirstOrDefault(view => view.Key == row.Key);
                if (existing is null) _rows.Insert(index, new RequestRowView(row));
                else
                {
                    var previous = _rows.IndexOf(existing);
                    if (previous != index) _rows.Move(previous, index);
                    existing.Update(row);
                }
                index++;
            }
            while (_rows.Count > index) _rows.RemoveAt(_rows.Count - 1);
            if (_lastRow is not null && !_rows.Contains(_lastRow)) _lastRow = _rows.FirstOrDefault();
        }
        finally { _updating = false; }
    }

    private void ShowState(string state)
    {
        var content = state == "Content";
        MarqueeRegion.Visibility = RowsScroller.Visibility = content ? Visibility.Visible : Visibility.Collapsed;
        StateOwner.Visibility = state is "Loading" or "Empty" ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = state == "Loading" ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = state == "Empty" ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = state == "Error" ? Visibility.Visible : Visibility.Collapsed;
        Windows.UI.Xaml.Automation.AutomationProperties.SetName(StateOwner, state == "Loading" ? "Loading requests" : "Nothing here yet");
        if (_active && _entryPending) FocusContent();
    }

    private void OnRailLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not ListView { DataContext: RequestRowView row } list) return;
        if (list.Tag is Rail existing) { row.Rail = existing; return; }
        var rail = Rail.Attach(list);
        rail.EntryItem = () => row.LastFocused ?? row.Cards.FirstOrDefault();
        rail.Entered += () => PinRow(list);
        row.Rail = rail;
        list.Tag = rail;
    }

    private void PinRow(ListView list)
    {
        if (VisualTreeHelper.GetParent(list) is not FrameworkElement panel) return;
        var top = panel.TransformToVisual(Rows).TransformPoint(new Point(0, 0)).Y;
        RowsScroller.ChangeView(null, Math.Max(0, top), null);
    }

    private void OnCardFocus(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is not ContentControl { Content: RequestCardView card } container) return;
        CardFocus.Apply(container, true);
        if (_updating || _leaving || !_active) return;
        if (sender is ListView { DataContext: RequestRowView row } list)
        {
            _lastRow = row;
            row.LastFocused = card;
            PinRow(list);
        }
        _pendingPreview = card;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void OnCardBlur(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is ContentControl { Content: RequestCardView } container) CardFocus.Apply(container, false);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        try
        {
            if (ActionDialog.IsOpen) return;
            var step = args.Key switch
            {
                VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => -1,
                VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => 1,
                _ => 0,
            };
            if (step != 0)
            {
                args.Handled = true;
                _focusVersion++;
                CancelPress();
                if (_rows.Count > 0 && _lastRow is { } row && RowsScroller.Visibility == Visibility.Visible)
                {
                    var next = _rows.IndexOf(row) + step;
                    if (next < 0) { _entryPending = false; ShellPage.Current?.FocusRequestsAvatar(); }
                    else if (next < _rows.Count) FocusRow(_rows[next]);
                }
                else if (step < 0) { _entryPending = false; ShellPage.Current?.FocusRequestsAvatar(); }
                return;
            }
            if (args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight
                or VirtualKey.GamepadLeftThumbstickLeft or VirtualKey.GamepadLeftThumbstickRight) _focusVersion++;
            if (FocusedCard() is not { Content: RequestCardView card } focused) return;
            if (args.Key is VirtualKey.Application or VirtualKey.GamepadMenu)
            {
                args.Handled = true;
                if (!args.KeyStatus.WasKeyDown) ShowActions(card);
            }
            else if (args.Key is VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA)
            {
                args.Handled = true;
                if (args.KeyStatus.WasKeyDown || _heldKey is not null) return;
                _pressed = focused;
                _heldKey = args.Key;
                _longPress = false;
                _holdTimer.Start();
            }
        }
        catch (Exception ex) { args.Handled = true; Log.Error("Requests keys", ex); }
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (_heldKey != args.Key) return;
        args.Handled = true;
        var card = !_longPress && ReferenceEquals(FocusedCard(), _pressed) ? _pressed?.Content as RequestCardView : null;
        CancelPress();
        if (card is not null) OpenCard(card);
    }

    private static ContentControl? FocusedCard() => FocusManager.GetFocusedElement() as ContentControl;
    private void CancelPress() { _holdTimer.Stop(); _heldKey = null; _pressed = null; _longPress = false; }
    private void OnCardClick(object sender, ItemClickEventArgs args) { if (args.ClickedItem is RequestCardView card) OpenCard(card); }

    private void OpenCard(RequestCardView card)
    {
        if (ActionDialog.IsOpen || _leaving) return;
        if (card.Item.LibraryItemToOpen is { } library)
        {
            _leaving = true;
            ShellPage.Current?.OpenDetail(library);
        }
        else if (RequestPresentation.CanRequest(card.Item.MediaType))
        {
            _leaving = true;
            ShellPage.Current?.OpenRequestDetail(new RequestDetailEntry(card.Item.MediaType, card.Item.TmdbId));
        }
        else ShowToast("Request details for this media type are not available on TV yet");
    }

    private void ShowActions(RequestCardView card)
    {
        if (card.Item.Record is not { } record || _client?.Requests.CanCancel(record) != true) return;
        _actionTarget = card;
        ActionDialog.Show(card.Item.Title, card.Item.Progress!.LongLabel, canCancel: true);
    }

    private async void OnCancelConfirmed()
    {
        var record = _actionTarget?.Item.Record;
        _actionTarget = null;
        if (record is null || _client is not { } client) return;
        var profile = _profile;
        FocusContent();
        var result = await client.Requests.CancelAsync(record);
        if (!_active || !ReferenceEquals(client, Session.Client) || profile != client.ProfileId) return;
        if (result.Message is { } message) ShowToast(message);
        // One read settles a write when possible; this never retries the POST.
        _entryPending = true;
        await LoadAsync();
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ShowPreview(RequestCardView? card)
    {
        if (card is null) return;
        _preview = card;
        var item = card.Item;
        MarqueeTitle.Text = item.Title;
        MarqueeFacts.Text = item.Facts;
        MarqueeOverview.Text = item.Overview ?? string.Empty;
        var progress = item.Progress is { Display: not RequestDisplay.Unavailable } value ? value : null;
        PreviewStatus.Show(progress, item.StatusText);
        PreviewTrack.Show(progress, track: true, label: false);
        if (item.BackdropUrl is { } backdrop)
        {
            _art?.Show(backdrop);
            _ = TintAsync(card, backdrop);
        }
        else { _art?.Hide(); _wash?.Show(null); }
    }

    private Task TintAsync(RequestCardView card, Uri backdrop)
    {
        AmbientTint.Apply(Wash, backdrop, tint =>
        {
            if (_active && ReferenceEquals(_preview, card)) _wash?.Show(tint);
        });
        return Task.CompletedTask;
    }

    private void FocusRow(RequestRowView row)
    {
        var version = ++_focusVersion;
        FocusRetry.Until(Rows, () => !_active || _leaving || version != _focusVersion || ActionDialog.IsOpen
            || row.Rail?.Focus(FocusState.Keyboard) == true);
    }

    public void FocusContent(bool first = false)
    {
        // A loading placeholder owns focus now, but the completed read must hand it to content.
        _entryPending = _loading;
        if (!_active) { _entryPending = true; return; }
        var version = ++_focusVersion;
        FocusRetry.Until(this, () =>
        {
            if (!_active || _leaving || version != _focusVersion || ActionDialog.IsOpen) return true;
            if (ErrorPanel.Visibility == Visibility.Visible) return RetryButton.Focus(FocusState.Programmatic);
            if (StateOwner.Visibility == Visibility.Visible) return StateOwner.Focus(FocusState.Programmatic);
            if (_rows.Count == 0) return false;
            var row = first ? _rows[0] : _lastRow ?? _rows[0];
            if (row.Rail is not { } rail) return false;
            if (first) return rail.FocusFirst();
            var target = row.LastFocused ?? row.Cards.FirstOrDefault();
            if (target is null) return false;
            if (rail.FocusItem(target, preserveHorizontalOffset: true)) return true;
            // Realize the exact remembered card, never accept a neighbouring realized container.
            if (rail.List is ListView list) list.ScrollIntoView(target);
            return false;
        });
    }

    internal async void EnterFromMenu()
    {
        _lastRow = null;
        foreach (var row in _rows) row.LastFocused = null;
        FocusContent(first: true);
        _entryPending = true;
        await LoadAsync();
    }

    public void RestoreFocus()
    {
        _entryPending = true;
        _leaving = false;
        FocusContent();
    }

    public bool OnBack() => ActionDialog.Dismiss();
    private async void OnRetryClick(object sender, RoutedEventArgs args) { _entryPending = true; await LoadAsync(); }
}
