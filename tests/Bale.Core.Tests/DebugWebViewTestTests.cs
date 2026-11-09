namespace Bale.Core.Tests;

/// <summary>The WebView2 comparison stays debug-only: its code and its WinUI 2 package never reach shipped builds.</summary>
public class DebugWebViewTestTests
{
    [Fact]
    public void RouteAndPackageAreDebugOnly()
    {
        var test = Read("DebugServer.WebViewTest.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", test);
        Assert.Contains("action is \"open-video\" or \"open-blank\" or \"close\";", test);
        Assert.Contains("muted loop", test);

        var project = Read("Bale.Xbox.csproj.txt");
        var reference = project.IndexOf("<PackageReference Include=\"Microsoft.UI.Xaml\"", StringComparison.Ordinal);
        Assert.True(reference > 0);
        var group = project.LastIndexOf("<ItemGroup", reference, StringComparison.Ordinal);
        Assert.StartsWith("<ItemGroup Condition=\"'$(BaleDebugServer)' == 'true'\">", project[group..]);
        Assert.Equal(1, Count(project, "Microsoft.UI.Xaml\""));

        var server = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"GET\", \"/webview-test\")", server);
        Assert.Contains("if (query.Count != 1 || !IsWebViewTestAction(webViewAction))", server);
        Assert.Contains("or \"/webview-test\"))", server);

        var script = Read("bale-debug.sh");
        Assert.Contains("^(open-video|open-blank|close|status)$", script);
    }

    private static int Count(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
