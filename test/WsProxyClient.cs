using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FrogProg.KafkaProxyClient;

public sealed class WsProxyClient : IAsyncDisposable
{
    private readonly ProxyConfig _cfg;
    private readonly ClientWebSocket _ws = new();
    private readonly byte[] _recvBuffer;

    public WsProxyClient(ProxyConfig cfg)
    {
        _cfg = cfg;
        _recvBuffer = new byte[_cfg.ReceiveBufferBytes];
    }

    public async Task ConnectAsync(string bearerToken, CancellationToken ct)
    {
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_cfg.ConnectTimeoutSeconds));
        await _ws.ConnectAsync(new Uri(_cfg.WsUrl), cts.Token);
    }

    public Task SubscribeAsync(string[] topics, string from = "latest", CancellationToken ct = default)
        => SendAsync(new { type = "subscribe", topics, from }, ct);

    public Task ProduceAsync(object value, string? key = null, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        => SendAsync(new { type = "produce", topic = _cfg.ProduceTopic, key, headers, value }, ct);

    public Task PingAsync(CancellationToken ct = default)
        => SendAsync(new { type = "ping", ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, ct);

    private async Task SendAsync(object obj, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var bytes = Encoding.UTF8.GetBytes(json);
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sendCts.CancelAfter(TimeSpan.FromSeconds(_cfg.SendTimeoutSeconds));
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, sendCts.Token);
    }

    public async Task ReceiveLoopAsync(Func<JsonElement, Task> onMessage, CancellationToken ct)
    {
        var seg = new ArraySegment<byte>(_recvBuffer);
        var ms = new MemoryStream();

        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult? res;
                do
                {
                    res = await _ws.ReceiveAsync(seg, ct);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                        return;
                    }
                    ms.Write(seg.Array!, 0, res.Count);
                }
                while (!res.EndOfMessage);

                if (res.MessageType != WebSocketMessageType.Text) continue;

                using var doc = JsonDocument.Parse(ms.ToArray());
                await onMessage(doc.RootElement.Clone());
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        finally { ms.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None); } catch { }
        _ws.Dispose();
    }
}
