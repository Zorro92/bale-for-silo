using Bale.Core.Connection;
using Bale.Xbox.Controls;
using Bale.Xbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>TvServerListScreen.kt / TvServerResolver.kt. Merely opening this page never signs out.</summary>
public sealed partial class ServerListPage : Page, IBackHandler, IRestoresFocus
{
    private ConnectionPreview? _preview;
    private SavedServerRegistry Servers => _preview?.Servers ?? AppSettings.Servers;
    private readonly List<SavedServerRow> _rows = [];
    private SavedServer? _removing;
    private SavedServerRow? _lastRow;
    private bool _lastRemove;
    private bool _active;
    internal bool IsAuthPreview => _preview is not null;

    public ServerListPage()
    {
        InitializeComponent();
        RemovePrompt.Confirmed += OnRemoveConfirmed;
        RemovePrompt.Dismissed += () => { _removing = null; RestoreFocus(); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        _active = true;
        _preview = args.Parameter as ConnectionPreview;
        var returning = args.NavigationMode == NavigationMode.Back;
        Populate(returning ? _lastRow?.Entry.Id : null, returning && _lastRemove, returning && _lastRow is null);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        _active = false;
        RemovePrompt.Hide();
        base.OnNavigatedFrom(args);
    }

    private void Populate(string? rememberedId = null, bool remove = false, bool focusAdd = false)
    {
        Rows.Children.Clear();
        _rows.Clear();
        foreach (var server in Servers.Sorted)
        {
            var row = new SavedServerRow();
            row.Bind(server, server.Id == Servers.Active?.Id);
            row.Selected += OnSelected;
            row.RemoveRequested += OnRemoveRequested;
            row.VerticalMove += OnVerticalMove;
            row.Focused += r =>
            {
                _lastRow = r;
                _lastRemove = r.RemoveFocused;
                r.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            };
            _rows.Add(row);
            Rows.Children.Add(row);
        }
        _lastRow = focusAdd ? null : _rows.FirstOrDefault(row => row.Entry.Id == rememberedId) ?? _rows.FirstOrDefault();
        _lastRemove = remove;
        RestoreFocus();
    }

    public void RestoreFocus() => FocusRetry.Until(Rows, () => !_active || RemovePrompt.IsOpen
        || (_lastRow is { } row ? row.FocusColumn(_lastRemove) : AddButton.Focus(FocusState.Programmatic)));

    private void OnVerticalMove(SavedServerRow row, int step, bool remove)
    {
        _lastRemove = remove;
        var index = _rows.IndexOf(row);
        if (index == 0 && step < 0)
        {
            // The Remove column has no row above it: Up must not slide sideways into Add.
            if (!remove) AddButton.Focus(FocusState.Keyboard);
            return;
        }
        var target = index + step;
        if (target >= 0 && target < _rows.Count) _rows[target].FocusColumn(remove);
    }

    private void OnAddPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (SettingsInput.VerticalStep(args.Key) is var step && step != 0)
        {
            args.Handled = true;
            if (step > 0) _rows.FirstOrDefault()?.FocusColumn(false);
        }
        else if (SettingsInput.HorizontalStep(args.Key) != 0) args.Handled = true;
    }

    private void OnAddGotFocus(object sender, RoutedEventArgs args) => _lastRow = null;

    private void OnAddClick(object sender, RoutedEventArgs args)
    {
        // Keep the signed-in page/session until a successful probe and (for HTTP) explicit consent.
        Frame.Navigate(typeof(MainPage), _preview is null ? MainPage.ServerEntry : new ConnectionNavigation(_preview));
    }

    private void OnSelected(SavedServer entry)
    {
        if (Servers.Active?.Id == entry.Id) return; // Official active row is a no-op.
        try
        {
            if (_preview is not null)
            {
                Servers.Activate(entry.Id);
                Populate();
                return;
            }
            Session.SwitchServer(entry.Id);
            ResolveActive();
        }
        catch (Exception ex)
        {
            Log.Info($"Saved server switch failed type={ex.GetType().Name}");
            ShowError("Silo couldn't switch servers. Please try again.");
        }
    }

    private void OnRemoveRequested(SavedServer entry)
    {
        _lastRemove = true;
        _removing = entry;
        RemovePrompt.ShowRemove(entry.DisplayName, Servers.Active?.Id == entry.Id);
    }

    private void OnRemoveConfirmed()
    {
        if (!_active || _removing is not { } entry) return;
        _removing = null;
        try
        {
            if (_preview is not null) Servers.Remove(entry.Id);
            else if (Session.RemoveServer(entry.Id))
            {
                ResolveActive();
                return;
            }
            Populate(); // Nonactive removal must leave the live session/navigation alone.
        }
        catch (Exception ex)
        {
            Log.Info($"Saved server removal failed type={ex.GetType().Name}");
            ShowError("Silo couldn't remove the saved server. Please try again.");
            RestoreFocus();
        }
    }

    private void ResolveActive()
    {
        Frame.Navigate(typeof(MainPage));
        Frame.BackStack.Clear(); // No journal entry may reopen the old server's UI/profile.
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    public bool OnBack()
    {
        if (RemovePrompt.Dismiss()) return true;
#if DEBUG_SERVER
        if (IsAuthPreview && DebugServer.CloseAuthPreview(Frame)) return true;
#endif
        if (Frame.CanGoBack) Frame.GoBack();
        else ResolveActive();
        return true;
    }
}
