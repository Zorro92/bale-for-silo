using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Bale.Core.Connection;
using Bale.Core.People;
using Bale.Core.Settings;

namespace Bale.Core.Tests;

/// <summary>
/// Settings › General › Top Menu › Show Audiobooks (TvSettingsScreen.kt:1124-1137, the manifest's client_local
/// nav.show_audiobooks): the Audiobooks tab, Search's Audiobooks chip and a person's audiobooks stay hidden until the
/// viewer turns it on, even when the server has an audiobook library (TvMediaDestinations.kt:16-24, TvMainShell.kt:327-341,
/// TvSearchViewModel.kt, TvPersonDetailViewModel.kt). Music is never gated. The Xbox wiring is pinned by source.
/// </summary>
public class ShowAudiobooksTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static UserLibrary L(string id, string type, int order) => new() { Id = id, Name = id, Type = type, SortOrder = order };

    private static IReadOnlyList<LibraryTab> AllTabs() =>
        LibraryTabs.From([L("books", "audiobooks", 1), L("films", "movies", 2), L("songs", "music", 3), L("shows", "series", 4)]);

    [Fact]
    public void TheAudiobooksTabShowsOnlyWhenTurnedOnAndMusicAlwaysShows()
    {
        Assert.Equal([LibraryTabType.Movies, LibraryTabType.Series, LibraryTabType.Music],
            LibraryTabs.Visible(AllTabs(), ShowAudiobooksSetting.Default).Select(tab => tab.Type));
        Assert.Equal([LibraryTabType.Movies, LibraryTabType.Series, LibraryTabType.Music, LibraryTabType.Audiobooks],
            LibraryTabs.Visible(AllTabs(), showAudiobooks: true).Select(tab => tab.Type));
        // The tabs shown are the same tabs, libraries and all, so the shell's selection survives a re-filter.
        var all = AllTabs();
        Assert.All(LibraryTabs.Visible(all, showAudiobooks: false), tab => Assert.Contains(tab, all));
        Assert.Empty(LibraryTabs.Visible(LibraryTabs.From([L("books", "audiobook", 1)]), showAudiobooks: false));
    }

    [Fact]
    public void SearchOffersTheAudiobooksChipOnlyWithTheTab()
    {
        Assert.Equal([SearchMediaFilter.All, SearchMediaFilter.Movies, SearchMediaFilter.Series],
            SearchMediaFilter.FromTabs(LibraryTabs.Visible(AllTabs(), showAudiobooks: false)));
        Assert.Equal([SearchMediaFilter.All, SearchMediaFilter.Movies, SearchMediaFilter.Series, SearchMediaFilter.Audiobooks],
            SearchMediaFilter.FromTabs(LibraryTabs.Visible(AllTabs(), showAudiobooks: true)));
    }

    [Fact]
    public void TheSettingIsTheManifestsLocalKeyAndOffUnlessATrueIsStored()
    {
        Assert.Equal("nav.show_audiobooks", ShowAudiobooksSetting.ManifestKey);
        Assert.False(ShowAudiobooksSetting.Default);
        Assert.True(ShowAudiobooksSetting.FromStored(true));
        Assert.False(ShowAudiobooksSetting.FromStored(false));
        Assert.False(ShowAudiobooksSetting.FromStored(null));
        Assert.False(ShowAudiobooksSetting.FromStored("true"));
        Assert.False(ShowAudiobooksSetting.FromStored(1));
    }

    [Fact]
    public void TheSettingIsKeptPerServerAndProfileWithNeitherInItsKey()
    {
        var server = new Uri("https://home.invalid/base");
        var key = ShowAudiobooksSetting.StorageKey(server, "one");
        Assert.StartsWith("nav.show_audiobooks.", key);
        Assert.Equal(key, ShowAudiobooksSetting.StorageKey(new Uri("https://home.invalid/base/"), "one"));
        Assert.NotEqual(key, ShowAudiobooksSetting.StorageKey(server, "two"));
        Assert.NotEqual(key, ShowAudiobooksSetting.StorageKey(server, "account/one"));
        Assert.NotEqual(key, ShowAudiobooksSetting.StorageKey(new Uri("https://other.invalid/base/"), "one"));
        Assert.DoesNotContain("home.invalid", key);
        Assert.DoesNotContain("one", key[ShowAudiobooksSetting.ManifestKey.Length..]);
        Assert.Throws<ArgumentException>(() => ShowAudiobooksSetting.StorageKey(server, " "));
    }

    [Fact]
    public async Task APersonsAudiobooksChipWaitsForTheSettingAndItsTypeIsNotProbed()
    {
        var stub = new StubHandler()
            .Enqueue("""{"items":[{"type":"audiobooks"},{"type":"music"}]}""")
            .Enqueue("""{"total":2}""")
            .Enqueue("""{"total":0}""")
            .Enqueue("""{"total":3}""");
        using var client = new SiloClient(new Uri("https://silo.example.com"), new ClientIdentity("d", "Test", "xbox", "Bale"), stub);

        Assert.Equal([PersonMediaFilter.All, PersonMediaFilter.Movies, PersonMediaFilter.Music],
            await client.People.FiltersAsync("p", showAudiobooks: false));
        Assert.Equal(["movie", "series", "music"], stub.Requests.Skip(1).Select(request =>
            Regex.Match(request.RequestUri!.Query, "(?:^|[?&])type=([^&]*)").Groups[1].Value));
    }

    [Fact]
    public void GeneralEndsWithTheTopMenuGroupInAndroidsWords()
    {
        var page = XDocument.Parse(Read("SettingsPage.xaml"));
        var general = page.Descendants().Single(element => element.Attribute(Xaml + "Name")?.Value == "GeneralPane");
        var groups = general.Elements(Ui + "StackPanel").ToList();
        // TvGeneralSettingsPane's order, less the groups Bale has no controls for.
        Assert.Equal(["PROFILE AT LAUNCH", "HOME SECTIONS", "TOP MENU"],
            groups.Select(group => (string?)group.Elements(Ui + "TextBlock").First().Attribute("Text")));
        var topMenu = groups[2];
        var row = Assert.Single(topMenu.Elements(Ui + "Button"));
        Assert.Equal("ShowAudiobooksButton", row.Attribute(Xaml + "Name")?.Value);
        Assert.Equal("OnShowAudiobooksClick", row.Attribute("Click")?.Value);
        Assert.Equal("{StaticResource SkylineSettingsValueRowStyle}", row.Attribute("Style")?.Value);
        var texts = row.Descendants(Ui + "TextBlock").ToList();
        Assert.Equal("Show Audiobooks", texts[0].Attribute("Text")?.Value);
        Assert.Equal("ShowAudiobooksValue", texts[1].Attribute(Xaml + "Name")?.Value);
        Assert.Equal("Adds an Audiobooks tab to the top menu when your server has an audiobook library. Hidden by default.",
            topMenu.Elements(Ui + "TextBlock").Last().Attribute("Text")?.Value);
    }

    [Fact]
    public void TheSwitchIsAGeneralRowThatWritesThisXboxsSetting()
    {
        var code = Read("SettingsPage.cs.txt");
        // Up/Down (and the pivot scroll that follows focus) reach it as General's last row.
        Assert.Contains("_ => [ProfileSelectionButton, HomeSectionsButton, ShowAudiobooksButton],", code);
        var click = Section(code, "private void OnShowAudiobooksClick(", "private void UpdatePolicy()");
        Assert.Contains("_returnTarget = ShowAudiobooksButton;", click);
        Assert.Contains("TopMenuSettings.SetShowAudiobooks(client, profileId, !TopMenuSettings.ShowAudiobooks(client))", click);
        Assert.Contains("UpdateShowAudiobooks();", Section(code, "protected override void OnNavigatedTo(", "protected override void OnNavigatedFrom("));
    }

    [Fact]
    public void TheShellFiltersItsTabsAndRefiltersOnTheWayBackFromSettings()
    {
        var code = Read("ShellPage.cs.txt");
        var show = Section(code, "private void ShowRootTabs(", "private async Task LoadProfileSubtitleAsync(");
        Assert.Contains("LibraryTabs.Visible(", show);
        Assert.Contains("Tabs.ItemsSource = _tabs.ToList();", show);
        var navigated = Section(code, "protected override async void OnNavigatedTo(", "private void ShowRootTabs(");
        // TvMainShell.kt re-reads the setting on each route change: Back from Settings applies a switch made there.
        var back = Section(navigated, "if (_settingsReturnHome)", "return;");
        Assert.True(back.IndexOf("ShowRootTabs(TopMenuSettings.ShowAudiobooks(Session.Client));", StringComparison.Ordinal)
            < back.IndexOf("Select(_tabs[0]);", StringComparison.Ordinal));
        Assert.Contains("ShowRootTabs(TopMenuSettings.ShowAudiobooks(Session.Client));", navigated[navigated.IndexOf("_rootTabs.Clear();", StringComparison.Ordinal)..]);
        // The rebuilt bar's checked tab is set again once laid out.
        Assert.Contains("UpdateChecked();", Section(code, "public void RestoreFocus()", "if (_personalReturnToProfile)"));
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    private static string Section(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, from);
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, to);
        return text[start..end];
    }
}
