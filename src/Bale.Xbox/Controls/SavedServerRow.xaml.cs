using Bale.Core.Connection;
using Bale.Xbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox.Controls;

public sealed partial class SavedServerRow : UserControl
{
    private readonly Rail _actions;
    private bool _removeFocused;
    public SavedServer Entry { get; private set; } = null!;
    public bool RemoveFocused => _removeFocused;
    public event Action<SavedServer>? Selected;
    public event Action<SavedServer>? RemoveRequested;
    public event Action<SavedServerRow, int, bool>? VerticalMove;
    public event Action<SavedServerRow>? Focused;

    public SavedServerRow()
    {
        InitializeComponent();
        _actions = Rail.Attach(Actions);
        _actions.Pin = false;
        _actions.EntryItem = () => _removeFocused ? RemoveButton : SelectButton;
    }

    public void Bind(SavedServer entry, bool active)
    {
        Entry = entry;
        NameText.Text = entry.DisplayName;
        NameText.FontWeight = active ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal;
        AddressText.Text = entry.Url;
        ActiveCheck.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        SelectButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(active ? (byte)26 : (byte)14, 255, 255, 255));
        AutomationProperties.SetName(SelectButton, entry.DisplayName + (active ? ", Active" : string.Empty));
        AutomationProperties.SetName(RemoveButton, "Remove " + entry.DisplayName);
    }

    public bool FocusColumn(bool remove)
    {
        _removeFocused = remove;
        _actions.Remembered = remove ? RemoveButton : SelectButton;
        return (remove ? RemoveButton : SelectButton).Focus(FocusState.Programmatic);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args) =>
        SelectButton.Width = Math.Max(0, args.NewSize.Width - RemoveButton.Width - Gap.Width);

    private void OnButtonGotFocus(object sender, RoutedEventArgs args)
    {
        _removeFocused = ReferenceEquals(sender, RemoveButton);
        Focused?.Invoke(this);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var step = SettingsInput.VerticalStep(args.Key);
        if (step == 0) return;
        args.Handled = true;
        VerticalMove?.Invoke(this, step, _removeFocused);
    }

    private void OnSelectClick(object sender, RoutedEventArgs args) => Selected?.Invoke(Entry);
    private void OnRemoveClick(object sender, RoutedEventArgs args) => RemoveRequested?.Invoke(Entry);
}
