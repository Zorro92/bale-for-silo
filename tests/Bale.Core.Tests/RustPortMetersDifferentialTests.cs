using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Native;
using Bale.Core.Playback;
using Reference = Bale.Core.Tests.Reference;

namespace Bale.Core.Tests;

/// <summary>
/// Slice 5 of the Rust port (docs/rust-port.md): the stateful meters behind the handle ABI against
/// their C# originals in Reference/, fed the same random event streams.
/// </summary>
public class RustPortMetersDifferentialTests
{
    private const int Runs = 300;

    /// <summary>
    /// The trace's .NET-style "0.0" formatter. This also guards the JSON bridge's double parsing: without
    /// serde_json's float_roundtrip, large values arrived one ULP off and printed differently.
    /// </summary>
    [Fact]
    public void FixedPointFormattingMatchesDotNet()
    {
        var values = new List<double>
        {
            0, -0.0, 0.05, 0.15, 0.25, 0.35, 0.45, 0.95, -0.05, -0.04, -0.25, 41.75, 41.25, 1.45, 2.675, 1e-7, 0.0499999999,
            9.95, 99.95, 999999999.95, 1e15, 1e16 + 2, 1.23456789012345e20, 123456789.123456789, double.MaxValue,
            double.Epsilon, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 16.6833333333333, 33.366666666,
        };
        var random = new Random(11);
        for (var i = 0; i < 20_000; i++)
        {
            values.Add(random.Next(4) switch
            {
                0 => Math.Round(random.NextDouble() * 10_000, 2) * (random.Next(2) == 0 ? 1 : -1),
                1 => random.Next(0, 100_000) / 4.0,
                2 => random.NextDouble() * Math.Pow(10, random.Next(-8, 20)),
                _ => BitConverter.Int64BitsToDouble(random.NextInt64()),
            });
        }
        foreach (var value in values)
        {
            foreach (var decimals in new[] { 0, 1, 2 })
            {
                var expected = value.ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), CultureInfo.InvariantCulture);
                var actual = BaleCoreNative.Call("dotnet.format_fixed", writer =>
                {
                    writer.Double("value", value);
                    writer.WriteNumber("decimals", decimals);
                }, BaleCoreJson.Default.String);
                Assert.True(expected == actual, $"{value:R} ({decimals}): .NET {expected}, Rust {actual}");
            }
        }
    }

    [Fact]
    public void FrameProcessingMatches()
    {
        var random = new Random(12);
        for (var run = 0; run < Runs; run++)
        {
            var collect = random.Next(2) == 0;
            var capacity = Pick(random, [1, 3, 512]);
            var expected = new Reference.PlaybackFrameRecordProcessor(capacity, collect);
            var actual = new PlaybackFrameRecordProcessor(capacity, collect);
            var (wall, pts, gc, waits, waitTime, gen) = (1000.0, 0L, 0L, 0L, 0L, 0);
            for (var step = 0; step < 400; step++)
            {
                switch (random.Next(20))
                {
                    case 0:
                    case 1:
                        gc += random.Next(3) == 0 ? random.Next(0, 200_000) : 0;
                        waits += random.Next(2);
                        waitTime += random.Next(3) == 0 ? random.Next(0, 100_000) : 0;
                        gen += random.Next(2);
                        var snapshot = new PlaybackFrameEnvironmentSnapshot(wall + random.Next(-30, 80), TimeSpan.FromTicks(gc),
                            gen, gen / 3, gen / 9, random.Next(4) == 0 ? null : waits,
                            random.Next(4) == 0 ? null : TimeSpan.FromTicks(waitTime));
                        expected.AddSnapshot(snapshot);
                        actual.AddSnapshot(snapshot);
                        Assert.Equal(expected.SnapshotFor(wall), actual.SnapshotFor(wall));
                        break;
                    case 2:
                        var kind = (uint)random.Next(2, 5);
                        expected.Process(new PlaybackFrameRecord(kind, 0, wall));
                        actual.Process(new PlaybackFrameRecord(kind, 0, wall));
                        break;
                    case 3:
                        var position = TimeSpan.FromTicks(pts + random.Next(-1_000_000, 1_000_000));
                        Assert.Equal(expected.Read(position, (long)wall), actual.Read(position, (long)wall));
                        Assert.Equal(expected.DescribeLateFrameTrace(), actual.DescribeLateFrameTrace());
                        break;
                    case 4 when random.Next(10) == 0:
                        expected.ResetLateFrameTrace();
                        actual.ResetLateFrameTrace();
                        break;
                    default:
                        wall += random.Next(4) == 0 ? random.Next(0, 300) + random.NextDouble() : 41.70833 + random.NextDouble() - 0.5;
                        var duration = Pick(random, [417_083L, 333_666L, 400_000L, 0L]);
                        pts += duration;
                        var record = new PlaybackFrameRecord(1, (uint)random.Next(4), wall, pts, duration,
                            pts + random.Next(-800_000, 400_000));
                        expected.Process(record);
                        actual.Process(record);
                        break;
                }
            }
            Assert.Equal(expected.Read(TimeSpan.FromTicks(pts), (long)wall), actual.Read(TimeSpan.FromTicks(pts), (long)wall));
            Assert.Equal(expected.DescribeLateFrameTrace(), actual.DescribeLateFrameTrace());
        }
    }

    [Fact]
    public void StandaloneMeterAndTraceMatch()
    {
        var random = new Random(13);
        for (var run = 0; run < Runs; run++)
        {
            var collect = random.Next(2) == 0;
            var (expectedMeter, actualMeter) = (new Reference.PlaybackFrameDeliveryMeter(collect), new PlaybackFrameDeliveryMeter(collect));
            var (expectedTrace, actualTrace) = (new Reference.PlaybackLateFrameTrace(), new PlaybackLateFrameTrace());
            long now = 0, pts = 0;
            for (var step = 0; step < 300; step++)
            {
                now += random.Next(0, 3000) == 0 ? 20_000 : random.Next(0, 80);
                pts += 417_083;
                var position = TimeSpan.FromTicks(pts + random.Next(-600_000, 300_000));
                switch (random.Next(30))
                {
                    case 0:
                        var seeking = random.Next(2) == 0;
                        expectedMeter.SetSeeking(seeking);
                        actualMeter.SetSeeking(seeking);
                        break;
                    case 1:
                        expectedMeter.InvalidateTimeline();
                        actualMeter.InvalidateTimeline();
                        break;
                    case 2:
                        Assert.Equal(expectedMeter.Read(position, now), actualMeter.Read(position, now));
                        break;
                    default:
                        var (playing, discontinuous) = (random.Next(6) != 0, random.Next(10) == 0);
                        var late = expectedMeter.Observe(TimeSpan.FromTicks(pts), TimeSpan.FromTicks(417_083), position, playing, discontinuous, now);
                        Assert.Equal(late, actualMeter.Observe(TimeSpan.FromTicks(pts), TimeSpan.FromTicks(417_083), position, playing, discontinuous, now));
                        long? waits = random.Next(3) == 0 ? null : step / 7;
                        TimeSpan? waitTime = random.Next(3) == 0 ? null : TimeSpan.FromTicks(step * random.Next(0, 3000));
                        var gcPause = TimeSpan.FromTicks(step * random.Next(0, 60_000));
                        expectedTrace.Sample(now + 0.25, TimeSpan.FromTicks(pts), TimeSpan.FromTicks(417_083), position, late, waits, waitTime, gcPause, step / 5, step / 50, step / 500);
                        actualTrace.Sample(now + 0.25, TimeSpan.FromTicks(pts), TimeSpan.FromTicks(417_083), position, late, waits, waitTime, gcPause, step / 5, step / 50, step / 500);
                        break;
                }
            }
            Assert.Equal(expectedTrace.Describe(), actualTrace.Describe());
            expectedTrace.Reset();
            actualTrace.Reset();
            Assert.Equal(expectedTrace.Describe(), actualTrace.Describe());
        }
    }

    [Fact]
    public void ThroughputMatches()
    {
        // Exact halves: Math.Round rounds them to even (1 byte in 3.2 s = 2.5 bps → 2; 3 bytes → 7.5 → 8).
        foreach (var (bytes, ticks) in new[] { (1L, 32_000_000L), (3L, 32_000_000L), (5L, 16_000_000L) })
        {
            Assert.Equal(new Reference.PlaybackThroughputMeter().Sample(bytes, TimeSpan.FromTicks(ticks)),
                new PlaybackThroughputMeter().Sample(bytes, TimeSpan.FromTicks(ticks)));
        }
        var random = new Random(14);
        for (var run = 0; run < Runs; run++)
        {
            var (expected, actual) = (new Reference.PlaybackThroughputMeter(), new PlaybackThroughputMeter());
            long bytes = 0, ticks = 0;
            for (var step = 0; step < 50; step++)
            {
                if (random.Next(15) == 0) (bytes, ticks) = (0, 0); // a replacement transport
                bytes += random.Next(4) == 0 ? 0 : random.Next(0, 5_000_000);
                ticks += random.Next(5) == 0 ? 0 : random.Next(1, 15_000_000);
                Assert.Equal(expected.Sample(bytes, TimeSpan.FromTicks(ticks)), actual.Sample(bytes, TimeSpan.FromTicks(ticks)));
            }
        }
    }

    [Fact]
    public void SpeedTestArithmeticMatches()
    {
        var random = new Random(15);
        for (var run = 0; run < Runs * 3; run++)
        {
            var mode = random.Next(2) == 0 ? ConnectionSpeedTestMode.Quick : ConnectionSpeedTestMode.Accurate;
            int? target = random.Next(4) == 0 ? null : Pick(random, [50, 5_000, 25_000, 80_000, 2_000_000]);
            var (expected, actual) = (new Reference.ConnectionSpeedMeter(mode, target), new ConnectionSpeedMeter(mode, target));
            var rate = Pick(random, [0, 1_000, 50_000, 300_000, 1_500_000]); // bytes per ms
            long now = 0;
            long? stop = null;
            while (stop is null && now < 12_000)
            {
                now += random.Next(1, 120);
                var bytes = random.Next(8) == 0 ? 0 : (long)(rate * random.NextDouble() * 40);
                expected.Add(now, bytes);
                actual.Add(now, bytes);
                stop = expected.StopAt(now);
                Assert.Equal(stop, actual.StopAt(now));
            }
            long? ended = random.Next(3) == 0 ? now - random.Next(0, 400) : null;
            Assert.Equal(expected.Result(stop ?? now, ended), actual.Result(stop ?? now, ended));
            Assert.Equal(expected.WindowKbps, actual.WindowKbps);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionSpeedMeter(ConnectionSpeedTestMode.Quick, null).Add(-1, 0));
    }

    [Fact]
    public void SeekReanchorMatches()
    {
        var random = new Random(16);
        for (var run = 0; run < Runs * 4; run++)
        {
            var current = Plan(random);
            var next = random.Next(4) == 0 ? Plan(random) : Vary(random, Plan(new Random(random.Next())), current);
            var decision = new PlaybackDecision
            {
                Outcome = Pick(random, new string?[] { "playable", "playable", "playable", "adaptation_unavailable", null }),
                SessionId = Pick(random, new string?[] { "s1", "s1", "s2", null }),
                PlaybackPlan = random.Next(10) == 0 ? null : next,
                ServerFeatures = random.Next(6) == 0 ? null : random.Next(5) == 0 ? ["other"] : [PlaybackSeekReanchor.Feature, "x"],
            };
            Assert.Equal(Reference.PlaybackSeekReanchor.Matches(current, decision, "s1", "f1"),
                PlaybackSeekReanchor.Matches(current, decision, "s1", "f1"));
        }
    }

    private static T Pick<T>(Random random, IReadOnlyList<T> values) => values[random.Next(values.Count)];

    private static PlaybackPlan Plan(Random random) => new()
    {
        PlanId = Pick(random, new string?[] { "p1", "p1", "p2", null }),
        PlanAttemptKey = Pick(random, new string?[] { "k1", "k1", "k2" }),
        SessionId = Pick(random, new string?[] { null, "s1", "s1", "s2" }),
        RequestedMediaFileId = Pick(random, new string?[] { null, "f1", "f2" }),
        EffectiveMediaFileId = Pick(random, new string?[] { null, "f1", "f2" }),
        Delivery = Pick(random, new string?[] { "original_http", "hls" }),
        Stream = new StreamV3
        {
            Protocol = Pick(random, new string?[] { "http", "hls" }), MimeType = "video/mp4", Container = Pick(random, new string?[] { "mkv", "mp4" }),
            HeaderRefresh = Pick(random, new string?[] { null, "auto" }), Url = $"/api/v2/stream/{random.Next()}",
        },
        SelectedTracks = random.Next(3) == 0 ? null : new SelectedTracksV3 { Audio = new TrackIdentityV3 { Id = "a", Index = random.Next(2) } },
        EffectiveRecipe = random.Next(3) == 0 ? null : new EffectiveRecipeV3 { VideoCodec = Pick(random, new string?[] { "hevc", "h264" }), Height = 1080 },
        Claims = random.Next(3) == 0 ? null : new ValidationClaimsV3 { Video = new VideoClaimsV3 { Hdr10 = random.Next(2) == 0 } },
        Subtitle = random.Next(3) == 0 ? null : new SubtitleDecisionV3
        {
            Mode = Pick(random, new string?[] { "render", "off" }), TrackId = Pick(random, new string?[] { null, "t1" }),
            Artifact = random.Next(2) == 0 ? null : new SubtitleArtifactV3 { MimeType = "text/x-ssa", Format = Pick(random, new string?[] { "ass", "srt" }) },
        },
        SubtitleFidelityPolicy = Pick(random, new string?[] { null, "preserve" }),
        Transformations = random.Next(2) == 0 ? null : [new TransformationV3 { Name = Pick(random, new string?[] { "a", "b" }), Executor = "client" }],
        RuntimeCorrections = random.Next(2) == 0 ? null : Pick(random, new List<string>[] { ["x"], ["x", "y"], ["y", "x", "x"], [] }),
    };

    /// <summary>A plan that mostly repeats <paramref name="current"/>, changing one route field at a time.</summary>
    private static PlaybackPlan Vary(Random random, PlaybackPlan fresh, PlaybackPlan current)
    {
        var next = new PlaybackPlan
        {
            PlanId = current.PlanId, PlanAttemptKey = current.PlanAttemptKey, SessionId = current.SessionId,
            RequestedMediaFileId = current.RequestedMediaFileId, EffectiveMediaFileId = current.EffectiveMediaFileId,
            Delivery = current.Delivery, Stream = current.Stream is null ? null : new StreamV3
            {
                Protocol = current.Stream.Protocol, MimeType = current.Stream.MimeType, Container = current.Stream.Container,
                HeaderRefresh = current.Stream.HeaderRefresh, Url = "/rotated/grant", // grants rotate on every plan
            },
            SelectedTracks = current.SelectedTracks, EffectiveRecipe = current.EffectiveRecipe, Claims = current.Claims,
            Subtitle = current.Subtitle, SubtitleFidelityPolicy = current.SubtitleFidelityPolicy,
            Transformations = current.Transformations?.AsEnumerable().Reverse().ToList(), RuntimeCorrections = current.RuntimeCorrections,
        };
        switch (random.Next(10))
        {
            case 0: next.PlanAttemptKey = fresh.PlanAttemptKey; break;
            case 1: next.EffectiveMediaFileId = fresh.EffectiveMediaFileId; break;
            case 2: next.EffectiveRecipe = fresh.EffectiveRecipe; break;
            case 3: next.Subtitle = fresh.Subtitle; break;
            case 4: next.RuntimeCorrections = fresh.RuntimeCorrections; break;
            case 5: next.SessionId = fresh.SessionId; break;
        }
        return next;
    }
}
