using System.Text.Json;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>silo-server's buffering-quality v1 probe vectors (copied fixture, AGPL-3.0 like Bale).</summary>
public class ConnectionSpeedMeterTests
{
    private static JsonElement Vectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures/buffering-quality-v1-probe-vectors.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    public static TheoryData<string> ProbeCases()
    {
        var data = new TheoryData<string>();
        foreach (var item in Vectors().GetProperty("probe_cases").EnumerateArray()) data.Add(item.GetProperty("name").GetString()!);
        return data;
    }

    [Fact]
    public void ConstantsMatchTheServer()
    {
        var c = Vectors().GetProperty("probe_constants");
        Assert.Equal(ConnectionSpeedMeter.WindowMs, c.GetProperty("PROBE_WINDOW_MS").GetInt32());
        Assert.Equal(ConnectionSpeedMeter.QuickBytes, c.GetProperty("QUICK_PROBE_BYTES").GetInt64());
        Assert.Equal(ConnectionSpeedMeter.AccurateBytes, c.GetProperty("ACCURATE_PROBE_BYTES").GetInt64());
        Assert.Equal(new ConnectionSpeedMeter(ConnectionSpeedTestMode.Quick, null).LimitMs, c.GetProperty("QUICK_PROBE_MAX_MS").GetInt32());
        Assert.Equal(new ConnectionSpeedMeter(ConnectionSpeedTestMode.Accurate, null).LimitMs, c.GetProperty("ACCURATE_PROBE_MAX_MS").GetInt32());
        Assert.Equal(ConnectionSpeedMeter.FirstByteTimeout.TotalMilliseconds, c.GetProperty("PROBE_FIRST_BYTE_DEADLINE_MS").GetInt32());
        Assert.Equal(ConnectionSpeedMeter.MinKbps, c.GetProperty("MIN_PROBE_KBPS").GetInt32());
        Assert.Equal(ConnectionSpeedMeter.MaxKbps, c.GetProperty("MAX_PROBE_KBPS").GetInt32());
    }

    [Theory]
    [MemberData(nameof(ProbeCases))]
    public void MeasuresLikeTheServerVectors(string name)
    {
        var item = Vectors().GetProperty("probe_cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var mode = item.GetProperty("mode").GetString() == "quick" ? ConnectionSpeedTestMode.Quick : ConnectionSpeedTestMode.Accurate;
        int? target = item.TryGetProperty("target_kbps", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : null;
        long? end = item.TryGetProperty("end", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : null;
        var meter = new ConnectionSpeedMeter(mode, target);

        // Replay on the client's clock: before each chunk, close the windows that have elapsed.
        long? stop = null;
        foreach (var chunk in item.GetProperty("chunks").EnumerateArray())
        {
            var at = chunk[0].GetInt64();
            if (end is { } finish && at > finish) break;
            if ((stop = meter.StopAt(at)) is not null && stop <= at) break;
            meter.Add(at, chunk[1].GetInt64());
        }
        stop ??= meter.StopAt(end ?? long.MaxValue) ?? end;
        var result = meter.Result(stop!.Value, end);

        var expect = item.GetProperty("expect");
        if (expect.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(result);
            return;
        }
        Assert.NotNull(result);
        Assert.Equal(expect.GetProperty("kbps").GetInt32(), result!.Kbps);
        Assert.Equal(expect.GetProperty("lower_bound").GetBoolean(), result.LowerBound);
        Assert.Equal(expect.GetProperty("bytes").GetInt64(), result.Bytes);
        Assert.Equal(expect.GetProperty("duration_ms").GetInt32(), result.DurationMs);
        Assert.Equal(expect.GetProperty("stop").GetInt64(), Math.Min(stop.Value, end ?? long.MaxValue));
    }
}
