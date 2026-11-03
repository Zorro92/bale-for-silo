using System.Net;
using System.Text;
using System.Text.Json;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public sealed class FilePlaybackJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bale-journal-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_directory, "playback.json");
    private static PlaybackJournalOwner Owner => new("https://silo.example.com/", "login", "account", "profile", "installation");

    [Fact]
    public async Task MissingJournalIsEmptyAndSnapshotsSurviveANewInstance()
    {
        var journal = new FilePlaybackJournal(JournalPath);
        Assert.Empty(await journal.ReadAsync());
        var entries = new[]
        {
            new PlaybackJournalEntry(Owner, "pending")
            {
                Sequence = 7, LastPosition = new(12.5, true),
                Progress = new("installation", 6, 10, false),
                Stop = new("installation", "stop-id", 7, 12.5, true),
            },
            new PlaybackJournalEntry(Owner, "settled") { Terminal = true },
        };
        await journal.WriteAsync(entries);

        Assert.Equal(entries, await new FilePlaybackJournal(JournalPath).ReadAsync());
        await journal.WriteAsync([entries[1]]);
        Assert.Equal(entries[1], Assert.Single(await new FilePlaybackJournal(JournalPath).ReadAsync()));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancelledReplacementPreservesTheCommittedSnapshot()
    {
        var journal = new FilePlaybackJournal(JournalPath);
        var saved = new PlaybackJournalEntry(Owner, "saved");
        await journal.WriteAsync([saved]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.WriteAsync([], cancellation.Token));

        Assert.Equal(saved, Assert.Single(await journal.ReadAsync()));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CorruptJournalIsNotSilentlyDiscarded()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(JournalPath, "{incomplete");
        await Assert.ThrowsAsync<JsonException>(() => new FilePlaybackJournal(JournalPath).ReadAsync());
    }

    [Fact]
    public async Task NewProcessReplaysTheLastDurablePositionAsAStop()
    {
        var journal = new FilePlaybackJournal(JournalPath);
        using (var first = Create(new RecoveryHandler()))
        {
            var reporter = first.Playback.CreateProgressReporter(new Installation(), journal, "login");
            await reporter.AttachSessionAsync("session");
            await reporter.RecordPositionAsync("session", 42.5, true);
        }
        var transport = new RecoveryHandler();
        using var restored = Create(transport);
        var recovery = restored.Playback.CreateProgressReporter(new Installation(), new FilePlaybackJournal(JournalPath), "login");

        Assert.Equal(1, await recovery.ReplayAsync());
        Assert.Equal(42.5, transport.FinalPosition);
        Assert.True(Assert.Single(await journal.ReadAsync()).Terminal);
        Assert.Equal(0, await recovery.ReplayAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class RecoveryHandler : HttpMessageHandler
    {
        public double? FinalPosition { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = Capabilities;
            if (request.Method == HttpMethod.Delete)
            {
                using var stop = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                FinalPosition = stop.RootElement.GetProperty("position").GetDouble();
                var stopId = stop.RootElement.GetProperty("stop_id").GetString();
                body = $$"""{"outcome":"stopped","stop_id":"{{stopId}}"}""";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
