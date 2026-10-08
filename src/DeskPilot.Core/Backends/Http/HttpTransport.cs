using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Backends.Http;

internal sealed record HttpReply(int StatusCode, string Body, JsonNode? Json)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>The provider's own error message, extracted from the usual error shapes.</summary>
    public string ErrorMessage => Endpoints.ExtractErrorMessage(Json, Body);
}

internal static class Endpoints
{
    public static string TrimBase(string? url) => (url ?? "").Trim().TrimEnd('/');

    public static string StripSuffix(string url, params string[] suffixes)
    {
        foreach (var s in suffixes)
            if (url.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return url[..^s.Length].TrimEnd('/');
        return url;
    }

    public static bool TryParseHttpUrl(string url, out Uri uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri!) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>True for loopback hosts (local model servers such as Ollama or LM Studio).</summary>
    public static bool IsLocal(string url)
    {
        if (!TryParseHttpUrl(url, out var uri)) return false;
        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host == "0.0.0.0") return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    public static string Host(string url) => TryParseHttpUrl(url, out var uri) ? uri.Host : "";

    public static string ExtractErrorMessage(JsonNode? json, string body)
    {
        var fromJson = FromJson(json, 0);
        if (!string.IsNullOrWhiteSpace(fromJson)) return Clip(fromJson.Trim(), 500);
        var text = (body ?? "").Trim();
        if (text.Length == 0 || text.StartsWith('<')) return "";   // empty or an HTML error page
        return Clip(string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), 300);
    }

    private static string? FromJson(JsonNode? node, int depth)
    {
        if (node is null || depth > 3) return null;
        if (node is JsonArray arr) return arr.Count > 0 ? FromJson(arr[0], depth + 1) : null;
        if (node is not JsonObject o) return JsonUtil.Str(node);
        if (o["error"] is JsonObject err)
        {
            var msg = JsonUtil.Str(err["message"]) ?? FromJson(err["error"], depth + 1);
            var type = JsonUtil.Str(err["type"]) ?? JsonUtil.Str(err["status"]);
            if (msg is { Length: > 0 }) return type is { Length: > 0 } && !msg.Contains(type, StringComparison.OrdinalIgnoreCase) ? $"{msg} ({type})" : msg;
            return type;
        }
        if (JsonUtil.Str(o["error"]) is { Length: > 0 } errText) return errText;
        if (JsonUtil.Str(o["message"]) is { Length: > 0 } message) return message;
        if (JsonUtil.Str(o["detail"]) is { Length: > 0 } detail) return detail;
        if (o["detail"] is JsonArray details && details.Count > 0 && details[0] is JsonObject d0) return JsonUtil.Str(d0["msg"]);
        return null;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}

/// <summary>
/// Sends requests for one provider: per-request timeout, retries with backoff for rate limits, overload,
/// gateway errors and timeouts, and readable error messages that never contain the API key.
/// </summary>
internal sealed class HttpTransport
{
    private static readonly HashSet<int> TransientStatus = new() { 429, 500, 502, 503, 504, 529 };
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly Action<AgentEvent> _emit;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public HttpTransport(HttpClient http, string providerName, string baseUrl, string apiKey, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? extraHeaders, Action<AgentEvent>? emit, Func<TimeSpan, CancellationToken, Task>? delay,
        int maxRetries = 3)
    {
        _http = http;
        ProviderName = providerName;
        BaseUrl = baseUrl;
        _apiKey = apiKey ?? "";
        Timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(300) : timeout;
        ExtraHeaders = extraHeaders ?? new Dictionary<string, string>();
        _emit = emit ?? (_ => { });
        _delay = delay ?? Task.Delay;
        MaxRetries = Math.Max(0, maxRetries);
    }

    public string ProviderName { get; }
    public string BaseUrl { get; }
    public TimeSpan Timeout { get; }
    public int MaxRetries { get; }
    public IReadOnlyDictionary<string, string> ExtraHeaders { get; }
    public bool IsLocal => Endpoints.IsLocal(BaseUrl);

    public HttpRequestMessage CreateRequest(HttpMethod method, string url, JsonNode? body, IEnumerable<KeyValuePair<string, string>> headers)
    {
        var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = new StringContent(JsonUtil.Serialize(body), Encoding.UTF8, "application/json");
        foreach (var (name, value) in headers) SetHeader(req, name, value);
        // Extra headers come last so a profile can override anything (e.g. a proxy's own auth header).
        foreach (var (name, value) in ExtraHeaders)
            if (!string.IsNullOrWhiteSpace(name)) SetHeader(req, name.Trim(), value ?? "");
        return req;
    }

    private static void SetHeader(HttpRequestMessage req, string name, string value)
    {
        if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase)) return;   // set by the JSON content
        req.Headers.Remove(name);
        req.Headers.TryAddWithoutValidation(name, value);
    }

    /// <summary>
    /// Sends with retries. Returns the final reply for any HTTP status (callers map errors); throws
    /// ProviderException for network failures and timeouts, OperationCanceledException when ct is cancelled.
    /// </summary>
    public async Task<HttpReply> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            string reason;
            TimeSpan? retryAfter = null;
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(Timeout);
                try
                {
                    using var request = makeRequest();
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    var reply = new HttpReply(status, body, JsonUtil.TryParse(body));
                    if (!TransientStatus.Contains(status) || attempt >= MaxRetries) return reply;
                    retryAfter = RetryAfter(response);
                    reason = status switch
                    {
                        429 => "rate limited (HTTP 429)",
                        529 => "overloaded (HTTP 529)",
                        _ => $"server error (HTTP {status})",
                    };
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (attempt >= MaxRetries)
                        throw new ProviderException($"{ProviderName} did not respond within {Timeout.TotalSeconds:0} seconds (tried {attempt + 1} times).");
                    reason = "the request timed out";
                }
                catch (HttpRequestException ex)
                {
                    var failure = Classify(ex);
                    if (failure != null) throw new ProviderException(failure);
                    if (attempt >= MaxRetries)
                        throw new ProviderException($"Could not connect to {ProviderName} at {BaseUrl}: {Scrub(ex.Message)}");
                    reason = "network error";
                }
            }

            var delay = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            if (delay > MaxRetryDelay) delay = MaxRetryDelay;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            _emit(new StatusEvent($"{ProviderName}: {reason}, retrying in {delay.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s (retry {attempt + 1} of {MaxRetries})", StatusLevel.Warning));
            await _delay(delay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A readable message for failures that retrying cannot fix, or null.</summary>
    private string? Classify(HttpRequestException ex)
    {
        var socket = FindSocketError(ex);
        bool connectFailed = ex.HttpRequestError == HttpRequestError.ConnectionError || socket is SocketError.ConnectionRefused;
        bool nameFailed = ex.HttpRequestError == HttpRequestError.NameResolutionError || socket is SocketError.HostNotFound or SocketError.NoData;
        if (IsLocal && (connectFailed || nameFailed)) return $"{ProviderName} is not running at {BaseUrl}. Start it and try again.";
        if (socket is SocketError.ConnectionRefused) return $"{ProviderName} refused the connection at {BaseUrl}. Check that the server is running and the base URL is right.";
        if (nameFailed) return $"Could not find the server '{Endpoints.Host(BaseUrl)}' for {ProviderName}. Check the base URL and your internet connection.";
        return null;
    }

    private static SocketError? FindSocketError(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is SocketException se) return se.SocketErrorCode;
        return null;
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out var msValues) &&
            double.TryParse(msValues.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms >= 0)
            return TimeSpan.FromMilliseconds(ms);
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>Maps a failed reply to a readable error. 401/403, 404 and everything else get distinct messages.</summary>
    public ProviderException ErrorFor(HttpReply reply, string? model)
    {
        var message = Scrub(reply.ErrorMessage);
        var code = reply.StatusCode;
        var text = code switch
        {
            401 => $"The API key was rejected by {ProviderName} (HTTP 401). Check the key in Settings.",
            403 => $"The API key was rejected by {ProviderName} (HTTP 403)." + (message.Length > 0 ? $" {message}" : " Check the key and its permissions in Settings."),
            404 => $"Model '{model}' not found at {BaseUrl}" + (message.Length > 0 ? $" ({message})." : ". Check the model name and the base URL."),
            413 => $"The request was too large for {ProviderName} (HTTP 413). Start a new conversation or keep fewer screenshots.",
            _ => $"{ProviderName} returned HTTP {code}" + (message.Length > 0 ? $": {message}" : "."),
        };
        return new ProviderException(text, code);
    }

    /// <summary>Removes the API key from any text that might be shown or logged.</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return _apiKey.Length >= 4 ? text.Replace(_apiKey, "[redacted]", StringComparison.Ordinal) : text;
    }
}
