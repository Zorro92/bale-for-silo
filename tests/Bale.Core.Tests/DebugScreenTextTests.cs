using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugScreenTextTests
{
    [Fact]
    public void EmptyScreenIsExplicit()
    {
        Assert.Equal("Visible: none\n", DebugScreenText.Visible([]));
    }

    [Theory]
    [InlineData("  Clear\r\n filters\t now  ", 100, "Clear filters now")]
    [InlineData("abcdefghijk", 8, "abcde...")]
    [InlineData("abcde", 5, "abcde")]
    public void CompactKeepsEveryDescriptionOnOneBoundedLine(string text, int limit, string expected)
    {
        Assert.Equal(expected, DebugScreenText.Compact(text, limit));
    }

    [Fact]
    public void GroupsKeepFirstSeenOrderAndReportEveryState()
    {
        DebugVisibleControl[] controls =
        [
            new("popup/Actions", "Apply", Focused: true, Checked: true),
            new("Cards", "First", Selected: true),
            new("popup/Actions", "Cancel", Disabled: true),
        ];
        Assert.Equal("Visible:\n  popup/Actions:\n    Apply [focused] [checked]\n    Cancel [disabled]\n  Cards:\n    First [selected]\n",
            DebugScreenText.Visible(controls));
    }

    [Fact]
    public void EachGroupHasItsOwnCapAndAccurateOmissionCount()
    {
        var controls = Enumerable.Range(1, 15).Select(i => new DebugVisibleControl("Cards", $"Card {i}"))
            .Concat([new DebugVisibleControl("Actions", "Play")]);
        var output = DebugScreenText.Visible(controls);
        Assert.Contains("    Card 12\n    +3 more\n  Actions:\n    Play\n", output);
        Assert.DoesNotContain("Card 13", output);
    }

    [Fact]
    public void GroupCapCountsOmittedGroupsNotOmittedControls()
    {
        DebugVisibleControl[] controls = [new("A", "One"), new("B", "Two"), new("C", "Three"), new("C", "Four")];
        Assert.Equal("Visible:\n  A:\n    One\n  +2 more groups\n", DebugScreenText.Visible(controls, maxGroups: 1));
    }

    [Fact]
    public void LongLabelsCannotHideTheirStateFlagsOrAddOutputLines()
    {
        var output = DebugScreenText.Visible([new("Cards\r\nAll", new string('x', 500) + "\nextra", Disabled: true)]);
        Assert.Contains("  Cards All:\n", output);
        Assert.Contains("... [disabled]\n", output);
        Assert.Equal(3, output.Count(ch => ch == '\n'));
        Assert.DoesNotContain("extra", output);
    }

    [Fact]
    public void WhereTreatsItemCollectionsAsGroupsAndKeepsTheirFocusableItemContainers()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.cs.txt"));
        var controls = source[source.IndexOf("private static IEnumerable<Control> VisibleControls", StringComparison.Ordinal)
            ..source.IndexOf("private static DebugVisibleControl VisibleControl", StringComparison.Ordinal)];
        Assert.Contains("VisibleElements(root).OfType<Control>()", controls); // Panels cannot be control lines.
        Assert.Contains("control.IsTabStop && control is not ItemsControl && IsOnScreen(control)", controls);
        Assert.DoesNotContain("not ContentControl", controls); // AOT projects ListViewItem/GridViewItem this way.
        Assert.Contains("controls.AddRange(VisibleControls(root)", source); // Popups use the same filter.
        Assert.Contains("controls.AddRange(VisibleControls(windowRoot)", source);

        var output = DebugScreenText.Visible(
        [
            new("ItemsControl#PickerActions", "Button#Manage \"Manage\""),
            new("ItemsControl#PickerActions", "Button#ChangeServer \"Change server\""),
            new("ItemsControl#PickerActions", "Button#SignOut \"Sign out\""),
        ]);
        Assert.Contains("  ItemsControl#PickerActions:\n", output);
        Assert.DoesNotContain("    ItemsControl#PickerActions", output);
        Assert.DoesNotContain("Grid#PickerPanel", output);
        Assert.Equal(3, output.Split('\n').Count(line => line.StartsWith("    Button", StringComparison.Ordinal)));
    }

    [Fact]
    public void FocusTextUsesVisibleDescendantsAndRejectsHiddenAncestors()
    {
        // Visibility/layout belong to UWP, so check that focus uses the same path as Visible.
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.cs.txt"));
        var focus = source[source.IndexOf("private static string DescribeFocus()", StringComparison.Ordinal)
            ..source.IndexOf("private static async Task<(string Status, string Body)> WaitAsync", StringComparison.Ordinal)];
        Assert.Contains("TextOf(element, 6, visibleOnly: true)", focus);
        Assert.Contains("element.Opacity <= 0 || depth > 60) yield break;", source);
        var text = source[source.IndexOf("private static List<string> TextsOf", StringComparison.Ordinal)
            ..source.IndexOf("private static Rect Bounds", StringComparison.Ordinal)];
        Assert.Contains("Ancestors(root).OfType<UIElement>().Any", text);
        Assert.Contains("ancestor.Visibility != Visibility.Visible", text);
        Assert.Contains("ancestor.Opacity <= 0 || ancestor is PasswordBox", text);
        Assert.Contains("VisibleElements(root).OfType<TextBlock>()", text);
        Assert.Contains("IsOnScreen(block)", text);
    }

    [Fact]
    public void ScreenInspectionStaysGuardedAndUsesTheActiveNavigationContent()
    {
        // Source checks cannot replace compiling/running UWP, but protect the release-build guard.
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.cs.txt"));
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("case (\"GET\", \"/where\"):", source);
        Assert.Contains("frame.Content as Page", source);
        Assert.Contains("VisualTreeHelper.GetOpenPopups(Window.Current)", source);
        Assert.Contains("if (element is PasswordBox) yield break;", source);
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bale-debug.sh"));
        Assert.Contains("where) curl -fsS --max-time 5 \"$BASE/where\" ;;", script);
    }
}
