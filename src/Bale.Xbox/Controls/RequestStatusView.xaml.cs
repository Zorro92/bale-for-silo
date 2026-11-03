using Bale.Core.Requests;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Controls;

/// <summary>TvRequestStatusLabel / RequestStageTrack, styled entirely by Skyline visual states.</summary>
public sealed partial class RequestStatusView : UserControl
{
    public RequestStatusView() => InitializeComponent();

    public void Show(RequestProgress? progress, string? text = null, bool track = false, bool steps = false, bool label = true)
    {
        Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        if (progress is null) return;
        LabelText.Text = text ?? progress.ShortLabel;
        LabelRow.Visibility = label ? Visibility.Visible : Visibility.Collapsed;
        Track.Visibility = track ? Visibility.Visible : Visibility.Collapsed;
        Dot.ApplyTemplate();
        VisualStateManager.GoToState(Dot, progress.Tint.ToString(), false);
        Control[] segments = [Step0, Step1, Step2, Step3];
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            segment.ApplyTemplate();
            VisualStateManager.GoToState(segment, progress.CurrentStep == index ? progress.Tint.ToString()
                : index < progress.CompletedSteps ? "Complete" : "Future", false);
            VisualStateManager.GoToState(segment, steps ? "Labelled" : "Unlabelled", false);
        }
    }
}
