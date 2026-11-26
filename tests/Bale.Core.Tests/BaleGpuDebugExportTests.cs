using System.Text.RegularExpressions;

namespace Bale.Core.Tests;

/// <summary>
/// bale_gpu's debug-server exports against CI's packaged-DLL lists (ci.yml, "Check FFmpeg native activation and package
/// contents"). Production packages are built only on request, so a probe that loses its Cargo gate would otherwise
/// surface only then: every export gated by the debug-server features (debug-probes, stage-timing) must be in the
/// debug-only list production DLLs may not export, that list must hold nothing else, and the common list nothing gated.
/// </summary>
public sealed partial class BaleGpuDebugExportTests
{
    private static readonly string Source = Path.Combine(AppContext.BaseDirectory, "bale-gpu-src");

    [Fact]
    public void TheDebugOnlyListIsExactlyTheFeatureGatedExports()
    {
        var exports = Exports();
        var gated = exports.Where(export => export.Value).Select(export => export.Key).Order().ToArray();
        Assert.Equal(gated, List("debugOnly").Order());
        // Spot checks of the walk: a gated module (memprobe), a gated function (the peak read) and a production export.
        Assert.True(exports["bale_memprobe_create"]);
        Assert.True(exports["bale_tonemap_peak_read"]);
        Assert.False(exports["bale_mem_account"]);
        Assert.False(exports["bale_tonemap_apply"]);
    }

    [Fact]
    public void TheCommonListNamesUngatedExportsAndTheRemovedOnesAreGone()
    {
        var exports = Exports();
        foreach (var name in List("common"))
            Assert.False(exports.GetValueOrDefault(name, true), $"{name} must be an ungated bale_gpu export");
        foreach (var name in List("removed"))
            Assert.DoesNotContain(name, exports.Keys);
    }

    /// <summary>Every `extern "C"` export in bale_gpu's sources, and whether a debug-server feature gates it.</summary>
    private static Dictionary<string, bool> Exports()
    {
        var exports = new Dictionary<string, bool>();
        Walk(Path.Combine(Source, "lib.rs"), gated: false, exports);
        Assert.NotEmpty(exports);
        return exports;
    }

    /// <summary>One module file: its exports, then the module files it declares (gated if this file or the declaration is).</summary>
    private static void Walk(string file, bool gated, Dictionary<string, bool> exports)
    {
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            if (ExportPattern().Match(lines[i]) is { Success: true } export)
            {
                Assert.True(Attributes(lines, i).Any(line => line.Contains("no_mangle", StringComparison.Ordinal)), export.Groups[1].Value);
                exports.Add(export.Groups[1].Value, gated || Attributes(lines, i).Any(DebugGate));
            }
            else if (ModulePattern().Match(lines[i]) is { Success: true } module)
            {
                var attributes = Attributes(lines, i).ToArray();
                // Unit-test modules (some outside src/, by #[path]) export nothing.
                if (attributes.Contains("#[cfg(test)]")) continue;
                var name = module.Groups[1].Value;
                var directory = Path.GetFileName(file) is "lib.rs" or "mod.rs"
                    ? Path.GetDirectoryName(file)!
                    : Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file));
                var child = attributes.Select(attribute => PathPattern().Match(attribute)).FirstOrDefault(match => match.Success) is { } path
                    ? Path.Combine(Path.GetDirectoryName(file)!, path.Groups[1].Value)
                    : Path.Combine(directory, name + ".rs");
                if (!File.Exists(child)) child = Path.Combine(directory, name, "mod.rs");
                Assert.True(File.Exists(child), $"module {name} declared in {Path.GetFileName(file)}");
                Walk(child, gated || attributes.Any(DebugGate), exports);
            }
        }
    }

    /// <summary>The attribute and comment lines directly above line <paramref name="index"/>.</summary>
    private static IEnumerable<string> Attributes(string[] lines, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#[", StringComparison.Ordinal) && !line.StartsWith("//", StringComparison.Ordinal)) yield break;
            yield return line;
        }
    }

    private static bool DebugGate(string attribute) => attribute.StartsWith("#[cfg(", StringComparison.Ordinal)
        && (attribute.Contains("feature = \"debug-probes\"", StringComparison.Ordinal)
            || attribute.Contains("feature = \"stage-timing\"", StringComparison.Ordinal));

    /// <summary>One of the CI step's PowerShell lists (<c>$common</c>, <c>$debugOnly</c>, <c>$removed</c>).</summary>
    private static string[] List(string name)
    {
        var workflow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ci.yml.txt"));
        var start = workflow.IndexOf($"${name} = @(", StringComparison.Ordinal);
        Assert.True(start >= 0, name);
        var list = workflow[start..workflow.IndexOf(')', start)];
        var names = SymbolPattern().Matches(list).Select(match => match.Groups[1].Value).ToArray();
        Assert.NotEmpty(names);
        Assert.Equal(names.Length, names.Distinct().Count());
        return names;
    }

    [GeneratedRegex(@"^\s*pub (?:unsafe )?extern ""C"" fn (bale_[a-z0-9_]+)\(")]
    private static partial Regex ExportPattern();

    [GeneratedRegex(@"^\s*(?:pub(?:\([a-z]+\))?\s+)?mod ([a-z0-9_]+);")]
    private static partial Regex ModulePattern();

    [GeneratedRegex("'(bale_[a-z0-9_]+)'")]
    private static partial Regex SymbolPattern();

    [GeneratedRegex(@"^#\[path = ""([^""]+)""\]$")]
    private static partial Regex PathPattern();
}
