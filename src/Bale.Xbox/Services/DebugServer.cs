using System.Text;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Gaming.Input;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Input.Preview.Injection;
using Windows.UI.Xaml;
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
///   GET  /tree            the visible element tree with bounds and text
///
/// Only started on sideloaded builds (see App). Anyone on the LAN can reach it while it runs, so it
/// must never ship in a Store build.
/// </summary>
internal sealed class DebugServer
{
    public const int Port = 8790;

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

            case ("GET", "/tree"):
                return ("200 OK", await OnUiAsync(DescribeTree));

            default:
                return ("404 Not Found", "routes: GET /log?since=N, POST /press/{up|down|left|right|a|b|menu|view|lb|rb|lt|rt}, POST /key/{VirtualKey}, POST /type?text=, GET /focus, GET /tree\n");
        }
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

        return $"typed \"{text}\"";
    }

    private static string DescribeFocus()
    {
        if (FocusManager.GetFocusedElement() is not FrameworkElement element)
        {
            return "nothing focused";
        }

        return $"{Describe(element)} text=\"{TextOf(element, 6)}\"";
    }

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
    private static string TextOf(DependencyObject element, int limit)
    {
        var texts = new List<string>();
        void Collect(DependencyObject node)
        {
            if (texts.Count >= limit)
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
        return string.Join(" | ", texts);
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
