using System.ComponentModel;
using Bale.Xbox.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Controls;

public sealed partial class RequestPoster : UserControl
{
    private RequestCardView? _view;
    private bool _loaded;

    public RequestPoster()
    {
        InitializeComponent();
        Loaded += (_, _) => { _loaded = true; BindCard(); };
        Unloaded += (_, _) => { _loaded = false; Unbind(); };
        DataContextChanged += (_, _) => { if (_loaded) BindCard(); };
    }

    private void Unbind()
    {
        if (_view is not null) _view.PropertyChanged -= OnChanged;
        _view = null;
    }

    private void BindCard()
    {
        Unbind();
        _view = DataContext as RequestCardView;
        if (_view is null) return;
        _view.PropertyChanged += OnChanged;
        Render();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs args) => Render();

    private void Render()
    {
        if (_view is not { } view) return;
        var item = view.Item;
        TitleText.Text = FocusCaption.Text = item.Title;
        PosterImage.Source = view.Image;
        var own = item.Record is not null;
        Status.Show(own ? item.Progress : null);
        YearText.Text = item.Year is > 0 ? item.Year.Value.ToString() : string.Empty;
        YearText.Visibility = !own && item.Year is > 0 ? Visibility.Visible : Visibility.Collapsed;
        Badge.ApplyTemplate();
        VisualStateManager.GoToState(Badge, own ? "Unavailable" : item.Progress?.Display.ToString() ?? "Unavailable", false);
        Windows.UI.Xaml.Automation.AutomationProperties.SetName(this,
            string.Join(", ", new[] { item.Title, item.Year?.ToString(), item.Progress?.ShortLabel }.Where(value => value is not null)));
    }
}
