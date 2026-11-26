using System.Globalization;
using Bale.Core.Playback;

internal sealed record PlayPlanOptions(bool ClaimAv1, string? UrlFile, int HoldSeconds, string? SaveHead = null, int HeadMegabytes = 0,
    string? SaveRange = null, long RangeOffset = 0, int RangeBytes = 0, string? FileId = null, int Av1MaxHeight = 2160,
    int? SubtitleIndex = null, string Delivery = "auto", double? StartSeconds = null,
    IReadOnlyList<double>? ReanchorSeconds = null, bool InspectHls = false, bool Hdr10 = false, bool FallbackClaims = false)
{
    internal static bool TryParse(IEnumerable<string> arguments, out PlayPlanOptions options, out string error)
    {
        options = new(false, null, 0);
        error = "invalid play-plan options";
        var args = arguments.ToArray();
        var profile = new List<string>();
        var holdSeen = false;
        var av1HeightSeen = false;
        var deliverySeen = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--delivery" when !deliverySeen && i + 1 < args.Length:
                    var delivery = args[++i];
                    if (delivery is not ("auto" or "hls" or "remux")) return false;
                    deliverySeen = true;
                    options = options with { Delivery = delivery };
                    break;
                case "--start" when options.StartSeconds is null && i + 1 < args.Length:
                    if (!Position(args[++i], out var start)) return false;
                    options = options with { StartSeconds = start };
                    break;
                case "--reanchor" when i + 1 < args.Length:
                    if (!Position(args[++i], out var target) || options.ReanchorSeconds?.Count >= 16) return false;
                    options = options with { ReanchorSeconds = [..options.ReanchorSeconds ?? [], target] };
                    break;
                case "--inspect-hls" when !options.InspectHls:
                    options = options with { InspectHls = true };
                    break;
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
                case "--subtitle" when options.SubtitleIndex is null && i + 1 < args.Length:
                    // --subtitle <ordinal|-1>: the Detail picker's choice (-1 = Off); omitted = Auto.
                    if (!int.TryParse(args[++i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var subtitle)
                        || subtitle < -1) return false;
                    options = options with { SubtitleIndex = subtitle };
                    break;
                case "--hdr10" when !options.Hdr10:
                    options = options with { Hdr10 = true };
                    break;
                case "--fallback-claims" when !options.FallbackClaims:
                    options = options with { FallbackClaims = true };
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

    private static bool Position(string value, out double position) =>
        double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out position)
        && double.IsFinite(position) && position is >= 0 and <= 31_536_000;

    // hls: same as the app's DebugPlaybackDelivery.Apply, package HLS without changing decode claims.
    // remux: only the original is withdrawn, as the app's audio-mapping failure replan does, so the
    // server itself chooses between its progressive and HLS packagers.
    internal PlaybackStartRequestV3 Apply(PlaybackStartRequestV3 start) => Delivery is not ("hls" or "remux") ? start : start with
    {
        ClientPlaybackContext = start.ClientPlaybackContext with
        {
            Deliveries = start.ClientPlaybackContext.Deliveries.ToDictionary(pair => pair.Key,
                pair => (Delivery == "hls" ? pair.Key == "hls" : pair.Key != "original_http")
                    ? pair.Value : pair.Value with { Enabled = false, SupportedOnDevice = false }),
        },
    };

    // --claim-av1 remains a compatibility no-op. AV1 is claimed as the app does with a usable rav1g.dll
    // (owner, 2026-11-26: rav1g's 2160p60 entry at 40 Mbps beside dav1d's 1080p60, and its HDR AV1 tone
    // map); --av1-max-height 1080 plans like a build without rav1g (dav1d's 1080p60 alone).
    // The Series X decoder report (docs/player-plan.md "GPU decode"): what PlaybackClaims.DetectAsync
    // claims after a successful probe on the owner's console.
    internal static HardwareDecodeLimits SeriesXDecoders { get; } = new(H264: new(1920, 1080),
        HevcMain: new(7680, 4320), HevcMain10: new(7680, 4320), Vp9Profile0: new(3840, 2160), Vp9Profile2: new(3840, 2160),
        Vc1: new(1920, 1080), Mpeg2: new(1920, 1080));

    internal DeviceCapabilities Capabilities()
    {
        // Default: plan like the app on a Series X with a 1080p SDR display (the owner's setup): the
        // console's decoder report, an exact SDR display and the console tone map, as the app claims
        // with its production defaults. --hdr10 plans like the app's `hdr pretend`: the same decoders
        // and an exact HDR10 display, so 4K HDR originals can be checked. --fallback-claims plans like
        // a console whose decoder probe failed (H.264/HEVC 1080p baseline, no VP9/VC-1/MPEG-2).
        var rav1g = Av1MaxHeight == 2160;
        return FallbackClaims
            ? ConsoleEvidence.ForHardware(null, rav1g)
            : ConsoleEvidence.ForHardware(SeriesXDecoders, rav1g) with
            {
                Hdr = Hdr10 ? new() { Hdr10 = true } : new(),
                DisplayHdrEvidence = "exact",
                OriginalDynamicRangeMappingSupported = !Hdr10,
                Av1DynamicRangeMappingSupported = rav1g,
            };
    }
}
