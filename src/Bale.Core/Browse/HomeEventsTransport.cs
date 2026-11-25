using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;

namespace Bale.Core.Browse;

/// <summary>What a ticket mint answered: the ticket, or the HTTP status (null: no answer) and its Retry-After.</summary>
internal sealed record HomeEventsMint(EventsSocketTicket? Ticket, int? Status = null, long? RetryAfterMs = null);

/// <summary>One open events socket.</summary>
internal interface IHomeEventsConnection : IDisposable
{
    /// <summary>The protocol the server picked.</summary>
    string? SubProtocol { get; }

    /// <summary>The close code once <see cref="ReceiveAsync"/> has returned null; null when no close frame came.</summary>
    int? CloseCode { get; }

    /// <summary>The next text frame, or null once the socket has ended. Throws only when cancelled.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);

    Task SendAsync(string text, CancellationToken cancellationToken);

    /// <summary>Best effort: a going-away close frame, then the socket is released.</summary>
    Task CloseAsync();
}

/// <summary>How Home's subscription reaches the server: the ticket mint and the socket.</summary>
internal interface IHomeEventsTransport
{
    Task<HomeEventsMint> MintAsync(CancellationToken cancellationToken);

    /// <summary>Opens the socket at <paramref name="path"/> below the server's base URL; a refusal throws <see cref="HomeEventsConnectException"/>.</summary>
    Task<IHomeEventsConnection> ConnectAsync(string path, IReadOnlyList<string> protocols, CancellationToken cancellationToken);
}

/// <summary>The upgrade did not open a socket: refused with HTTP <see cref="Status"/>, or failed with no answer (null).</summary>
internal sealed class HomeEventsConnectException(int? status, Exception inner) : Exception("The events socket did not open.", inner)
{
    public int? Status { get; } = status;
}

/// <summary>
/// The real transport: <c>POST /api/v2/events/ws-ticket</c> through the client's own request pipeline (bearer refresh and
/// the profile's identity and PIN proof, as every API call), and a <see cref="ClientWebSocket"/> whose only credential is
/// the ticket in <c>Sec-WebSocket-Protocol</c> (docs/realtime-api.md, "Session-bound socket handshake").
/// </summary>
internal sealed class SiloHomeEventsTransport(SiloClient client, string profileId) : IHomeEventsTransport
{
    public async Task<HomeEventsMint> MintAsync(CancellationToken cancellationToken)
    {
        // The ticket binds the profile and its PIN proof; a profile switch meanwhile fails the request instead of minting
        // for the old one (the same fence as playback's requests).
        if (client.CaptureProfileScope(profileId) is not { } scope) return new(null);
        try
        {
            var ticket = await client.Api.Api.V2.Events.WsTicket.PostAsync(config =>
            {
                config.Options.Add(scope);
                // The subscription backs off by itself, Retry-After included (home_events.rs). Kiota's own retries would
                // repeat a 503 the server sends when it cannot check the credential before Bale ever saw it.
                config.Options.Add(new RetryHandlerOption { MaxRetry = 0 });
            }, cancellationToken).ConfigureAwait(false);
            return ticket is null ? new(null, 200) : new(ticket, 200);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiException error) when (error.ResponseStatusCode > 0)
        {
            return new(null, error.ResponseStatusCode, RetryAfterMs(error.ResponseHeaders, DateTimeOffset.UtcNow));
        }
        catch (Exception)
        {
            // No answer: the network, a timeout, a profile switched meanwhile, or a body that could not be read.
            return new(null);
        }
    }

    public async Task<IHomeEventsConnection> ConnectAsync(string path, IReadOnlyList<string> protocols, CancellationToken cancellationToken)
    {
        // No cookie, no redirect: the ticket is the only credential, and it goes only where the API goes.
        var handler = NetworkTuning.CreateHandler();
        handler.AllowAutoRedirect = false;
        handler.UseCookies = false;
        var invoker = new HttpMessageInvoker(handler);
        var socket = new ClientWebSocket();
        try
        {
            foreach (var protocol in protocols) socket.Options.AddSubProtocol(protocol);
            socket.Options.CollectHttpResponseDetails = true;
            // The server pings every 20 s and drops a peer that misses a pong for 10 s (ws_shared.go); the same here, so a
            // connection that silently died ends within half a minute instead of at its lifetime.
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
            await socket.ConnectAsync(Endpoint(client.Server, path), invoker, cancellationToken).ConfigureAwait(false);
            return new SocketConnection(socket, invoker);
        }
        catch (Exception error)
        {
            var status = (int)socket.HttpStatusCode;
            socket.Dispose();
            invoker.Dispose();
            if (cancellationToken.IsCancellationRequested) throw;
            throw new HomeEventsConnectException(status > 0 ? status : null, error);
        }
    }

    /// <summary>
    /// <paramref name="path"/> (with its query) below the server's base URL, as every API request is
    /// (<see cref="ServerAddress.ToBaseUrl"/>), on <c>ws</c>/<c>wss</c> for <c>http</c>/<c>https</c>. Never user info: the
    /// ticket is the socket's only credential (HomeWatchStateRealtime.swift refuses a URL with one).
    /// </summary>
    internal static Uri Endpoint(Uri server, string path)
    {
        var query = path.IndexOf('?');
        var builder = new UriBuilder(server)
        {
            Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            UserName = string.Empty,
            Password = string.Empty,
            Fragment = string.Empty,
        };
        builder.Path = builder.Path.TrimEnd('/') + "/" + (query < 0 ? path : path[..query]);
        builder.Query = query < 0 ? string.Empty : path[(query + 1)..];
        return builder.Uri;
    }

    /// <summary>"Retry-After" in seconds or as an HTTP date, in milliseconds; null when absent or unreadable.</summary>
    internal static long? RetryAfterMs(IDictionary<string, IEnumerable<string>>? headers, DateTimeOffset now)
    {
        var value = headers?.FirstOrDefault(header => header.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
            .Value?.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return Math.Min(seconds, long.MaxValue / 1000) * 1000;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            return Math.Max(0, (long)(at - now).TotalMilliseconds);
        return null;
    }

    private sealed class SocketConnection(ClientWebSocket socket, HttpMessageInvoker invoker) : IHomeEventsConnection
    {
        /// <summary>HomeWatchStateRealtime.swift <c>maximumMessageSize</c>; the server's frames are far smaller.</summary>
        private const int MaxMessageBytes = 64 * 1024;

        private readonly byte[] _buffer = new byte[8 * 1024];
        private readonly ArrayBufferWriter<byte> _message = new(1024);
        private int _released;

        public string? SubProtocol => socket.SubProtocol;

        public int? CloseCode { get; private set; }

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            try
            {
                _message.Clear();
                while (true)
                {
                    var result = await socket.ReceiveAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        CloseCode = (int?)socket.CloseStatus;
                        // The server waits up to a second for the close reply (docs/realtime-api.md, "Close codes").
                        await CloseAsync(WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
                        return null;
                    }
                    if (_message.WrittenCount + result.Count > MaxMessageBytes)
                    {
                        CloseCode = (int)WebSocketCloseStatus.MessageTooBig;
                        await CloseAsync(WebSocketCloseStatus.MessageTooBig).ConfigureAwait(false);
                        return null;
                    }
                    _message.Write(_buffer.AsSpan(0, result.Count));
                    if (!result.EndOfMessage) continue;
                    // The protocol is JSON text; a binary frame is not part of it.
                    if (result.MessageType == WebSocketMessageType.Text) return Encoding.UTF8.GetString(_message.WrittenSpan);
                    _message.Clear();
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // No close frame (1006 at the client): the network or the server went away, or the keep-alive gave up.
                CloseCode = null;
                return null;
            }
        }

        public Task SendAsync(string text, CancellationToken cancellationToken) =>
            socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, cancellationToken);

        public Task CloseAsync() => CloseAsync(WebSocketCloseStatus.EndpointUnavailable);

        private async Task CloseAsync(WebSocketCloseStatus status)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseOutputAsync(status, null, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Best effort: the socket is released either way.
            }
            finally
            {
                socket.Dispose();
                invoker.Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            socket.Dispose();
            invoker.Dispose();
        }
    }
}
