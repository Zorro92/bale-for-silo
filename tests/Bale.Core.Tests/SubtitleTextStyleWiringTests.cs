using System.Text.RegularExpressions;

namespace Bale.Core.Tests;

/// <summary>
/// Text subtitles in the subtitle appearance: the player loads SubRip/WebVTT with Bale.Core's SubtitleTextStyle through
/// bale_gpu's styled text load (subtitle ABI 4). The Xbox app compiles only on Windows, so the player's side and the C
/// ABI on both sides are pinned by source here; the mapping itself is SubtitleAppearanceTests, the Rust side its own
/// tests (subs::convert, tests/subs_warp.rs).
/// </summary>
public class SubtitleTextStyleWiringTests
{
    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    private static string Section(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, from);
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, to);
        return text[start..end];
    }

    private static void InOrder(string text, params string[] parts)
    {
        var at = -1;
        foreach (var part in parts)
        {
            var next = text.IndexOf(part, at + 1, StringComparison.Ordinal);
            Assert.True(next > at, "out of order or missing: " + part);
            at = next;
        }
    }

    [Fact]
    public void TextSubtitlesLoadInTheAppearanceThroughTheStyledAbi()
    {
        var subtitles = Read("PlayerPage.Subtitles.cs.txt");
        InOrder(Section(subtitles, "private async Task LoadPlanSubtitlesAsync(", "internal int LoadSubtitles("),
            "await Task.WhenAny(attempt.SubtitleAppearanceRead, Task.Delay(SubtitleAppearanceWait, token));",
            "var style = SubtitleStyle(attempt.Client);", "overlay.LoadText(source.Format, loading.Text, style)");
        Assert.Contains("overlay.LoadText(format, utf8, SubtitleStyle(attempt.Client))", subtitles);
        Assert.Contains("SubtitleTextStyle.For(settings.EffectiveAppearance(settings.MatchDevice ? SystemCaptions.Read() : null))", subtitles);
        Assert.Contains("attempt.SubtitleAppearanceRead = ReadSubtitleAppearanceAsync(client);", Read("PlayerPage.cs.txt"));

        var overlay = Read("SubtitleOverlay.cs.txt");
        Assert.Contains("private const uint ExpectedAbi = 4;", overlay);
        Assert.Contains("pub const SUB_ABI_VERSION: u32 = 4;", Read("subs_mod.rs.txt"));
        Assert.Contains("bale_sub_overlay_load_text_styled(nint overlay, uint format, byte* data, nuint length, BaleSubTextStyle* style, "
            + "byte* fontName, nuint fontNameLength)", overlay);
        Assert.DoesNotContain("bale_sub_overlay_load_text(", overlay);

        // The C# struct and Rust's subs::convert::TextStyle list the same fields, in the same order and widths (the C ABI).
        var csharp = Regex.Matches(Section(overlay, "internal struct BaleSubTextStyle", "public static BaleSubTextStyle From("),
                @"public (uint|float) ([A-Za-z, ]+);")
            .SelectMany(match => match.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries)
                .Select(name => (Name: Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant(), Type: match.Groups[1].Value == "uint" ? "u32" : "f32")))
            .ToList();
        var rust = Regex.Matches(Section(Read("ass_writer.rs.txt"), "pub struct TextStyle {", "\n}"), @"pub ([a-z_]+): (u32|f32),")
            .Select(match => (Name: match.Groups[1].Value, Type: match.Groups[2].Value)).ToList();
        Assert.Equal(9, rust.Count);
        Assert.Equal(rust, csharp);
        Assert.Contains("#[repr(C)]\n#[derive(Clone, Copy, Debug, PartialEq)]\npub struct TextStyle {", Read("ass_writer.rs.txt"));
    }
}
