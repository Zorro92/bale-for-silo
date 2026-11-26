#if DEBUG_SERVER
using System.Text.Json;

namespace Bale.Core.Native;

/// <summary>
/// Debug only: the go/no-go spike for moving the HTTP streams to Rust (docs/rust-port.md). Downloads
/// <paramref name="url"/> with bale_core's Rust HTTP + TLS stack for <paramref name="limitMilliseconds"/>
/// after the first byte and returns its report (status, throughput, the thread's CPU time) as JSON.
/// Blocks the calling thread. The URL and headers are credentials: the report never contains them.
/// </summary>
public static class RustHttpProbe
{
    public static string Run(Uri url, IEnumerable<KeyValuePair<string, string>> headers, int limitMilliseconds) =>
        BaleCoreNative.Call("http_probe.run", writer =>
        {
            writer.WriteString("url", url.AbsoluteUri);
            writer.WriteStartArray("headers");
            foreach (var (name, value) in headers)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(name);
                writer.WriteStringValue(value);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteNumber("limit_ms", limitMilliseconds);
        }, BaleCoreJson.Default.JsonElement).GetRawText();
}
#endif
