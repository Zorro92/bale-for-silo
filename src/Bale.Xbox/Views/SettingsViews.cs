using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Views;

/// <summary>A row of the Settings picker sheet (Controls/SettingsPickerSheet): a Bale.Core SettingsChoice and whether it is checked.</summary>
public sealed partial class SettingsPickerOptionView(string id, string label, bool selected, int index)
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public bool Selected { get; } = selected;
    public int Index { get; } = index;

    /// <summary>The row's button once its template loaded (rows are not virtualised: the panel is a StackPanel).</summary>
    internal Control? Row { get; set; }
}
