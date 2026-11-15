using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Bale.Core.Native;

/// <summary>
/// The Rust port of Bale.Core's logic (native/bale-core, docs/rust-port.md): one generic call, a
/// function name and a JSON argument in, a JSON result out. Ported C# APIs keep their signatures
/// and forward here.
/// </summary>
internal static unsafe class BaleCoreNative
{
    // bale_core.dll on Windows, libbale_core.so on Linux.
    private const string Library = "bale_core";
    private const uint AbiVersion = 2;
    private const int CallOk = 0;
    private const int CallFailed = 1;

    [DllImport(Library, ExactSpelling = true)]
    private static extern uint bale_core_abi_version();

    [DllImport(Library, ExactSpelling = true)]
    private static extern int bale_core_call(byte* name, nuint nameLength, byte* input, nuint inputLength,
        byte** output, nuint* outputLength);

    [DllImport(Library, ExactSpelling = true)]
    private static extern void bale_core_free(byte* buffer, nuint length);

    private static readonly Lazy<bool> AbiChecked = new(() =>
    {
        var version = bale_core_abi_version();
        return version == AbiVersion
            ? true
            : throw new InvalidOperationException($"bale_core ABI {version} does not match Bale.Core's {AbiVersion}.");
    });

    public static TResult Call<TArgument, TResult>(string name, TArgument argument,
        JsonTypeInfo<TArgument> argumentType, JsonTypeInfo<TResult> resultType) =>
        Call(name, JsonSerializer.SerializeToUtf8Bytes(argument, argumentType), resultType);

    /// <summary>Checks the library's ABI version once (the handle ABI calls this before its first use).</summary>
    public static void EnsureLoaded() => _ = AbiChecked.Value;

    /// <summary>Releases a buffer the library handed out.</summary>
    public static void Free(byte* buffer, nuint length) => bale_core_free(buffer, length);

    /// <summary>Calls with an argument object written by <paramref name="write"/> (see BaleCoreArgs).</summary>
    public static TResult Call<TResult>(string name, Action<Utf8JsonWriter> write, JsonTypeInfo<TResult> resultType)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return Call(name, buffer.WrittenSpan, resultType);
    }

    /// <summary>A <c>Checked</c> result: the value, or the original's InvalidDataException.</summary>
    public static T Checked<T>(BaleCoreChecked<T> result) =>
        result.InvalidData is { } message ? throw new InvalidDataException(message) : result.Ok!;

    private static TResult Call<TResult>(string name, ReadOnlySpan<byte> input, JsonTypeInfo<TResult> resultType)
    {
        _ = AbiChecked.Value;
        var nameBytes = Encoding.UTF8.GetBytes(name);
        byte* output = null;
        nuint outputLength = 0;
        int status;
        fixed (byte* namePointer = nameBytes)
        fixed (byte* inputPointer = input)
        {
            status = bale_core_call(namePointer, (nuint)nameBytes.Length, inputPointer, (nuint)input.Length,
                &output, &outputLength);
        }

        if (status != CallOk && status != CallFailed)
        {
            throw new InvalidOperationException($"bale_core rejected the call to {name} ({status}).");
        }

        try
        {
            var body = new ReadOnlySpan<byte>(output, checked((int)outputLength));
            return status == CallOk
                ? JsonSerializer.Deserialize(body, resultType)!
                : throw new BaleCoreException(JsonSerializer.Deserialize(body, BaleCoreJson.Default.String) ?? name);
        }
        finally
        {
            bale_core_free(output, outputLength);
        }
    }
}

/// <summary>A Rust call failed (unknown function, mismatched JSON or a caught panic).</summary>
public sealed class BaleCoreException(string message) : Exception(message);

/// <summary>The Rust side's <c>Checked</c>: exactly one of the two is set.</summary>
public sealed record BaleCoreChecked<T>(T? Ok = default, string? InvalidData = null);

// Snake case matches serde's field names. Unknown result keys and missing constructor values are
// errors, so a name that differs between the two sides fails loudly instead of defaulting.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(long?))]
[JsonSerializable(typeof(uint?))]
[JsonSerializable(typeof(AudioSelectionOutcome))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(double?))]
[JsonSerializable(typeof(ulong?))]
[JsonSerializable(typeof(Playback.VideoDecodeCapability))]
[JsonSerializable(typeof(Detail.DisplayVersionMatch))]
[JsonSerializable(typeof(BaleCoreChecked<int>))]
[JsonSerializable(typeof(BaleCoreChecked<bool>))]
[JsonSerializable(typeof(Playback.DeviceCapabilities))]
[JsonSerializable(typeof(Playback.HdrCapabilities))]
[JsonSerializable(typeof(Playback.DisplayModeFacts))]
[JsonSerializable(typeof(Playback.PlaybackRouteDecision))]
[JsonSerializable(typeof(Playback.HardwareDecodeLimits))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Playback.VideoDecodeCapability[]))]
[JsonSerializable(typeof(HardwareClaims))]
[JsonSerializable(typeof(SoftwareDecodeLists))]
[JsonSerializable(typeof(FilteredDevice))]
[JsonSerializable(typeof(FilteredStart))]
[JsonSerializable(typeof(DisplayHdr))]
internal sealed partial class BaleCoreJson : JsonSerializerContext;

internal sealed record SoftwareDecodeLists(string[] VideoCodecs, string[] OriginalOnlyVideoCodecs,
    string[] FfmpegOnlyContainers, string[] SystemVideoCodecs, string[] DemuxedVideoCodecs,
    string[] SystemAudioCodecs, string[] AudioCodecs);

internal sealed record FilteredDevice(string[] VideoCodecs, int[] VideoDecode, string[] AudioDecodeCodecs,
    string[] Containers, bool DropFfmpegExecutors);

internal sealed record DeliveryLists(string Key, string[] VideoCodecs, string[] AudioDecodeCodecs,
    string[] Containers, string[] ValidatedClaims);

internal sealed record FilteredStart(string[] CodecsVideo, string[] CodecsVideoHardware, int[] VideoDecode,
    string[] CodecsAudio, string[] Containers, DeliveryLists[] Deliveries, string[] ClientFeatures);

internal sealed record HardwareClaims(string[] VideoCodecs, Playback.VideoDecodeCapability[] VideoDecode);

internal sealed record AudioSelectionFailed(bool DecoderUnsupported);

internal sealed record AudioSelectionOutcome(int? Index = null, AudioSelectionFailed? Failed = null);

internal sealed record DisplayHdr(Playback.HdrCapabilities Hdr, string Evidence);
