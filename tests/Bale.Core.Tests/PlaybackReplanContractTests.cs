using System.Net;
using System.Text;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class PlaybackReplanContractTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SeekRequestMeetsTheServerEnvelopeAndKeepsTheCurrentAttempt(int activeAttempt)
    {
        var request = Request(activeAttempt);
        using var handler = new ContractHandler();
        using var client = Create(handler);
        await client.Playback.ReplanAsync("session", request, new Installation());
        Assert.NotNull(handler.Body);
        using var body = JsonDocument.Parse(handler.Body);
        var root = body.RootElement;
        Assert.Empty(EnvelopeErrors(root));
        Assert.Equal(activeAttempt, root.GetProperty("attempt_count").GetInt32());
        Assert.Equal("seek_reanchor", root.GetProperty("operation").GetString());
        Assert.False(root.TryGetProperty("failure", out _));
        Assert.Equal(new[] { "v3:previous-failed" }, root.GetProperty("attempted_plan_keys").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("plan-0123456789", root.GetProperty("failed_plan_id").GetString());
        Assert.Equal("mount-0123456789", root.GetProperty("plan_attempt_id").GetString());
        Assert.Equal("v3:0123456789abcdef", root.GetProperty("plan_attempt_key").GetString());
        Assert.Equal("audio-selected", root.GetProperty("selected_tracks").GetProperty("audio").GetProperty("id").GetString());
        Assert.Equal(1, root.GetProperty("selected_tracks").GetProperty("audio").GetProperty("index").GetInt32());
        Assert.False(root.GetProperty("selected_tracks").TryGetProperty("subtitle", out _));
        Assert.Equal(900.5, root.GetProperty("position_seconds").GetDouble());
    }

    [Fact]
    public async Task LegacyZeroCountIsRejectedByServerValidationBeforeReplanning()
    {
        using var handler = new ContractHandler();
        using var client = Create(handler);
        var problem = await Assert.ThrowsAsync<Problem>(() => client.Playback.ReplanAsync("session",
            Request(1) with { AttemptCount = 0 }, new Installation()));
        Assert.Equal(422, problem.ResponseStatusCode);
        Assert.Equal("body.attempt_count", Assert.Single(problem.Errors!).Location);
        Assert.Contains("http=422 problem=validation_failed", PlaybackProblemLog.Describe(problem));
    }

    [Theory]
    [InlineData(0, 900)]
    [InlineData(9, 900)]
    [InlineData(1, -1)]
    [InlineData(1, double.NaN)]
    [InlineData(1, double.PositiveInfinity)]
    [InlineData(1, 31_536_001)]
    public void SeekBuilderRejectsInvalidCountOrPosition(int count, double position) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(count, position));

    private static PlaybackReplanRequestV3 Request(int activeAttempt, double position = 900.5) => PlaybackReplan.ForSeek(
        Start(new(AudioIndex: 0, SubtitleIndex: -1)), new PlaybackPlan
        {
            PlanId = "plan-0123456789", PlanAttemptKey = "v3:0123456789abcdef",
            SelectedTracks = new() { Audio = new() { Id = "audio-selected", Index = 1 } },
        }, "mount-0123456789", ["v3:previous-failed"], activeAttempt, position);

    // Exact top-level OpenAPI constraints, copied with source revision in the fixture. Check the
    // HTTP body, not just builder properties: the original regression serialized zero successfully.
    internal static IReadOnlyList<string> EnvelopeErrors(JsonElement body)
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/PlaybackReplanEnvelope.json")));
        var errors = new List<string>();
        foreach (var required in schema.RootElement.GetProperty("required").EnumerateArray())
            if (!body.TryGetProperty(required.GetString()!, out _)) errors.Add("body." + required.GetString());
        var properties = schema.RootElement.GetProperty("properties");
        foreach (var field in body.EnumerateObject())
        {
            if (!properties.TryGetProperty(field.Name, out var rules)) { errors.Add("body." + field.Name); continue; }
            var value = field.Value;
            foreach (var rule in rules.EnumerateObject())
            {
                var invalid = rule.Name switch
                {
                    "type" => rule.Value.GetString() switch
                    {
                        "string" => value.ValueKind != JsonValueKind.String,
                        "array" => value.ValueKind != JsonValueKind.Array,
                        "boolean" => value.ValueKind is not (JsonValueKind.True or JsonValueKind.False),
                        "number" => value.ValueKind != JsonValueKind.Number,
                        "integer" => value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _),
                        _ => false,
                    },
                    "minimum" => !value.TryGetDouble(out var number) || number < rule.Value.GetDouble(),
                    "maximum" => !value.TryGetDouble(out var number) || number > rule.Value.GetDouble(),
                    "minLength" => value.GetString()!.Length < rule.Value.GetInt32(),
                    "maxLength" => value.GetString()!.Length > rule.Value.GetInt32(),
                    "maxItems" => value.GetArrayLength() > rule.Value.GetInt32(),
                    "enum" => !rule.Value.EnumerateArray().Any(option => option.GetString() == value.GetString()),
                    _ => false,
                };
                if (invalid) errors.Add("body." + field.Name);
            }
        }
        return errors.Distinct().ToArray();
    }

    private sealed class ContractHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            using var body = JsonDocument.Parse(Body);
            var errors = EnvelopeErrors(body.RootElement);
            return errors.Count == 0 ? new(HttpStatusCode.OK) { Content = new StringContent(Decision, Encoding.UTF8, "application/json") }
                : new(HttpStatusCode.UnprocessableEntity) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    type = "https://siloserver.org/docs/api/v2/problems/validation_failed", title = "Validation failed", status = 422,
                    detail = "Request did not pass validation", errors = errors.Select(location => new { location, code = "out_of_range" }),
                }), Encoding.UTF8, "application/problem+json") };
        }
    }
}
