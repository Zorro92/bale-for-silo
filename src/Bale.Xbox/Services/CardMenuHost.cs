using Bale.Core;
using Bale.Core.Browse;
using Bale.Core.Detail;
using Bale.Xbox.Controls;
using Bale.Xbox.Views;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Services;

/// <summary>Shared Menu/Application/hold-Select input, modal focus, optimistic writes and rollback for TV cards.</summary>
internal sealed class CardMenuHost
{
    // Android's TvMediaCard/TvDetailEpisodeCard use the platform long-press (DPAD-center hold).
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Page _page;
    private readonly Control _surface;
    private readonly Action<object> _activate;
    private readonly Func<CardView, MediaRow?>? _homeRow;
    private readonly CardActionMenu _menu = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private static CardMenuHost? _active;
    private ContentControl? _pressed;
    private object? _pressedItem;
    private VirtualKey? _heldKey;
    private bool _longPress;
    private bool _loaded;
    private bool _surfaceEnabled;
    private bool _surfaceDisabled;
    private int _generation;
    private CancellationTokenSource? _opening;
    private ContentControl? _origin;
    private Target? _target;
    private SiloClient? _client;
    private string? _profile;

    private sealed record Target(string ContentId, object Item, ItemFlags Flags, IReadOnlyList<CardMenuEntry> Entries,
        Action<ItemFlags, CardAction> Apply, Action Restore, MediaCard? HomeCard = null);

    public CardMenuHost(Page page, Control surface, Action<object> activate, Func<CardView, MediaRow?>? homeRow = null)
    {
        _page = page;
        _surface = surface;
        _activate = activate;
        _homeRow = homeRow;
        // All card pages have a full-page Grid. Appending the shared control leaves rail XAML/geometry alone.
        ((Grid)page.Content).Children.Add(_menu);
        page.PreviewKeyDown += OnKeyDown;
        page.PreviewKeyUp += OnKeyUp;
        page.Loaded += (_, _) => _loaded = true;
        page.Unloaded += (_, _) =>
        {
            _loaded = false;
            CancelPress();
            Close(restore: false);
            _menu.ClearStatus();
        };
        page.LostFocus += (_, _) =>
        {
            if (!_longPress && _pressed is not null && !ReferenceEquals(FocusedCard(), _pressed))
            {
                _hold.Stop();
                _pressed = null;
                _pressedItem = null;
            }
        };
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            if (_pressed is { } origin && ReferenceEquals(origin.Content, _pressedItem))
            {
                _longPress = true;
                _ = OpenAsync(origin);
            }
        };
        _menu.Dismissed += () => Close();
        _menu.Chosen += OnChosen;
    }

    public event Action<object>? Changed;
    public event Action<MediaCard>? DismissedFromHome;
    public bool IsOpen => _menu.IsOpen || _opening is not null;

    /// <summary>System Back must dismiss a card popup before the page/shell handles its own Back chain.</summary>
    public static bool CloseActive() => _active?.Close() == true;

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        try
        {
            if (args.Handled) return;
            // TvMediaCardActions.kt swallows the rest of the opening hold, including repeat downs and key-up.
            if (_heldKey == args.Key)
            {
                args.Handled = true;
                return;
            }

            if (IsOpen)
            {
                if (args.Key is VirtualKey.Escape or VirtualKey.Back or VirtualKey.GamepadB or VirtualKey.GamepadMenu or VirtualKey.Application)
                {
                    args.Handled = true;
                    if (!args.KeyStatus.WasKeyDown) Close();
                }
                else if (_menu.IsOpen)
                {
                    _menu.HandleNavigation(args);
                }
                else if (IsDirection(args.Key))
                {
                    // Don't open an asynchronously loaded membership menu after its owner moved on.
                    Close();
                }

                return;
            }

            if (FocusedCard() is not { } origin) return;
            if (args.Key is VirtualKey.GamepadMenu or VirtualKey.Application)
            {
                args.Handled = true;
                if (!args.KeyStatus.WasKeyDown) _ = OpenAsync(origin);
            }
            else if (args.Key is VirtualKey.GamepadA or VirtualKey.Enter or VirtualKey.Space)
            {
                // Delay the ordinary click until release, so a hold cannot also open/play the card.
                args.Handled = true;
                if (args.KeyStatus.WasKeyDown || _heldKey is not null) return;
                _pressed = origin;
                _pressedItem = origin.Content;
                _heldKey = args.Key;
                _longPress = false;
                _hold.Start();
            }
        }
        catch (Exception ex)
        {
            args.Handled = true;
            Log.Error("Card menu input", ex);
        }
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs args)
    {
        try
        {
            if (_heldKey != args.Key) return;
            args.Handled = true;
            var item = !_longPress && _pressed is { } origin && ReferenceEquals(origin.Content, _pressedItem)
                && ReferenceEquals(origin, FocusedCard()) ? _pressedItem : null;
            CancelPress();
            if (item is not null) _activate(item);
        }
        catch (Exception ex)
        {
            Log.Error("Card activation", ex);
        }
    }

    private async Task OpenAsync(ContentControl origin)
    {
        if (!_loaded || IsOpen || Session.Client is not { } client || origin.Content is not { } item) return;
        var id = item switch
        {
            CardView card => card.Card.ContentId,
            EpisodeView episode => episode.Episode.ContentId,
            CalendarCardView calendar => calendar.Item.ContentId,
            _ => string.Empty,
        };
        if (id.Length == 0 || _pending.Contains(id)) return;
        _active?.Close();
        _active = this;
        var generation = ++_generation;
        var profile = client.ProfileId;
        using var opening = new CancellationTokenSource();
        _opening = opening;
        try
        {
            var target = await CreateTargetAsync(client, item, opening.Token);
            if (generation != _generation || !_loaded || !ReferenceEquals(client, Session.Client) || client.ProfileId != profile
                || !ReferenceEquals(origin.Content, item)) return;
            _target = target;
            _origin = origin;
            _client = client;
            _profile = profile;
            _surfaceEnabled = _surface.IsEnabled;
            _surfaceDisabled = true;
            _surface.IsEnabled = false;
            _menu.Show(target.Entries, origin);
        }
        catch (OperationCanceledException) when (opening.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Card menu load", ex);
            if (generation == _generation && _loaded)
            {
                Close();
                _menu.ShowStatus("Couldn't load card actions. Try again.");
            }
        }
        finally
        {
            if (ReferenceEquals(_opening, opening))
            {
                _opening = null;
                if (!_menu.IsOpen && ReferenceEquals(_active, this)) _active = null;
            }
        }
    }

    private async Task<Target> CreateTargetAsync(SiloClient client, object item, CancellationToken cancellationToken)
    {
        switch (item)
        {
            case CardView view:
            {
                var before = view.Card;
                var row = _homeRow?.Invoke(view);
                return new(before.ContentId, item, before.Flags, CardMenu.ForCard(before, row is not null, row?.SectionType ?? string.Empty),
                    (flags, action) =>
                    {
                        view.UpdateCard((action == CardAction.Watched ? before.WithPlayed(flags.Played) : before) with { Flags = flags });
                        Changed?.Invoke(view);
                    },
                    () => { view.UpdateCard(before); Changed?.Invoke(view); }, row is not null ? before : null);
            }
            case EpisodeView view:
            {
                var before = view.Episode;
                var favorite = await client.Detail.IsFavoriteAsync(before.ContentId, cancellationToken);
                return new(before.ContentId, item, new(false, favorite, before.Played), CardMenu.ForEpisode(before.Played, favorite),
                    (flags, action) =>
                    {
                        if (action == CardAction.Watched) view.Update(before.WithPlayed(flags.Played));
                        Changed?.Invoke(view);
                    },
                    () => { view.Update(before); Changed?.Invoke(view); });
            }
            case CalendarCardView view:
            {
                var before = view.Item;
                // Calendar events carry watched only; the catalog supplies current memberships for the standard card menu.
                var detail = await client.Detail.GetAsync(before.ContentId).WaitAsync(cancellationToken);
                var flags = detail.Flags with { Played = before.Watched };
                return new(before.ContentId, item, flags, CardMenu.ForFlags(flags),
                    (after, _) => { view.Update(before with { Watched = after.Played }); Changed?.Invoke(view); },
                    () => { view.Update(before); Changed?.Invoke(view); });
            }
            default:
                throw new ArgumentException("Not a media card.", nameof(item));
        }
    }

    private async void OnChosen(CardAction action)
    {
        if (_target is not { } target || _client is not { } client || !ReferenceEquals(client, Session.Client) || client.ProfileId != _profile) return;
        Close();
        if (action == CardAction.Play)
        {
            await Playback.ShowComingAsync();
            return;
        }

        if (!_pending.Add(target.ContentId)) return;
        var profile = client.ProfileId;
        try
        {
            if (action == CardAction.RemoveFromHome && target.HomeCard is { } card)
            {
                // Match HomeViewModel: keep the card on a failed dismissal, remove only after server acceptance.
                await client.Browse.DismissFromHomeAsync(card);
                if (_loaded && ReferenceEquals(client, Session.Client) && client.ProfileId == profile) DismissedFromHome?.Invoke(card);
            }
            else
            {
                target.Apply(CardMenu.Toggle(target.Flags, action), action);
                await CardMenu.ToggleAsync(client.Detail, target.ContentId, target.Flags, action);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Card action", ex);
            if (_loaded && ReferenceEquals(client, Session.Client) && client.ProfileId == profile)
            {
                if (action != CardAction.RemoveFromHome) target.Restore();
                _menu.ShowStatus(action == CardAction.RemoveFromHome ? "Couldn't remove that from Home. Try again." : "Couldn't update that. Try again.");
            }
        }
        finally
        {
            _pending.Remove(target.ContentId);
        }
    }

    private bool Close(bool restore = true)
    {
        if (!IsOpen) return false;
        _generation++;
        _opening?.Cancel();
        _opening = null;
        var origin = _origin;
        var item = _target?.Item;
        if (_surfaceDisabled)
        {
            _surfaceDisabled = false;
            _surface.IsEnabled = _surfaceEnabled;
        }

        _menu.Hide();
        _origin = null;
        _target = null;
        _client = null;
        if (ReferenceEquals(_active, this)) _active = null;
        if (restore && _loaded && origin is not null)
        {
            // No geometric focus search: the exact originating container restores its rail's position.
            FocusRetry.Until(_surface, () => !_loaded || !ReferenceEquals(origin.Content, item) || origin.Focus(FocusState.Programmatic));
        }

        return true;
    }

    private void CancelPress()
    {
        _hold.Stop();
        _heldKey = null;
        _pressed = null;
        _pressedItem = null;
        _longPress = false;
    }

    private ContentControl? FocusedCard() => FindCard(FocusManager.GetFocusedElement() as DependencyObject);

    private ContentControl? FindCard(DependencyObject? element)
    {
        for (var node = element; node is not null && !ReferenceEquals(node, _page); node = VisualTreeHelper.GetParent(node))
        {
            if (node is ContentControl { Content: CardView or EpisodeView or CalendarCardView } card) return card;
        }

        return null;
    }

    private static bool IsDirection(VirtualKey key) => key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right
        or VirtualKey.GamepadDPadUp or VirtualKey.GamepadDPadDown or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight
        or VirtualKey.GamepadLeftThumbstickUp or VirtualKey.GamepadLeftThumbstickDown or VirtualKey.GamepadLeftThumbstickLeft or VirtualKey.GamepadLeftThumbstickRight;
}
