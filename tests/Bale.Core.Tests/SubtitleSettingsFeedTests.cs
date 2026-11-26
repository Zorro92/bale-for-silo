using System.Net;
using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Bale.Core.Settings;

namespace Bale.Core.Tests;

/// <summary>
/// Settings › Subtitles' reads and writes (TvSettingsScreen.kt TvSubtitleSettingsPane; ProfileSettingsController.kt for
/// the profile preferences, AndroidPlayerSettingsStore.kt for the appearance) against the settings contract's keys.
/// </summary>
public class SubtitleSettingsFeedTests
{
    private static SiloConnection Connect(StubHandler handler) => new(new Uri("https://example.com/silo/"),
        new ClientIdentity("device", "TV", "xbox", "Bale"), transport: handler);

    private sealed class Memory : ISettingsMemory
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Read(string key) => Values.GetValueOrDefault(key);
        public void Write(string key, string value) => Values[key] = value;
    }

    private const string Effective = """
        {"items":[
          {"key":"playback.subtitle_language","value":"pt-BR","scope":"profile","source":"stored","suggested_values":["en","ja",""]},
          {"key":"playback.subtitle_mode","value":"Always","scope":"profile_device","source":"stored"},
          {"key":"playback.show_forced_subtitles","value":false,"scope":"profile","source":"stored"},
          {"key":"playback.subtitle_appearance","value":{"fontSize":"small","fontColor":"#FACC15","backgroundStyle":"shadow","bogus":1},"scope":"profile_device","source":"stored"}
        ],"revision":30}
        """;

    [Fact]
    public async Task ReadsTheFourKeysInOneEffectiveRead()
    {
        var handler = new StubHandler().Enqueue(Effective);
        using var connection = Connect(handler);
        var feed = new SubtitleSettingsFeed(connection.Api);
        Assert.Equal(new SubtitlePreferences(), feed.Preferences);
        Assert.Equal(new SubtitleAppearanceSetting(SubtitleAppearance.Default), feed.Appearance);

        Assert.Equal(new SubtitlePreferences("pt-BR", SubtitleMode.Always, false, true), await feed.RefreshAsync());
        Assert.Equal(["en", "ja"], feed.LanguageSuggestions);
        // A sparse stored object merges over the contract default; a property the schema does not know is ignored.
        Assert.Equal(new SubtitleAppearanceSetting(
            SubtitleAppearance.Default with { FontSize = SubtitleFontSize.Small, FontColor = "#facc15", BackgroundStyle = SubtitleBackgroundStyle.Shadow },
            DeviceOverride: true, Known: true, TextOpacitySupported: true), feed.Appearance);

        var request = handler.Requests.Single();
        Assert.Equal("/silo/api/v2/settings/values/effective", request.RequestUri!.AbsolutePath);
        foreach (var key in new[] { "playback.subtitle_language", "playback.subtitle_mode", "playback.show_forced_subtitles", "playback.subtitle_appearance" })
            Assert.Contains("keys=" + key, request.RequestUri.Query);
    }

    [Fact]
    public async Task UnsetAndUnknownValuesReadAsTheContractDefaults()
    {
        // The legacy empty string means unset; a mode Bale does not know is the default; an unset language is "".
        var handler = new StubHandler()
            .Enqueue("""
                {"items":[{"key":"playback.subtitle_language","value":null,"scope":"default","source":"default"},
                {"key":"playback.subtitle_mode","value":"","scope":"profile"},
                {"key":"playback.subtitle_appearance","value":"box","scope":"profile"}],"revision":13}
                """)
            .Enqueue("""{"items":[{"key":"playback.subtitle_language","value":"Off","scope":"profile"},{"key":"playback.subtitle_mode","value":"forced_only"}],"revision":30}""");
        using var connection = Connect(handler);
        var feed = new SubtitleSettingsFeed(connection.Api);
        Assert.Equal(new SubtitlePreferences(Known: true), await feed.RefreshAsync());
        // Revision 13 predates textOpacity (revision 14); a value that is not an object is the default.
        Assert.Equal(new SubtitleAppearanceSetting(SubtitleAppearance.Default, false, true, TextOpacitySupported: false), feed.Appearance);
        Assert.Equal(new SubtitlePreferences(Known: true), await feed.RefreshAsync());
        Assert.True(feed.Appearance.TextOpacitySupported);
    }

    [Fact]
    public async Task AFailedReadThrowsAndKeepsTheLastValues()
    {
        var handler = new StubHandler().Enqueue(Effective).Enqueue(HttpStatusCode.InternalServerError,
            """{"type":"about:blank","title":"Internal Server Error","status":500}""");
        using var connection = Connect(handler);
        var feed = new SubtitleSettingsFeed(connection.Api);
        await feed.RefreshAsync();
        var known = feed.Preferences;
        await Assert.ThrowsAnyAsync<Exception>(() => feed.RefreshAsync());
        Assert.Equal(known, feed.Preferences);
        // Detail's and Up Next's read answers null instead, so they fall back to the profile record.
        using var failing = Connect(new StubHandler().Enqueue(HttpStatusCode.InternalServerError,
            """{"type":"about:blank","title":"Internal Server Error","status":500}"""));
        Assert.Null(await new SubtitleSettingsFeed(failing.Api).ReadPreferencesAsync());
        feed.Reset();
        Assert.Equal(new SubtitlePreferences(), feed.Preferences);
        Assert.Equal(new SubtitleAppearanceSetting(SubtitleAppearance.Default), feed.Appearance);
        Assert.Empty(feed.LanguageSuggestions);
    }

    [Fact]
    public async Task ProfilePreferencesWriteScopeProfileThenReresolve()
    {
        // ProfileSettingsController.kt: each edit writes scope=profile and re-resolves; None clears the profile's row.
        var handler = new StubHandler()
            .Enqueue("""{"key":"playback.subtitle_language","value":"fr","scope":"profile","revision":5}""")
            .Enqueue("""{"items":[{"key":"playback.subtitle_language","value":"fr","scope":"profile"}],"revision":30}""")
            .Enqueue(HttpStatusCode.NoContent, "")
            .Enqueue("""{"items":[],"revision":30}""")
            .Enqueue("""{"key":"playback.subtitle_mode","value":"off","scope":"profile","revision":6}""")
            // A device row outranks the profile's: the screen shows what the server resolves.
            .Enqueue("""{"items":[{"key":"playback.subtitle_mode","value":"always","scope":"profile_device"}],"revision":30}""")
            .Enqueue("""{"key":"playback.show_forced_subtitles","value":false,"scope":"profile","revision":7}""")
            .Enqueue("""{"items":[{"key":"playback.show_forced_subtitles","value":false,"scope":"profile"}],"revision":30}""");
        using var connection = Connect(handler);
        var feed = new SubtitleSettingsFeed(connection.Api);
        Assert.Equal("fr", (await feed.SetLanguageAsync(" fr ")).Language);
        Assert.Equal("", (await feed.SetLanguageAsync(SettingsLanguages.Unset)).Language);
        Assert.Equal(SubtitleMode.Always, (await feed.SetModeAsync(SubtitleMode.Off)).Mode);
        Assert.False((await feed.SetShowForcedAsync(false)).ShowForced);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/silo/api/v2/settings/values/playback.subtitle_language", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("scope=profile", handler.Requests[0].RequestUri!.Query);
        Assert.DoesNotContain("profile_device", handler.Requests[0].RequestUri!.Query);
        Assert.Equal("""{"value":"fr"}""", handler.RequestBodies[0]);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Delete, handler.Requests[2].Method);
        Assert.Equal("/silo/api/v2/settings/values/playback.subtitle_language", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.Contains("scope=profile", handler.Requests[2].RequestUri!.Query);
        Assert.DoesNotContain("profile_device", handler.Requests[2].RequestUri!.Query);
        Assert.Equal("""{"value":"off"}""", handler.RequestBodies[4]);
        Assert.Equal("/silo/api/v2/settings/values/playback.show_forced_subtitles", handler.Requests[6].RequestUri!.AbsolutePath);
        Assert.Equal("""{"value":false}""", handler.RequestBodies[6]);
    }

    [Fact]
    public async Task AWriteThatLandsKeepsItsValueWhenOnlyTheReresolveFails()
    {
        var handler = new StubHandler()
            .Enqueue("""{"key":"playback.subtitle_mode","value":"off","scope":"profile","revision":6}""")
            .Enqueue(HttpStatusCode.ServiceUnavailable, """{"type":"about:blank","title":"Unavailable","status":503}""");
        using var connection = Connect(handler);
        var feed = new SubtitleSettingsFeed(connection.Api);
        Assert.Equal(new SubtitlePreferences(Mode: SubtitleMode.Off, Known: true), await feed.SetModeAsync(SubtitleMode.Off));
    }

    [Fact]
    public async Task AFailedWriteThrowsAndChangesNothing()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.BadRequest, """{"type":"about:blank","title":"Bad Request","status":400}""");
        using var connection = Connect(handler);
        var feed = new SubtitleSettingsFeed(connection.Api);
        await Assert.ThrowsAnyAsync<Exception>(() => feed.SetLanguageAsync("fr"));
        Assert.Equal(new SubtitlePreferences(), feed.Preferences);
    }

    [Fact]
    public async Task AppearanceEditsSaveTheProfilesUnlessOnlyOnThisDeviceIsOn()
    {
        var handler = new StubHandler()
            .Enqueue("""{"items":[{"key":"playback.subtitle_appearance","value":{"fontSize":"large"},"scope":"profile"}],"revision":30}""")
            .Enqueue("""{"key":"playback.subtitle_appearance","value":{},"scope":"profile","revision":8}""")
            .Enqueue("""{"key":"playback.subtitle_appearance","value":{},"scope":"profile_device","revision":9}""")
            .Enqueue("""{"key":"playback.subtitle_appearance","value":{},"scope":"profile_device","revision":10}""")
            .Enqueue(HttpStatusCode.NoContent, "")
            .Enqueue("""{"items":[{"key":"playback.subtitle_appearance","value":{"position":"top"},"scope":"profile"}],"revision":30}""");
        using var connection = Connect(handler);
        var feed = new SubtitleSettingsFeed(connection.Api);
        await feed.RefreshAsync();
        var bigger = feed.Appearance.Value with { FontSize = SubtitleFontSize.XXLarge };
        Assert.Equal(new SubtitleAppearanceSetting(bigger, false, true), await feed.SetAppearanceAsync(bigger));
        // Only on This Device: on copies the appearance to this device; edits then stay on this device.
        Assert.Equal(new SubtitleAppearanceSetting(bigger, true, true), await feed.SetAppearanceOnlyOnThisDeviceAsync(true));
        var top = bigger with { Position = SubtitlePosition.Top };
        Assert.Equal(new SubtitleAppearanceSetting(top, true, true), await feed.SetAppearanceAsync(top));
        // Off deletes this device's value and rereads the profile's.
        Assert.Equal(new SubtitleAppearanceSetting(SubtitleAppearance.Default with { Position = SubtitlePosition.Top }, false, true),
            await feed.SetAppearanceOnlyOnThisDeviceAsync(false));

        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal("/silo/api/v2/settings/values/playback.subtitle_appearance", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.DoesNotContain("profile_device", handler.Requests[1].RequestUri!.Query);
        Assert.Contains("scope=profile", handler.Requests[1].RequestUri!.Query);
        Assert.Equal("""
            {"value":{"fontSize":"xxlarge","fontFamily":"sans-serif","fontColor":"#ffffff","backgroundColor":"#000000","backgroundStyle":"box","backgroundOpacity":75,"textOpacity":100,"textOutline":false,"textOutlineColor":"#000000","position":"bottom"}}
            """, handler.RequestBodies[1]);
        Assert.Contains("scope=profile_device", handler.Requests[2].RequestUri!.Query);
        Assert.Contains("\"fontSize\":\"xxlarge\"", handler.RequestBodies[2]);
        Assert.Contains("scope=profile_device", handler.Requests[3].RequestUri!.Query);
        Assert.Contains("\"position\":\"top\"", handler.RequestBodies[3]);
        Assert.Equal(HttpMethod.Delete, handler.Requests[4].Method);
        Assert.Contains("scope=profile_device", handler.Requests[4].RequestUri!.Query);
        Assert.Equal(HttpMethod.Get, handler.Requests[5].Method);
    }

    [Fact]
    public async Task AServerBeforeRevision14GetsNoTextOpacity()
    {
        var handler = new StubHandler()
            .Enqueue("""{"items":[{"key":"playback.subtitle_appearance","value":{"textOpacity":40},"scope":"profile"}],"revision":13}""")
            .Enqueue("""{"key":"playback.subtitle_appearance","value":{},"scope":"profile","revision":8}""");
        using var connection = Connect(handler);
        var feed = new SubtitleSettingsFeed(connection.Api);
        await feed.RefreshAsync();
        await feed.SetAppearanceAsync(SubtitleAppearance.Default);
        // additionalProperties is false: an older server refuses an object that carries it (SubtitleAppearance.kt).
        Assert.DoesNotContain("textOpacity", handler.RequestBodies[1]);
        Assert.False(feed.Appearance.TextOpacitySupported);
    }

    [Fact]
    public void UseDeviceSettingsIsStoredOnThisDevicePerServerAndProfile()
    {
        var memory = new Memory();
        var profile = "a";
        using var connection = Connect(new StubHandler());
        var feed = new SubtitleSettingsFeed(connection.Api, () => "https://example.com/silo/\n" + profile) { Memory = memory };
        Assert.False(feed.MatchDevice);
        Assert.True(feed.SetMatchDevice(true));
        Assert.True(feed.MatchDevice);
        var key = Assert.Single(memory.Values.Keys);
        // A hash: no address or profile id in clear.
        Assert.StartsWith("subtitles_match_device.", key);
        Assert.DoesNotContain("example", key);
        profile = "b";
        Assert.False(feed.MatchDevice);
        profile = "a";
        Assert.True(feed.SetMatchDevice(false));
        Assert.False(feed.MatchDevice);
        // Without a profile nothing is stored.
        var none = new SubtitleSettingsFeed(connection.Api, () => null) { Memory = memory };
        Assert.False(none.SetMatchDevice(true));
        Assert.False(none.MatchDevice);
    }

    [Fact]
    public async Task TheEffectiveAppearanceFollowsTheConsoleOnlyWhileUseDeviceSettingsIsOn()
    {
        var handler = new StubHandler().Enqueue("""{"items":[{"key":"playback.subtitle_appearance","value":{"position":"top"},"scope":"profile"}],"revision":30}""");
        using var connection = Connect(handler);
        var feed = new SubtitleSettingsFeed(connection.Api, () => "server\nprofile") { Memory = new Memory() };
        await feed.RefreshAsync();
        var system = new SystemCaptionStyle(FontColor: "#ffff00", FontScale: 2);
        Assert.Equal(feed.Appearance.Value, feed.EffectiveAppearance(system));
        feed.SetMatchDevice(true);
        var effective = feed.EffectiveAppearance(system);
        Assert.Equal("#ffff00", effective.FontColor);
        Assert.Equal(SubtitleFontSize.XXLarge, effective.FontSize);
        Assert.Equal(SubtitlePosition.Top, effective.Position);
        // No readable system settings: the stored appearance.
        Assert.Equal(feed.Appearance.Value, feed.EffectiveAppearance(null));
    }

    [Fact]
    public void DetailAndUpNextStartAutoFromTheCanonicalValuesThenTheProfileRecord()
    {
        // TvItemDetailViewModel.kt loadSubtitlePreferences: "" (no preference) is null for the preview.
        Assert.Equal(new Detail.SubtitleAutoContext(null, "auto", true), new SubtitlePreferences(Known: true).AutoContext());
        Assert.Equal(new Detail.SubtitleAutoContext("ja", "always", false),
            SubtitlePreferences.AutoContextFor(new SubtitlePreferences("ja", SubtitleMode.Always, false, true),
                new Profile { SubtitleLanguage = "en", SubtitleMode = "off", ShowForcedSubtitles = true }));
        // The canonical read failed: the profile record's fields, show forced on when the record leaves it out.
        Assert.Equal(new Detail.SubtitleAutoContext("en", "off", true),
            SubtitlePreferences.AutoContextFor(null, new Profile { SubtitleLanguage = "en", SubtitleMode = "off" }));
        Assert.Equal(new Detail.SubtitleAutoContext("en", "off", false),
            SubtitlePreferences.AutoContextFor(new SubtitlePreferences(), new Profile { SubtitleLanguage = "en", SubtitleMode = "off", ShowForcedSubtitles = false }));
        Assert.Null(SubtitlePreferences.AutoContextFor(null, null));
    }

    [Fact]
    public async Task AChangeInThePaneIsWhatAutoStartsFromAfterwards()
    {
        // The Settings pane writes the profile's language; the next Detail read returns it, and Auto picks the track.
        var handler = new StubHandler()
            .Enqueue("""{"key":"playback.subtitle_language","value":"es","scope":"profile","revision":5}""")
            .Enqueue("""{"items":[{"key":"playback.subtitle_language","value":"es","scope":"profile"},{"key":"playback.subtitle_mode","value":"always","scope":"profile"}],"revision":30}""")
            .Enqueue("""{"items":[{"key":"playback.subtitle_language","value":"es","scope":"profile"},{"key":"playback.subtitle_mode","value":"always","scope":"profile"}],"revision":30}""");
        using var connection = Connect(handler);
        var settings = new SubtitleSettingsFeed(connection.Api);
        await settings.SetLanguageAsync("es");
        var detail = new SubtitleSettingsFeed(connection.Api);
        var context = SubtitlePreferences.AutoContextFor(await detail.ReadPreferencesAsync(),
            new Profile { SubtitleLanguage = "en", SubtitleMode = "auto" });
        Assert.Equal(new Detail.SubtitleAutoContext("es", "always", true), context);
    }
}
