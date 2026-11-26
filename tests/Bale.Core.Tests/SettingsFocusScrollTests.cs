using Bale.Core.Settings;

namespace Bale.Core.Tests;

/// <summary>
/// Where a Settings list scrolls for a focused row: Compose's TV PivotBringIntoViewSpec (BringIntoViewSpec.android.kt),
/// which TvSettingsScreen.kt's panes and TvSettingsPickerSheet keep. The Xbox side (SettingsScroll) is pinned by source in
/// SettingsNavigationWiringTests.
/// </summary>
public class SettingsFocusScrollTests
{
    // The Playback pane on the console: 540 high less 40/40 outer padding and the 53-high pane header.
    private const double Pane = 407;

    [Fact]
    public void AFocusedRowSettlesThirtyPercentDownTheList()
    {
        Assert.Equal(0.3, SettingsFocusScroll.PivotFraction);
        // A row whose top sits 600 into the content settles 122.1 below the viewport's top, reached from above or below.
        Assert.Equal(600 - 0.3 * Pane, SettingsFocusScroll.Target(600, 38, Pane, 2000)!.Value, 6);
    }

    [Fact]
    public void AGroupHeaderStaysOnScreenAboveItsFirstRow()
    {
        // A group header (8 + 15.48 + 2 high) and the group's 4 spacing sit above its first row; the pivot leaves 122.1.
        var rowTop = 900.0;
        var offset = SettingsFocusScroll.Target(rowTop, 38, Pane, 2000)!.Value;
        Assert.True(rowTop - offset >= 8 + 15.48 + 2 + 4);
    }

    [Fact]
    public void RowsNearEitherEndLeaveTheListWhereItCanGo()
    {
        // The first rows sit above the pivot: the list stays at its top.
        Assert.Equal(0, SettingsFocusScroll.Target(30, 38, Pane, 900));
        Assert.Equal(0, SettingsFocusScroll.Target(110, 38, Pane, 900));
        // The last rows can't reach the pivot: the list stops at its end (the 40 bottom padding below them).
        Assert.Equal(900, SettingsFocusScroll.Target(1290, 38, Pane, 900));
        // A list that fits never scrolls.
        Assert.Equal(0, SettingsFocusScroll.Target(300, 38, Pane, 0));
        Assert.Equal(0, SettingsFocusScroll.Target(300, 38, Pane, -20));
    }

    [Fact]
    public void ARowTooTallForTheSpaceBelowThePivotEndsOnTheViewportsEnd()
    {
        // childSmallerThanParent and less room below the pivot (70) than the row (80): its bottom on the viewport's end.
        Assert.Equal(500 - (100 - 80), SettingsFocusScroll.Target(500, 80, 100, 2000)!.Value, 6);
        // A row taller than the viewport keeps its top on the pivot.
        Assert.Equal(500 - 30, SettingsFocusScroll.Target(500, 150, 100, 2000)!.Value, 6);
    }

    [Fact]
    public void NothingScrollsBeforeTheListAndRowAreMeasured()
    {
        Assert.Null(SettingsFocusScroll.Target(600, 38, 0, 2000));
        Assert.Null(SettingsFocusScroll.Target(600, 0, Pane, 2000));
    }

    [Fact]
    public void ThePickerSheetPivotsTheCheckedLanguage()
    {
        // TvSettingsPickerSheet: a 460 card less its 67 header and divider leaves 392 for the rows; each row is 34.34 with
        // 4 between and 10 of padding above. The 21st language settles 30% down the list.
        const double viewport = 460 - 67 - 1;
        const double row = 34.34;
        var top = 10 + 20 * (row + 4);
        var extent = 10 + 38 * row + 37 * 4 + 10;
        var offset = SettingsFocusScroll.Target(top, row, viewport, extent - viewport)!.Value;
        Assert.Equal(top - 0.3 * viewport, offset, 6);
        Assert.InRange(top - offset, 0, viewport - row);
    }
}
