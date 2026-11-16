using Bale.Core.Native;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>
/// The Rust HLS reader's child-URI rule (hls.rs resolve_child) against the C# rule Windows' HLS
/// downloads use (.NET Uri resolution + PlaybackMediaUrl.IsAllowedChild). Rust may refuse more (it
/// never escapes), never less, and resolves what both accept to the same URL.
/// </summary>
public sealed class RustHlsChildUrlDifferentialTests
{
    private static string? Rust(string @base, string reference, string server) =>
        BaleCoreNative.Call("hls.resolve_child", writer =>
        {
            writer.WriteString("base", @base);
            writer.WriteString("reference", reference);
            writer.WriteString("server", server);
        }, BaleCoreJson.Default.String);

    private static string? CSharp(string @base, string reference, string server) =>
        Uri.TryCreate(new Uri(@base), reference, out var uri) && PlaybackMediaUrl.IsAllowedChild(uri, new Uri(server))
            ? uri.OriginalString.Contains("://", StringComparison.Ordinal) ? uri.AbsoluteUri : null
            : null;

    public static TheoryData<string, string, string> Cases()
    {
        var data = new TheoryData<string, string, string>();
        string[] servers = ["https://silo.example:8443", "https://silo.example:8443/base", "http://10.0.0.5:8096"];
        string[] references =
        [
            "seg_00001.ts", "seg_00001.ts?st=abc&seek=1", "video/index.m3u8?st=t", "init.mp4", "?r=video",
            "/api/v2/stream/s/seg.ts", "/base/api/v2/stream/s/seg.ts", "/api/v2/playback/transcode/x/seg.ts",
            "/base/api/v2/playback/transcode/x/seg.ts", "/api/v2/users/me", "/base/api/v2/users/me",
            "../seg.ts", "../../../users/me", "./seg.ts", "%2e%2e/seg.ts", "a%2Fb.ts", "a%2fb.ts", "a%5cb.ts",
            "a\\b.ts", "seg.ts#frag", "https://evil.example/api/v2/stream/s/a.ts",
            "https://silo.example:8443/api/v2/stream/s/a.ts", "https://silo.example:8443/base/api/v2/stream/s/a.ts",
            "http://silo.example:8443/api/v2/stream/s/a.ts", "https://SILO.example:8443/api/v2/stream/s/a.ts",
            "https://silo.example/api/v2/stream/s/a.ts", "https://user@silo.example:8443/api/v2/stream/s/a.ts",
            "http://10.0.0.5:8096/api/v2/stream/s/a.ts", "http://10.0.0.5/api/v2/stream/s/a.ts",
            "//evil.example/api/v2/stream/s/a.ts", "seg%20one.ts", "seg one.ts",
        ];
        foreach (var server in servers)
        {
            var root = server.TrimEnd('/');
            foreach (var @base in new[] { root + "/api/v2/stream/s/master.m3u8?st=t", root + "/api/v2/playback/transcode/x/index.m3u8" })
                foreach (var reference in references)
                    data.Add(@base, reference, server);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RustNeverAcceptsWhatCSharpRefuses(string @base, string reference, string server)
    {
        var rust = Rust(@base, reference, server);
        var csharp = CSharp(@base, reference, server);
        if (rust is not null) Assert.Equal(csharp, rust, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinarySiloReferencesAreAcceptedByBoth()
    {
        const string server = "https://silo.example:8443";
        const string @base = server + "/api/v2/stream/s/master.m3u8?st=t";
        foreach (var reference in new[] { "seg_00001.ts?st=abc", "video/index.m3u8?st=t", "/api/v2/stream/s/init.mp4", "?r=v" })
        {
            Assert.NotNull(CSharp(@base, reference, server));
            Assert.Equal(CSharp(@base, reference, server), Rust(@base, reference, server));
        }
    }
}
