using System.Net.Http.Headers;
using System.Text;

namespace FrogProg.KafkaProxyClient;

public sealed class SseChatClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _baseStreamUri;

    public SseChatClient(string baseStreamUrl, bool allowInsecureHttp = true, bool skipTlsVerify = false)
    {
        var handler = new HttpClientHandler();
        if (skipTlsVerify)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        if (!allowInsecureHttp && baseStreamUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HTTPS required");

        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

        _baseStreamUri = new Uri(baseStreamUrl, UriKind.Absolute);
    }

    public async Task SubscribeChatAsync(string bearerToken, string chatId, string from = "latest",
        Func<string, Task>? onEvent = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chatId)) throw new ArgumentNullException(nameof(chatId));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var uriBuilder = new UriBuilder(_baseStreamUri);
        var chatQ = $"chatId={Uri.EscapeDataString(chatId)}";
        var fromQ = string.IsNullOrWhiteSpace(from) ? "" : $"&from={Uri.EscapeDataString(from)}";
        if (string.IsNullOrEmpty(uriBuilder.Query))
            uriBuilder.Query = chatQ + fromQ;
        else
            uriBuilder.Query = uriBuilder.Query.TrimStart('?') + "&" + chatQ + fromQ;

        using var req = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);

        string? line;
        var sb = new StringBuilder();
        string? evtName = null;

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null) break;

            if (line.Length == 0)
            {
                var data = sb.ToString();
                sb.Clear();
                if (!string.IsNullOrEmpty(data))
                {
                    if (onEvent is not null) await onEvent.Invoke(data).ConfigureAwait(false);
                    else Console.WriteLine($"← [{evtName ?? "message"}] {data}");
                }
                evtName = null;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                evtName = line.Substring("event:".Length).Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line.Substring("data:".Length).TrimStart());
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
