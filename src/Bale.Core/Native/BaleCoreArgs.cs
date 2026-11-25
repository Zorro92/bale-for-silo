using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Serialization.Json;

namespace Bale.Core.Native;

/// <summary>Writers for the argument objects passed to bale_core (snake_case keys).</summary>
internal static class BaleCoreArgs
{
    /// <summary>
    /// A server model in the server's own JSON shape, from Kiota's AOT-safe generated serializer.
    /// Plans carry stream URLs and headers; they stay in memory, and the Rust side never logs them.
    /// </summary>
    public static void Server(this Utf8JsonWriter writer, string name, IParsable? value)
    {
        writer.WritePropertyName(name);
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteRawValue(Serialize(value), skipInputValidation: true);
    }

    public static void Server<T>(this Utf8JsonWriter writer, string name, IEnumerable<T>? values) where T : IParsable
    {
        writer.WritePropertyName(name);
        if (values is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteRawValue(Serialize(value), skipInputValidation: true);
        writer.WriteEndArray();
    }

    public static void Record<T>(this Utf8JsonWriter writer, string name, T value, JsonTypeInfo<T> type)
    {
        writer.WritePropertyName(name);
        JsonSerializer.Serialize(writer, value, type);
    }

    public static void Records<T>(this Utf8JsonWriter writer, string name, IEnumerable<T> values, JsonTypeInfo<T> type)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) JsonSerializer.Serialize(writer, value, type);
        writer.WriteEndArray();
    }

    /// <summary>A double; NaN and infinities as .NET's named literals, which the Rust side reads.</summary>
    public static void Double(this Utf8JsonWriter writer, string name, double value)
    {
        if (double.IsFinite(value)) writer.WriteNumber(name, value);
        // System.Text.Json's literal names; double.ToString() would write "∞".
        else writer.WriteString(name, double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity");
    }

    public static void Strings(this Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    public static void Numbers(this Utf8JsonWriter writer, string name, IEnumerable<int> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteNumberValue(value);
        writer.WriteEndArray();
    }

    public static void Number(this Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }

    public static void Text(this Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    /// <summary>Video decode claims as (codec, hardware), the part the filters read.</summary>
    public static void VideoDecode(this Utf8JsonWriter writer, string name, IEnumerable<Playback.VideoDecodeCapability> decode)
    {
        writer.WriteStartArray(name);
        foreach (var claim in decode)
        {
            writer.WriteStartObject();
            writer.WriteString("codec", claim.Codec);
            writer.WriteBoolean("hardware", claim.Hardware);
            if (claim.MaxWidth is { } width) writer.WriteNumber("max_width", width);
            if (claim.MaxHeight is { } height) writer.WriteNumber("max_height", height);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    // Kiota's own options plus named NaN/Infinity: tests and replans can put non-finite doubles in
    // a plan, which the original C# logic tolerated. The server never sends them.
    private static readonly KiotaJsonSerializationContext Context = new(
        new JsonSerializerOptions(KiotaJsonSerializationContext.Default.Options)
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        });

    private static byte[] Serialize(IParsable value)
    {
        using var serializer = new JsonSerializationWriter(Context);
        serializer.WriteObjectValue(null, value);
        using var content = serializer.GetSerializedContent();
        using var bytes = new MemoryStream();
        content.CopyTo(bytes);
        return bytes.ToArray();
    }
}
