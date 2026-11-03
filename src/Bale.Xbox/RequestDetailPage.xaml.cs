using System.Collections.ObjectModel;
using System.Numerics;
using Bale.Core;
using Bale.Core.Detail;
using Bale.Core.Requests;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>TvRequestDetailScreen.kt: a full-screen detail route with explicit viewer-only actions.</summary>
public sealed partial class RequestDetailPage : Page, IRestoresFocus, IBackHandler
{
    private readonly ObservableCollection<RequestCardView> _recommendations = new();
    private readonly Rail _actionRail;
    private readonly Rail _recommendationRail;
    private RequestDetailEntry? _entry;
    private RequestDetailState? _state;
    private SiloClient? _client;
    private string? _profile;
    private CancellationTokenSource? _load;
    private int _version;
    private int _focusVersion;
    private bool _active;
    private bool _needFocus = true;
    private bool _restoring;
    private bool _restoringFocus;
    private bool _offsetsRestored;
    private bool _submitting;
    private bool _cancelling;
    private bool _following;
    private bool _backPressed;
    private string _focusArea = "primary";
    private RequestCardView? _lastRecommendation;
    private MaskedArt? _art;
    private Uri? _backdrop;

    public RequestDetailPage()
    {
        InitializeComponent();
        Recommendations.ItemsSource = _recommendations;
        _actionRail = Rail.Attach(Actions);
        _actionRail.Pin = false;
        _recommendationRail = Rail.Attach(Recommendations);
        _recommendationRail.EntryItem = () => _lastRecommendation ?? _recommendations.FirstOrDefault();
        _recommendationRail.Entered += () => { if (!_restoringFocus) PinRecommendations(); };
        PageContent.BringIntoViewRequested += (_, args) => args.Handled = true;
        PageContent.GotFocus += OnContentFocus;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        _entry = args.Parameter as RequestDetailEntry;
        _restoring = args.NavigationMode == NavigationMode.Back;
        _focusArea = _restoring ? _entry?.FocusArea ?? "primary" : "primary";
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        if (_entry is { } entry)
        {
            // The journal parameter owns this snapshot, not a static title-keyed cache. The same
            // title can occur more than once in a nested recommendation chain (TvAppNavigation.kt).
            entry.FocusArea = _focusArea;
            entry.RecommendationKey = _lastRecommendation?.Item.Key;
            entry.ScrollOffset = PageScroller.VerticalOffset;
            entry.RailOffset = Rail.FindChild<ScrollViewer>(Recommendations)?.HorizontalOffset ?? 0;
        }
        _active = false;
        _focusVersion++;
        _load?.Cancel();
        base.OnNavigatedFrom(args);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _active = true;
        try
        {
            _art = new MaskedArt(ArtBox, "ms-appx:///Assets/ArtMaskDetail.png");
            AttachTitleShadow(TitleText, TitleShadow);
            AttachTitleShadow(SubtitleText, SubtitleShadow);
        }
        catch (Exception ex) { Log.Error("Request detail artwork", ex); }
        RestoreFocus();
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
        _art?.Dispose();
        _art = null;
    }

    private async Task LoadAsync()
    {
        if (_entry is not { } entry || Session.Client is not { } client) return;
        _client = client;
        _profile = client.ProfileId;
        _load?.Cancel();
        _load?.Dispose();
        var cancellation = new CancellationTokenSource();
        _load = cancellation;
        var version = ++_version;
        if (_state is null)
        {
            LoadingOwner.Visibility = Visibility.Visible;
            ErrorPanel.Visibility = PageScroller.Visibility = Visibility.Collapsed;
            FocusTarget();
        }
        try
        {
#if DEBUG_SERVER
            DebugServer.ThrowIfRequestDetailFaulted();
#endif
            // No action is taken by a read, including on recommendation Back and Retry.
            var state = await client.Requests.LoadDetailAsync(entry.MediaType, entry.TmdbId, forceMine: true, cancellation.Token);
            if (!IsCurrent()) return;
            _state = state;
            Render();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrent()) return;
            Log.Error("Request detail load", ex);
            if (_state is null)
            {
                LoadingOwner.Visibility = PageScroller.Visibility = Visibility.Collapsed;
                ErrorText.Text = RequestsFeed.ErrorMessage(ex);
                ErrorPanel.Visibility = Visibility.Visible;
            }
            else ShowNotice(RequestsFeed.ErrorMessage(ex));
        }
        finally { if (IsCurrent() && _needFocus) FocusTarget(); }

        bool IsCurrent() => _active && version == _version && ReferenceEquals(Session.Client, client) && client.ProfileId == _profile;
    }

    private void Render()
    {
        if (_state is not { } state) return;
        var detail = state.Detail;
        var (title, subtitle) = DetailTitle.Split(detail.Title ?? string.Empty);
        TitleText.Text = title;
        SubtitleText.Text = subtitle ?? string.Empty;
        SubtitleSlot.Visibility = subtitle is null ? Visibility.Collapsed : Visibility.Visible;
        FactsText.Text = state.Facts;
        GenresText.Text = string.Join(" · ", (detail.Genres ?? []).Take(3));
        RatingText.Text = detail.ContentRating ?? string.Empty;
        RatingChip.Visibility = string.IsNullOrWhiteSpace(detail.ContentRating) ? Visibility.Collapsed : Visibility.Visible;
        Synopsis.Text = detail.Overview ?? string.Empty;
        SynopsisButton.IsTabStop = !string.IsNullOrWhiteSpace(detail.Overview);
        CreditText.Text = state.Credit ?? string.Empty;
        FullSynopsisTitle.Text = detail.Title ?? string.Empty;
        FullSynopsis.Text = detail.Overview ?? string.Empty;
        FullSynopsisTagline.Text = detail.Tagline ?? string.Empty;
        FullSynopsisTagline.Visibility = string.IsNullOrWhiteSpace(detail.Tagline) ? Visibility.Collapsed : Visibility.Visible;
        RenderActions();
        var progress = state.ShowsStatus ? state.Progress : null;
        StatusStrip.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        StatusValue.Show(progress, progress?.LongLabel, summary: true);
        StageTrack.Show(progress, track: true, steps: true, label: false);
        RequestedText.Text = state.RequestedDay ?? string.Empty;
        RequestedField.Visibility = state.RequestedDay is null ? Visibility.Collapsed : Visibility.Visible;
        QualityText.Text = state.Quality ?? string.Empty;
        QualityField.Visibility = state.Quality is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateRecommendations(state.Recommendations);
        _backdrop = RequestPresentation.Image(detail.BackdropPath, backdrop: true);
        if (_backdrop is { } backdrop)
        {
            _art?.Show(backdrop);
            _ = TintAsync(backdrop);
        }
        else
        {
            _art?.Hide();
            // Native AOT: match SkylineRequestDetailSurface without reading XAML resources in code.
            Wash.Fill = new SolidColorBrush(Color.FromArgb(255, 4, 13, 15));
        }
        LoadingOwner.Visibility = ErrorPanel.Visibility = Visibility.Collapsed;
        PageScroller.Visibility = Visibility.Visible;
    }

    private void RenderActions()
    {
        if (_state is not { } state) return;
        state = state with { IsSubmitting = _submitting, IsCancelling = _cancelling };
        var reclaim = CancelButton.FocusState != FocusState.Unfocused && !state.CanCancel
            || FollowButton.FocusState != FocusState.Unfocused && !state.CanFollow;
        PrimaryLabel.Text = state.PrimaryTitle;
        AddIcon.Visibility = state.PrimaryAction == RequestPrimaryAction.Request ? Visibility.Visible : Visibility.Collapsed;
        OpenIcon.Visibility = state.PrimaryAction == RequestPrimaryAction.OpenInLibrary ? Visibility.Visible : Visibility.Collapsed;
        SubmitSpinner.IsActive = _submitting;
        SubmitSpinner.Visibility = _submitting ? Visibility.Visible : Visibility.Collapsed;
        PrimaryDot.Visibility = state.PrimaryAction == RequestPrimaryAction.Status ? Visibility.Visible : Visibility.Collapsed;
        PrimaryDot.ApplyTemplate();
        VisualStateManager.GoToState(PrimaryDot, state.Progress?.Tint.ToString() ?? "Neutral", false);
        CancelButton.Visibility = state.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        FollowButton.Visibility = state.CanFollow ? Visibility.Visible : Visibility.Collapsed;
        FollowLabel.Text = state.FollowTitle;
        NotifyGlyph.Visibility = state.IsFollowing ? Visibility.Collapsed : Visibility.Visible;
        NotifyOffGlyph.Visibility = state.IsFollowing ? Visibility.Visible : Visibility.Collapsed;
        FollowSpinner.IsActive = _following;
        FollowSpinner.Visibility = _following ? Visibility.Visible : Visibility.Collapsed;
        FollowIcon.Visibility = _following ? Visibility.Collapsed : Visibility.Visible;
        if (reclaim)
        {
            // RequestActionFocusFallback: never let removing Cancel put the next Select on synopsis.
            _actionRail.Remembered = PrimaryButton;
            _focusArea = "primary";
            FocusRetry.Until(this, () => !_active || PrimaryButton.Focus(FocusState.Programmatic));
        }
    }

    private void UpdateRecommendations(IReadOnlyList<RequestTitle> items)
    {
        var index = 0;
        foreach (var item in items)
        {
            var existing = _recommendations.Skip(index).FirstOrDefault(card => card.Item.Key == item.Key);
            if (existing is null) _recommendations.Insert(index, new RequestCardView(item));
            else
            {
                var previous = _recommendations.IndexOf(existing);
                if (previous != index) _recommendations.Move(previous, index);
                existing.Update(item);
            }
            index++;
        }
        while (_recommendations.Count > index) _recommendations.RemoveAt(_recommendations.Count - 1);
        _lastRecommendation = _recommendations.FirstOrDefault(card => card.Item.Key == (_lastRecommendation?.Item.Key ?? (_restoring ? _entry?.RecommendationKey : null)));
        RecommendationsSection.Visibility = _recommendations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnPrimaryClick(object sender, RoutedEventArgs args)
    {
        if (_submitting || _state is not { } state || _client is not { } client) return;
        if (state.LibraryItemToOpen is { } library)
        {
            Frame.Navigate(typeof(DetailPage), library);
            return;
        }
        if (state.PrimaryAction != RequestPrimaryAction.Request || !client.Requests.Capability.Enabled) return;
        _submitting = true;
        ShowNotice(null);
        RenderActions();
        var profile = _profile;
        var result = await client.Requests.CreateAsync(state.Detail);
        _submitting = false;
        if (!IsCurrent(client, profile)) return;
        _state = client.Requests.StateFor(state.Detail);
        Render();
        ShowNotice(result.Message);
        if (result.Succeeded || result.Unconfirmed) await LoadAsync();
    }

    private async void OnCancelClick(object sender, RoutedEventArgs args)
    {
        if (_cancelling || _state is not { CanCancel: true, Record: { } record } state || _client is not { } client) return;
        // Like the official detail pill, this explicit press IS confirmation; there is no second dialog.
        _cancelling = true;
        ShowNotice(null);
        RenderActions();
        var profile = _profile;
        var result = await client.Requests.CancelAsync(record);
        _cancelling = false;
        if (!IsCurrent(client, profile)) return;
        _state = client.Requests.StateFor(state.Detail);
        Render();
        ShowNotice(result.Message);
        if (result.Succeeded || result.Unconfirmed) await LoadAsync();
    }

    private async void OnFollowClick(object sender, RoutedEventArgs args)
    {
        if (_following || _state is not { CanFollow: true } state || _client is not { } client) return;
        _following = true;
        ShowNotice(null);
        RenderActions();
        var profile = _profile;
        var result = await client.Requests.ToggleFollowAsync(state);
        _following = false;
        if (!IsCurrent(client, profile)) return;
        _state = client.Requests.StateFor(state.Detail);
        RenderActions();
        ShowNotice(result.Message);
    }

    private bool IsCurrent(SiloClient client, string? profile) => _active && ReferenceEquals(Session.Client, client) && client.ProfileId == profile;
    private void ShowNotice(string? message)
    {
        ActionNotice.Text = message ?? string.Empty;
        ActionNotice.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRecommendationClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not RequestCardView card) return;
        _lastRecommendation = card;
        _focusArea = "recommendations";
        if (card.Item.LibraryItemToOpen is { } library) Frame.Navigate(typeof(DetailPage), library);
        else Frame.Navigate(typeof(RequestDetailPage), new RequestDetailEntry(card.Item.MediaType, card.Item.TmdbId));
    }

    private void OnRecommendationFocus(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is not ContentControl { Content: RequestCardView card } container) return;
        CardFocus.Apply(container, true);
        if (!_restoringFocus) { _lastRecommendation = card; _focusArea = "recommendations"; PinRecommendations(); }
    }

    private void OnRecommendationBlur(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is ContentControl { Content: RequestCardView } container) CardFocus.Apply(container, false);
    }

    private void OnContentFocus(object sender, RoutedEventArgs args)
    {
        if (_restoringFocus) return;
        var element = args.OriginalSource as DependencyObject;
        if (Within(element, Recommendations)) _focusArea = "recommendations";
        else if (Within(element, SynopsisButton)) { _focusArea = "synopsis"; PageScroller.ChangeView(null, 0, null); }
        else if (Within(element, CancelButton)) _focusArea = "cancel";
        else if (Within(element, FollowButton)) _focusArea = "follow";
        else if (Within(element, PrimaryButton)) _focusArea = "primary";
    }

    private static bool Within(DependencyObject? element, DependencyObject root)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, root)) return true;
        return false;
    }

    private void PinRecommendations()
    {
        var top = RecommendationsSection.TransformToVisual(PageContent).TransformPoint(new Point(0, 0)).Y;
        // Normal detail section anchor (TvItemDetailScreen.kt); no default bring-into-view.
        PageScroller.ChangeView(null, Math.Max(0, top - Root.ActualHeight * .18), null);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        try
        {
            if (DialogLayer.Visibility == Visibility.Visible)
            {
                if (args.Key is VirtualKey.Escape or VirtualKey.Back or VirtualKey.GamepadB) { _backPressed = true; args.Handled = true; }
                else if (args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Tab
                    or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight
                    or VirtualKey.GamepadLeftThumbstickLeft or VirtualKey.GamepadLeftThumbstickRight) args.Handled = true;
                return;
            }
            var step = args.Key switch
            {
                VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => -1,
                VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => 1,
                _ => 0,
            };
            var left = args.Key is VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft;
            var right = args.Key is VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight;
            if (_state is not null && (left || right || step != 0)) { _focusVersion++; _needFocus = false; }
            if (Within(args.OriginalSource as DependencyObject, Actions) && (left || right))
            {
                // Hidden secondary nodes are not endpoints. Rail supplies entry memory; explicitly
                // traverse only visible pills so Right cannot fall through to a synopsis or poster.
                args.Handled = true;
                var buttons = new[] { PrimaryButton, CancelButton, FollowButton }.Where(button => button.Visibility == Visibility.Visible).ToArray();
                var index = Array.FindIndex(buttons, button => button.FocusState != FocusState.Unfocused);
                buttons[Math.Clamp(index + (left ? -1 : 1), 0, buttons.Length - 1)].Focus(FocusState.Keyboard);
                return;
            }
            if (step == 0) return;
            args.Handled = true; // Every failed vertical move stays put, never sideways.
            if (_state is null) return;
            if (Within(args.OriginalSource as DependencyObject, Recommendations))
            {
                if (step < 0) { PrimaryButton.Focus(FocusState.Keyboard); PageScroller.ChangeView(null, 0, null); }
            }
            else if (Within(args.OriginalSource as DependencyObject, SynopsisButton))
            {
                if (step > 0) PrimaryButton.Focus(FocusState.Keyboard);
            }
            else if (step < 0)
            {
                if (SynopsisButton.IsTabStop) SynopsisButton.Focus(FocusState.Keyboard);
            }
            else if (_recommendations.Count > 0)
            {
                PinRecommendations();
                var version = _focusVersion;
                FocusRetry.Until(Recommendations, () => !_active || version != _focusVersion || _recommendationRail.Focus(FocusState.Keyboard));
            }
        }
        catch (Exception ex) { args.Handled = true; Log.Error("Request detail keys", ex); }
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (DialogLayer.Visibility != Visibility.Visible || args.Key is not (VirtualKey.Escape or VirtualKey.Back or VirtualKey.GamepadB)) return;
        args.Handled = true;
        if (_backPressed) CloseSynopsis();
    }

    private void OnSynopsisClick(object sender, RoutedEventArgs args)
    {
        if (_state is null) return;
        PageScroller.IsEnabled = false;
        DialogLayer.Visibility = Visibility.Visible;
        SynopsisScroller.ChangeView(null, 0, null, disableAnimation: true);
        FocusRetry.Until(this, () => !_active || DialogLayer.Visibility != Visibility.Visible || SynopsisScroller.Focus(FocusState.Programmatic));
    }

    private bool CloseSynopsis()
    {
        if (DialogLayer.Visibility != Visibility.Visible) return false;
        DialogLayer.Visibility = Visibility.Collapsed;
        PageScroller.IsEnabled = true;
        _backPressed = false;
        FocusRetry.Until(this, () => !_active || SynopsisButton.Focus(FocusState.Programmatic));
        return true;
    }

    private void OnDialogTapped(object sender, TappedRoutedEventArgs args)
    {
        if (ReferenceEquals(args.OriginalSource, DialogLayer)) { args.Handled = true; CloseSynopsis(); }
    }

    public bool OnBack() => CloseSynopsis();
    private async void OnRetryClick(object sender, RoutedEventArgs args) { _needFocus = true; await LoadAsync(); }
    public void RestoreFocus() { _needFocus = true; FocusTarget(); }

    private void FocusTarget()
    {
        var version = ++_focusVersion;
        FocusRetry.Until(this, () =>
        {
            if (!_active || version != _focusVersion || DialogLayer.Visibility == Visibility.Visible) return true;
            if (ErrorPanel.Visibility == Visibility.Visible) return RetryButton.Focus(FocusState.Programmatic);
            if (_state is null) return LoadingOwner.Focus(FocusState.Programmatic);
            _restoringFocus = true;
            try
            {
                if (_restoring && !_offsetsRestored && _entry is { } entry)
                {
                    PageScroller.ChangeView(null, entry.ScrollOffset, null, disableAnimation: true);
                    if (_focusArea != "recommendations") _offsetsRestored = true;
                    else if (Rail.FindChild<ScrollViewer>(Recommendations) is { } scroller)
                    {
                        scroller.ChangeView(entry.RailOffset, null, null, disableAnimation: true);
                        _offsetsRestored = true;
                    }
                }
                bool focused;
                if (_focusArea == "recommendations" && (_lastRecommendation ?? _recommendations.FirstOrDefault()) is { } card)
                {
                    focused = _recommendationRail.FocusItem(card, preserveHorizontalOffset: true);
                    if (!focused) Recommendations.ScrollIntoView(card);
                }
                else
                {
                    Control target = _focusArea switch
                    {
                        "synopsis" when SynopsisButton.IsTabStop => SynopsisButton,
                        "cancel" when CancelButton.Visibility == Visibility.Visible => CancelButton,
                        "follow" when FollowButton.Visibility == Visibility.Visible => FollowButton,
                        _ => PrimaryButton,
                    };
                    _actionRail.Remembered = target;
                    focused = target.Focus(FocusState.Programmatic);
                }
                if (focused) _needFocus = false;
                return focused;
            }
            finally { _restoringFocus = false; }
        });
    }

    private void OnSecondaryFocus(object sender, RoutedEventArgs args) => RecolorSecondary(sender, true);
    private void OnSecondaryBlur(object sender, RoutedEventArgs args) => RecolorSecondary(sender, false);
    private void RecolorSecondary(object sender, bool focused)
    {
        // Template foreground cannot reach inline Path fills under Native AOT (Services/ActionButton).
        var ink = new SolidColorBrush(focused ? Colors.Black : Colors.White);
        if (ReferenceEquals(sender, CancelButton)) CancelGlyph.Fill = ink;
        else { NotifyGlyph.Fill = NotifyOffGlyph.Fill = ink; FollowSpinner.Foreground = ink; }
    }

    private async Task TintAsync(Uri backdrop)
    {
        var tint = await AmbientTint.SampleAsync(backdrop);
        if (!_active || backdrop != _backdrop) return;
        // TvDetailPageSurfaceColor: sampled tint at .42 over black, not the hub's diagonal wash.
        byte Channel(byte value) => (byte)Math.Round(value * .42);
        Wash.Fill = new SolidColorBrush(tint is { } color
            ? Color.FromArgb(255, Channel(color.R), Channel(color.G), Channel(color.B))
            : Color.FromArgb(255, 4, 13, 15));
    }

    private static void AttachTitleShadow(TextBlock text, FrameworkElement host)
    {
        // TvDetailHero.kt, the same half-density text shadow as Bale's normal detail hero.
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.Color = Colors.Black;
        shadow.Opacity = .55f;
        shadow.BlurRadius = 8;
        shadow.Offset = new Vector3(0, 2, 0);
        shadow.Mask = text.GetAlphaMask();
        var visual = compositor.CreateSpriteVisual();
        visual.Shadow = shadow;
        void Resize() => visual.Size = new Vector2((float)text.ActualWidth, (float)text.ActualHeight);
        text.SizeChanged += (_, _) => Resize();
        Resize();
        ElementCompositionPreview.SetElementChildVisual(host, visual);
    }
}
