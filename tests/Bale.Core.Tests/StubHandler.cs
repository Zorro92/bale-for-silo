using System.Net;
using System.Text;

namespace Bale.Core.Tests;

/// <summary>Answers requests from a queue of canned responses and records what was sent.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Json)> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    // Kiota disposes request content when a call completes; capture it while the transport owns it.
    public List<string?> RequestBodies { get; } = new();

    public StubHandler Enqueue(HttpStatusCode status, string json)
    {
        _responses.Enqueue((status, json));
        return this;
    }

    public StubHandler Enqueue(string json) => Enqueue(HttpStatusCode.OK, json);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is { } content ? await content.ReadAsStringAsync(cancellationToken) : null);
        var (status, json) = _responses.Dequeue();
        var contentType = (int)status >= 400 ? "application/problem+json" : "application/json";
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, contentType),
            RequestMessage = request,
        };
    }
}
