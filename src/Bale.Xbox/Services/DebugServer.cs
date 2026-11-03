#if DEBUG_SERVER
using System.Text;
using Bale.Core.Connection;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Gaming.Input;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Input.Preview.Injection;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Services;

/// <summary>
/// Development-only control channel, so the app can be driven and inspected from a Linux shell
/// (scripts/bale-debug.sh) instead of a controller. A tiny HTTP server on the LAN:
///
///   GET  /log?since=N     log lines after sequence N; the reply's first line is the next N
///   POST /press/{button}  up, down, left, right, a, b, menu, view: injected as real gamepad input
///   POST /key/{key}       a real keystroke, by VirtualKey name: Enter, Escape, Tab, Down, Back, F5...
///   POST /type?text=...   types text as real keystrokes into whatever has focus
///   GET  /focus           the focused element and its text
///   POST /goto?target=... focus a named or labelled control (optional #N), for setup only
///   GET  /targets         the visible, enabled tab-stop controls goto can reach
///   GET  /tree            the visible element tree with bounds and text
///   POST /fault/profiles  fail the next picker profile-list load once (Retry then uses the server)
///   POST /fault/search    fail the next title-search request once (Try again then uses the server)
///   POST /fault/home      fail the next Home load once (Retry then uses the server)
///   POST /fault/home-empty  return no rows on the next Home load once (Refresh then uses the server)
///   POST /fault/requests  fail the next Requests hub load once (Retry then uses the server)
///   POST /fault/requests-empty  return no rows on the next Requests hub load once
///   POST /fault/request-detail  fail the next request-detail load once (Retry then uses the server)
///   POST /show/login      preview Login with disposable fixtures, without any network or saved-state writes
///   POST /show/login/password  preview password sign-in (submissions are local simulated failures)
///   POST /show/servers    preview saved-server switching/removal on an in-memory registry
///   POST /show/server/http-warning  preview exact-origin HTTP consent
///   POST /show/server/discovery  preview two identity-confirmed nearby rows
///   POST /show/login/expired  preview Login with the session-expired notice
///   POST /show/server     preview manual server entry; Back returns to the signed-in page
///   POST /show/profiles/manage  preview Manage/Done, Add and non-primary deletion in a disposable household
///   POST /show/profile/create  preview Create Profile; Save only changes the fixture
///   POST /show/profile/edit  preview Edit Profile; Save only changes the fixture
///   POST /show/home       close a disposable preview and return to the signed-in app
///
/// Only started on sideloaded builds (see App). Anyone on the LAN can reach it while it runs, so it
/// must never ship in a Store build.
/// </summary>
internal sealed class DebugServer
{
    public const int Port = 8790;

    private static int _failProfilesOnce;
    private static int _failSearchOnce;
    private static int _homeFaultOnce;
    private static int _requestsFaultOnce;
    private static int _requestDetailFaultOnce;

    // Read-only, one-shot placeholders; these hooks cannot create/cancel/follow anything.
    internal static bool TakeRequestsEmptyOrThrow() => Interlocked.Exchange(ref _requestsFaultOnce, 0) switch
    {
        1 => throw new HttpRequestException("Injected Requests failure (fault requests)."),
        2 => true,
        _ => false,
    };

    internal static void ThrowIfRequestDetailFaulted()
    {
        if (Interlocked.Exchange(ref _requestDetailFaultOnce, 0) != 0)
            throw new HttpRequestException("Injected request-detail failure (fault request-detail).");
    }

    // TvHomeScreen.kt: exercise error/empty independently, without changing the server's libraries.
    // The newest armed Home fault wins, and only a Home load (not a library/For You load) consumes it.
    internal static bool TakeHomeEmptyOrThrow() => Interlocked.Exchange(ref _homeFaultOnce, 0) switch
    {
        1 => throw new HttpRequestException("Injected Home failure (fault home)."),
        2 => true,
        _ => false,
    };

    // Exercise TvProfileSelectionScreen.kt's error branch without making a real server unreachable.
    // Both this hook and its call site are absent when BaleDebugServer=false (DEBUG_SERVER unset).
    internal static void ThrowIfProfilesFaulted()
    {
        if (Interlocked.Exchange(ref _failProfilesOnce, 0) != 0)
        {
            throw new HttpRequestException("Injected profile-list failure (fault profiles).");
        }
    }

    // TvSearchViewModel.kt keeps people independent; fault only the next title page, not people.
    // Like the profile hook, this and its call sites disappear when BaleDebugServer=false.
    internal static void ThrowIfSearchFaulted()
    {
        if (Interlocked.Exchange(ref _failSearchOnce, 0) != 0)
        {
            throw new HttpRequestException("Injected title-search failure (fault search).");
        }
    }

    private readonly StreamSocketListener _listener = new();
    private InputInjector? _injector;

    public async Task StartAsync()
    {
        _listener.ConnectionReceived += (listener, args) => _ = HandleAsync(args.Socket);
        await _listener.BindServiceNameAsync(Port.ToString());

        // Set up injection now: the first input after InitializeGamepadInjection is otherwise lost.
        Log.Info("Debug server: " + await OnUiAsync(() =>
        {
            _injector = InputInjector.TryCreate();
            _injector?.InitializeGamepadInjection();
            return _injector is null ? "input injection unavailable" : "input injection ready";
        }));
        Log.Info($"Debug server listening on port {Port}");
    }

    private async Task HandleAsync(StreamSocket socket)
    {
        using (socket)
        {
            try
            {
                var request = await ReadRequestLineAsync(socket.InputStream);
                var (status, body) = await RouteAsync(request);
                await WriteResponseAsync(socket.OutputStream, status, body);
            }
            catch (Exception ex)
            {
                Log.Error("Debug server", ex);
            }
        }
    }

    private async Task<(string Status, string Body)> RouteAsync(string requestLine)
    {
        // "POST /press/down HTTP/1.1"
        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
        {
            return ("400 Bad Request", "bad request\n");
        }

        var method = parts[0];
        var target = new Uri("http://bale" + parts[1]);
        var path = target.AbsolutePath.TrimEnd('/');
        var query = ParseQuery(target.Query);

        switch (method, path)
        {
            case ("GET", "/log"):
                var (lines, next) = Log.Since(long.TryParse(query.GetValueOrDefault("since"), out var since) ? since : 0);
                return ("200 OK", next + "\n" + string.Join("\n", lines) + (lines.Count > 0 ? "\n" : string.Empty));

            case ("POST", var press) when press.StartsWith("/press/", StringComparison.Ordinal):
                var button = press.Substring("/press/".Length);
                await OnUiAsync(() => Press(button));
                await Task.Delay(150);
                return ("200 OK", await OnUiAsync(DescribeFocus) + "\n");

            case ("POST", var key) when key.StartsWith("/key/", StringComparison.Ordinal):
                var result = await OnUiAsync(() => Key(key.Substring("/key/".Length)));
                await Task.Delay(150);
                return ("200 OK", result + "; " + await OnUiAsync(DescribeFocus) + "\n");

            case ("POST", "/type"):
                var typed = await OnUiAsync(() => Type(query.GetValueOrDefault("text") ?? string.Empty));
                await Task.Delay(150);
                return ("200 OK", typed + "; " + await OnUiAsync(DescribeFocus) + "\n");

            case ("GET", "/focus"):
                return ("200 OK", await OnUiAsync(DescribeFocus) + "\n");

            case ("POST", "/goto"):
                var gotoStatus = "500 Internal Server Error";
                var gotoBody = await OnUiAsync(() =>
                {
                    var response = GoTo(query.GetValueOrDefault("target") ?? string.Empty);
                    gotoStatus = response.Status;
                    return response.Body;
                });
                return (gotoStatus, gotoBody);

            case ("GET", "/targets"):
                return ("200 OK", await OnUiAsync(() => DescribeTargets(FindTargets())));

            case ("GET", "/tree"):
                return ("200 OK", await OnUiAsync(DescribeTree));

            case ("POST", "/fault/profiles"):
                Interlocked.Exchange(ref _failProfilesOnce, 1);
                return ("200 OK", "next picker profile-list load will fail once; Retry will load normally\n");

            case ("POST", "/fault/search"):
                Interlocked.Exchange(ref _failSearchOnce, 1);
                return ("200 OK", "next title-search request will fail once; Try again will search normally\n");

            case ("POST", "/fault/home"):
                Interlocked.Exchange(ref _homeFaultOnce, 1);
                return ("200 OK", "next Home load will fail once; existing rows survive quiet refresh failures; Retry will load normally\n");

            case ("POST", "/fault/home-empty"):
                Interlocked.Exchange(ref _homeFaultOnce, 2);
                return ("200 OK", "next Home load will return no rows once; Refresh will load normally\n");

            case ("POST", "/fault/requests"):
                Interlocked.Exchange(ref _requestsFaultOnce, 1);
                return ("200 OK", "next Requests load will fail once; existing shelves survive; Retry will read normally\n");

            case ("POST", "/fault/requests-empty"):
                Interlocked.Exchange(ref _requestsFaultOnce, 2);
                return ("200 OK", "next Requests load will return no rows once; re-enter Requests to read normally\n");

            case ("POST", "/fault/request-detail"):
                Interlocked.Exchange(ref _requestDetailFaultOnce, 1);
                return ("200 OK", "next request-detail load will fail once; Retry will read normally\n");

            case ("POST", "/show/login"):
            case ("POST", "/show/login/expired"):
            case ("POST", "/show/login/password"):
            case ("POST", "/show/server"):
            case ("POST", "/show/server/http-warning"):
            case ("POST", "/show/server/discovery"):
            case ("POST", "/show/servers"):
            case ("POST", "/show/profiles/manage"):
            case ("POST", "/show/profile/create"):
            case ("POST", "/show/profile/edit"):
            case ("POST", "/show/home"):
                var showStatus = "200 OK";
                var showBody = await OnUiAsync(() =>
                {
                    var response = Show(path);
                    showStatus = response.Status;
                    return response.Body;
                });
                return (showStatus, showBody);

            default:
                return ("404 Not Found", "routes: GET /log?since=N, POST /press/{up|down|left|right|a|b|menu|view|lb|rb|lt|rt}, POST /key/{VirtualKey}, POST /type?text=, GET /focus, POST /goto?target=, GET /targets, GET /tree, POST /fault/{profiles|search|home|home-empty|requests|requests-empty|request-detail}, POST /show/{login|login/expired|login/password|server|server/http-warning|server/discovery|servers|profiles/manage|profile/create|profile/edit|home}\n");
        }
    }

    private static (string Status, string Body) Show(string path)
    {
        if (Window.Current.Content is not Frame frame || Session.Client?.IsSignedIn != true)
        {
            return ("409 Conflict", "previews require an existing signed-in session\n");
        }

        if (path == "/show/home")
        {
            CloseAuthPreview(frame, all: true);
            return ("200 OK", "returned to the signed-in app; session unchanged\n");
        }

        if (path is "/show/profiles/manage" or "/show/profile/create" or "/show/profile/edit")
        {
            CloseAuthPreview(frame, all: true);
            var profiles = new ProfilePreview();
            var household = new ProfileNavigation(profiles,
                static value => Uri.TryCreate(value, UriKind.Absolute, out var url) ? url : null, IsPreview: true);
            // Keep the fixture picker under both editors: Save/Back cannot enter the real household.
            frame.Navigate(typeof(ProfilesPage), household);
            if (path != "/show/profiles/manage")
            {
                var edit = path == "/show/profile/edit" ? profiles.Snapshot.First(profile => profile.IsPrimary != true) : null;
                frame.Navigate(typeof(ProfileEditorPage), new ProfileEditorNavigation(household, edit));
            }
            return ("200 OK", "profile preview opened; Save/Delete only change this disposable household; primary PIN 1234; Back or show home returns; signed-in profile unchanged\n");
        }

        // Only disposable fixtures: even Connect, password submission, switch/remove and consent stay in memory.
        var fixture = new ConnectionPreview();
        if (path == "/show/servers")
        {
            CloseAuthPreview(frame, all: true);
            frame.Navigate(typeof(ServerListPage), fixture);
        }
        else
        {
            var screen = path switch
            {
                "/show/server" => ConnectionScreen.Server,
                "/show/server/http-warning" => ConnectionScreen.HttpWarning,
                "/show/server/discovery" => ConnectionScreen.Discovery,
                "/show/login/expired" => ConnectionScreen.Expired,
                "/show/login/password" => ConnectionScreen.Password,
                _ => ConnectionScreen.Login,
            };
            var request = new ConnectionNavigation(fixture, screen);
            if (frame.Content is MainPage { IsAuthPreview: true } page) page.ShowPreview(request);
            else
            {
                CloseAuthPreview(frame, all: true);
                frame.Navigate(typeof(MainPage), request);
            }
        }
        return ("200 OK", "connection preview opened; no network requests or saved-state writes; Back or show home returns\n");
    }

    internal static bool CloseAuthPreview(Frame frame, bool all = false)
    {
        var closed = false;
        while (frame.Content is MainPage { IsAuthPreview: true } or ServerListPage { IsAuthPreview: true }
            or ProfilesPage { IsProfilePreview: true } or ProfileEditorPage { IsProfilePreview: true })
        {
            if (!frame.CanGoBack)
            {
                // Profile previews must never re-resolve, select or persist a real profile, even without an opener.
                if (frame.Content is ProfilesPage or ProfileEditorPage) break;
                Session.ContinueToHome(frame);
                closed = true;
                break;
            }
            closed = true;
            frame.GoBack();
            if (!all) break;
        }
        if (closed) frame.ForwardStack.Clear();
        return closed;
    }

    /// <summary>A real gamepad press and release, so focus, XY navigation, ItemClick and Back behave exactly as with a controller.</summary>
    private string Press(string button)
    {
        var buttons = button.ToLowerInvariant() switch
        {
            "up" => GamepadButtons.DPadUp,
            "down" => GamepadButtons.DPadDown,
            "left" => GamepadButtons.DPadLeft,
            "right" => GamepadButtons.DPadRight,
            "a" or "select" => GamepadButtons.A,
            "b" or "back" => GamepadButtons.B,
            "menu" => GamepadButtons.Menu,
            "view" => GamepadButtons.View,
            "lb" => GamepadButtons.LeftShoulder,
            "rb" => GamepadButtons.RightShoulder,
            "lt" or "rt" => GamepadButtons.None,
            _ => (GamepadButtons?)null,
        };
        if (buttons is null)
        {
            return $"unknown button {button}";
        }

        if (_injector is null)
        {
            return "input injection unavailable";
        }

        // Triggers are analog: fully pulled, then released.
        var name = button.ToLowerInvariant();
        _injector.InjectGamepadInput(new InjectedInputGamepadInfo
        {
            Buttons = buttons.Value,
            LeftTrigger = name == "lt" ? 1 : 0,
            RightTrigger = name == "rt" ? 1 : 0,
        });
        _injector.InjectGamepadInput(new InjectedInputGamepadInfo());
        return "pressed";
    }

    /// <summary>A keystroke by VirtualKey name, pressed and released.</summary>
    private string Key(string name)
    {
        if (!Enum.TryParse<Windows.System.VirtualKey>(name, ignoreCase: true, out var key))
        {
            return $"unknown key {name}";
        }

        if (_injector is null)
        {
            return "input injection unavailable";
        }

        _injector.InjectKeyboardInput(new[]
        {
            new InjectedInputKeyboardInfo { VirtualKey = (ushort)key },
            new InjectedInputKeyboardInfo { VirtualKey = (ushort)key, KeyOptions = InjectedInputKeyOptions.KeyUp },
        });
        return $"key {key}";
    }

    /// <summary>Text as real Unicode keystrokes, so it reaches whatever has focus exactly like a keyboard would.</summary>
    private string Type(string text)
    {
        if (_injector is null)
        {
            return "input injection unavailable";
        }

        // One injection per character: a batch mixing Unicode keystrokes with a virtual key is rejected
        // (E_INVALIDARG), and a space only works as the Space virtual key.
        foreach (var ch in text)
        {
            var down = ch == ' '
                ? new InjectedInputKeyboardInfo { VirtualKey = (ushort)Windows.System.VirtualKey.Space }
                : new InjectedInputKeyboardInfo { ScanCode = ch, KeyOptions = InjectedInputKeyOptions.Unicode };
            var up = ch == ' '
                ? new InjectedInputKeyboardInfo { VirtualKey = (ushort)Windows.System.VirtualKey.Space, KeyOptions = InjectedInputKeyOptions.KeyUp }
                : new InjectedInputKeyboardInfo { ScanCode = ch, KeyOptions = InjectedInputKeyOptions.Unicode | InjectedInputKeyOptions.KeyUp };
            _injector.InjectKeyboardInput(new[] { down, up });
        }

        // Never echo entered credentials, including when the software keyboard owns focus.
        return $"typed {text.Length} characters";
    }

    private static string DescribeFocus()
    {
        if (FocusManager.GetFocusedElement() is not FrameworkElement element)
        {
            return "nothing focused";
        }

        return $"{Describe(element)} text=\"{TextOf(element, 6)}\"";
    }

    private static (string Status, string Body) GoTo(string target)
    {
        var occurrence = 1;
        var suffix = target.LastIndexOf('#');
        if (suffix >= 0 && int.TryParse(target.Substring(suffix + 1), out var index))
        {
            target = target.Substring(0, suffix);
            occurrence = index;
        }

        if (string.IsNullOrWhiteSpace(target) || occurrence < 1)
        {
            return ("400 Bad Request", "target is required; an optional #N must be 1-based\n");
        }

        var candidates = FindTargets();
        var matches = new List<(Control Control, FrameworkElement Root)>();
        var tier = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var matchTier = MatchTier(candidate.Control, target);
            if (matchTier == 0 || matchTier > tier)
            {
                continue;
            }

            if (matchTier < tier)
            {
                tier = matchTier;
                matches.Clear();
            }

            matches.Add(candidate);
        }

        if (occurrence > matches.Count)
        {
            var detail = matches.Count == 0 ? "no matches" : $"tier={tier} matches={matches.Count}";
            var near = candidates.OrderByDescending(candidate => NearScore(candidate.Control, target)).Take(20);
            return ("404 Not Found", $"no match for target=\"{EscapeText(target)}\" #{occurrence}; {detail}\nnear targets:\n" + DescribeTargets(near));
        }

        var chosen = matches[occurrence - 1].Control;
        var focused = chosen.Focus(FocusState.Keyboard);
        // Rail.OnGettingFocus sends focus entering a rail from outside to its remembered item. goto is
        // for setup, so focus again: now coming from inside the rail, the second request isn't redirected.
        var retried = !FocusIsWithin(chosen);
        if (retried) focused = chosen.Focus(FocusState.Keyboard);
        var redirected = !FocusIsWithin(chosen);

        var tierName = tier switch { 1 => "x:Name", 2 => "automation name", 3 => "text", _ => "substring" };
        var note = redirected ? "; redirected" : retried ? "; entered rail, refocused" : string.Empty;
        return (redirected ? "409 Conflict" : "200 OK", $"tier={tier} ({tierName}) matches={matches.Count} match={occurrence} chosen={Describe(chosen)} focus={focused}{note}; {DescribeFocus()}\n");
    }

    private static bool FocusIsWithin(DependencyObject target)
    {
        for (var node = FocusManager.GetFocusedElement() as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, target)) return true;
        }

        return false;
    }

    private static int MatchTier(Control control, string target)
    {
        if (string.Equals(control.Name, target, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var automationName = AutomationProperties.GetName(control) ?? string.Empty;
        if (string.Equals(automationName, target, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        var text = TextOf(control, 6);
        var texts = TextsOf(control, int.MaxValue);
        if (string.Equals(text, target, StringComparison.OrdinalIgnoreCase)
            || texts.Any(value => string.Equals(value, target, StringComparison.OrdinalIgnoreCase)))
        {
            return 3;
        }

        return automationName.Contains(target, StringComparison.OrdinalIgnoreCase)
            || text.Contains(target, StringComparison.OrdinalIgnoreCase)
            || texts.Any(value => value.Contains(target, StringComparison.OrdinalIgnoreCase)) ? 4 : 0;
    }

    private static List<(Control Control, FrameworkElement Root)> FindTargets()
    {
        var targets = new List<(Control Control, FrameworkElement Root)>();
        var window = Window.Current;
        if (window.Content is FrameworkElement root)
        {
            CollectTargets(root, root, 0, targets);
        }

        foreach (var popup in VisualTreeHelper.GetOpenPopups(window))
        {
            if (popup.Visibility == Visibility.Visible && popup.Opacity != 0 && popup.Child is FrameworkElement popupRoot)
            {
                // Popup children live outside Window.Content's tree; their bounds use the popup root.
                CollectTargets(popupRoot, popupRoot, 0, targets);
            }
        }

        return targets;
    }

    private static void CollectTargets(FrameworkElement element, FrameworkElement root, int depth, List<(Control Control, FrameworkElement Root)> targets)
    {
        if (element.Visibility != Visibility.Visible || element.Opacity == 0 || depth > 60)
        {
            return;
        }

        if (element is Control { IsEnabled: true, IsTabStop: true, ActualWidth: > 0, ActualHeight: > 0 } control)
        {
            targets.Add((control, root));
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (VisualTreeHelper.GetChild(element, i) is FrameworkElement child)
            {
                CollectTargets(child, root, depth + 1, targets);
            }
        }
    }

    private static string DescribeTargets(IEnumerable<(Control Control, FrameworkElement Root)> targets)
    {
        var builder = new StringBuilder();
        foreach (var (control, root) in targets)
        {
            if (builder.Length > 400_000)
            {
                break;
            }

            var bounds = Bounds(control, root);
            builder.Append(Describe(control).Replace("\r", "\\r").Replace("\n", "\\n"))
                .Append($" [{bounds.X:0},{bounds.Y:0} {bounds.Width:0}x{bounds.Height:0}]");
            var automationName = AutomationProperties.GetName(control);
            if (!string.IsNullOrEmpty(automationName))
            {
                builder.Append($" automation=\"{EscapeText(automationName)}\"");
            }

            builder.Append($" text=\"{EscapeText(TextOf(control, 6))}\"\n");
        }

        return builder.ToString();
    }

    private static int NearScore(Control control, string target)
    {
        int Score(string value)
        {
            if (value.Length == 0)
            {
                return 0;
            }

            if (value.Contains(target, StringComparison.OrdinalIgnoreCase) || target.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                return Math.Min(value.Length, target.Length);
            }

            var prefix = 0;
            while (prefix < Math.Min(value.Length, target.Length)
                && string.Compare(value, prefix, target, prefix, 1, StringComparison.OrdinalIgnoreCase) == 0)
            {
                prefix++;
            }

            return prefix;
        }

        return Math.Max(Score(control.Name ?? string.Empty),
            Math.Max(Score(AutomationProperties.GetName(control) ?? string.Empty), Score(TextOf(control, 6))));
    }

    private static string EscapeText(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string DescribeTree()
    {
        var builder = new StringBuilder();
        if (Window.Current.Content is FrameworkElement root)
        {
            Walk(root, root, 0, builder);
        }

        return builder.ToString();
    }

    private static void Walk(FrameworkElement element, FrameworkElement root, int depth, StringBuilder builder)
    {
        if (element.Visibility != Visibility.Visible || element.Opacity == 0 || depth > 60 || builder.Length > 400_000)
        {
            return;
        }

        var text = element is TextBlock block && !string.IsNullOrWhiteSpace(block.Text) ? $" \"{block.Text}\"" : string.Empty;
        var bounds = Bounds(element, root);
        builder.Append(' ', depth * 2).Append(Describe(element)).Append($" [{bounds.X:0},{bounds.Y:0} {bounds.Width:0}x{bounds.Height:0}]").Append(text).Append('\n');
        if (element is PasswordBox) return; // Do not inspect native/revealed credential template parts.
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (VisualTreeHelper.GetChild(element, i) is FrameworkElement child)
            {
                Walk(child, root, depth + 1, builder);
            }
        }
    }

    private static string Describe(FrameworkElement element)
    {
        var name = string.IsNullOrEmpty(element.Name) ? string.Empty : $"#{element.Name}";
        var content = element is ContentControl { Content: { } value and not UIElement } ? $" content={value}" : string.Empty;
        var focus = element is Control { FocusState: not FocusState.Unfocused } ? " (focused)" : string.Empty;
        return $"{element.GetType().Name}{name}{content}{focus}";
    }

    /// <summary>The first few TextBlock texts under an element, joined: what a person would read on it.</summary>
    private static string TextOf(DependencyObject element, int limit) => string.Join(" | ", TextsOf(element, limit));

    private static List<string> TextsOf(DependencyObject element, int limit)
    {
        var texts = new List<string>();
        void Collect(DependencyObject node)
        {
            if (texts.Count >= limit || node is PasswordBox)
            {
                return;
            }

            if (node is TextBlock { Text.Length: > 0 } block)
            {
                texts.Add(block.Text);
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Collect(VisualTreeHelper.GetChild(node, i));
            }
        }

        Collect(element);
        return texts;
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement root)
    {
        try
        {
            return element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (ArgumentException)
        {
            return Rect.Empty;
        }
    }

    private static async Task<string> OnUiAsync(Func<string> action)
    {
        var done = new TaskCompletionSource<string>();
        await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
        {
            try
            {
                done.SetResult(action());
            }
            catch (Exception ex)
            {
                done.SetResult($"error: {ex.Message}");
            }
        });
        return await done.Task;
    }

    private static async Task<string> ReadRequestLineAsync(IInputStream input)
    {
        using var reader = new DataReader(input) { InputStreamOptions = InputStreamOptions.Partial };
        var text = new StringBuilder();
        while (!text.ToString().Contains("\r\n\r\n") && text.Length < 16_384)
        {
            var read = await reader.LoadAsync(1024);
            if (read == 0)
            {
                break;
            }

            text.Append(reader.ReadString(read));
        }

        reader.DetachStream();
        var all = text.ToString();
        var end = all.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? all : all.Substring(0, end);
    }

    private static async Task WriteResponseAsync(IOutputStream output, string status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        using var writer = new DataWriter(output);
        writer.WriteBytes(header);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>();
        foreach (var pair in query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split(new[] { '=' }, 2);
            values[Uri.UnescapeDataString(split[0])] = split.Length > 1 ? Uri.UnescapeDataString(split[1].Replace('+', ' ')) : string.Empty;
        }

        return values;
    }
}
#endif
