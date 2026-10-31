using Bale.Xbox.Services;
using Windows.ApplicationModel.Activation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

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
            Artwork.TrimInBackground();
            if (DeviceIdentity.IsXbox)
            {
                // Draw edge to edge so artwork reaches the TV's edges; pages keep text inside the safe area themselves.
                ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);
            }

            // Inter for everything under the root frame (literal path: resources can't be read from code under AOT).
            frame = new Frame { FontFamily = new Windows.UI.Xaml.Media.FontFamily("ms-appx:///Assets/Fonts/InterVariable.ttf#Inter Variable") };
            // Arrow keys move focus like the d-pad everywhere; without root keyboard XY navigation,
            // panels that enable it (top bar, chips, detail actions) trap arrows inside them.
            frame.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
            Window.Current.Content = frame;
            frame.PreviewKeyDown += OnPreviewKeyDown;
            frame.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) => OnBackKey(frame, args)), handledEventsToo: false);
            SystemNavigationManager.GetForCurrentView().BackRequested += (_, args) => OnBackRequested(frame, args);
        }

#if DEBUG_SERVER
        if (_debugServer is null)
        {
            _debugServer = new DebugServer();
            _ = StartDebugServerAsync(_debugServer);
        }
#endif

        Session.Attach(frame);
        if (frame.Content is null)
        {
            frame.Navigate(typeof(MainPage), e.Arguments);
        }

        Window.Current.Activate();
    }

#if DEBUG_SERVER
    private DebugServer? _debugServer;

    private static async Task StartDebugServerAsync(DebugServer server)
    {
        try
        {
            await server.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Debug server start", ex);
        }
    }
#endif

    /// <summary>
    /// Bumpers, triggers and Page Up/Down do nothing anywhere, as on Silo's Android TV app, which
    /// handles none of them. Left alone, a ScrollViewer pages its content with them and leaves focus
    /// behind, off screen. Runs on the tunnelling pass so no ScrollViewer sees them. Escape instead
    /// waits for OnBackKey on the bubbling pass so dialogs and text controls get first refusal.
    /// </summary>
    private static void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.GamepadLeftShoulder or VirtualKey.GamepadRightShoulder
            or VirtualKey.GamepadLeftTrigger or VirtualKey.GamepadRightTrigger
            or VirtualKey.PageUp or VirtualKey.PageDown)
        {
            e.Handled = true;
        }
    }

    /// <summary>Routes system Back through the same policy as keyboard Escape and Backspace.</summary>
    private static void OnBackRequested(Frame frame, BackRequestedEventArgs args)
    {
        args.Handled = GoBack(frame);
    }

    /// <summary>Escape follows Back everywhere; Backspace does so only outside text fields.</summary>
    private static void OnBackKey(Frame frame, KeyRoutedEventArgs args)
    {
        try
        {
            // Android TV maps Escape to Back (MainTvActivity.kt). Wait for the bubbling pass,
            // not PreviewKeyDown, so a dialog or text control cannot also send its page back.
            if (args.Handled || args.Key is not (VirtualKey.Escape or VirtualKey.Back))
            {
                return;
            }

            if (args.Key == VirtualKey.Back && FocusManager.GetFocusedElement() is TextBox or PasswordBox or RichEditBox)
            {
                return;
            }

            args.Handled = GoBack(frame);
            if (args.Key == VirtualKey.Escape && !args.Handled)
            {
                // Unhandled system Back leaves the app at its root; Escape has no OS fallback.
                args.Handled = true;
                Application.Current.Exit();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Back key", ex);
        }
    }

    /// <summary>Offers Back to the page before navigating, without returning to sign-in.</summary>
    private static bool GoBack(Frame frame)
    {
        if (CardMenuHost.CloseActive() || frame.Content is IBackHandler page && page.OnBack())
        {
            return true;
        }

        // Never Back into sign-in: once signed in, Back from Home leaves the app (Skyline §7).
        if (!frame.CanGoBack || frame.BackStack[frame.BackStack.Count - 1].SourcePageType == typeof(MainPage))
        {
            return false;
        }

        frame.GoBack();
        return true;
    }
}
