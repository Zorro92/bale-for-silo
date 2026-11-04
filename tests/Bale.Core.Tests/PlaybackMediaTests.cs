using System.Net;
using System.Text;
using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Bale.Core.Playback;
using Microsoft.Kiota.Http.HttpClientLibrary.Extensions;
using static Bale.Core.Tests.PlaybackMediaTestSupport;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class PlaybackMediaTests
{
    [Fact]
    public void OpenMediaCapturesSourceMetadataAndTheTwoClocks()
    {
        using var client = Create(new StubHandler());
        var media = client.Playback.OpenMedia(Plan());
        Assert.Equal(new Uri("https://silo.example.com/api/v2/stream/session/file.mp4"), media.Uri);
        Assert.Equal("video/mp4", media.MimeType);
        Assert.Equal("mp4", media.Container);
        Assert.Equal("original_http", media.Delivery);
        Assert.False(media.IsHls);
        Assert.Equal(1800, media.DurationSeconds);
        Assert.Equal(new PlaybackTimeline(120, 0, 120), media.Timeline);
    }

    [Fact]
    public void MissingSourceDurationRemainsUnknown()
    {
        using var client = Create(new StubHandler());
        var plan = Plan();
        plan.Source = null;
        plan.Timeline = null;
        var media = client.Playback.OpenMedia(plan);
        Assert.Null(media.DurationSeconds);
        Assert.Equal(new PlaybackTimeline(0, 0, 0), media.Timeline);
    }

    [Theory]
    [InlineData("HLS", "original_http", "/api/v2/stream/session/master", true)]
    [InlineData("http", "transcoded_HLS", "/api/v2/stream/session/master", true)]
    [InlineData("http", "original_http", "/api/v2/stream/session/master.M3U8?seek=0", true)]
    [InlineData(null, null, "/api/v2/stream/session/master", false)]
    public void HlsDetectionUsesProtocolDeliveryOrPath(string? protocol, string? delivery, string url, bool expected)
    {
        using var client = Create(new StubHandler());
        var plan = Plan(url);
        plan.Stream!.Protocol = protocol;
        plan.Delivery = delivery;
        Assert.Equal(expected, client.Playback.OpenMedia(plan).IsHls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://silo.example.com/api/v2/stream/session/file.mp4")]
    [InlineData("https://elsewhere.example.com/api/v2/stream/session/file.mp4")]
    public void OpenMediaRejectsAnUnplayableOrAbsoluteUrlWithoutEchoingIt(string? url)
    {
        using var client = Create(new StubHandler());
        var error = Assert.Throws<InvalidDataException>(() => client.Playback.OpenMedia(Plan(url)));
        Assert.Equal("The playback plan has no playable stream.", error.Message);
    }

    [Fact]
    public void OpenMediaRequiresASignedInProfile()
    {
        using var client = new SiloClient(new Uri("https://silo.example.com"), Identity, new StubHandler());
        Assert.Throws<InvalidOperationException>(() => client.Playback.OpenMedia(Plan()));
    }

    [Fact]
    public async Task MediaRequestsUseACopyOfStringHeadersAndTheCapturedProfileOption()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        var plan = Plan();
        plan.Stream!.Headers = new StreamV3_headers
        {
            AdditionalData = new Dictionary<string, object>
            {
                ["aUtHoRiZaTiOn"] = "untrusted-plan-bearer",
                ["X-Profile-Id"] = "untrusted-profile",
                ["X-Profile-Token"] = "untrusted-proof",
                ["X-Plan-Header"] = "original",
                ["X-Not-A-String"] = 42,
            },
        };
        var media = client.Playback.OpenMedia(plan);
        plan.Stream.Headers.AdditionalData["X-Plan-Header"] = "mutated";
        using var response = await media.SendAsync(media.Uri, 10, 20, HttpCompletionOption.ResponseHeadersRead, default);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("bytes=10-29", request.Headers.Range!.ToString());
        Assert.Equal("original", Assert.Single(request.Headers.GetValues("X-Plan-Header")));
        Assert.False(request.Headers.Contains("X-Not-A-String"));
        Assert.Equal("Bearer test-access", request.Headers.Authorization!.ToString());
        Assert.Equal("profile", Assert.Single(request.Headers.GetValues("X-Profile-Id")));
        Assert.Equal("test-profile-proof", Assert.Single(request.Headers.GetValues("X-Profile-Token")));
        Assert.Equal("device", Assert.Single(request.Headers.GetValues("X-Silo-Device-Id")));
        var option = Assert.IsType<ProfileRequestOption>(request.GetRequestOption<ProfileRequestOption>());
        Assert.Equal("profile", option.ProfileId);
    }

    [Fact]
    public async Task ChildRequestsCanUseServerMintedQueries()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        var media = client.Playback.OpenMedia(Plan());
        var child = new Uri("https://silo.example.com/api/v2/playback/transcode/session/segment.ts?sequence=2&grant=opaque");
        using var response = await media.SendAsync(child, 0, null, HttpCompletionOption.ResponseHeadersRead, default);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(child, request.RequestUri);
        Assert.Equal("bytes=0-", request.Headers.Range!.ToString());
        Assert.Equal("profile", Assert.Single(request.Headers.GetValues("X-Profile-Id")));
    }

    [Theory]
    [InlineData("https://elsewhere.example.com/api/v2/stream/session/file.mp4")]
    [InlineData("https://silo.example.com/api/v2/account/me")]
    public async Task DisallowedRequestsFailBeforeSending(string uri)
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        var media = client.Playback.OpenMedia(Plan());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            media.SendAsync(new Uri(uri), null, null, HttpCompletionOption.ResponseHeadersRead, default));
        Assert.DoesNotContain(uri, error.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("other-profile")]
    [InlineData("profile")]
    public async Task CapturedMediaCannotAdoptALaterProfileSelection(string nextProfile)
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        var media = client.Playback.OpenMedia(Plan());
        client.UseProfile(nextProfile);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            media.SendAsync(media.Uri, null, null, HttpCompletionOption.ResponseHeadersRead, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CapturedMediaCannotAdoptAnotherLogin()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        var media = client.Playback.OpenMedia(Plan());
        client.SignIn(new TokenPair { AccessToken = "other-test-access", ExpiresIn = 3600 }, "profile");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => media.GetBytesAsync(media.Uri, null, null, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BytesIncludeTheContentTypeAndTheRequestedRange()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        var media = client.Playback.OpenMedia(Plan());
        var (bytes, contentType) = await media.GetBytesAsync(media.Uri, 500, 1000, default);
        Assert.Equal(Data[500..1500], bytes);
        Assert.Equal("video/mp4", contentType);
        Assert.True(Assert.Single(handler.Bodies).IsDisposed);
    }

    [Fact]
    public async Task FailedByteRequestsExposeTheStatusButNoSensitiveResponseText()
    {
        var handler = new RangeHandler
        {
            ConfigureResponse = (response, _) =>
            {
                response.StatusCode = HttpStatusCode.Forbidden;
                response.ReasonPhrase = "https://silo.example.com/sensitive";
            },
        };
        using var client = Create(handler);
        var media = client.Playback.OpenMedia(Plan());
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => media.GetBytesAsync(media.Uri, null, null, default));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("The media request failed.", error.Message);
        Assert.True(Assert.Single(handler.Bodies).IsDisposed);
    }

    [Fact]
    public async Task Media401UsesTheExistingBearerRefreshPathOnce()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.Unauthorized, """{"status":401}""")
            .Enqueue("""{"access_token":"rotated-test-access","refresh_token":"rotated-test-refresh","expires_in":3600}""")
            .Enqueue("media bytes");
        using var client = Create(handler, refresh: true);
        var media = client.Playback.OpenMedia(Plan());
        var (bytes, _) = await media.GetBytesAsync(media.Uri, null, null, default);
        Assert.Equal("media bytes", Encoding.UTF8.GetString(bytes));
        Assert.Equal(3, handler.Requests.Count);
        Assert.EndsWith("/api/v2/auth/refresh", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("Bearer rotated-test-access", handler.Requests[2].Headers.Authorization!.ToString());
        Assert.Equal("profile", Assert.Single(handler.Requests[2].Headers.GetValues("X-Profile-Id")));
        Assert.Equal("test-profile-proof", Assert.Single(handler.Requests[2].Headers.GetValues("X-Profile-Token")));
    }
}
