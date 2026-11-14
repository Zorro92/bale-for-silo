using System.Net;
using System.Net.Sockets;

namespace Bale.Core.Connection;

/// <summary>
/// Socket options for Bale's own HTTP connections (API, media ranges, speed test). On a remote route one
/// connection's throughput is capped near receive window ÷ round trip: the console measured ~17 Mbps on a
/// single connection where a desktop reached ~250 Mbps on the same path. A larger receive buffer lets the
/// window grow. Null keeps the OS default. Read when each connection is opened.
/// </summary>
public static class NetworkTuning
{
    private static int _receiveBufferBytes;

    /// <summary>SO_RCVBUF for new connections, or null for the OS default.</summary>
    public static int? SocketReceiveBufferBytes
    {
        get => Volatile.Read(ref _receiveBufferBytes) is > 0 and var bytes ? bytes : null;
        set => Volatile.Write(ref _receiveBufferBytes, value is > 0 ? value.Value : 0);
    }

    /// <summary>The main connection's handler: SocketsHttpHandler with the tuned connect callback.</summary>
    public static SocketsHttpHandler CreateHandler(DecompressionMethods decompression = DecompressionMethods.None) => new()
    {
        AutomaticDecompression = decompression,
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (SocketReceiveBufferBytes is { } bytes) socket.ReceiveBufferSize = bytes;
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
