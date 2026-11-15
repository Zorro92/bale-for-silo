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
    private const uint AbiVersion = 1;
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
        JsonTypeInfo<TArgument> argumentType, JsonTypeInfo<TResult> resultType)
    {
        _ = AbiChecked.Value;
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = JsonSerializer.SerializeToUtf8Bytes(argument, argumentType);
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

[JsonSerializable(typeof(string))]
internal sealed partial class BaleCoreJson : JsonSerializerContext;
