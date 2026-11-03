using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Bale.Core.Connection;

public enum ServerContractOutcome { V2, UpdateRequired, Connection, Timeout, Authentication, RateLimited, ServerError, MalformedResponse, UnexpectedStatus }
public sealed record ServerSetupResult(Uri? Server, bool NeedsSetup = false, bool SignupEnabled = false, string? VerifiedServerId = null, string? Error = null);

/// <summary>ApiV2Probe.kt / TvServerSetupViewModel.kt: public contract/setup reads before saving or signing in.</summary>
public sealed class ServerSetupProbe : IDisposable
{
    public const string NetworkError = "Could not reach a Silo server at that address.";
    // ServerContract.kt UPDATE_REQUIRED_MESSAGE, shared by every official v2 gate.
    public const string UpdateRequired = "This server runs an older Silo release that this app no longer supports. Update the server, then try again.";
    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    public ServerSetupProbe(HttpMessageHandler? transport = null, Action<string>? log = null)
    {
        // LanServerProbe: unsolicited addresses get no credentials/device headers, redirects, or unbounded bodies.
        _http = new HttpClient(transport ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _log = log;
    }

    public async Task<ServerSetupResult> ProbeAsync(IReadOnlyList<Uri> candidates, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var contract = await ContractAsync(candidate, cancellationToken).ConfigureAwait(false);
            _log?.Invoke($"Server probe candidate={i + 1}/{candidates.Count} tls={candidate.Scheme == "https"} contract={contract}");
            if (contract == ServerContractOutcome.UpdateRequired) return new(null, Error: UpdateRequired);
            var setup = await ReadAsync(candidate, "/api/v2/system/setup", cancellationToken).ConfigureAwait(false);
            if (Boolean(setup.Json, "needs_setup") is not { } needsSetup)
            {
                if (i < candidates.Count - 1) continue;
                return new(null, Error: setup.Detail is { Length: > 0 } detail ? "Could not reach server: " + detail : NetworkError);
            }
            // A valid v2 setup response supersedes a transient/malformed info probe, never a v1 fallback.
            _log?.Invoke($"Server probe setup=ready needsSetup={needsSetup}");
            if (needsSetup) return new(candidate, NeedsSetup: true);
            var signup = await ReadAsync(candidate, "/api/v2/auth/signup", cancellationToken).ConfigureAwait(false);
            var identity = await IdentityAsync(candidate, cancellationToken).ConfigureAwait(false);
            return new(candidate, SignupEnabled: Boolean(signup.Json, "enabled") == true, VerifiedServerId: identity);
        }
        return new(null, Error: NetworkError);
    }

    public async Task<ServerContractOutcome> ContractAsync(Uri server, CancellationToken cancellationToken = default)
    {
        var response = await ReadAsync(server, "/api/v2/system/info", cancellationToken).ConfigureAwait(false);
        return ClassifyContract(response.Status, response.Body, response.Failure);
    }

    public static ServerContractOutcome ClassifyContract(int status, string? body, ServerContractOutcome failure = ServerContractOutcome.Connection)
    {
        if (status == 0) return failure;
        if (status == 404 && body is "404 page not found" or "404 page not found\n") return ServerContractOutcome.UpdateRequired;
        if (status is 401 or 403) return ServerContractOutcome.Authentication;
        if (status == 429) return ServerContractOutcome.RateLimited;
        if (status >= 500) return ServerContractOutcome.ServerError;
        if (status != 200) return ServerContractOutcome.UnexpectedStatus;
        try
        {
            using var document = JsonDocument.Parse(body ?? string.Empty);
            var info = document.RootElement;
            return info.ValueKind == JsonValueKind.Object && info.TryGetProperty("api_major", out var major) && major.TryGetInt32(out var version) && version == 2
                && String(info, "server_version") is not null && String(info, "contract_digest") is not null
                && info.TryGetProperty("links", out var links) && String(links, "openapi") is not null && String(links, "capabilities") is not null
                ? ServerContractOutcome.V2 : ServerContractOutcome.MalformedResponse;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return ServerContractOutcome.MalformedResponse; }
    }

    public async Task<string?> IdentityAsync(Uri server, CancellationToken cancellationToken = default) =>
        String((await ReadAsync(server, "/api/v2/system/identity", cancellationToken).ConfigureAwait(false)).Json, "server_id");

    public async Task<string?> NameAsync(Uri server, CancellationToken cancellationToken = default) =>
        String((await ReadAsync(server, "/api/v2/theme/branding", cancellationToken).ConfigureAwait(false)).Json, "server_name");

    private async Task<PublicResponse> ReadAsync(Uri server, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var response = await _http.GetAsync(ServerAddress.ToBaseUrl(server) + path, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            while (bytes.Length <= 64 * 1024)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 64 * 1024 + 1 - bytes.Length)), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                bytes.Write(buffer, 0, read);
            }
            if (bytes.Length > 64 * 1024) return new((int)response.StatusCode, null, null);
            var body = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
            JsonElement? json = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object) json = document.RootElement.Clone();
            }
            catch (JsonException) { }
            return new((int)response.StatusCode, body, response.IsSuccessStatusCode ? json : null,
                Detail: response.IsSuccessStatusCode ? null : String(json, "detail"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(0, null, null, ServerContractOutcome.Timeout); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return new(0, null, null); }
    }

    private static string? String(JsonElement? value, string key) => value is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static bool? Boolean(JsonElement? value, string key) => value is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(key, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean() : null;
    private sealed record PublicResponse(int Status, string? Body, JsonElement? Json, ServerContractOutcome Failure = ServerContractOutcome.Connection, string? Detail = null);
    public void Dispose() => _http.Dispose();
}
