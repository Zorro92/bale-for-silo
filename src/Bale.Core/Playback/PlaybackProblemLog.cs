using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>Bounded playback diagnostics from v2 Problem metadata, never free-text details.</summary>
public static class PlaybackProblemLog
{
    // apiv2/problem.go fixes these type identifiers and titles. Emit only recognized pairs:
    // even a nominally safe title/location could contain a credential in a malformed response.
    private static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["malformed_request"] = "Malformed request",
        ["authentication_required"] = "Authentication required",
        ["invalid_token"] = "Invalid token",
        ["session_expired"] = "Session expired",
        ["permission_denied"] = "Permission denied",
        ["profile_verification_required"] = "Profile verification required",
        ["not_found"] = "Not found",
        ["conflict"] = "Conflict",
        ["idempotency_conflict"] = "Idempotency conflict",
        ["validation_failed"] = "Validation failed",
        ["rate_limited"] = "Rate limited",
        ["internal_error"] = "Internal error",
        ["dependency_unavailable"] = "Dependency unavailable",
        ["capability_disabled"] = "Capability disabled",
        ["capability_not_configured"] = "Capability not configured",
        ["capability_unsupported"] = "Capability unsupported",
        ["playback_installation_changed"] = "Playback installation changed",
        ["playback_session_ended"] = "Playback session ended",
        ["playback_progress_conflict"] = "Playback progress conflict",
    };

    // Log the owning request field, not dynamic dictionary keys, rejected values, or error prose.
    private static readonly HashSet<string> BodyFields = new(StringComparer.Ordinal)
    {
        "installation_id", "protocol_version", "client_features", "operation", "file_id", "profile_id",
        "playback_attempt_id", "replan_request_id", "failed_plan_id", "plan_attempt_id", "plan_attempt_key",
        "attempted_plan_keys", "local_mutations", "attempt_count", "quality_preference", "position_seconds",
        "metered", "bandwidth_estimate_kbps", "bandwidth_estimate_source", "bandwidth_cap_kbps",
        "selected_tracks", "failure", "client_capabilities", "client_playback_context", "start_position",
        "audio_track_id", "audio_track_index", "subtitle_track_id", "subtitle_track_index",
        "subtitle_fidelity_preference", "progress_persistence", "allow_alternate_versions",
        "position", "sequence", "is_paused", "stop_id",
    };

    public static string Describe(Problem problem)
    {
        const string prefix = "https://siloserver.org/docs/api/v2/problems/";
        var code = problem.Type?.StartsWith(prefix, StringComparison.Ordinal) == true ? problem.Type[prefix.Length..] : "?";
        if (!Titles.TryGetValue(code, out var title)) { code = "?"; title = "?"; }
        else if (problem.Title != title) title = "?";
        var status = problem.ResponseStatusCode is >= 100 and <= 599 ? problem.ResponseStatusCode
            : problem.Status is >= 100 and <= 599 ? problem.Status.Value : 0;
        var fields = (problem.Errors ?? []).Take(8).Select(error => Field(error.Location) + ":" + ErrorCode(error.Code))
            .Distinct(StringComparer.Ordinal);
        // The type's final segment IS the v2 problem code. Do not print its URI or instance.
        return FormattableString.Invariant($"http={status} problem={code} title=\"{title}\" fields=[{string.Join(',', fields)}]");
    }

    private static string Field(string? location)
    {
        if (location == "body") return "body";
        if (location?.StartsWith("body.", StringComparison.Ordinal) != true) return "?";
        var field = location[5..].Split('.', '[')[0];
        return BodyFields.Contains(field) ? "body." + field : "?";
    }

    private static string ErrorCode(string? code) => code is "required" or "out_of_range" or "invalid_type"
        or "invalid_enum" or "unknown_field" or "unknown_parameter" or "invalid" or "malformed_json" ? code : "?";
}
