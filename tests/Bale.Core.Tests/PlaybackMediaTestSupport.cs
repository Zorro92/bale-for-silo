using System.Net;
using System.Net.Http.Headers;
using Bale.Core.Api.Models;

namespace Bale.Core.Tests;

internal static class PlaybackMediaTestSupport
{
    public static byte[] Data { get; } = Enumerable.Range(0, 3 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();

    public static PlaybackPlan Plan(string? url = "/api/v2/stream/session/file.mp4") => new()
    {
        Stream = new StreamV3 { Url = url, MimeType = "video/mp4", Container = "mp4", Protocol = "http" },
        Source = new PlaybackSource { DurationSeconds = 1800 },
        Delivery = "original_http",
        Timeline = new TimelineV3 { SourceStartSeconds = 120, PlayerStartSeconds = 0, TimelineOffsetSeconds = 120 },
    };

    internal sealed class RangeHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly List<HttpRequestMessage> _requests = [];
        private readonly List<MediaBody> _bodies = [];
        private TaskCompletionSource _requestsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<HttpRequestMessage> Requests { get { lock (_lock) return _requests.ToArray(); } }
        public IReadOnlyList<MediaBody> Bodies { get { lock (_lock) return _bodies.ToArray(); } }
        public byte[] Payload { get; init; } = Data;
        public bool IgnoreRanges { get; init; }
        public Func<HttpRequestMessage, int, int?>? FailAfterBytes { get; init; }
        public Func<Exception> ReadError { get; init; } = () => new IOException("Simulated dropped media connection.");
        public Func<HttpRequestMessage, int, CancellationToken, Task>? BeforeSendAsync { get; init; }
        public Func<HttpRequestMessage, CancellationToken, Task>? BeforeReadAsync { get; init; }
        public Action<HttpResponseMessage, int>? ConfigureResponse { get; init; }

        public async Task WaitForRequestsAsync(int count)
        {
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    if (_requests.Count >= count) return;
                    changed = _requestsChanged.Task;
                }
                await changed.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int number;
            lock (_lock)
            {
                _requests.Add(request);
                number = _requests.Count;
                _requestsChanged.TrySetResult();
                _requestsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (BeforeSendAsync is not null) await BeforeSendAsync(request, number, cancellationToken);
            var range = IgnoreRanges ? null : request.Headers.Range?.Ranges.Single();
            var start = checked((int)(range?.From ?? 0));
            var end = checked((int)(range?.To ?? (Payload.Length - 1)));
            var beforeRead = BeforeReadAsync;
            var body = new MediaBody(Payload, start, end + 1, FailAfterBytes?.Invoke(request, number), ReadError,
                beforeRead is null ? null : ct => beforeRead(request, ct));
            lock (_lock) _bodies.Add(body);
            var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(body), RequestMessage = request,
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            response.Content.Headers.ContentLength = end - start + 1;
            if (range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, Payload.Length);
            ConfigureResponse?.Invoke(response, number);
            return response;
        }
    }

    internal sealed class MediaBody(byte[] data, int start, int end, int? failAfter, Func<Exception> readError,
        Func<CancellationToken, Task>? beforeRead) : Stream
    {
        private readonly int _start = start;
        private readonly TaskCompletionSource _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position = start;
        public bool IsDisposed { get; private set; }
        public Task Disposal => _disposal.Task;
        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => end - _start;
        public override long Position { get => _position - _start; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (beforeRead is not null) await beforeRead(cancellationToken);
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (failAfter is { } limit && _position - _start >= limit) throw readError();
            var count = Math.Min(buffer.Length, end - _position);
            if (failAfter is { } maximum) count = Math.Min(count, maximum - (_position - _start));
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
                _disposal.TrySetResult();
            }
            base.Dispose(disposing);
        }
    }
}
