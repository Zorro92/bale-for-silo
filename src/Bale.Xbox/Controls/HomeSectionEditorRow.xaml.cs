using Bale.Core.Browse;
using Bale.Core.Settings;
using Bale.Xbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox.Controls;

public sealed partial class HomeSectionEditorRow : UserControl
{
    private readonly Rail _actions;
    private bool _visible;

    public HomeSectionEditorRow()
    {
        InitializeComponent();
        _actions = Rail.Attach(Actions);
        _actions.Pin = false;
        _actions.Remembered = VisibilityButton;
        Loaded += (_, _) => UpdateIcons();
    }

    public string SectionId { get; private set; } = string.Empty;
    public event Action<HomeSectionEditorRow, int>? Activated;

    public void Update(MediaRow row, HomeSectionLayout layout, bool editing, int index, int count)
    {
        SectionId = row.Id;
        _visible = !layout.HiddenSectionIds.Contains(row.Id);
        ShownTitle.Text = HiddenTitle.Text = row.Title;
        ShownCount.Text = HiddenCount.Text = row.Cards.Count == 1 ? "1 item" : $"{row.Cards.Count} items";
        ShownCaption.Visibility = _visible ? Visibility.Visible : Visibility.Collapsed;
        HiddenCaption.Visibility = _visible ? Visibility.Collapsed : Visibility.Visible;
        MoveUpButton.Visibility = MoveDownButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index < count - 1;
        AutomationProperties.SetName(MoveUpButton, "Move " + row.Title + " earlier");
        AutomationProperties.SetName(MoveDownButton, "Move " + row.Title + " later");
        AutomationProperties.SetName(VisibilityButton, (_visible ? "Hide " : "Show ") + row.Title);
        UpdateIcons();
    }

    private void UpdateIcons()
    {
        MoveUpButton.ApplyTemplate();
        MoveDownButton.ApplyTemplate();
        VisibilityButton.ApplyTemplate();
        VisualStateManager.GoToState(MoveUpButton, "MoveUp", false);
        VisualStateManager.GoToState(MoveDownButton, "MoveDown", false);
        VisualStateManager.GoToState(VisibilityButton, _visible ? "VisibleEye" : "HiddenEye", false);
    }

    public int FocusedAction => MoveUpButton.FocusState != FocusState.Unfocused ? -1
        : MoveDownButton.FocusState != FocusState.Unfocused ? 1 : 0;

    public bool FocusAction(int action)
    {
        var button = ButtonFor(action);
        if (!button.IsEnabled || button.Visibility != Visibility.Visible) return false;
        // Rail's remembered entry must agree with deliberate vertical/move restoration.
        _actions.Remembered = button;
        return button.Focus(FocusState.Programmatic) && button.FocusState != FocusState.Unfocused;
    }

    public bool CanFocusAction(int action) => ButtonFor(action) is { IsEnabled: true, Visibility: Visibility.Visible };

    private Button ButtonFor(int action) => action switch { -1 => MoveUpButton, 1 => MoveDownButton, _ => VisibilityButton };

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var step = SettingsInput.HorizontalStep(e.Key);
        if (step == 0) return;
        // TvHomeSectionsEditor.kt: disabled endpoint arrows are skipped horizontally, never by Up/Down.
        Button[] actions = [MoveUpButton, MoveDownButton, VisibilityButton];
        var index = Array.FindIndex(actions, button => button.FocusState != FocusState.Unfocused);
        for (var next = index + step; next >= 0 && next < actions.Length; next += step)
        {
            if (!actions[next].IsEnabled || actions[next].Visibility != Visibility.Visible) continue;
            _actions.Remembered = actions[next];
            actions[next].Focus(FocusState.Keyboard);
            break;
        }
        e.Handled = true;
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => Activated?.Invoke(this, -1);
    private void OnMoveDownClick(object sender, RoutedEventArgs e) => Activated?.Invoke(this, 1);
    private void OnVisibilityClick(object sender, RoutedEventArgs e) => Activated?.Invoke(this, 0);
}
