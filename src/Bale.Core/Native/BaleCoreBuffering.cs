using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Bale.Core.Native;

// The buffering-quality handle ABI (native/bale-core/src/abi_buffering.rs). The controller takes one
// JSON event per call and returns its ordered steps; the signal mapper takes bits and returns packed calls.

internal sealed class BufferingHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() { BaleCoreBuffering.bale_core_buffering_destroy(handle); return true; }
}

internal sealed class SignalsHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() { BaleCoreBuffering.bale_core_signals_destroy(handle); return true; }
}

internal static unsafe class BaleCoreBuffering
{
    private const string Library = "bale_core";
    private const int CallOk = 0;
    private const int CallFailed = 1;

    [DllImport(Library, ExactSpelling = true)] public static extern BufferingHandle bale_core_buffering_create();
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_buffering_destroy(nint handle);
    [DllImport(Library, ExactSpelling = true)]
    private static extern int bale_core_buffering_call(BufferingHandle handle, byte* input, nuint inputLength, byte** output, nuint* outputLength);

    [DllImport(Library, ExactSpelling = true)] public static extern SignalsHandle bale_core_signals_create();
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_signals_destroy(nint handle);
    [DllImport(Library, ExactSpelling = true)] public static extern uint bale_core_signals_update(SignalsHandle handle, uint bits);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_signals_plan_adopted(SignalsHandle handle);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_signals_invalidate(SignalsHandle handle);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_signals_note_request_failed(SignalsHandle handle);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_signals_note_data_arrived(SignalsHandle handle);
    [DllImport(Library, ExactSpelling = true)] public static extern byte bale_core_signals_plan_has_run(SignalsHandle handle);

    /// <summary>Runs one event written into <paramref name="input"/> and reads its outcome.</summary>
    public static BufferingOutcome Call(BufferingHandle handle, ArrayBufferWriter<byte> input)
    {
        BaleCoreNative.EnsureLoaded();
        byte* output = null;
        nuint outputLength = 0;
        int status;
        fixed (byte* pointer = input.WrittenSpan)
            status = bale_core_buffering_call(handle, pointer, (nuint)input.WrittenCount, &output, &outputLength);
        if (status != CallOk && status != CallFailed)
            throw new InvalidOperationException($"bale_core rejected the buffering call ({status}).");
        try
        {
            var body = new ReadOnlySpan<byte>(output, checked((int)outputLength));
            return status == CallOk
                ? JsonSerializer.Deserialize(body, BaleCoreBufferingJson.Default.BufferingOutcome)!
                : throw new BaleCoreException(JsonSerializer.Deserialize(body, BaleCoreBufferingJson.Default.String) ?? "buffering");
        }
        finally
        {
            BaleCoreNative.Free(output, outputLength);
        }
    }
}

/// <summary>One call's result: its steps in order, the state the host reads between calls, and the event's answer.</summary>
internal sealed record BufferingOutcome(BufferingStep[] Steps, BufferingStateWire State, bool? Returned,
    BufferingReplanWire? Replan, BufferingSummaryWire? Summary);

internal sealed record BufferingStateWire(BufferingPillWire? Pill, bool PillPending, int? KeptEstimateKbps, string? CurrentPlanId);

internal sealed record BufferingPillWire(string Kind, long? DeadlineMs, long RemainingMs);

/// <summary>A schedule/cancel of a timer, or an output (Rust's <c>Step</c>, tagged by <see cref="Step"/>).</summary>
internal sealed record BufferingStep(string Step, string? Timer = null, long? At = null, BufferingReplanWire? Replan = null,
    string? Pill = null, string? Notice = null, BufferingSummaryWire? Summary = null);

internal sealed record BufferingReplanWire(string Operation, string QualityPreference, int? BandwidthEstimateKbps,
    string? BandwidthEstimateSource, string? LearnedLimit, int? ObservedThroughputKbps);

internal sealed record BufferingSummaryWire(string PlanId, long PlayedMs, int StallCount, long StallTotalMs, long StallLongestMs,
    int? DeliveredKbps, string Mode, string Action, int? BandwidthEstimateKbps);

// The same options as BaleCoreJson: snake case, unknown keys and missing constructor values are errors.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(BufferingOutcome))]
internal sealed partial class BaleCoreBufferingJson : JsonSerializerContext;
