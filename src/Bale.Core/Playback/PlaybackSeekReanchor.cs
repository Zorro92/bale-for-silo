using Bale.Core.Api.Models;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Serialization.Json;

namespace Bale.Core.Playback;

/// <summary>PlaybackSessionManager.kt seekReanchorMismatch/hasSameSeekReanchorBaseRoute.</summary>
public static class PlaybackSeekReanchor
{
    public const string Feature = "seek_reanchor_v1";

    public static bool Matches(PlaybackPlan current, PlaybackDecision decision, string sessionId, string fileId)
    {
        if (!PlaybackMessages.ValidatePlayable(decision).IsPlayable || decision.PlaybackPlan is not { } next
            || decision.ServerFeatures?.Contains(Feature) != true || decision.SessionId != sessionId
            || next.SessionId is { } session && session != sessionId) return false;
        var requested = current.RequestedMediaFileId ?? fileId;
        var effective = current.EffectiveMediaFileId ?? requested;
        var nextRequested = next.RequestedMediaFileId ?? requested;
        return next.PlanId == current.PlanId && next.PlanAttemptKey == current.PlanAttemptKey
            && nextRequested == requested && (next.EffectiveMediaFileId ?? nextRequested) == effective
            && next.Delivery == current.Delivery
            && next.Stream?.Protocol == current.Stream?.Protocol
            && next.Stream?.MimeType == current.Stream?.MimeType
            && next.Stream?.Container == current.Stream?.Container
            && next.Stream?.HeaderRefresh == current.Stream?.HeaderRefresh
            && Same(next.SelectedTracks, current.SelectedTracks)
            && Same(next.EffectiveRecipe, current.EffectiveRecipe)
            && Same(next.Claims, current.Claims)
            && next.Subtitle?.Mode == current.Subtitle?.Mode
            && next.Subtitle?.TrackId == current.Subtitle?.TrackId
            && next.Subtitle?.Artifact?.MimeType == current.Subtitle?.Artifact?.MimeType
            && next.Subtitle?.Artifact?.Format == current.Subtitle?.Artifact?.Format
            && Same(next.Subtitle?.HlsRendition, current.Subtitle?.HlsRendition)
            && next.SubtitleFidelityPolicy == current.SubtitleFidelityPolicy
            && SameSet(next.Transformations, current.Transformations)
            && SameSet(next.AppliedQuirks, current.AppliedQuirks)
            && (next.RuntimeCorrections ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(current.RuntimeCorrections ?? []);
    }

    // Kiota's generated serializers are AOT-safe. Compare only route identity, not rotating stream
    // grants, headers, timeline or expiration. These values stay in memory and must never be logged.
    private static bool Same(IParsable? left, IParsable? right) => ReferenceEquals(left, right)
        || left is not null && right is not null && Value(left) == Value(right);

    private static bool SameSet<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right) where T : IParsable =>
        (left ?? []).Select(value => Value(value)).ToHashSet(StringComparer.Ordinal)
            .SetEquals((right ?? []).Select(value => Value(value)));

    private static string Value(IParsable value)
    {
        using var writer = new JsonSerializationWriter();
        writer.WriteObjectValue(null, value);
        using var reader = new StreamReader(writer.GetSerializedContent());
        return reader.ReadToEnd();
    }
}
