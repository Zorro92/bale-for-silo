using System.Text;

namespace Bale.Core.Auth;

/// <summary>A stable warm avatar tint per profile ID, shared by the official phone and TV pickers.</summary>
public static class ProfileTilePalette
{
    // silo-android android-shared/.../common/ui/marquee/MarqueeProfileAvatar.kt,
    // ProfileTilePalette: sRGB float channels rounded to 8-bit, as Compose's Color constructor does.
    private static readonly uint[] Colors =
    [
        0xFFD97561, // coral:      (0.850, 0.460, 0.380)
        0xFF668FB8, // slate blue: (0.400, 0.560, 0.720)
        0xFFB08A66, // warm tan:   (0.690, 0.540, 0.400)
        0xFF759E85, // sage:       (0.460, 0.620, 0.520)
        0xFFC77A85, // dusty rose: (0.780, 0.480, 0.520)
        0xFF8F7AB8, // lavender:   (0.560, 0.480, 0.720)
        0xFF5C949E, // teal:       (0.360, 0.580, 0.620)
        0xFFC7A361, // amber:      (0.780, 0.640, 0.380)
    ];

    /// <summary>Opaque ARGB tint, using the official unsigned 64-bit DJB2 hash over UTF-8 bytes, not the name.</summary>
    public static uint Tint(string profileId)
    {
        var hash = 5381UL;
        foreach (var value in Encoding.UTF8.GetBytes(profileId))
        {
            hash = unchecked(hash * 33 + value);
        }
        return Colors[(int)(hash % (ulong)Colors.Length)];
    }
}
