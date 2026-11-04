using System.Globalization;
using Bale.Core.Playback;

internal sealed record PlayPlanOptions(bool ClaimAv1, string? UrlFile, int HoldSeconds)
{
    internal static bool TryParse(IEnumerable<string> arguments, out PlayPlanOptions options, out string error)
    {
        options = new(false, null, 0);
        error = "invalid play-plan options";
        var args = arguments.ToArray();
        var profile = new List<string>();
        var holdSeen = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--claim-av1" when !options.ClaimAv1:
                    options = options with { ClaimAv1 = true };
                    break;
                case "--url-file" when options.UrlFile is null && i + 1 < args.Length:
                    var path = args[++i];
                    if (string.IsNullOrWhiteSpace(path) || path.StartsWith("--", StringComparison.Ordinal)) return false;
                    options = options with { UrlFile = path };
                    break;
                case "--hold" when !holdSeen && i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                        || seconds is < 0 or > 3600) return false;
                    holdSeen = true;
                    options = options with { HoldSeconds = seconds };
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) return false;
                    profile.Add(args[i]);
                    break;
            }
        }
        // Keep the old explicit-profile syntax, but never permit playback writes to a real profile.
        if (profile.Count > 0 && string.Join(' ', profile) != "Bale Test")
        {
            error = "play-plan only uses the Bale Test profile";
            return false;
        }
        error = string.Empty;
        return true;
    }

    internal DeviceCapabilities Capabilities() => ClaimAv1
        ? DebugPlaybackCapabilities.Av1SoftwareClaim : ConsoleEvidence.Default;
}
