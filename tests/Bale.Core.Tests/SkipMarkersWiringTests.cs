using System.Xml.Linq;

namespace Bale.Core.Tests;

/// <summary>
/// How the Xbox player uses the skip markers (TvPlayerScreen.kt, TvIntroAutoSkipBanner.kt). The page compiles only on
/// Windows, so its layout and wiring are pinned by source here; the decisions are PlaybackSkipMarkersTests and
/// IntroSkipPromptTests.
/// </summary>
public class SkipMarkersWiringTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element => element.Attribute(Xaml + "Name")?.Value == name);

    private static string? Setter(string key, string property)
    {
        var theme = XDocument.Parse(Read("Skyline.xaml"));
        return theme.Root!.Elements().Single(element => element.Attribute(Xaml + "Key")?.Value == key)
            .Elements().Single(element => element.Attribute("Property")?.Value == property).Attribute("Value")?.Value;
    }

    private static string Section(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, from);
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, to);
        return text[start..end];
    }

    private static void InOrder(string text, params string[] parts)
    {
        var at = -1;
        foreach (var part in parts)
        {
            var next = text.IndexOf(part, at + 1, StringComparison.Ordinal);
            Assert.True(next > at, "out of order or missing: " + part);
            at = next;
        }
    }

    [Fact]
    public void ThePillIsOneButtonInTheBottomEndSlotWithItsCaptionAbove()
    {
        var page = XDocument.Parse(Read("PlayerPage.xaml"));
        var host = Named(page, "SkipPillHost");
        Assert.Equal("Collapsed", host.Attribute("Visibility")?.Value);
        Assert.Equal("{StaticResource SkylinePlayerSkipPillHostStyle}", host.Attribute("Style")?.Value);
        var button = Assert.Single(host.Descendants(), element => element.Name.LocalName == "Button");
        Assert.Equal("SkipPillButton", button.Attribute(Xaml + "Name")?.Value);
        Assert.Equal("OnSkipPillClick", button.Attribute("Click")?.Value);
        // The caption sits above the capsule; the fill under the button, over the black capsule.
        var children = host.Elements().ToList();
        Assert.Equal("SkipPillCaption", children[0].Attribute(Xaml + "Name")?.Value);
        var capsule = children[1].Elements().Select(element => element.Attribute(Xaml + "Name")?.Value ?? element.Attribute("Style")?.Value).ToList();
        Assert.Equal(["{StaticResource SkylinePlayerSkipPillCapsuleStyle}", "SkipPillFill", "SkipPillButton"], capsule);
        // Above the controls in z-order, under Options and Up Next (which cover it anyway).
        var order = page.Root!.Element(page.Root.Name.Namespace + "Grid")!.Elements()
            .Select(element => element.Attribute(Xaml + "Name")?.Value).ToList();
        Assert.True(order.IndexOf("TransportControls") < order.IndexOf("SkipPillHost"));
        Assert.True(order.IndexOf("SkipPillHost") < order.IndexOf("OptionsPanel"));
        Assert.True(order.IndexOf("SkipPillHost") < order.IndexOf("UpNextCard"));
    }

    [Fact]
    public void ThePillUsesTheOfficialPromptPillGeometryAndColours()
    {
        // TvIntroPromptPill: bottom-end 32/56dp, 18sp label (× 0.86) at 32dp padding, 28dp radius, black 65%, white 25%/100%
        // ring and 55%/100% label, 14%/40% fill; caption 14sp at white 70%, 12dp in from the end and 6dp above.
        Assert.Equal("0,0,32,56", Setter("SkylinePlayerSkipPillHostStyle", "Margin"));
        Assert.Equal("Right", Setter("SkylinePlayerSkipPillHostStyle", "HorizontalAlignment"));
        Assert.Equal("Bottom", Setter("SkylinePlayerSkipPillHostStyle", "VerticalAlignment"));
        Assert.Equal("56", Setter("SkylinePlayerSkipPillStyle", "Height"));
        Assert.Equal("32,0", Setter("SkylinePlayerSkipPillStyle", "Padding"));
        Assert.Equal("15.48", Setter("SkylinePlayerSkipPillStyle", "FontSize"));
        Assert.Equal("Medium", Setter("SkylinePlayerSkipPillStyle", "FontWeight"));
        Assert.Equal("#8CFFFFFF", Setter("SkylinePlayerSkipPillStyle", "Foreground"));
        Assert.Equal("28", Setter("SkylinePlayerSkipPillCapsuleStyle", "CornerRadius"));
        Assert.Equal("#A6000000", Setter("SkylinePlayerSkipPillCapsuleStyle", "Background"));
        Assert.Equal("28", Setter("SkylinePlayerSkipPillFillStyle", "CornerRadius"));
        Assert.Equal("#24FFFFFF", Setter("SkylinePlayerSkipPillFillStyle", "Background"));
        Assert.Equal("12.04", Setter("SkylinePlayerSkipPillCaptionStyle", "FontSize"));
        Assert.Equal("#B3FFFFFF", Setter("SkylinePlayerSkipPillCaptionStyle", "Foreground"));
        Assert.Equal("0,0,12,6", Setter("SkylinePlayerSkipPillCaptionStyle", "Margin"));
        var theme = Read("Skyline.xaml");
        var template = Section(theme, "<Style x:Key=\"SkylinePlayerSkipPillStyle\"", "</Style>");
        Assert.Contains("BorderBrush=\"#40FFFFFF\" BorderThickness=\"2\" CornerRadius=\"28\"", template);
        InOrder(template, "<VisualState x:Name=\"Focused\">", "<Setter Target=\"Ring.BorderBrush\" Value=\"White\" />",
            "<Setter Target=\"Content.Foreground\" Value=\"White\" />");
        // No implicit transitions inside the template (AGENTS.md: the first layout crashed natively).
        Assert.DoesNotContain("Transition", template);
        var code = Read("PlayerPage.SkipMarkers.cs.txt");
        Assert.Contains("FromArgb(0x66, 255, 255, 255)", code);
        Assert.Contains("FromArgb(0x24, 255, 255, 255)", code);
        Assert.Contains("var target = _controls.Visible ? -(200f - 56f) : 0f;", code);
    }

    [Fact]
    public void ThePlayerSeeksThroughTheSkipQueueAndCoversThePillUnderMenus()
    {
        var code = Read("PlayerPage.SkipMarkers.cs.txt");
        // A real seek through the triggers' and scrubber's path, clamped to the file, never a native seek of its own.
        InOrder(Section(code, "private void SkipTo(", "private void ShowSkipPill("), "Math.Min(skip.Target, end)", "QueueSeekTo(target);");
        Assert.DoesNotContain("PlaybackSession.Position =", code);
        // A seek in flight counts at its target.
        Assert.Contains("Position = _skipTarget ?? SourcePosition(attempt),", code);
        // Options, Up Next and the error screen take the pill down (a new pill when it returns); the track menu only hides
        // it, so the same pill does not claim focus again (TvQuickSubtitlePicker leaves it composed).
        InOrder(Section(code, "private void ShowSkipPill()", "private void HideSkipPill()"),
            "OptionsOpen || UpNextVisible || ErrorPanel.Visibility == Visibility.Visible", "HideSkipPill();",
            "if (TrackMenuOpen)", "CollapseSkipPill();", "var appeared = _skipPillShown != (pill.Kind, pill.Run);",
            "if (pill.TakesFocus && !_scrub.Active && !_holdScan.Held)");
        InOrder(Section(code, "private void RefocusAfterSkipPill()", "private void UpdateSkipPillLift()"),
            "if (_controls.Visible) ScrubberButton.Focus(FocusState.Keyboard);", "else if (!FocusBufferingPill()) Focus(FocusState.Programmatic);");

        var lifecycle = Read("PlayerPage.cs.txt");
        InOrder(lifecycle, "_bufferingController?.NewAttempt();", "ResetSkipMarkers();", "Log.Info(\"player: start\");");
        InOrder(Section(lifecycle, "private async void OnTick(", "if (_recording || _seeking) return;"),
            "UpdateUpNext();", "UpdateControls();", "UpdateSkipMarkers();", "UpdateBufferingQuality();");
        // The buffering pill waits while a skip pill shows (TvPlayerScreen.kt bufferingPillBlocked).
        Assert.Contains("|| SkipPillVisible);", Read("PlayerPage.BufferingQuality.cs.txt"));
        // Up Next reads the closing credits from the same markers, and only for the series-page return.
        Assert.Contains("var credits = PlayingMarkers(attempt, fileId).FinalCreditsStart();", Read("PlayerPage.UpNext.cs.txt"));
    }

    [Fact]
    public void TheControllerReachesThePillAsTheOfficialRootHandlerDoes()
    {
        var controls = Read("PlayerPage.Controls.cs.txt");
        // With the controls hidden, A / Enter press a visible pill before the owner's pause.
        InOrder(Section(controls, "case PlaybackKeyAction.Select:", "case PlaybackKeyAction.RevealControls:"),
            "if (!_holdScan.Held && _skipMarkers.Pill is { TakesFocus: true } && SelectSkipPill()) break;", "var paused =");
        // Back: a scrub preview first, then the intro pill, then the buffering pill, then the controls.
        InOrder(Section(controls, "private bool BackClosesControls()", "private float _subtitleLift"),
            "if (_controls.Visible && _scrub.Active)", "if (BackDismissesSkipPill()) return true;",
            "if (BackClosesBufferingPill()) return true;", "HideControls();");
        // Up from the bar reaches the pill; a focused pill keeps its focus when the controls hide.
        Assert.Contains("if (SkipPillVisible) SkipPillButton.Focus(FocusState.Keyboard);", controls);
        InOrder(Section(controls, "private void HideControls()", "/// <summary>Back: a scrub preview first"),
            "UpdateSkipPillLift();", "if (SkipPillHasFocus()) return;", "if (!FocusBufferingPill()) Focus(FocusState.Programmatic);");
    }
}
