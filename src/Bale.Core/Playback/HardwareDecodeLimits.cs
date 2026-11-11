namespace Bale.Core.Playback;

/// <summary>A picture size a hardware decoder accepts.</summary>
public readonly record struct DecodeSize(int Width, int Height);

/// <summary>
/// What the console's own hardware video decoders report: the largest picture each decoder
/// profile accepts, null when the profile is missing. Plain data, so the claims built from it
/// can be tested off the console.
/// </summary>
public sealed record HardwareDecodeLimits(DecodeSize? H264 = null, DecodeSize? HevcMain = null,
    DecodeSize? HevcMain10 = null, DecodeSize? Vp9Profile0 = null, DecodeSize? Vp9Profile2 = null)
{
    // D3D11_DECODER_PROFILE_* from the Windows SDK's d3d11.h
    // (microsoft/win32metadata, generation/WinSDK/RecompiledIdlHeaders/um/d3d11.h).
    public static readonly Guid H264Profile = new("1b81be68-a0c7-11d3-b984-00c04f2e73c5");
    public static readonly Guid HevcMainProfile = new("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0");
    public static readonly Guid HevcMain10Profile = new("107af0e0-ef1a-4d19-aba8-67a163073d13");
    public static readonly Guid Vp9Profile0Profile = new("463707f8-a1d0-4585-876d-83aa6d60b89e");
    public static readonly Guid Vp9Profile2Profile = new("a4c749ef-6ecf-48aa-8448-50a7a1165ff7");

    // DXGI_FORMAT_NV12 and DXGI_FORMAT_P010 (dxgiformat.h).
    public const int Nv12 = 103;
    public const int P010 = 104;

    /// <summary>True when no claimable profile reported a size.</summary>
    public bool IsEmpty => H264 is null && HevcMain is null && HevcMain10 is null && Vp9Profile0 is null && Vp9Profile2 is null;

    /// <summary>
    /// Folds the probe's accepted (profile, output format, size) answers into the largest size per
    /// profile. An 8-bit profile counts only with NV12 output and a 10-bit one only with P010, the
    /// formats the playback pipeline uses for those depths. Other profiles are ignored.
    /// </summary>
    public static HardwareDecodeLimits FromAcceptedSizes(IEnumerable<(Guid Profile, int Format, int Width, int Height)> accepted)
    {
        var sizes = accepted.ToArray();
        return new(Largest(H264Profile, Nv12), Largest(HevcMainProfile, Nv12), Largest(HevcMain10Profile, P010),
            Largest(Vp9Profile0Profile, Nv12), Largest(Vp9Profile2Profile, P010));

        DecodeSize? Largest(Guid profile, int format)
        {
            DecodeSize? largest = null;
            foreach (var size in sizes)
            {
                if (size.Profile != profile || size.Format != format || size.Width <= 0 || size.Height <= 0) continue;
                if (largest is not { } current || (long)size.Width * size.Height > (long)current.Width * current.Height)
                    largest = new(size.Width, size.Height);
            }
            return largest;
        }
    }
}
