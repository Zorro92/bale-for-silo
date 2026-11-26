using Bale.Core.Settings;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Controls;

/// <summary>
/// TvSettingsPickerSheet for pickers whose rows come from Bale.Core (Settings › Subtitles): the checked row takes focus
/// (else the first), a choice raises <see cref="Chosen"/> with the row's id. The page keeps the keys and the focus
/// boundary, as for its fixed pickers: Up/Down call <see cref="Move"/>, focus that strays returns to
/// <see cref="FocusTarget"/>, Back closes. All chrome is Skyline.xaml's.
/// </summary>
public sealed partial class SettingsPickerSheet : UserControl
{
    private List<SettingsPickerOptionView> _options = [];
    private int _focusIndex;

    public SettingsPickerSheet() => InitializeComponent();

    public bool IsOpen => ModalLayer.Visibility == Visibility.Visible;

    /// <summary>A row was pressed; its id. The sheet stays open: the page closes it.</summary>
    public event Action<string>? Chosen;

    public void Show(SettingsPickerModel model)
    {
        _options = [.. model.Options.Select((option, index) => new SettingsPickerOptionView(option.Id, option.Label, option.Id == model.SelectedId, index))];
        _focusIndex = Math.Min(model.FocusIndex, Math.Max(0, _options.Count - 1));
        TitleText.Text = model.Title;
        Options.ItemsSource = _options;
        ModalLayer.Visibility = Visibility.Visible;
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        FocusRetry.Until(this, () => !IsOpen || FocusRow(_focusIndex, FocusState.Programmatic));
    }

    /// <summary>
    /// Collapses the sheet. The rows stay until the next <see cref="Show"/> replaces them: a choice closes the sheet from
    /// inside its row's Click, which must not tear that row out of the tree.
    /// </summary>
    public void Hide() => ModalLayer.Visibility = Visibility.Collapsed;

    /// <summary>Up/Down: the next row, stopping at the ends; a focused row scrolls itself into view.</summary>
    public void Move(int step)
    {
        if (_options.Count == 0) return;
        FocusRow(Math.Clamp(_focusIndex + step, 0, _options.Count - 1), FocusState.Keyboard);
    }

    /// <summary>Where focus goes back to when it strays outside the open sheet; null until the rows loaded.</summary>
    public Control? FocusTarget => _focusIndex >= 0 && _focusIndex < _options.Count ? _options[_focusIndex].Row : null;

    private bool FocusRow(int index, FocusState state)
    {
        if (index < 0 || index >= _options.Count || _options[index].Row is not { } row) return false;
        return row.Focus(state) && row.FocusState != FocusState.Unfocused;
    }

    private void OnOptionLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Control { DataContext: SettingsPickerOptionView view } row) return;
            view.Row = row;
            row.ApplyTemplate();
            VisualStateManager.GoToState(row, view.Selected ? "Selected" : "Unselected", false);
        }
        catch (Exception ex) { Log.Error("Settings picker row", ex); }
    }

    private void OnOptionFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingsPickerOptionView view }) _focusIndex = view.Index;
    }

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        if (IsOpen && sender is FrameworkElement { DataContext: SettingsPickerOptionView view }) Chosen?.Invoke(view.Id);
    }
}
