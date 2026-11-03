namespace Bale.Core.Profiles;

/// <summary>TvProfileAvatarPresets.kt: the same five styles and deterministic 18-picture batches.</summary>
public static class ProfileAvatarPresets
{
    public sealed record Style(string Id, string Label, string Summary);
    public sealed record Preset(string StyleId, string Seed)
    {
        public string Reference => $"preset:dicebear:{StyleId}:{Seed}";
        public Uri ImageUrl => new($"https://api.dicebear.com/9.x/{Uri.EscapeDataString(StyleId)}/png?seed={Uri.EscapeDataString(Seed)}&size=256");
    }

    public const string DefaultStyleId = "fun-emoji";
    public static IReadOnlyList<Style> Styles { get; } = [
        new("identicon", "Identicon", "Geometric patterns"),
        new("initials", "Initials", "Letter-based with bold backgrounds"),
        new("bottts-neutral", "Bottts", "Modular robot faces"),
        new("fun-emoji", "Fun Emoji", "Big, colorful faces"),
        new("pixel-art-neutral", "Pixel Art", "Retro pixel characters"),
    ];

    public static IReadOnlyList<Preset> Batch(string styleId, int batch = 0)
    {
        var style = Styles.Any(style => style.Id == styleId) ? styleId : DefaultStyleId;
        return Enumerable.Range(0, 18).Select(index => new Preset(style,
            $"{Adjectives[Mod(batch * 7 + index * 3 + style.Length, Adjectives.Length)]}-{Nouns[Mod(batch * 11 + index * 5 + style.Length * 2, Nouns.Length)]}")).ToArray();
    }

    public static Preset? Parse(string? value)
    {
        var parts = value?.Trim().Split(':', 4);
        return parts is { Length: 4 } && parts[0].Equals("preset", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("dicebear", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(parts[2]) && !string.IsNullOrWhiteSpace(parts[3])
            ? new(parts[2].Trim(), parts[3].Trim()) : null;
    }

    public static string EffectiveReference(string styleId, string? selectedSeed, int batch, string name, string? fallbackAvatar)
    {
        if (styleId == "initials") return new Preset(styleId, string.IsNullOrWhiteSpace(name) ? "You" : name.Trim()).Reference;
        if (!string.IsNullOrWhiteSpace(selectedSeed)) return new Preset(styleId, selectedSeed).Reference;
        if (!string.IsNullOrWhiteSpace(fallbackAvatar) && (Parse(fallbackAvatar) is not { } preset || preset.StyleId == styleId))
            return fallbackAvatar;
        return Batch(styleId, batch)[0].Reference;
    }

    private static int Mod(int value, int count) => (value % count + count) % count;
    private static readonly string[] Adjectives = [
        "cosmic", "jelly", "starlight", "mango", "bubble", "neon", "marble", "comet", "snappy", "pepper", "glimmer", "ripple", "pocket", "candy",
        "ember", "mochi", "sprout", "twinkle", "whiz", "plasma", "mint", "velvet", "crunchy", "sunbeam", "toffee", "splash", "boomer", "lunar",
        "fizzy", "biscuit", "rocket", "cobalt",
    ];
    private static readonly string[] Nouns = [
        "otter", "rocket", "fox", "cookie", "gecko", "bunny", "penguin", "puffin", "tiger", "panda", "saturn", "wizard", "skater", "nebula",
        "robot", "pirate", "meteor", "sprite", "pebble", "nova", "dragon", "donut", "parrot", "panther", "goblin", "muffin", "laser", "pickle",
        "falcon", "sunset", "toaster", "bandit",
    ];
}
