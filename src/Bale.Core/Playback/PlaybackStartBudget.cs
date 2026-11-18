using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>What the player page knows about a mount at one moment (<see cref="PlaybackStartBudget.Update"/>).</summary>
public readonly record struct PlaybackStartFacts
{
    /// <summary>The first frame is up: the clock moved while playing, or a paused mount opened.</summary>
    public bool Started { get; init; }
    /// <summary>The start ended some other way: a failure was queued, the mount was replaced, or the attempt stopped.</summary>
    public bool Ended { get; init; }
    /// <summary>The backend opened the source.</summary>
    public bool Opened { get; init; }
    /// <summary>The viewer holds the opened mount paused.</summary>
    public bool Paused { get; init; }
    /// <summary>The device has no network connection.</summary>
    public bool Offline { get; init; }
    /// <summary>A seek, track change or replan owns the mount and decides what replaces it.</summary>
    public bool Deferred { get; init; }
}

/// <summary>
/// One mount's start budget: silo-apple <c>b9446d5d</c> ("fail a direct-play load that never opens so the server can
/// replan") bounds the engine's load with <c>PlaybackStartDeadline.budgetMs</c>, the 45 s AetherEngine gives server
/// HLS, and its start deadline bounds the adopted plan until it plays. A Bale mount opens and starts in one step, so
/// one budget runs from the mount (first plan or replacement, on every route) to its first frame. It is held while the
/// device is offline and, once the source opened, while the viewer holds it paused; a seek, track change or replan
/// that owns the mount defers it. The update that runs it out returns true once: the player then asks the
/// buffering-quality controller (<see cref="BufferingQualityController.StartDeadlinePassed"/>) and otherwise fails the
/// mount with <see cref="Failure"/> through the single recovery replan.
/// <para>The decision lives in Rust (native/bale-core/src/start_budget.rs); the page updates it every UI tick.</para>
/// </summary>
public sealed class PlaybackStartBudget : IDisposable
{
    /// <summary>AetherEngine's server-HLS readiness budget, which silo-apple reuses for every route.</summary>
    public const long BudgetMs = 45_000;

    /// <summary>
    /// The failure classification: AetherEngine's <c>PlaybackErrorKind.noPlayableTrackWithinBudget</c>, which silo-apple
    /// sends as <c>failure.classification</c> when this budget runs out. The server takes it as an ordinary
    /// failure_recovery and excludes the failed route (playback-protocol-v3.md §6).
    /// </summary>
    public const string Classification = "noPlayableTrackWithinBudget";

    /// <summary>silo-apple's <c>startDeadlineMessage</c>: English, as the server stores it.</summary>
    public static PlaybackFailureV3 Failure { get; } = new(Classification, $"Playback didn't start within {BudgetMs / 1000} seconds.");

    public static bool Is(PlaybackFailureV3? failure) => failure?.Classification == Classification;

    private readonly StartBudgetHandle _handle;

    /// <summary>A mount begins at <paramref name="nowMs"/>; <paramref name="budgetMs"/> is the default unless a debug override asks.</summary>
    public PlaybackStartBudget(long nowMs, long budgetMs = BudgetMs)
    {
        BaleCoreNative.EnsureLoaded();
        _handle = BaleCoreStartBudget.bale_core_start_budget_create(budgetMs, nowMs);
    }

    /// <summary>
    /// Records <paramref name="facts"/> at <paramref name="nowMs"/>. True only on the update that runs the budget out. A
    /// first frame or an end in the same update wins, and the budget never fires twice or after the start is over.
    /// </summary>
    public bool Update(long nowMs, PlaybackStartFacts facts)
    {
        var bits = (facts.Started ? 1u : 0) | (facts.Ended ? 2u : 0) | (facts.Opened ? 4u : 0) | (facts.Paused ? 8u : 0)
            | (facts.Offline ? 16u : 0) | (facts.Deferred ? 32u : 0);
        return BaleCoreStartBudget.bale_core_start_budget_update(_handle, nowMs, bits) != 0;
    }

    /// <summary>What is left at <paramref name="nowMs"/>, never negative.</summary>
    public long RemainingMs(long nowMs) => Math.Max(0, BaleCoreStartBudget.bale_core_start_budget_remaining(_handle, nowMs));

    public void Dispose() => _handle.Dispose();
}
