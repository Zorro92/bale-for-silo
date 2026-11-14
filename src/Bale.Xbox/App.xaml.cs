using Bale.Xbox.Services;
using Bale.Core.Playback;
using Windows.ApplicationModel;
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
        Suspending += OnSuspending;
        Resuming += async (_, _) =>
        {
            // Resuming may be raised away from the UI thread, before LeavingBackground. Do
            // not restore artwork into an app that has resumed but is still off screen.
            if (_uiDispatcher is { } dispatcher)
                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    Session.SetForeground(Window.Current?.Visible == true);
                    if (Window.Current?.Visible == true)
                    {
                        _inBackground = false;
                        UiResidency.Resume();
                    }
                });
        };
        EnteredBackground += OnEnteredBackground;
        LeavingBackground += async (_, _) =>
        {
            _inBackground = false;
            UiResidency.Resume();
            if (Window.Current?.Content is Frame { Content: PlayerPage player })
                await player.RestoreAfterBackgroundAsync();
        };
    }

    private bool _inBackground;
    private CoreDispatcher? _uiDispatcher;
    internal bool IsInBackground => _inBackground;

    private async void OnEnteredBackground(object sender, EnteredBackgroundEventArgs e)
    {
        var deferral = e.GetDeferral();
        _inBackground = true;
        Session.SetForeground(false);
        try { await ReleaseBackgroundResourcesAsync("background"); }
        catch (Exception ex) { Log.Info($"background cleanup hr=0x{unchecked((uint)ex.HResult):X8}"); }
        finally { deferral.Complete(); }
    }

    private async void OnSuspending(object sender, SuspendingEventArgs e)
    {
        var deferral = e.SuspendingOperation.GetDeferral();
        _inBackground = true;
        Session.SetForeground(false);
        try { await ReleaseBackgroundResourcesAsync("suspend"); }
        catch (Exception ex) { Log.Info($"suspend cleanup hr=0x{unchecked((uint)ex.HResult):X8}"); }
        finally { deferral.Complete(); }
    }

    private async Task ReleaseBackgroundResourcesAsync(string reason)
    {
        // Close the UI gate synchronously, before player shutdown can yield. Downloads/decode
        // callbacks must not refill a cached page while the app is waiting for media Close.
        UiResidency.Release();
        var generation = UiResidency.Generation;
        if (Window.Current?.Content is Frame { Content: PlayerPage player })
            await player.SuspendPlaybackAsync();
        // A quick return can race asynchronous shutdown. Never trim the resumed foreground UI.
        if (!_inBackground || generation != UiResidency.Generation) return;
        _ = PlaybackMemoryRelease.Shared.Schedule();
        await PlaybackMemoryRelease.Shared.WaitAsync(PlaybackMemoryRelease.StartWaitLimit);
        if (_inBackground && generation == UiResidency.Generation) PlaybackResources.ReleaseIdle(reason);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        Log.Info($"Launched {Windows.ApplicationModel.Package.Current.Id.Version.Build} on {Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily}");
        _ = DeviceIdentity.SoftwareVideoSupported; // Logs the console's software video decision once per launch.
        if (Window.Current.Content is not Frame frame)
        {
            Artwork.TrimInBackground();
            if (DeviceIdentity.IsXbox)
            {
                // Draw edge to edge so artwork reaches the TV's edges; pages keep text inside the safe area themselves.
                ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);
            }

            // Inter for everything under the root frame (literal path: resources can't be read from code under AOT).
            frame = new PageFrame { FontFamily = new Windows.UI.Xaml.Media.FontFamily("ms-appx:///Assets/Fonts/InterVariable.ttf#Inter Variable") };
            // Arrow keys move focus like the d-pad everywhere; without root keyboard XY navigation,
            // panels that enable it (top bar, chips, detail actions) trap arrows inside them.
            frame.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
            Window.Current.Content = frame;
            _uiDispatcher = frame.Dispatcher;
            UiResidency.Attach(frame);
            Window.Current.VisibilityChanged += (_, args) => Session.SetForeground(args.Visible);
            frame.PreviewKeyDown += OnPreviewKeyDown;
            frame.PreviewKeyUp += OnPreviewKeyUp;
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
        Session.SetForeground(Window.Current.Visible);
        // The HDMI modes belong to the view, so read them once the window is active.
        PlaybackClaims.RefreshVersionMatch();
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
    /// Bumpers, triggers and Page Up/Down never page a ScrollViewer away from focus. The player
    /// alone maps triggers (and keyboard J/L) to the official TV app's dedicated skip actions.
    /// Escape waits for OnBackKey on the bubbling pass so dialogs and text controls get first refusal.
    /// </summary>
    private static void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (PageFrame.IsTransitioning)
            e.Handled = true;
        else if (sender is Frame { Content: PlayerPage transport } && transport.HandleSkipKey(e.Key, firstDown: !e.KeyStatus.WasKeyDown))
            e.Handled = true;
        else if (e.Key is VirtualKey.GamepadLeftShoulder or VirtualKey.GamepadRightShoulder
            or VirtualKey.GamepadLeftTrigger or VirtualKey.GamepadRightTrigger
            or VirtualKey.PageUp or VirtualKey.PageDown)
            e.Handled = true;
#if DEBUG_SERVER
        else if (sender is Frame { Content: PlayerPage player } && e.Key is VirtualKey.GamepadView or VirtualKey.I)
        {
            // Consume on the tunnelling pass so View/I cannot also activate a player control.
            e.Handled = true;
            if (!e.KeyStatus.WasKeyDown) player.ToggleDebugStats();
        }
#endif
    }

    private static void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        // A Select released after navigation must not activate the incoming page's first control.
        if (PageFrame.IsTransitioning) e.Handled = true;
        else if (sender is Frame { Content: PlayerPage transport } && transport.HandleSkipKey(e.Key, firstDown: false))
            e.Handled = true;
#if DEBUG_SERVER
        else if (sender is Frame { Content: PlayerPage } && e.Key is VirtualKey.GamepadView or VirtualKey.I)
            e.Handled = true;
#endif
    }

    /// <summary>Routes system Back through the same policy as keyboard Escape and Backspace.</summary>
    private static void OnBackRequested(Frame frame, BackRequestedEventArgs args)
    {
        // Names the source of an unexpected stop (a remote, HDMI-CEC or a controller).
        Log.Info("back: system");
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

            Log.Info($"back: key={args.Key} original={args.OriginalKey}");
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
        if (PageFrame.IsTransitioning)
        {
            return true;
        }

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
