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
        _recvBuffer = new byte[Math.Max(16_384, _cfg.ReceiveBufferBytes)];
    }

    public async Task ConnectAsync(string bearerToken, CancellationToken ct = default)
    {
        if (_ws.State == WebSocketState.Open) return;

        _ws.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_cfg.ConnectTimeoutSeconds));

        await _ws.ConnectAsync(new Uri(_cfg.WsUrl), cts.Token).ConfigureAwait(false);
    }

    public async Task SendAsync(object payload, CancellationToken ct = default)
    {
        var json = payload is string s ? s : JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        var seg = new ArraySegment<byte>(bytes);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_cfg.SendTimeoutSeconds));
        await _ws.SendAsync(seg, WebSocketMessageType.Text, endOfMessage: true, cancellationToken: cts.Token)
                 .ConfigureAwait(false);
    }

    public Task ProduceAsync(object value, string? key = null, Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        // Сервер ждёт {"chatId":"...", "message":"..."} — формируй это на стороне вызывающего кода.
        return SendAsync(value, ct);
    }

    public async Task ReceiveLoopAsync(Func<JsonElement, Task> onMessage, CancellationToken ct = default)
    {
        var ms = new MemoryStream(_recvBuffer.Length);
        try
        {
            var seg = new ArraySegment<byte>(_recvBuffer);
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var res = await _ws.ReceiveAsync(seg, ct).ConfigureAwait(false);
                if (res.MessageType == WebSocketMessageType.Close) break;

                ms.Write(seg.Array!, seg.Offset, res.Count);
                if (res.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    ms.SetLength(0);
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        await onMessage(doc.RootElement.Clone()).ConfigureAwait(false);
                    }
                    catch
                    {
                        var raw = JsonSerializer.Serialize(new { type = "raw", data = json });
                        using var doc = JsonDocument.Parse(raw);
                        await onMessage(doc.RootElement.Clone()).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* ignore */ }
        finally
        {
            ms.Dispose();
        }
    }

    public async Task PingAsync(CancellationToken ct = default)
        => await SendAsync(new { type = "ping", ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, ct);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None);
        }
        catch { /* ignore */ }
        _ws.Dispose();
    }
}
