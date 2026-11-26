namespace Bale.Core.Settings;

/// <summary>
/// What Silo does when playback enters a detected intro: <c>playback.intro_skip_mode</c>, settings contract revision 7
/// (silo-server docs/design/2026-08-16-intro-skip-mode.md; silo-android shared/.../domain/player/IntroSkipMode.kt).
/// Every server with the v2 settings API knows it (revision 7 predates API v2), so Bale never falls back to the
/// deprecated <c>playback.auto_skip_intro</c> boolean.
/// </summary>
public enum IntroSkipMode
{
    /// <summary>Entering an intro does nothing: no pill, no skip.</summary>
    Never,
    /// <summary>Offer a "Skip Intro" pill for the spec's 5 s (<c>INTRO_PROMPT_SECONDS</c>).</summary>
    Ask,
    /// <summary>Skip at once and offer an "Intro skipped / Watch Intro" undo.</summary>
    Always,
}

public static class IntroSkipModes
{
    public const string Key = "playback.intro_skip_mode";

    /// <summary>The contract default, exactly what <c>auto_skip_intro = false</c> did.</summary>
    public const IntroSkipMode Default = IntroSkipMode.Ask;

    /// <summary>Settings › Playback › Episodes: the row and the picker title (settings_control_intros).</summary>
    public const string Title = "Skip Intros";

    /// <summary>The picker's order (IntroSkipMode.entries).</summary>
    public static IReadOnlyList<IntroSkipMode> All { get; } = [IntroSkipMode.Never, IntroSkipMode.Ask, IntroSkipMode.Always];

    public static string Wire(IntroSkipMode mode) => mode switch
    {
        IntroSkipMode.Never => "never",
        IntroSkipMode.Always => "always",
        _ => "ask",
    };

    /// <summary>A stored or wire value; null for absent or unrecognised input.</summary>
    public static IntroSkipMode? FromWire(string? value) => value switch
    {
        "never" => IntroSkipMode.Never,
        "ask" => IntroSkipMode.Ask,
        "always" => IntroSkipMode.Always,
        _ => null,
    };

    /// <summary>The label each mode is offered under (settings_introSkip_never / _ask / _always, the manifest's labels).</summary>
    public static string Label(IntroSkipMode mode) => mode switch
    {
        IntroSkipMode.Never => "Never",
        IntroSkipMode.Always => "Skip automatically",
        _ => "Ask to skip",
    };
}
