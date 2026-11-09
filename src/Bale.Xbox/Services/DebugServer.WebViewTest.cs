#if DEBUG_SERVER
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls.Primitives;
using WebView2 = Microsoft.UI.Xaml.Controls.WebView2;

namespace Bale.Xbox.Services;

/// <summary>
/// Input-idle comparison: does a playing WebView2 (the engine Jellyfin's Xbox app runs in) keep the
/// console out of the slow state that starts ~10 s after the last input (docs/player-plan.md)?
/// Opens a small popup that loops a muted public sample, or a blank page. The slowdown is
/// console-wide, so `upload-probe` in Bale measures it while the popup is open.
/// WinUI 2 is referenced only in debug-server builds (Bale.Xbox.csproj).
/// </summary>
internal sealed partial class DebugServer
{
    internal const string WebViewTestUsage =
        "usage: GET /webview-test or POST /webview-test?action={open-video|open-blank|close}\n";

    // Chromium's own media test sample (the playtest suite's first clip), 1280x720 H.264, looped.
    private const string WebViewTestVideo =
        "https://raw.githubusercontent.com/chromium/chromium/main/media/test/data/bear-1280x720.mp4";

    private static Popup? _webViewPopup;
    private static WebView2? _webView;
    private static string _webViewMode = "closed";

    private static bool IsWebViewTestAction(string? action) =>
        action is "open-video" or "open-blank" or "close";

    private static async Task<string> RunWebViewTestAsync(string action)
    {
        CloseWebViewTest();
        if (action == "close")
            return "webview: closed\n";

        var view = new WebView2 { Width = 640, Height = 360, IsTabStop = false };
        _webView = view;
        _webViewPopup = new Popup { Child = view, HorizontalOffset = 48, VerticalOffset = 48, IsOpen = true };
        _webViewMode = action;
        await view.EnsureCoreWebView2Async();
        view.CoreWebView2.NavigateToString(action == "open-video"
            ? "<!doctype html><body style=\"margin:0;background:#000\">"
              + $"<video src=\"{WebViewTestVideo}\" autoplay muted loop playsinline style=\"width:100%;height:100%\"></video></body>"
            : "<!doctype html><body style=\"margin:0;background:#123;color:#fff;font:32px sans-serif\">blank</body>");
        return "webview: " + action + "\n";
    }

    private static async Task<string> DescribeWebViewTestAsync()
    {
        if (_webView?.CoreWebView2 is not { } core)
            return "webview: " + _webViewMode + "\n";
        var state = await core.ExecuteScriptAsync(
            "(() => { const v = document.querySelector('video'); if (!v) return 'no video';"
            + " return (v.paused ? 'paused' : 'playing') + ' t=' + v.currentTime.toFixed(1)"
            + " + ' ' + v.videoWidth + 'x' + v.videoHeight + ' error=' + (v.error ? v.error.code : 0); })()");
        return "webview: " + _webViewMode + ", " + state + "\n";
    }

    private static void CloseWebViewTest()
    {
        if (_webViewPopup is { } popup)
        {
            popup.IsOpen = false;
            popup.Child = null;
        }
        // WinUI 2 projects IClosable as IDisposable; release the browser process with the popup.
        (_webView as IDisposable)?.Dispose();
        _webView = null;
        _webViewPopup = null;
        _webViewMode = "closed";
    }

    /// <summary>Like OnUiAsync, for work that awaits on the UI thread (WebView2 setup and scripts).</summary>
    private static async Task<string> OnUiTaskAsync(Func<Task<string>> action)
    {
        var done = new TaskCompletionSource<string>();
        await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
        {
            try
            {
                done.SetResult(await action());
            }
            catch (Exception ex)
            {
                done.SetResult($"error: {ex.Message}");
            }
        });
        return await done.Task;
    }
}
#endif
