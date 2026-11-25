using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Bale.Core.Native;

// The handle ABI of Home's events-socket subscription (native/bale-core/src/abi_home_events.rs): one JSON event per
// call, its actions in order, the next deadline and the state back.

internal sealed class HomeEventsHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() { BaleCoreHomeEvents.bale_core_home_events_destroy(handle); return true; }
}

internal static unsafe class BaleCoreHomeEvents
{
    private const string Library = "bale_core";
    private const int CallOk = 0;
    private const int CallFailed = 1;

    [DllImport(Library, ExactSpelling = true)] public static extern HomeEventsHandle bale_core_home_events_create();
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_home_events_destroy(nint handle);
    [DllImport(Library, ExactSpelling = true)]
    private static extern int bale_core_home_events_call(HomeEventsHandle handle, byte* input, nuint inputLength, byte** output, nuint* outputLength);

    /// <summary>Runs one event and reads its outcome.</summary>
    public static HomeEventsOutcomeWire Call(HomeEventsHandle handle, ReadOnlySpan<byte> input)
    {
        byte* output = null;
        nuint outputLength = 0;
        int status;
        fixed (byte* pointer = input)
            status = bale_core_home_events_call(handle, pointer, (nuint)input.Length, &output, &outputLength);
        if (status != CallOk && status != CallFailed)
            throw new InvalidOperationException($"bale_core rejected the home events call ({status}).");
        try
        {
            var body = new ReadOnlySpan<byte>(output, checked((int)outputLength));
            return status == CallOk
                ? JsonSerializer.Deserialize(body, BaleCoreHomeEventsJson.Default.HomeEventsOutcomeWire)!
                : throw new BaleCoreException(JsonSerializer.Deserialize(body, BaleCoreHomeEventsJson.Default.String) ?? "home events");
        }
        finally
        {
            BaleCoreNative.Free(output, outputLength);
        }
    }
}

/// <summary>One call's result: the actions in order, when to tick next, and the state.</summary>
internal sealed record HomeEventsOutcomeWire(HomeEventsActionWire[] Actions, long? NextDeadline, bool Live, bool Stopped, string[] Notes);

/// <summary>Rust's <c>Action</c>, tagged by <see cref="Action"/>.</summary>
internal sealed record HomeEventsActionWire(string Action, uint? Generation = null, string? Path = null, string[]? Protocols = null,
    string? Text = null);

// The same options as BaleCoreJson: snake case, unknown keys and missing constructor values are errors.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(HomeEventsOutcomeWire))]
internal sealed partial class BaleCoreHomeEventsJson : JsonSerializerContext;
