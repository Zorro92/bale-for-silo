namespace Bale.Core.Detail;

/// <summary>The Detail hero row's actions (TVDetailActions.swift TVDetailAction).</summary>
public enum DetailHeroAction
{
    Play,
    StartOver,
    Edition,
    Version,
    Audio,
    Subtitles,
    Watchlist,
    More,
}

/// <summary>
/// TVDetailActions.swift TVDetailActionRow entryAction (Apple 067e6cee; owner, 2026-11-18, a deliberate deviation from
/// Android's selectorFocus): entering the hero row lands on the action last focused there, else Play. The memory lasts
/// for the page (Apple's focusResetKey is the page's content id), so a season change keeps it.
/// </summary>
public static class DetailHeroFocus
{
    /// <summary>
    /// Play stands in for a remembered action that cannot take focus now (Start Over gone, a selector disabled or
    /// hidden for this episode); Watchlist stands in for Play while Play cannot take focus.
    /// </summary>
    public static DetailHeroAction Entry(DetailHeroAction? remembered, Func<DetailHeroAction, bool> canFocus)
    {
        var action = remembered ?? DetailHeroAction.Play;
        if (action != DetailHeroAction.Play && !canFocus(action)) action = DetailHeroAction.Play;
        return action == DetailHeroAction.Play && !canFocus(action) ? DetailHeroAction.Watchlist : action;
    }
}
