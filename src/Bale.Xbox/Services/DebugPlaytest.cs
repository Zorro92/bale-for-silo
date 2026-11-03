#if DEBUG_SERVER
using System.Globalization;

namespace Bale.Xbox.Services;

/// <summary>Public-URL probe validation and verdicts, without a UWP dependency.</summary>
internal static class DebugPlaytest
{
    internal const int OpenTimeoutMs = 8000;

    internal static bool TryParse(string? url, string? seconds, out Uri uri, out int duration, out string error)
    {
        uri = null!;
        duration = 3;
        error = "url must be an absolute http(s) URL without credentials";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https") || parsed.UserInfo.Length != 0) return false;
        error = "seconds must be an integer from 1 to 10";
        if (seconds is not null && (!int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out duration)
            || duration is < 1 or > 10)) return false;
        uri = parsed;
        error = string.Empty;
        return true;
    }

    internal static string Verdict(bool opened, TimeSpan initialPosition, TimeSpan finalPosition, IEnumerable<string> failures)
    {
        var reasons = failures.ToList();
        if (!opened) reasons.Add("media did not open");
        else if (finalPosition <= initialPosition) reasons.Add("playback position did not advance");
        return reasons.Count == 0 ? "Verdict: PLAYS\n"
            : "Verdict: FAILS - " + string.Join("; ", reasons.Select(reason => DebugScreenText.Compact(reason, 300)).Distinct()) + "\n";
    }
}
#endif
