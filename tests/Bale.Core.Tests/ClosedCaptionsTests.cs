using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Bale.Xbox.Services;
using FFmpegInteropX;

namespace Bale.Core.Tests;

/// <summary>
/// In-band closed captions: bale_gpu's caption decoder (native/bale-gpu src/subs/cc, docs/closed-captions.md) behind
/// ClosedCaptions and SubtitleOverlay.AttachCaptions. The Xbox app compiles only on Windows, so the C ABI on both sides
/// is pinned by source here (the decoder itself is tested in Rust, the overlay side on WARP); with a Linux build of
/// bale_gpu next to the tests (cargo build --release in native/bale-gpu), the wrapper also runs against it.
/// </summary>
public class ClosedCaptionsWiringTests
{
    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    /// <summary>A Rust or C# parameter or return type as its C ABI kind.</summary>
    private static string Kind(string type)
    {
        type = type.Trim();
        if (type.StartsWith('*') || type.EndsWith('*') || type == "nint") return "pointer";
        return type switch
        {
            "u64" or "ulong" => "u64",
            "i64" or "long" => "i64",
            "u32" or "uint" => "u32",
            "i32" or "int" => "i32",
            "usize" or "nuint" => "usize",
            "" or "void" => "void",
            _ => "unknown " + type,
        };
    }

    /// <summary>name → (return kind, parameter kinds) of every <c>bale_cc_*</c> export in bale_gpu's lib.rs.</summary>
    private static Dictionary<string, (string Return, string[] Parameters)> RustExports() =>
        Regex.Matches(Read("bale_gpu_lib.rs.txt"), @"pub (?:unsafe )?extern ""C"" fn (bale_cc_\w+)\(([^)]*)\)(?: -> (\w+))?")
            .ToDictionary(match => match.Groups[1].Value, match => (Kind(match.Groups[3].Value),
                match.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(parameter => Kind(parameter[(parameter.IndexOf(':') + 1)..])).ToArray()));

    /// <summary>The same for the C# imports of <paramref name="source"/>.</summary>
    private static Dictionary<string, (string Return, string[] Parameters)> CSharpImports(string source) =>
        Regex.Matches(source, @"static extern (\w+) (bale_cc_\w+)\(([^)]*)\);")
            .ToDictionary(match => match.Groups[2].Value, match => (Kind(match.Groups[1].Value),
                match.Groups[3].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(parameter => Kind(parameter[..parameter.LastIndexOf(' ')])).ToArray()));

    [Fact]
    public void TheImportsMatchTheExportsAndTheForkKeepsItsTap()
    {
        var rust = RustExports();
        // The fork's tap (bale/README.md "In-band caption packet tap", ABI 1) is exactly this.
        Assert.Equal(("u32", Array.Empty<string>()), (rust["bale_cc_abi_version"].Return, rust["bale_cc_abi_version"].Parameters));
        Assert.Equal(["u64", "pointer", "usize", "i64", "u32", "u32"], rust["bale_cc_push"].Parameters);
        Assert.Equal("i32", rust["bale_cc_push"].Return);
        Assert.Equal(["u64"], rust["bale_cc_flush"].Parameters);
        Assert.Contains("pub const CC_ABI_VERSION: u32 = 1;", Read("bale_gpu_cc_mod.rs.txt"));
        Assert.Contains("internal const uint ExpectedAbi = 1;", Read("ClosedCaptions.cs.txt"));

        // The app's imports: the decoder in ClosedCaptions, the attach (which takes the style) in SubtitleOverlay.
        var imports = CSharpImports(Read("ClosedCaptions.cs.txt"))
            .Concat(CSharpImports(Read("SubtitleOverlay.cs.txt")))
            .ToDictionary();
        Assert.Equal(["bale_cc_abi_version", "bale_cc_attach", "bale_cc_create", "bale_cc_destroy", "bale_cc_detach",
            "bale_cc_register", "bale_cc_seen", "bale_cc_select", "bale_cc_stats"], imports.Keys.Order());
        foreach (var (name, import) in imports)
        {
            Assert.True(rust.TryGetValue(name, out var export), name);
            Assert.Equal(export.Return, import.Return);
            Assert.Equal(export.Parameters, import.Parameters);
        }
        // Only push and flush are left to the fork.
        Assert.Equal(["bale_cc_flush", "bale_cc_push"], rust.Keys.Except(imports.Keys).Order());

        // CI's packaged-DLL check requires every export.
        var required = Read("ci.yml.txt");
        foreach (var name in rust.Keys) Assert.Contains($"'{name}'", required);
    }

    [Fact]
    public void TheCountersAndSeenBitsMatchTheRustSide()
    {
        var csharp = Read("ClosedCaptions.cs.txt");
        var rust = Read("bale_gpu_cc_mod.rs.txt");
        var start = csharp.IndexOf("internal struct BaleCcStats", StringComparison.Ordinal);
        var fields = Regex.Matches(csharp[start..csharp.IndexOf('}', start)], @"public (ulong|uint) ([A-Za-z, ]+);")
            .SelectMany(match => match.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries)
                .Select(name => (Name: Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant(),
                    Type: match.Groups[1].Value == "ulong" ? "u64" : "u32")))
            .ToList();
        var rustStart = rust.IndexOf("pub struct BaleCcStats {", StringComparison.Ordinal);
        var rustFields = Regex.Matches(rust[rustStart..rust.IndexOf("\n}", rustStart, StringComparison.Ordinal)], @"pub ([a-z_]+): (u64|u32),")
            .Select(match => (Name: match.Groups[1].Value, Type: match.Groups[2].Value)).ToList();
        Assert.Equal(8, rustFields.Count);
        Assert.Equal(rustFields, fields);
        Assert.Contains("#[repr(C)]\n#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]\npub struct BaleCcStats {", rust);
        Assert.Equal(48, Marshal.SizeOf<BaleCcStats>());

        foreach (var (rustName, value) in new[] { ("SEEN_CC1", "1 << 0"), ("SEEN_CC2", "1 << 1"), ("SEEN_CC3", "1 << 2"),
            ("SEEN_CC4", "1 << 3"), ("SEEN_CC_DATA", "1 << 8"), ("SEEN_DTVCC", "1 << 9") })
            Assert.Contains($"pub const {rustName}: u32 = {value};", rust);
        Assert.Equal((1u, 2u, 4u, 8u, 256u, 512u), (ClosedCaptions.SeenCc1, ClosedCaptions.SeenCc2, ClosedCaptions.SeenCc3,
            ClosedCaptions.SeenCc4, ClosedCaptions.SeenCcData, ClosedCaptions.SeenDtvcc));
        Assert.Contains("pub const FLAG_PLAIN: u32 = 1;", Read("bale_gpu_cc_ass.rs.txt"));
        Assert.Contains("plain ? 1u : 0u", Read("SubtitleOverlay.cs.txt"));
    }

    [Fact]
    public void TheSinkOptionTakesTheForksTokenRange()
    {
        var config = new MediaSourceConfig();
        FfmpegSource.ApplyCaptionSink(config, 0);
        FfmpegSource.ApplyCaptionSink(config, ClosedCaptions.MaxToken + 1);
        Assert.Empty(config.FFmpegOptions);
        FfmpegSource.ApplyCaptionSink(config, 42);
        Assert.Equal("42", config.FFmpegOptions["bale_cc_sink"]);
        FfmpegSource.ApplyCaptionSink(config, ClosedCaptions.MaxToken);
        Assert.Equal("999999999", config.FFmpegOptions["bale_cc_sink"]);

        // The player hands each mount's own token over (PlayerClosedCaptionsWiringTests); nothing is left to do.
        Assert.Contains("FfmpegSource.ApplyCaptionSink(config, token);", Read("PlayerPage.Captions.cs.txt"));
        Assert.DoesNotContain("TODO(captions)", Read("PlayerPage.Media.cs.txt"));
    }
}

/// <summary>
/// The wrapper against a Linux build of bale_gpu (the test project copies native/bale-gpu's release libbale_gpu.so as
/// bale_gpu.dll when there is one; CI's .NET job has none and skips), with the fork's tap played by this test.
/// </summary>
public class ClosedCaptionsNativeTests
{
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe int bale_cc_push(ulong token, byte* data, nuint length, long pts100ns, uint codec, uint nalLengthSize);

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern int bale_cc_flush(ulong token);

    private const int NotFound = unchecked((int)0x80070490);
    private const int Closed = unchecked((int)0x80000013);

    private static byte Parity(int value)
    {
        var b = (byte)(value & 0x7F);
        return (byte)(int.PopCount(b) % 2 == 0 ? b | 0x80 : b);
    }

    /// <summary>
    /// An Annex B H.264 access unit: an A/53 SEI with this frame's pair of a pop-on "HI" (shown at frame 10, erased at frame
    /// 40, control codes twice) in field 1 as CC1, or in field 2 as CC3 (its miscellaneous codes on 15, the field-2 form),
    /// and a null pair in the other field, then a slice.
    /// </summary>
    private static byte[] Packet(int frame, bool field2)
    {
        var misc = field2 ? 0x15 : 0x14;
        int[] pair = frame switch
        {
            0 or 1 => [misc, 0x20],
            2 or 3 => [0x14, 0x60],
            4 => ['H', 'I'],
            10 or 11 => [misc, 0x2F],
            40 or 41 => [misc, 0x2C],
            _ => [0, 0],
        };
        byte[] cc = field2
            ? [0xFC, 0x80, 0x80, 0xFD, Parity(pair[0]), Parity(pair[1])]
            : [0xFC, Parity(pair[0]), Parity(pair[1]), 0xFD, 0x80, 0x80];
        byte[] payload = [0xB5, 0x00, 0x31, (byte)'G', (byte)'A', (byte)'9', (byte)'4', 0x03, 0x42, 0xFF, .. cc, 0xFF];
        return [0, 0, 0, 1, 0x06, 0x04, (byte)payload.Length, .. payload, 0x80, 0, 0, 1, 0x65, 0x88, 0x84, 0x21];
    }

    private static unsafe int Push(ulong token, int frame, bool field2 = false)
    {
        var data = Packet(frame, field2);
        fixed (byte* bytes = data)
            return bale_cc_push(token, bytes, (nuint)data.Length, frame * 333_333L, 1, 0);
    }

    private static bool HasNativeBuild() =>
        OperatingSystem.IsLinux() && File.Exists(Path.Combine(AppContext.BaseDirectory, "bale_gpu.dll"));

    [Fact]
    public void TheWrapperDrivesTheDecoderTheTapFeeds()
    {
        if (!HasNativeBuild()) return;
        var captions = ClosedCaptions.Create();
        Assert.NotNull(captions);
        const ulong token = 0xCC_0C5;
        Assert.Equal(NotFound, Push(token, 0));
        Assert.Equal(0, captions.Register(token));
        for (var frame = 0; frame < 60; frame++) Assert.Equal(0, Push(token, frame));
        Assert.Equal(ClosedCaptions.SeenCc1 | ClosedCaptions.SeenCcData, captions.Seen());
        Assert.True(captions.TryGetStats(out var stats));
        Assert.Equal((60ul, 60ul, 1ul, 0u), (stats.Packets, stats.CaptionPackets, stats.Cues, stats.Late));
        Assert.Equal(captions.Seen(), stats.Seen);
        Assert.Equal(0, bale_cc_flush(token));
        Assert.True(captions.TryGetStats(out stats));
        Assert.Equal(1u, stats.Flushes);
        Assert.Equal(0, captions.Select(3));
        Assert.True(captions.Select(5) < 0);
        Assert.Equal(0, captions.Detach());
        captions.Dispose();
        Assert.Equal(NotFound, Push(token, 0));
        Assert.Equal(Closed, captions.Register(token));
        Assert.Equal(0u, captions.Seen());
        Assert.False(captions.TryGetStats(out _));
        captions.Dispose();
    }

    /// <summary>
    /// CC3 is field 2 (cc_type 1): the player's pick of the CC3 row (ClosedCaptions.Select) decodes a field-2 stream, which
    /// CC1 only sees; a field-1 stream has nothing on CC3.
    /// </summary>
    [Fact]
    public void TheCc3RowDecodesField2()
    {
        if (!HasNativeBuild()) return;
        using var captions = ClosedCaptions.Create();
        Assert.NotNull(captions);
        const ulong token = 0xCC_0C3;
        Assert.Equal(0, captions.Register(token));
        // A new decoder is on CC1: the field-2 caption is seen as CC3 and shows nothing.
        for (var frame = 0; frame < 60; frame++) Assert.Equal(0, Push(token, frame, field2: true));
        Assert.Equal(ClosedCaptions.SeenCc3 | ClosedCaptions.SeenCcData, captions.Seen());
        Assert.True(captions.TryGetStats(out var stats));
        Assert.Equal((0ul, 0ul), (stats.Pairs, stats.Cues));

        // The CC3 row, then the same stream again from the start (a seek): one caption, from field 2.
        Assert.Equal(0, captions.Select(3));
        Assert.Equal(0, bale_cc_flush(token));
        for (var frame = 0; frame < 60; frame++) Assert.Equal(0, Push(token, frame, field2: true));
        Assert.True(captions.TryGetStats(out stats));
        Assert.Equal((1ul, 0u), (stats.Cues, stats.Late));
        Assert.True(stats.Pairs > 0);

        // A new stream on the same decoder keeps CC3, and a field-1 caption is not CC3's.
        Assert.Equal(0, captions.Register(token));
        for (var frame = 0; frame < 60; frame++) Assert.Equal(0, Push(token, frame));
        Assert.Equal(ClosedCaptions.SeenCc1 | ClosedCaptions.SeenCcData, captions.Seen());
        Assert.True(captions.TryGetStats(out stats));
        Assert.Equal((0ul, 0ul), (stats.Pairs, stats.Cues));
    }
}
