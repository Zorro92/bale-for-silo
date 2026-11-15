using Bale.Core.Native;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

// The C# side of the Rust bridge (native/bale-core, docs/rust-port.md).
public class BaleCoreNativeTests
{
    [Fact]
    public void CallsRoundTripThroughRust()
    {
        Assert.Equal("hevc", SoftwareDecodePolicy.NormalizeCodec(" H.265 "));
        Assert.Equal("pcm", SoftwareDecodePolicy.NormalizeCodec("PCM_S24LE"));
        Assert.Null(SoftwareDecodePolicy.NormalizeCodec(null));
        Assert.Null(SoftwareDecodePolicy.NormalizeCodec(" "));
    }

    [Fact]
    public void LowerCasesLikeToLowerInvariant()
    {
        foreach (var name in new[] { "ΣΑΣ", "İX", "Ǆ", "ẞ" })
        {
            Assert.Equal(name.Trim().ToLowerInvariant(), SoftwareDecodePolicy.NormalizeCodec(name));
        }
    }

    [Fact]
    public void AnUnknownFunctionThrowsWithoutEchoingItsArgument()
    {
        var error = Assert.Throws<BaleCoreException>(() => BaleCoreNative.Call("no.such_function", "secret-value",
            BaleCoreJson.Default.String, BaleCoreJson.Default.String));
        Assert.Contains("no.such_function", error.Message);
        Assert.DoesNotContain("secret-value", error.Message);
    }
}
