using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FrogProg.KafkaProxyClient;

public sealed class WsChatClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly Uri _uri;

    public WsChatClient(string wsUrl, bool skipTlsVerify = false)
    {
        _uri = new Uri(wsUrl, UriKind.Absolute);
        // Для wss:// тут лучше иметь доверенный сертификат; кастомную валидацию ClientWebSocket не даёт.
    }

    public async Task ConnectAsync(string bearerToken, CancellationToken ct)
    {
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
        await _ws.ConnectAsync(_uri, ct);
        if (_ws.State != WebSocketState.Open)
            throw new InvalidOperationException("WS not open");
    }

    public async Task SendAsync(JsonElement json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(json));
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        }
        catch { /* ignore */ }
        _ws.Dispose();
    }
}
