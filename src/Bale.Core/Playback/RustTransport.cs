using System.Runtime.InteropServices;
using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>The Rust media transport's shared HTTP agent (native/bale-core/src/http.rs).</summary>
public static class RustTransport
{
    /// <summary>
    /// Opens a spare pooled Rust connection to <paramref name="server"/>'s host in the background, via the
    /// public health route. The C# client's pool is kept warm by every API call; the Rust pool only by
    /// media, and a progressive remux's connection closes at each seek, so a playback start, a seek or a
    /// reconnect would otherwise pay TCP and TLS handshakes (~0.4 s at the console's 150 ms RTT). Idle
    /// connections live 60 s, as the C# handler's do. Never throws.
    /// </summary>
    public static unsafe void WarmConnection(Uri server)
    {
        try
        {
            BaleCoreNative.EnsureLoaded();
            var url = System.Text.Encoding.UTF8.GetBytes(new Uri(server, "/api/v1/health").AbsoluteUri);
            fixed (byte* pointer = url) bale_core_http_warm(pointer, (nuint)url.Length);
        }
        catch (Exception)
        {
            // Only a head start; the next request still connects on its own.
        }
    }

    [DllImport("bale_core", ExactSpelling = true)] private static extern unsafe int bale_core_http_warm(byte* url, nuint length);
}
