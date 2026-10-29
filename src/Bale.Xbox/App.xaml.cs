using Bale.Xbox.Services;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox;

public sealed partial class App : Application
{
    public App()
    {
        InitializeComponent();

        // Controller-first: the cursor only appears if a page explicitly asks for it.
        RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
        UnhandledException += (_, e) =>
        {
            // e.Message carries XAML's own error text, often more specific than the exception's.
            Log.Error($"Unhandled ({e.Message})", e.Exception);
#if DIAGNOSTICS
            e.Handled = true;
#endif
        };
        DebugSettings.BindingFailed += (_, e) => Log.Info($"XAML binding failed: {e.Message}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        Log.Info($"Launched {Windows.ApplicationModel.Package.Current.Id.Version.Build} on {Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily}");
        if (Window.Current.Content is not Frame frame)
        {
            if (DeviceIdentity.IsXbox)
            {
                // Draw edge to edge so artwork reaches the TV's edges; pages keep text inside the safe area themselves.
                ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);
            }

            frame = new Frame();
            Window.Current.Content = frame;
            SystemNavigationManager.GetForCurrentView().BackRequested += (_, args) => OnBackRequested(frame, args);
        }

        if (frame.Content is null)
        {
            frame.Navigate(typeof(MainPage), e.Arguments);
        }

        Window.Current.Activate();
    }

    private static void OnBackRequested(Frame frame, BackRequestedEventArgs args)
    {
        if (frame.Content is IBackHandler page && page.OnBack())
        {
            args.Handled = true;
        }
        else if (frame.CanGoBack)
        {
            frame.GoBack();
            args.Handled = true;
        }
    }
}
