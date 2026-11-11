using System.Text;
using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>
/// The player's one safe log line per accepted plan: why the server chose this delivery and
/// whether it played another version than the one asked for. Only protocol ids are written,
/// each through <see cref="PlaybackStats.MetadataId"/>; a value that fails it shows as "?".
/// Never the plan's stream URL, headers, grants, source details or a warning's message text.
/// </summary>
public static class PlaybackPlanLog
{
    // A plan's lists are short (silo-server plan_v3.go); the bound keeps a line readable anyway.
    private const int MaxListItems = 8;

    /// <summary>
    /// <c>plan delivery=… reason=… requested=… effective=… quality=… warnings=a,b transformations=c</c>,
    /// omitting empty fields. Values can't contain "=", so the keys stay unambiguous.
    /// </summary>
    public static string Describe(PlaybackPlan? plan)
    {
        var line = new StringBuilder("plan");
        if (plan is null) return line.ToString();
        Add("delivery", Id(plan.Delivery));
        Add("reason", Id(plan.DecisionReason));
        Add("requested", Id(plan.RequestedMediaFileId));
        Add("effective", Id(plan.EffectiveMediaFileId));
        Add("quality", Id(plan.ActiveQuality));
        // The code only: a warning's message is server prose and may name tracks or files.
        Add("warnings", Ids(plan.DegradationWarnings?.Select(warning => warning?.Code)));
        // The name is the transformation's id (protocol_v3.go Transformation…V3 constants).
        Add("transformations", Ids(plan.Transformations?.Select(transformation => transformation?.Name)));
        return line.ToString();

        void Add(string key, string? value)
        {
            if (value is not null) line.Append(' ').Append(key).Append('=').Append(value);
        }
    }

    // Absent stays absent; present but unexpected is "?", so the line shows something was sent.
    private static string? Id(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : PlaybackStats.MetadataId(value) ?? "?";

    private static string? Ids(IEnumerable<string?>? values)
    {
        var ids = (values ?? []).Select(Id).OfType<string>().ToList();
        if (ids.Count == 0) return null;
        var shown = string.Join(',', ids.Take(MaxListItems));
        return ids.Count > MaxListItems ? FormattableString.Invariant($"{shown},+{ids.Count - MaxListItems}") : shown;
    }
}
