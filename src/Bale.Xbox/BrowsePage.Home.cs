using Bale.Core;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.ApplicationModel;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

public sealed partial class BrowsePage
{
    private readonly HomeFeedState _homeState = new();
    private SiloClient? _homeClient;
    private string? _homeProfile;
    private CancellationTokenSource? _homeLoad;
    private HomeLoadRequest? _homeRequest;
    private Window? _homeWindow;
    private bool _homeActive;
    private bool _homeSuspended;
    private bool _homeReturnPending;
    private int _homeReturnVersion;
    private bool _homeEntryFocusPending;
    private int _homeEntryFocusVersion;

    private bool IsHome => _libraryId is null && !_forYou;
    private bool CanFocusHome => _homeActive && _isLoaded && _homeState.IsShown;

    private async Task LoadHomeAsync(HomeLoadRequest request)
    {
        if (Session.Client is not { } client) return;
        var profileId = client.ProfileId;
        _homeClient = client;
        _homeProfile = profileId;
        _homeState.SetLayout(HomeSectionSettings.Load(client));
        using var load = new CancellationTokenSource();
        _homeLoad = load;
        _homeRequest = request;
        if (!request.Quiet)
        {
            _homeEntryFocusPending = true;
            _homeEntryFocusVersion = _focusVersion;
        }
        VisualStateManager.GoToState(this, _homeState.Status.ToString(), false);
        Log.Info(request.Quiet ? "Home: quiet refresh" : "Home: load");

        bool OwnsHome() => IsHome && ReferenceEquals(client, Session.Client) && client.ProfileId == profileId;
        bool IsCurrent() => OwnsHome() && !load.IsCancellationRequested && _homeState.IsCurrent(request);
        try
        {
#if DEBUG_SERVER
            var empty = DebugServer.TakeHomeEmptyOrThrow();
#else
            const bool empty = false;
#endif
            var rows = empty ? Array.Empty<MediaRow>() : await client.Browse.LoadHomeAsync(ArtworkWidths.Tv1080, load.Token);
            if (!IsCurrent()) return;

            var previous = RememberedTarget();
            var headerFocused = SeeAllButtonOf(FocusManager.GetFocusedElement() as DependencyObject) is not null;
            // An initial read may finish beneath Settings. Project the latest device choice, not
            // the preference captured before the editor's immediate saves.
            _homeState.SetLayout(HomeSectionSettings.Load(client));
            if (!_homeState.Apply(request, rows)) return;
            RememberHomeRows();
            var restore = UpdateRows(_homeState.Rows, preserveFocus: request.Quiet && !_cardMenu.IsOpen);
            Log.Info($"Home: loaded {_rows.Count} rows");
            if (_rows.Count == 0)
            {
                ShowEmpty(CanFocusHome && restore);
            }
            else
            {
                VisualStateManager.GoToState(this, "Content", false);
                ShowMarquee((_lastCard?.Card as CardView) ?? _shownMarquee ?? _rows[0].Cards.OfType<CardView>().First(), refresh: true);
                if (CanFocusHome && restore && !headerFocused)
                {
                    // TvSkylineSectionFeed.kt: unchanged rows never replay entry scrolling. Only a
                    // moved/removed target needs re-pinning; surviving containers keep their offsets.
                    RestoreCardFocus(preserveScroll: previous == RememberedTarget());
                }
            }
            FocusPendingHomeEntry();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            // HomeViewModel.onHidden defers this quiet refresh until the next visible Home.
        }
        catch (Exception error)
        {
            if (!IsCurrent() || !_homeState.Fail(request, error)) return;
            Log.Error("Home load", error);
            if (_homeState.Status == HomeFeedStatus.Error)
            {
                ShowError(_homeState.Error!, CanFocusHome && !_cardMenu.IsOpen
                    && IsInPage(FocusManager.GetFocusedElement() as DependencyObject));
            }
            FocusPendingHomeEntry();
        }
        finally
        {
            if (ReferenceEquals(_homeLoad, load))
            {
                _homeLoad = null;
                _homeRequest = null;
            }
        }

        if (OwnsHome()) StartHomeLoad(_homeState.TakePendingRefresh());
    }

    private void StartHomeLoad(HomeLoadRequest? request)
    {
        if (request is not null) _ = LoadHomeAsync(request);
    }

    private void AttachHomeLifecycle()
    {
        if (!IsHome) return;
        _homeActive = true;
        _homeSuspended = false;
        _homeWindow = Window.Current;
        _homeWindow.VisibilityChanged += OnHomeVisibilityChanged;
        Application.Current.Suspending += OnHomeSuspending;
        Application.Current.Resuming += OnHomeResuming;
        ShowHome();
    }

    private void DetachHomeLifecycle()
    {
        if (!IsHome) return;
        _homeActive = false;
        HideHome(restoringFocus: true);
        if (_homeWindow is { } window) window.VisibilityChanged -= OnHomeVisibilityChanged;
        _homeWindow = null;
        Application.Current.Suspending -= OnHomeSuspending;
        Application.Current.Resuming -= OnHomeResuming;
    }

    private void ShowHome()
    {
        if (!_homeActive || _homeSuspended || _homeWindow?.Visible != true) return;
        ApplyHomeSectionLayout();
        if (!_homeState.IsShown && _homeEntryFocusPending) _homeEntryFocusVersion = _focusVersion;
        var request = _homeState.OnShown(deferUntilFocusRestored: _homeReturnPending);
        FocusPendingHomeEntry();
        StartHomeLoad(request);
    }

    private void HideHome(bool restoringFocus = false)
    {
        _homeReturnPending |= restoringFocus;
        _homeReturnVersion++;
        _homeState.OnHidden();
        if (_homeRequest is { Quiet: true }) _homeLoad?.Cancel();
    }

    internal void PauseHomeForNavigation()
    {
        if (!IsHome) return;
        // TvHomeScreen.kt.onPauseOrDispose: the outgoing page may still be loaded for its fade.
        _homeActive = false;
        _returning = _lastCard is not null;
        HideHome(restoringFocus: true);
    }

    private void OnHomeVisibilityChanged(object sender, VisibilityChangedEventArgs args)
    {
        if (args.Visible) ShowHome();
        else HideHome();
    }

    private void OnHomeSuspending(object sender, SuspendingEventArgs args)
    {
        _homeSuspended = true;
        HideHome();
    }

    private void OnHomeResuming(object? sender, object args)
    {
        // UWP can raise Resuming off-thread; the feed and focus state belong to the UI dispatcher.
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
        {
            _homeSuspended = false;
            ShowHome();
        });
    }

    private void FocusPendingHomeEntry()
    {
        if (!_homeEntryFocusPending || !CanFocusHome || _homeReturnPending || _homeState.IsLoading) return;
        _homeEntryFocusPending = false;
        if (_homeEntryFocusVersion == _focusVersion) FocusContent();
    }

    private void RememberHomeRows()
    {
        if (_homeClient is { } client) HomeSectionSettings.Remember(client, _homeProfile, _homeState.AvailableRows);
    }

    private void ApplyHomeSectionLayout()
    {
        if (_homeClient is not { } client || !ReferenceEquals(client, Session.Client) || client.ProfileId != _homeProfile) return;
        var layout = HomeSectionSettings.Load(client);
        if (layout.Serialize() == _homeState.Layout.Serialize()) return;
        // TvHomeScreen.kt observes the local projection immediately, even with no connection.
        _homeState.SetLayout(layout);
        UpdateRows(_homeState.Rows, preserveFocus: false);
        if (_homeState.IsLoading) return;
        if (_rows.Count == 0) ShowEmpty(focus: false);
        else
        {
            VisualStateManager.GoToState(this, "Content", false);
            ShowMarquee((_lastCard?.Card as CardView) ?? _rows[0].Cards.OfType<CardView>().First(), refresh: true);
        }
    }

    private async Task RestoreHomeFocusAsync()
    {
        var version = ++_homeReturnVersion;
        try
        {
            // TvMainShell.kt restores the launch card before allowing resume refreshes. PageFrame
            // waits on these same FocusRetry moves while the incoming shell is still transparent.
            ApplyHomeSectionLayout();
            FocusContent();
            await FocusRetry.WhenSettledAsync(this, CancellationToken.None);
            if (version != _homeReturnVersion || !_homeActive || !_isLoaded) return;
            _homeReturnPending = false;
            FocusPendingHomeEntry();
            StartHomeLoad(_homeState.OnFocusRestored());
        }
        catch (Exception error)
        {
            Log.Error("Home return focus", error);
        }
    }
}
