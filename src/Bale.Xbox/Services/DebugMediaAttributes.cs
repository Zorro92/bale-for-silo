#if DEBUG_SERVER
using System.Globalization;
using System.Text;

namespace Bale.Xbox.Services;

/// <summary>
/// Every attribute of a video media type, by Media Foundation name (Windows SDK mfapi.h). Debug only: compares what
/// Windows' own source and FFmpegInteropX's MSS hand the system decoder for the same file (the first-10-bit-after-boot
/// slowdown trips on Bale's paths, not on Windows' own player; docs/player-plan.md).
/// </summary>
internal static class DebugMediaAttributes
{
    private const int MaxBlobBytes = 96;

    internal static string Describe(IDictionary<Guid, object>? properties)
    {
        if (properties is null) return "attrs=unavailable";
        var parts = new List<string>();
        foreach (var (key, value) in properties)
            parts.Add(Name(key) + "=" + Value(key, value));
        parts.Sort(StringComparer.Ordinal);
        return "attrs(" + parts.Count.ToString(CultureInfo.InvariantCulture) + ") " + string.Join("; ", parts);
    }

    private static string Name(Guid key) => Names.TryGetValue(key, out var name) ? name : key.ToString("D");

    private static string Value(Guid key, object? value)
    {
        switch (value)
        {
            case null: return "null";
            case ulong packed when Names.TryGetValue(key, out var name) && name is "FRAME_SIZE" or "FRAME_RATE" or "PIXEL_ASPECT_RATIO"
                or "FRAME_RATE_RANGE_MAX" or "FRAME_RATE_RANGE_MIN":
                return string.Create(CultureInfo.InvariantCulture, $"{packed >> 32}:{packed & 0xffffffff}");
            case Guid guid: return Subtypes.TryGetValue(guid, out var subtype) ? subtype : guid.ToString("D");
            case byte[] blob:
                var hex = new StringBuilder(blob.Length * 2);
                foreach (var b in blob.AsSpan(0, Math.Min(blob.Length, MaxBlobBytes))) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return string.Create(CultureInfo.InvariantCulture, $"blob[{blob.Length}]{hex}{(blob.Length > MaxBlobBytes ? "…" : "")}");
            case bool flag: return flag ? "true" : "false";
            case string text: return "\"" + DebugScreenText.Compact(text, 60) + "\"";
            case IFormattable number: return number.ToString(null, CultureInfo.InvariantCulture);
            default: return value.GetType().Name;
        }
    }

    // MFVideoFormat_* and MFMediaType_Video, for the subtype/major type values.
    private static readonly Dictionary<Guid, string> Subtypes = new()
    {
        [new Guid("73646976-0000-0010-8000-00aa00389b71")] = "Video",
        [new Guid("3231564e-0000-0010-8000-00aa00389b71")] = "NV12",
        [new Guid("30313050-0000-0010-8000-00aa00389b71")] = "P010",
        [new Guid("43564548-0000-0010-8000-00aa00389b71")] = "HEVC",
        [new Guid("53564548-0000-0010-8000-00aa00389b71")] = "HEVC_ES",
        [new Guid("34363248-0000-0010-8000-00aa00389b71")] = "H264",
        [new Guid("3f40f4f0-5622-4ff8-b6d8-a17a584bee5e")] = "H264_ES",
        [new Guid("30395056-0000-0010-8000-00aa00389b71")] = "VP90",
        [new Guid("31305641-0000-0010-8000-00aa00389b71")] = "AV1",
    };

    // MF_MT_* video attributes from the Windows SDK's mfapi.h (generated; audio attributes left out).
    private static readonly Dictionary<Guid, string> Names = new()
    {
        [new Guid("1652c33d-d6b2-4012-b834-72030849a37d")] = "FRAME_SIZE",
        [new Guid("168f1b4a-3e91-450f-aea7-e4baeadae5ba")] = "MPEG2_HDCP",
        [new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e")] = "AVG_BITRATE",
        [new Guid("21a800f5-3189-4797-beba-f13cd9a31a5e")] = "DEPTH_VALUE_UNIT",
        [new Guid("24974215-1b7b-41e4-8625-ac469f2dedaa")] = "TIMESTAMP_CAN_BE_DTS",
        [new Guid("261e9d83-9529-4b8f-a111-8b9c950a81a9")] = "MPEG4_SAMPLE_DESCRIPTION",
        [new Guid("31e3991d-f701-4b2f-b426-8ae3bda9e04b")] = "MPEG2_FLAGS",
        [new Guid("359ce3a5-af00-49ca-a2f4-2ac94ca82b61")] = "H264_USAGE",
        [new Guid("3afd0cee-18f2-4ba5-a110-8bea502e1f92")] = "COMPRESSED",
        [new Guid("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3")] = "MPEG_SEQUENCE_HEADER",
        [new Guid("3e23d450-2c75-4d25-a00e-b91670d12327")] = "YUV_MATRIX",
        [new Guid("3f5b106f-6bc2-4ee3-b7ed-8902c18f5351")] = "VIDEO_NO_FRAME_ORDERING",
        [new Guid("45256d30-7215-4576-9336-b0f1bcd59bb2")] = "H264_MAX_MB_PER_SEC",
        [new Guid("47537213-8cfb-4722-aa34-fbc9e24d77b8")] = "CUSTOM_VIDEO_PRIMARIES",
        [new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f")] = "MAJOR_TYPE",
        [new Guid("4b7f6bc3-8b13-40b2-a993-abf630b8204e")] = "PAN_SCAN_ENABLED",
        [new Guid("4c547c24-af9a-4f38-96ad-978773cf53e7")] = "DECODER_USE_MAX_RESOLUTION",
        [new Guid("4d0e73e5-80ea-4354-a9d0-1176ceb028ea")] = "PAD_CONTROL_FLAGS",
        [new Guid("50253128-c110-4de4-98ae-46a324fae6da")] = "MAX_LUMINANCE_LEVEL",
        [new Guid("51267a39-dd0c-4bb9-a7bd-9173ad4b131c")] = "MPEGH_AUDIO_PROFILE_LEVEL_INDICATION",
        [new Guid("5229ba10-e29d-4f80-a59c-df4f180207d2")] = "MPEG2_TIMECODE",
        [new Guid("5315d8a0-87c5-4697-b793-6606c67c049b")] = "VIDEO_3D_FORMAT",
        [new Guid("53a0529c-890b-4216-8bf9-599367ad6d20")] = "VIDEO_LIGHTING",
        [new Guid("54f486dd-9327-4f6d-80ab-6f709ebb4cce")] = "MPEG4_TRACK_TYPE",
        [new Guid("58d4bf57-6f52-4733-a195-a9e29ecf9e27")] = "MAX_FRAME_AVERAGE_LUMINANCE_LEVEL",
        [new Guid("5d959b0d-4cbf-4d04-919f-3f5f7f284211")] = "ALPHA_MODE",
        [new Guid("5fb0fce9-be5c-4935-a811-ec838f8eed93")] = "TRANSFER_FUNCTION",
        [new Guid("60b1a998-dc01-40ce-9736-aba845a2dbdc")] = "H264_SUPPORTED_USAGES",
        [new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6")] = "DEFAULT_STRIDE",
        [new Guid("65df2370-c773-4c33-aa64-843e068efb0c")] = "VIDEO_CHROMA_SITING",
        [new Guid("66758743-7e5f-400d-980a-aa8596c85696")] = "GEOMETRIC_APERTURE",
        [new Guid("67be144c-88b7-4ca9-9628-c808d5262217")] = "DECODER_MAX_DPB_COUNT",
        [new Guid("68aca3cc-22d0-44e6-85f8-28167197fa38")] = "SOURCE_CONTENT_HINT",
        [new Guid("6a8ac47e-519c-4f18-9bb3-7eeaaea5594d")] = "H264_SUPPORTED_RATE_CONTROL_MODES",
        [new Guid("6d283f42-9846-4410-afd9-654d503b1a54")] = "PALETTE",
        [new Guid("6d4b7bff-5629-4404-948c-c634f4ce26d4")] = "VIDEO_3D_LEFT_IS_BASE",
        [new Guid("705177d8-45cb-11e0-ac7d-b91ce0d72085")] = "H264_RATE_CONTROL_MODES",
        [new Guid("75da5090-910b-4a03-896c-7b898feea5af")] = "IN_BAND_PARAMETER_SET",
        [new Guid("79614dde-9187-48fb-b8c7-4d52689de649")] = "PAN_SCAN_APERTURE",
        [new Guid("799cabd6-3508-4db4-a3c7-569cd533deb1")] = "AVG_BIT_ERROR_RATE",
        [new Guid("825d55e4-4f12-4197-9eb3-59b6e4710f06")] = "MPEG2_CONTENT_PACKET",
        [new Guid("83877f5e-0444-4e28-8479-6db0989b8c09")] = "CONTAINER_RATE_SCALING",
        [new Guid("839a4460-4e7e-4b4f-ae79-cc08905c7b27")] = "MIN_MASTERING_LUMINANCE",
        [new Guid("8437d4b9-d448-4fcd-9b6b-839bf96c7798")] = "VIDEO_RENDERER_EXTENSION_PROFILE",
        [new Guid("85e299b2-90e3-4fe8-b2f5-c067e0bfe57a")] = "H264_LAYOUT_PER_STREAM",
        [new Guid("8772f323-355a-4cc7-bb78-6d61a048ae82")] = "DRM_FLAGS",
        [new Guid("89a52c01-f282-48d2-b522-22e6ae633199")] = "H264_SUPPORTED_SYNC_FRAME_TYPES",
        [new Guid("91a49eb5-1d20-4b42-ace8-804269bf95ed")] = "MPEG2_ONE_FRAME_PER_PACKET",
        [new Guid("91f67885-4333-4280-97cd-bd5a6c03a06e")] = "MPEG_START_TIME_CODE",
        [new Guid("96f66574-11c5-4015-8666-bff516436da7")] = "VIDEO_LEVEL",
        [new Guid("9aa7e155-b64a-4c1d-a500-455d600b6560")] = "MPEG4_CURRENT_SAMPLE_ENTRY",
        [new Guid("9ea2d63d-53f0-4a34-b94e-9de49a078cb3")] = "H264_SIMULCAST_SUPPORT",
        [new Guid("a20af9e8-928a-4b26-aaa9-f05c74cac47c")] = "MPEG2_STANDARD",
        [new Guid("a505d3ac-f930-436e-8ede-93a509ce23b2")] = "OUTPUT_BUFFER_NUM",
        [new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036")] = "VIDEO_PROFILE",
        [new Guid("b6bc765f-4c3b-40a4-bd51-2535b66fe09d")] = "USER_DATA",
        [new Guid("b8ebefaf-b718-4e04-b0a9-116775e3321b")] = "FIXED_SIZE_SAMPLES",
        [new Guid("bb077e8a-dcbf-42eb-af60-418df98aa495")] = "VIDEO_3D_NUM_VIEWS",
        [new Guid("bb12d222-2bdb-425e-91ec-2308e189a58f")] = "REALTIME_CONTENT",
        [new Guid("bb3bd508-490a-11e0-99e4-1316dfd72085")] = "H264_CAPABILITIES",
        [new Guid("c16eb52b-73a1-476f-8d62-839d6a020652")] = "MAX_KEYFRAME_SPACING",
        [new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11")] = "VIDEO_NOMINAL_RANGE",
        [new Guid("c380465d-2271-428c-9b83-ecea3b4a85c1")] = "VIDEO_ROTATION",
        [new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0")] = "FRAME_RATE",
        [new Guid("c5acc4fd-0304-4ecf-809f-47bc97ff63bd")] = "SECURE",
        [new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6")] = "PIXEL_ASPECT_RATIO",
        [new Guid("c8be1937-4d64-4549-8343-a8086c0bfda5")] = "H264_SUPPORTED_SLICE_MODES",
        [new Guid("c9173739-5e56-461c-b713-46fb995cb95f")] = "ALL_SAMPLES_INDEPENDENT",
        [new Guid("cb5e88cf-7b5b-476b-85aa-1ca5ae187555")] = "VIDEO_3D",
        [new Guid("d2e7558c-dc1f-403f-9a72-d28bb1eb3b5e")] = "FRAME_RATE_RANGE_MIN",
        [new Guid("d6c6b997-272f-4ca1-8d00-8042111a0ff6")] = "MAX_MASTERING_LUMINANCE",
        [new Guid("d7388766-18fe-48c6-a177-ee894867c8c4")] = "MINIMUM_DISPLAY_APERTURE",
        [new Guid("d7be3fe0-2bc7-492d-b843-61a1919b70c3")] = "ORIGINAL_4CC",
        [new Guid("dad3ab78-1990-408b-bce2-eba673dacc10")] = "SAMPLE_SIZE",
        [new Guid("dbfbe4d7-0740-4ee0-8192-850ab0e21935")] = "VIDEO_PRIMARIES",
        [new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd")] = "INTERLACE_MODE",
        [new Guid("e27362f1-b136-41d1-9594-3a7e4febf2d1")] = "FORWARD_CUSTOM_SEI",
        [new Guid("e3371d41-b4cf-4a05-bd4e-20b88bb2c4d6")] = "FRAME_RATE_RANGE_MAX",
        [new Guid("e3854272-f715-4757-ba90-1b696c773457")] = "H264_RESOLUTION_SCALING",
        [new Guid("ec298493-0ada-4ea1-a4fe-cbbd36ce9331")] = "VIDEO_3D_FIRST_IS_LEFT",
        [new Guid("ed336efd-244f-428d-9153-28f399458890")] = "FORWARD_CUSTOM_NALU",
        [new Guid("ed461cd6-ec9f-416a-a8a3-26d7d31018d7")] = "VIDEO_H264_NO_FMOASO",
        [new Guid("f5929986-4c45-4fbb-bb49-6cc534d05b9b")] = "H264_MAX_CODEC_CONFIG_DELAY",
        [new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5")] = "SUBTYPE",
        [new Guid("f8993abe-d937-4a8f-bbca-6966fe9e1152")] = "H264_SVC_CAPABILITIES",
        [new Guid("fd5ac489-0917-4bb6-9d54-3122bf70144b")] = "DEPTH_MEASUREMENT",
    };
}
#endif
