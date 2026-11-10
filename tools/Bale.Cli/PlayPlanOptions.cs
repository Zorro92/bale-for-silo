using System.Globalization;
using Bale.Core.Playback;

internal sealed record PlayPlanOptions(bool ClaimAv1, string? UrlFile, int HoldSeconds, string? SaveHead = null, int HeadMegabytes = 0,
    string? SaveRange = null, long RangeOffset = 0, int RangeBytes = 0, string? FileId = null, int Av1MaxHeight = 1080,
    int? SubtitleIndex = null, int Av1MaxFps = 30)
{
    internal static bool TryParse(IEnumerable<string> arguments, out PlayPlanOptions options, out string error)
    {
        options = new(false, null, 0);
        error = "invalid play-plan options";
        var args = arguments.ToArray();
        var profile = new List<string>();
        var holdSeen = false;
        var av1HeightSeen = false;
        var av1FpsSeen = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--file" when options.FileId is null && i + 1 < args.Length:
                    var fileId = args[++i];
                    if (string.IsNullOrWhiteSpace(fileId) || fileId.StartsWith("--", StringComparison.Ordinal)) return false;
                    options = options with { FileId = fileId };
                    break;
                case "--av1-max-height" when !av1HeightSeen && i + 1 < args.Length:
                    var height = args[++i] switch { "1080" => 1080, "2160" => 2160, _ => 0 };
                    if (height == 0) return false;
                    av1HeightSeen = true;
                    options = options with { Av1MaxHeight = height };
                    break;
                case "--av1-max-fps" when !av1FpsSeen && i + 1 < args.Length:
                    // Debug probe for sources above the measured 30 fps bound.
                    var fps = args[++i] switch { "30" => 30, "60" => 60, _ => 0 };
                    if (fps == 0) return false;
                    av1FpsSeen = true;
                    options = options with { Av1MaxFps = fps };
                    break;
                case "--subtitle" when options.SubtitleIndex is null && i + 1 < args.Length:
                    // --subtitle <ordinal|-1>: the Detail picker's choice (-1 = Off); omitted = Auto.
                    if (!int.TryParse(args[++i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var subtitle)
                        || subtitle < -1) return false;
                    options = options with { SubtitleIndex = subtitle };
                    break;
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
                case "--save-head" when options.SaveHead is null && i + 2 < args.Length:
                    // --save-head <path> <MiB>: debug copy of the first bytes of the original file.
                    var head = args[++i];
                    if (string.IsNullOrWhiteSpace(head) || head.StartsWith("--", StringComparison.Ordinal)) return false;
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var megabytes)
                        || megabytes is < 1 or > 2048) return false;
                    options = options with { SaveHead = head, HeadMegabytes = megabytes };
                    break;
                case "--save-range" when options.SaveRange is null && i + 3 < args.Length:
                    // --save-range <path> <offset> <bytes>: debug copy of one byte range (e.g. a Matroska Cues index).
                    var range = args[++i];
                    if (string.IsNullOrWhiteSpace(range) || range.StartsWith("--", StringComparison.Ordinal)) return false;
                    if (!long.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                        || !int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
                        || bytes is < 1 or > 64 * 1024 * 1024) return false;
                    options = options with { SaveRange = range, RangeOffset = offset, RangeBytes = bytes };
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

    // --claim-av1 remains a compatibility no-op; only the explicit height probe expands AV1.
    internal DeviceCapabilities Capabilities() => Av1MaxHeight == 2160 || Av1MaxFps != 30
        ? ConsoleEvidence.Default with { VideoDecode = Av1MaxHeight == 2160
            ? ConsoleEvidence.BuildVideoDecode(3840, 2160, Av1MaxFps) : ConsoleEvidence.BuildVideoDecode(av1MaxFps: Av1MaxFps) }
        : ConsoleEvidence.Default;
}
