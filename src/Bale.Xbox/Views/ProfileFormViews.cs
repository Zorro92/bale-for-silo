using System.ComponentModel;
using Bale.Core.Api.Models;
using Bale.Core.Profiles;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Views;

/// <summary>Partial XAML collection models for Native AOT; selections are reasserted after ToggleButton.Click.</summary>
public sealed partial class ProfileChoiceView(string? key, string label) : INotifyPropertyChanged
{
    public string? Key { get; } = key;
    public string Label { get; } = label;
    public bool Selected { get; private set; }
    public Brush Fill { get; private set; } = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255));
    public Brush Ink { get; private set; } = new SolidColorBrush(Color.FromArgb(158, 255, 255, 255));
    public Brush Stroke { get; private set; } = new SolidColorBrush(Color.FromArgb(41, 255, 255, 255));
    public double Width { get; private set; } = double.NaN;
    // TvProfileAccessControls.kt ladderShape: rounded outer ends, tight inner joins.
    public CornerRadius Radius { get; private set; } = new(5);
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(bool selected, bool included = false)
    {
        Selected = selected;
        Fill = new SolidColorBrush(Color.FromArgb(included ? (byte)46 : (byte)8, 255, 255, 255));
        Ink = new SolidColorBrush(Color.FromArgb(included ? (byte)255 : (byte)158, 255, 255, 255));
        Stroke = new SolidColorBrush(Color.FromArgb(selected || included ? (byte)0 : (byte)41, 255, 255, 255));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
    public void SetGeometry(double width, int index, int count)
    {
        Width = width;
        Radius = new(index == 0 ? 14 : 5, index == count - 1 ? 14 : 5, index == count - 1 ? 14 : 5, index == 0 ? 14 : 5);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed partial class ProfilePresetView(ProfileAvatarPresets.Preset preset) : INotifyPropertyChanged
{
    public ProfileAvatarPresets.Preset Preset { get; } = preset;
    public string Label => $"Avatar {Preset.Seed}";
    public ImageSource Image { get; } = ProfileView.AvatarImage(preset.ImageUrl, 42);
    public bool Selected { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(string seed)
    {
        Selected = Preset.Seed == seed;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
    }
}

public sealed partial class ProfileLibraryView(UserLibrary library) : INotifyPropertyChanged
{
    public string Id => library.Id!;
    public string Name => library.Name ?? string.Empty;
    public bool Allowed { get; private set; }
    public Brush Ink { get; private set; } = new SolidColorBrush(Color.FromArgb(158, 255, 255, 255));
    public Visibility CheckVisibility => Allowed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => Allowed ? Visibility.Collapsed : Visibility.Visible;
    public string AccessibleName => $"{Name}, {(Allowed ? "selected" : "not selected")}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(IReadOnlySet<string> allowed)
    {
        Allowed = allowed.Contains(Id);
        Ink = new SolidColorBrush(Color.FromArgb(Allowed ? (byte)255 : (byte)158, 255, 255, 255));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
