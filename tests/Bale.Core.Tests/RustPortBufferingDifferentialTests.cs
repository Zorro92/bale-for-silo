using Bale.Core.Playback;
using Bale.Core.Settings;
using Reference = Bale.Core.Tests.Reference;

namespace Bale.Core.Tests;

/// <summary>
/// The buffering-quality controller and its signal mapper (docs/rust-port.md, "Buffering quality") against their C#
/// originals in Reference/, fed the same random event streams. Each side runs on its own manual clock; the streams
/// include output handlers that call back in (the page refuses a replan it cannot send) and outside timers on the same
/// clock (the debug stall injection), so step order and timer interleaving are compared too.
/// </summary>
public sealed class RustPortBufferingDifferentialTests
{
    private const int Runs = 400;
    private const int EventsPerRun = 200;

    /// <summary>The surface both controllers share.</summary>
    private interface IController
    {
        BufferingQualityMode Mode { set; }
        bool SendsBufferingQualityFields { set; }
        BufferingPill? Pill { get; }
        int? KeptEstimateKbps { get; }
        bool PillPending { get; }
        string? CurrentPlanId { get; }
        BufferingPlanSummary? RunningSummary { get; }
        void PlanAdopted(BufferingQualityPlan plan, bool controllerInitiated);
        void PlanEnded();
        void NewAttempt();
        void Playing();
        void Waiting(BufferingWaitCause cause, long startedAgoMs);
        void Paused();
        void Seeked();
        void TransportTroubleChanged(bool trouble);
        void ReplanFailed();
        void ReplanRefused();
        bool StartDeadlinePassed();
        void SelectLower();
        void SelectKeep();
        void SelectRestore();
        bool Back();
        void SelectPrimary();
        void QualityChanged();
        void PickSent();
        void PickRefused();
        BufferingQualityReplan? ClientReplan(string operation);
        void SetCompetingPillVisible(bool visible);
    }

    private sealed class Port(BufferingQualityController c) : IController
    {
        public BufferingQualityMode Mode { set => c.Mode = value; }
        public bool SendsBufferingQualityFields { set => c.SendsBufferingQualityFields = value; }
        public BufferingPill? Pill => c.Pill;
        public int? KeptEstimateKbps => c.KeptEstimateKbps;
        public bool PillPending => c.PillPending;
        public string? CurrentPlanId => c.CurrentPlanId;
        public BufferingPlanSummary? RunningSummary => c.RunningSummary;
        public void PlanAdopted(BufferingQualityPlan plan, bool controllerInitiated) => c.PlanAdopted(plan, controllerInitiated);
        public void PlanEnded() => c.PlanEnded();
        public void NewAttempt() => c.NewAttempt();
        public void Playing() => c.Playing();
        public void Waiting(BufferingWaitCause cause, long startedAgoMs) => c.Waiting(cause, startedAgoMs);
        public void Paused() => c.Paused();
        public void Seeked() => c.Seeked();
        public void TransportTroubleChanged(bool trouble) => c.TransportTroubleChanged(trouble);
        public void ReplanFailed() => c.ReplanFailed();
        public void ReplanRefused() => c.ReplanRefused();
        public bool StartDeadlinePassed() => c.StartDeadlinePassed();
        public void SelectLower() => c.SelectLower();
        public void SelectKeep() => c.SelectKeep();
        public void SelectRestore() => c.SelectRestore();
        public bool Back() => c.Back();
        public void SelectPrimary() => c.SelectPrimary();
        public void QualityChanged() => c.QualityChanged();
        public void PickSent() => c.PickSent();
        public void PickRefused() => c.PickRefused();
        public BufferingQualityReplan? ClientReplan(string operation) => c.ClientReplan(operation);
        public void SetCompetingPillVisible(bool visible) => c.SetCompetingPillVisible(visible);
    }

    private sealed class Original(Reference.BufferingQualityController c) : IController
    {
        public BufferingQualityMode Mode { set => c.Mode = value; }
        public bool SendsBufferingQualityFields { set => c.SendsBufferingQualityFields = value; }
        public BufferingPill? Pill => c.Pill;
        public int? KeptEstimateKbps => c.KeptEstimateKbps;
        public bool PillPending => c.PillPending;
        public string? CurrentPlanId => c.CurrentPlanId;
        public BufferingPlanSummary? RunningSummary => c.RunningSummary;
        public void PlanAdopted(BufferingQualityPlan plan, bool controllerInitiated) => c.PlanAdopted(plan, controllerInitiated);
        public void PlanEnded() => c.PlanEnded();
        public void NewAttempt() => c.NewAttempt();
        public void Playing() => c.Playing();
        public void Waiting(BufferingWaitCause cause, long startedAgoMs) => c.Waiting(cause, startedAgoMs);
        public void Paused() => c.Paused();
        public void Seeked() => c.Seeked();
        public void TransportTroubleChanged(bool trouble) => c.TransportTroubleChanged(trouble);
        public void ReplanFailed() => c.ReplanFailed();
        public void ReplanRefused() => c.ReplanRefused();
        public bool StartDeadlinePassed() => c.StartDeadlinePassed();
        public void SelectLower() => c.SelectLower();
        public void SelectKeep() => c.SelectKeep();
        public void SelectRestore() => c.SelectRestore();
        public bool Back() => c.Back();
        public void SelectPrimary() => c.SelectPrimary();
        public void QualityChanged() => c.QualityChanged();
        public void PickSent() => c.PickSent();
        public void PickRefused() => c.PickRefused();
        public BufferingQualityReplan? ClientReplan(string operation) => c.ClientReplan(operation);
        public void SetCompetingPillVisible(bool visible) => c.SetCompetingPillVisible(visible);
    }

    /// <summary>One controller on its own manual clock, logging everything it does.</summary>
    private sealed class Side
    {
        public readonly ManualBufferingQualityClock Clock = new();
        public readonly List<string> Log = [];
        /// <summary>Starts as the vectors' link: too slow for every plan.</summary>
        public int? Measured = 1;
        public IController Controller = null!;
        private int _replans;

        public static Side Rust()
        {
            var side = new Side();
            side.Controller = new Port(new BufferingQualityController(side.Clock.Timers)
            {
                Output = side.Record,
                MeasuredThroughputKbps = () => side.Measured,
            });
            return side;
        }

        public static Side CSharp()
        {
            var side = new Side();
            side.Controller = new Original(new Reference.BufferingQualityController(side.Clock.Timers)
            {
                Output = side.Record,
                MeasuredThroughputKbps = () => side.Measured,
            });
            return side;
        }

        private void Record(BufferingQualityOutput output)
        {
            Log.Add($"{Clock.NowMs} out {Describe(output)}");
            // The page refuses a lowering it cannot send at once, from inside the output (RunBufferingReplanAsync).
            if (output is BufferingReplanOutput && ++_replans % 3 == 0) Controller.ReplanRefused();
        }

        public void State(string what) => Log.Add(string.Join(" | ", Clock.NowMs, what, Describe(Controller.Pill), Controller.KeptEstimateKbps,
            Controller.PillPending, Controller.CurrentPlanId, Clock.Timers.Pending, Clock.Timers.NextDueMs));
    }

    private static string Describe(object? value) => value switch
    {
        null => "-",
        BufferingReplanOutput replan => "replan " + replan.Request,
        BufferingSummaryOutput summary => "summary " + summary.Summary,
        _ => value.ToString()!,
    };

    private static readonly string[] Labels = ["original", "1080p-high", "1080p-medium", "720p-high", "auto", "480p"];

    private static BufferingQualityPlan RandomPlan(Random random, int id)
    {
        BufferingQualityRung[] ladder = [new("original", 63_000), new("1080p-high", 10_000), new("1080p-medium", 6_000), new("720p-high", 4_000)];
        var rungs = random.Next(2) == 0 ? ladder : Enumerable.Range(0, random.Next(0, 6))
            .Select(_ => new BufferingQualityRung(Labels[random.Next(Labels.Length)], random.Next(6) switch
            {
                0 => 0,
                1 => random.Next(-10, 200),
                2 => 63_000,
                3 => 10_000,
                4 => 6_000,
                _ => random.Next(100, 80_000),
            })).ToArray();
        int? delivered = random.Next(7) switch
        {
            0 => null,
            1 => 142,
            2 => 143,
            3 => 31_600,
            4 => 63_000,
            5 => int.MaxValue,
            _ => random.Next(1, 80_000),
        };
        var quality = random.Next(3) == 0 && rungs.Length > 0 ? rungs[random.Next(rungs.Length)].Label : "auto";
        return new BufferingQualityPlan("a" + id, delivered, quality, rungs, random.Next(8) == 0)
        {
            ActiveQuality = random.Next(3) == 0 ? Labels[random.Next(Labels.Length)] : null,
            RequestedBandwidthEstimateKbps = random.Next(3) == 0 ? random.Next(-5, 50_000) : null,
        };
    }

    [Fact]
    public void ControllerMatchesTheOriginal()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var run = 0; run < Runs; run++)
        {
            var random = new Random(4_000 + run);
            var sides = new[] { Side.CSharp(), Side.Rust() };
            var now = 0L;
            var plans = 0;
            for (var step = 0; step < EventsPerRun; step++)
            {
                now += random.Next(5) switch
                {
                    0 => 0,
                    1 => random.Next(1, 2_000),
                    2 => random.Next(1, 8_000),
                    3 => random.Next(1, 20_000),
                    _ => random.Next(1, 200_000),
                };
                // A visible pill is often answered while it is up (the sides' states are equal, or the logs differ already).
                var roll = sides[0].Controller.Pill is not null && random.Next(2) == 0 ? random.Next(61, 74) : random.Next(100);
                var plan = roll < 12 ? RandomPlan(random, ++plans) : null;
                var initiated = random.Next(4) != 0;
                var cause = (BufferingWaitCause)random.Next(5);
                var ago = random.Next(3) == 0 ? random.Next(-100, 6_000) : 0;
                var flag = random.Next(2) == 0;
                var mode = (BufferingQualityMode)random.Next(3);
                int? measured = random.Next(4) switch { 0 => null, 1 => 1, 2 => 90_000, _ => random.Next(100, 70_000) };
                var delay = random.Next(0, 10_000);
                var operation = random.Next(2) == 0 ? "track_change" : "seek_reanchor";
                foreach (var side in sides)
                {
                    side.Clock.AdvanceTo(now);
                    var c = side.Controller;
                    var what = roll switch
                    {
                        < 12 => Do(() => c.PlanAdopted(plan!, initiated), "adopt"),
                        < 28 => Do(c.Playing, "playing"),
                        < 42 => Do(() => c.Waiting(flag ? BufferingWaitCause.Network : cause, ago), "waiting"),
                        < 46 => Do(c.Paused, "paused"),
                        < 48 => Do(c.Seeked, "seeked"),
                        < 52 => Do(() => c.TransportTroubleChanged(flag), "trouble"),
                        < 54 => Do(c.PlanEnded, "ended"),
                        < 55 => Do(c.NewAttempt, "attempt"),
                        < 57 => Do(c.ReplanFailed, "failed"),
                        < 59 => Do(c.ReplanRefused, "refused"),
                        < 61 => "deadline " + c.StartDeadlinePassed(),
                        < 64 => Do(c.SelectLower, "lower"),
                        < 66 => Do(c.SelectKeep, "keep"),
                        < 68 => Do(c.SelectRestore, "restore"),
                        < 71 => "back " + c.Back(),
                        < 74 => Do(c.SelectPrimary, "primary"),
                        < 76 => Do(c.QualityChanged, "quality"),
                        < 78 => Do(c.PickSent, "pick"),
                        < 80 => Do(c.PickRefused, "pick refused"),
                        < 85 => Do(() => c.SetCompetingPillVisible(flag), "competing"),
                        < 87 => "client " + Describe(c.ClientReplan(operation)),
                        < 89 => "running " + Describe(c.RunningSummary),
                        < 91 => Do(() => c.Mode = mode, "mode"),
                        < 92 => Do(() => c.SendsBufferingQualityFields = flag, "fields"),
                        < 96 => Do(() => side.Measured = measured, "measured"),
                        // The debug stall: a network wait now, ended by a Playing from a timer on the same clock.
                        _ => Do(() =>
                        {
                            c.Waiting(BufferingWaitCause.Network, 0);
                            side.Clock.Timers.Schedule(delay, c.Playing);
                        }, "stall"),
                    };
                    side.State(what);
                }
            }
            foreach (var side in sides)
            {
                side.Clock.AdvanceTo(now + 400_000);
                side.Controller.PlanEnded();
                side.State("end");
            }
            Assert.Equal(sides[0].Log, sides[1].Log);
            foreach (var line in sides[0].Log)
            {
                foreach (var action in Enum.GetValues<BufferingAction>())
                    if (line.Contains($" out summary BufferingPlanSummary {{ PlanId = ", StringComparison.Ordinal) && line.Contains($"Action = {action},", StringComparison.Ordinal))
                        seen.Add("action " + action);
                foreach (var output in (string[])["Notice = Lowered", "Notice = CantLower", "Kind = Prompt", "Kind = Undo", "ObservedThroughputKbps = 1,", "LearnedLimit = clear"])
                    if (line.Contains(" out ", StringComparison.Ordinal) && line.Contains(output, StringComparison.Ordinal)) seen.Add(output);
            }
        }
        // The streams must reach every action, notice, pill and replan field, not only idle paths.
        Assert.Equal(Enum.GetValues<BufferingAction>().Select(action => "action " + action)
            .Concat(["Notice = Lowered", "Notice = CantLower", "Kind = Prompt", "Kind = Undo", "ObservedThroughputKbps = 1,", "LearnedLimit = clear"])
            .Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));

        static string Do(Action action, string what)
        {
            action();
            return what;
        }
    }

    [Fact]
    public void SignalsMatchTheOriginal()
    {
        for (var run = 0; run < Runs; run++)
        {
            var random = new Random(5_000 + run);
            var port = new BufferingSignals();
            var original = new Reference.BufferingSignals();
            for (var step = 0; step < EventsPerRun; step++)
            {
                switch (random.Next(10))
                {
                    case 0:
                        port.PlanAdopted();
                        original.PlanAdopted();
                        break;
                    case 1:
                        port.NoteMediaRequestFailed();
                        original.NoteMediaRequestFailed();
                        break;
                    case 2:
                        port.NoteMediaDataArrived();
                        original.NoteMediaDataArrived();
                        break;
                    case 3:
                        port.Invalidate();
                        original.Invalidate();
                        break;
                }
                var snapshot = new BufferingSnapshot
                {
                    HasMount = random.Next(5) != 0,
                    WantsToPlay = random.Next(4) != 0,
                    Playing = random.Next(2) == 0,
                    SeekInFlight = random.Next(5) == 0,
                    ReachedEnd = random.Next(12) == 0,
                    UpstreamSilent = random.Next(5) == 0,
                };
                Assert.Equal(original.Update(snapshot), port.Update(snapshot));
                Assert.Equal(original.PlanHasRun, port.PlanHasRun);
            }
        }
    }
}
