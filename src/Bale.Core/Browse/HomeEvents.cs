using System.Buffers;
using System.Text.Json;
using Bale.Core.Native;

namespace Bale.Core.Browse;

/// <summary>The timings of Home's events-socket subscription (Rust <c>home_events::Policy</c>); tests shrink them.</summary>
public sealed record HomeEventsPolicy(long BackoffBaseMs, long BackoffMaxMs, long RetryAfterMaxMs, long PollMs, long DebounceMs,
    long MintTimeoutMs, long ConnectTimeoutMs, long SubscribeTimeoutMs, long StableMs, long LifetimeGraceMs);

public enum HomeEventsActionKind { Mint, Connect, Send, Close, Refresh }

/// <summary>
/// What the host does next. <see cref="HomeEventsActionKind.Mint"/> starts attempt <see cref="Generation"/> and ends every
/// older one; <see cref="HomeEventsActionKind.Connect"/> opens its socket at <see cref="Path"/> below the server's base URL
/// offering <see cref="Protocols"/> in order; <see cref="HomeEventsActionKind.Send"/> sends <see cref="Text"/>;
/// <see cref="HomeEventsActionKind.Close"/> ends the attempt; <see cref="HomeEventsActionKind.Refresh"/> re-reads Home.
/// </summary>
public sealed record HomeEventsAction(HomeEventsActionKind Kind, uint Generation = 0, string? Path = null,
    IReadOnlyList<string>? Protocols = null, string? Text = null);

/// <summary>One call's actions in order, when to tick next (host clock, ms), the state, and fixed diagnostics.</summary>
public sealed record HomeEventsOutcome(IReadOnlyList<HomeEventsAction> Actions, long? NextDeadlineMs, bool Live, bool Stopped,
    IReadOnlyList<string> Notes);

/// <summary>
/// Home's watch-state subscription over the server's v2 events socket, one showing of Home: which ticket to mint, when to
/// connect, subscribe, give up or back off, how each close code ends a connection, and when Home must be read again
/// (silo-server <c>165ab2b</c>, <c>f31842a</c>; silo-apple <c>0414ff74</c>, silo-roku <c>c1841ae</c>, Android TV
/// <c>HomeRealtimeCoordinator.kt</c>). The decisions live in Rust (native/bale-core/src/home_events.rs, where the rules and
/// their sources are spelled out); <see cref="HomeWatchState"/> performs the actions. Times are milliseconds of the
/// caller's monotonic clock; HTTP statuses and close codes are passed as received.
/// </summary>
public sealed class HomeEventsMachine : IDisposable
{
    private readonly HomeEventsHandle _handle;

    public HomeEventsMachine()
    {
        BaleCoreNative.EnsureLoaded();
        _handle = BaleCoreHomeEvents.bale_core_home_events_create();
    }

    /// <summary>Home is on screen for <paramref name="profileId"/>; null <paramref name="policy"/> is the app's.</summary>
    public HomeEventsOutcome Start(long now, string profileId, HomeEventsPolicy? policy = null) => Call(now, "start", writer =>
    {
        writer.WriteString("profile_id", profileId);
        if (policy is null)
        {
            writer.WriteNull("policy");
            return;
        }
        writer.WriteStartObject("policy");
        writer.WriteNumber("backoff_base_ms", policy.BackoffBaseMs);
        writer.WriteNumber("backoff_max_ms", policy.BackoffMaxMs);
        writer.WriteNumber("retry_after_max_ms", policy.RetryAfterMaxMs);
        writer.WriteNumber("poll_ms", policy.PollMs);
        writer.WriteNumber("debounce_ms", policy.DebounceMs);
        writer.WriteNumber("mint_timeout_ms", policy.MintTimeoutMs);
        writer.WriteNumber("connect_timeout_ms", policy.ConnectTimeoutMs);
        writer.WriteNumber("subscribe_timeout_ms", policy.SubscribeTimeoutMs);
        writer.WriteNumber("stable_ms", policy.StableMs);
        writer.WriteNumber("lifetime_grace_ms", policy.LifetimeGraceMs);
        writer.WriteEndObject();
    });

    public HomeEventsOutcome Tick(long now) => Call(now, "tick", static _ => { });

    /// <summary>The mint answered with these <c>EventsSocketTicket</c> fields; Rust decides whether they are usable.</summary>
    public HomeEventsOutcome MintSucceeded(long now, uint generation, string? ticket, long? expiresIn, long? maxConnectionSeconds,
        string? protocol) => Call(now, "mint_succeeded", writer =>
    {
        writer.WriteNumber("generation", generation);
        WriteOptional(writer, "ticket", ticket);
        WriteOptional(writer, "expires_in", expiresIn);
        WriteOptional(writer, "max_connection_seconds", maxConnectionSeconds);
        WriteOptional(writer, "protocol", protocol);
    });

    /// <summary>The mint failed with HTTP <paramref name="status"/> (null: no answer) and its Retry-After.</summary>
    public HomeEventsOutcome MintFailed(long now, uint generation, int? status, long? retryAfterMs) => Call(now, "mint_failed", writer =>
    {
        writer.WriteNumber("generation", generation);
        WriteOptional(writer, "status", HttpStatus(status));
        WriteOptional(writer, "retry_after_ms", retryAfterMs);
    });

    /// <summary>The socket opened; the server picked <paramref name="protocol"/>.</summary>
    public HomeEventsOutcome Opened(long now, uint generation, string? protocol) => Call(now, "opened", writer =>
    {
        writer.WriteNumber("generation", generation);
        WriteOptional(writer, "protocol", protocol);
    });

    /// <summary>The upgrade was refused with HTTP <paramref name="status"/>, or failed with no answer (null).</summary>
    public HomeEventsOutcome ConnectFailed(long now, uint generation, int? status) => Call(now, "connect_failed", writer =>
    {
        writer.WriteNumber("generation", generation);
        WriteOptional(writer, "status", HttpStatus(status));
    });

    public HomeEventsOutcome Frame(long now, uint generation, string text) => Call(now, "frame", writer =>
    {
        writer.WriteNumber("generation", generation);
        writer.WriteString("text", text);
    });

    /// <summary>The socket ended with close <paramref name="code"/> (null: no close frame, or it failed).</summary>
    public HomeEventsOutcome Closed(long now, uint generation, int? code) => Call(now, "closed", writer =>
    {
        writer.WriteNumber("generation", generation);
        WriteOptional(writer, "code", code is >= 1000 and <= 4999 ? code : null);
    });

    public void Dispose() => _handle.Dispose();

    private HomeEventsOutcome Call(long now, string type, Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("now", now);
            writer.WriteStartObject("event");
            writer.WriteString("type", type);
            write(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        var outcome = BaleCoreHomeEvents.Call(_handle, buffer.WrittenSpan);
        return new(outcome.Actions.Select(ToAction).ToArray(), outcome.NextDeadline, outcome.Live, outcome.Stopped, outcome.Notes);
    }

    private static HomeEventsAction ToAction(HomeEventsActionWire action) => new(action.Action switch
    {
        "mint" => HomeEventsActionKind.Mint,
        "connect" => HomeEventsActionKind.Connect,
        "send" => HomeEventsActionKind.Send,
        "close" => HomeEventsActionKind.Close,
        "refresh" => HomeEventsActionKind.Refresh,
        _ => throw new BaleCoreException("home events: unknown action"),
    }, action.Generation ?? 0, action.Path, action.Protocols, action.Text);

    /// <summary>Rust takes a status as u16; anything that is no HTTP status is no answer.</summary>
    private static int? HttpStatus(int? status) => status is >= 100 and <= 999 ? status : null;

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, int? value) => WriteOptional(writer, name, (long?)value);
}
