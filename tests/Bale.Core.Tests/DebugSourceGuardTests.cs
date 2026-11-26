namespace Bale.Core.Tests;

/// <summary>
/// Every source file named for debugging is debug-server code from its first line to its last: the debug server and its
/// tools exist only in builds that opt in with BaleDebugServer=true (AGENTS.md), so a Debug file must not reach a
/// production build, whole or in part. A legitimate exception goes into <see cref="Exceptions"/> with its reason.
/// </summary>
public sealed class DebugSourceGuardTests
{
    /// <summary>src-relative path → why it compiles outside DEBUG_SERVER. None today.</summary>
    private static readonly Dictionary<string, string> Exceptions = [];

    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "debug-guard-src");

    [Fact]
    public void EveryDebugSourceFileIsWhollyDebugServerCode()
    {
        var files = Directory.GetFiles(Root, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(Root, file).Replace('\\', '/')).Order().ToArray();
        // The project's glob found the files (85 when this test was written); a broken glob must not pass silently.
        Assert.True(files.Length >= 80, $"only {files.Length} Debug source files found");
        Assert.Contains("Bale.Xbox/Services/DebugServer.cs", files);
        Assert.Contains("Bale.Core/Playback/PlaybackMedia.Debug.cs", files);
        foreach (var file in files.Where(file => !Exceptions.ContainsKey(file)))
        {
            var lines = File.ReadAllLines(Path.Combine(Root, file)).Select(line => line.TrimEnd()).ToArray();
            Assert.True(lines.Length > 0 && lines[0] == "#if DEBUG_SERVER", $"{file} must start with #if DEBUG_SERVER");
            var last = Array.FindLastIndex(lines, line => line.Length > 0);
            var depth = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var directive = lines[i].TrimStart();
                if (directive.StartsWith("#if", StringComparison.Ordinal)) depth++;
                else if (directive.StartsWith("#endif", StringComparison.Ordinal) && --depth == 0)
                    Assert.True(i == last, $"{file}: the opening #if DEBUG_SERVER ends at line {i + 1}, before the end of the file");
                // An #else of the outer guard would be code that only production builds compile.
                else if (depth == 1 && (directive.StartsWith("#else", StringComparison.Ordinal)
                    || directive.StartsWith("#elif", StringComparison.Ordinal)))
                    Assert.Fail($"{file}: line {i + 1} opens a production branch of the file's DEBUG_SERVER guard");
            }
            Assert.True(depth == 0, $"{file}: unbalanced #if/#endif");
        }
        foreach (var (file, reason) in Exceptions)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), file);
            Assert.Contains(file, files);
        }
    }
}
