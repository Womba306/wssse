using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace FrogProg.KafkaProxyClient;

public sealed class SseProxyClient : IDisposable
{
    private readonly ProxyConfig _cfg;
    private readonly HttpClient _http;

    public SseProxyClient(ProxyConfig cfg, bool skipTlsVerify = false)
    {
        _cfg = cfg;
        var handler = new HttpClientHandler
        {
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ServerCertificateCustomValidationCallback = skipTlsVerify
                ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                : null
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task SubscribeAsync(string bearerToken, Func<JsonElement, Task> onMessage, CancellationToken ct)
    {
        var url = new UriBuilder(_cfg.SseUrl);
        url.Query = $"topics={Uri.EscapeDataString(string.Join(",", _cfg.Topics))}&from=latest";

        using var req = new HttpRequestMessage(HttpMethod.Get, url.Uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"SSE subscribe failed {resp.StatusCode}: {body}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var sb = new StringBuilder();
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync() ?? string.Empty;

            if (string.IsNullOrEmpty(line))
            {
                var block = sb.ToString();
                sb.Clear();

                var dataLine = block.Split('\n').FirstOrDefault(l => l.StartsWith("data: "));
                if (dataLine is not null)
                {
                    var json = dataLine.AsSpan(6).ToString();
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        await onMessage(doc.RootElement.Clone());
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"! SSE parse error: {ex.Message}");
                    }
                }
                continue;
            }

            // keep-alive / comments начинаются с ':'
            if (!line.StartsWith(":"))
                sb.AppendLine(line);
        }
    }

    public void Dispose() => _http.Dispose();
}
