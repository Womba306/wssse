using System.Text.Json;

namespace FrogProg.KafkaProxyClient;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Параметры:
        // --mode ws|sse (по умолчанию ws)
        // --chat <CHAT_ID> (обязателен)
        // --msg "<TEXT|JSON>" (для ws; если нет — читаем из stdin)
        // --from latest|beginning (для sse)
        // --once (для ws отправить одно и выйти; для sse — слушать ~10с и выйти)
        var mode = Arg(args, "--mode") ?? "ws";
        var chatId = Arg(args, "--chat");
        var msg = Arg(args, "--msg");
        var from = Arg(args, "--from") ?? "latest";
        var once = args.Contains("--once");

        if (string.IsNullOrWhiteSpace(chatId))
        {
            Console.Error.WriteLine("error: --chat <CHAT_ID> обязателен.");
            return 2;
        }

        var cfg = ConfigLoader.Load("appsettings.json");
        Console.WriteLine($"Mode={mode}  Chat={chatId}  Once={once}");
        Console.WriteLine($"Auth={cfg.Auth.Authority}  WS={cfg.Proxy.WsUrl}  SSE={cfg.Proxy.SseUrl}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            // Токен из ENV или password grant
            var token = Environment.GetEnvironmentVariable("ACCESS_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                var auth = new AuthTokenClient(cfg.Auth);
                Console.WriteLine("→ Получаю access_token...");
                var tok = await auth.GetPasswordTokenAsync(cts.Token);
                token = tok.Token;
                Console.WriteLine("✓ Токен получен");
            }
            else Console.WriteLine("↪ Использую ACCESS_TOKEN из окружения");

            if (mode.Equals("sse", StringComparison.OrdinalIgnoreCase))
            {
                await using var sse = new SseChatClient(cfg.Proxy.SseUrl, cfg.Auth.AllowHttp, cfg.Auth.SkipTlsVerify);
                Console.WriteLine($"→ SSE подписка chatId={chatId} (from={from})");
                var doneCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                if (once) _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(10)); doneCts.Cancel(); });

                await sse.SubscribeChatAsync(token!, chatId!, from, async data =>
                {
                    // Сервер уже нормализует формат под ваш, просто печатаем
                    Console.WriteLine($"← SSE: {data}");
                    await Task.CompletedTask;
                }, doneCts.Token);

                return 0;
            }
            else
            {
                await using var ws = new WsChatClient(cfg.Proxy.WsUrl, cfg.Auth.SkipTlsVerify);
                Console.WriteLine("→ WS подключение...");
                await ws.ConnectAsync(token!, cts.Token);

                if (!string.IsNullOrWhiteSpace(msg))
                {
                    await SendOneAsync(ws, chatId!, msg!, cts.Token);
                    if (once) return 0;
                }

                Console.WriteLine("Вводи текст или JSON. Пустая строка — пропуск. Ctrl+C — выход.");
                string? line;
                while (!cts.IsCancellationRequested && (line = Console.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    await SendOneAsync(ws, chatId!, line, cts.Token);
                    if (once) break;
                }

                return 0;
            }
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"Fatal: {ex}"); return 1; }
    }

    private static string? Arg(string[] a, string name)
    {
        var i = Array.IndexOf(a, name);
        return (i >= 0 && i < a.Length - 1) ? a[i + 1] : null;
    }

    private static async Task SendOneAsync(WsChatClient ws, string chatId, string raw, CancellationToken ct)
    {
        // Если raw — JSON-объект, используем его, добавив chatId при необходимости.
        // Иначе завернём в {"chatId": "...", "message":"...", "lesson_phase":0}
        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("chatId", out _))
                {
                    payload = root.Clone();
                }
                else
                {
                    using var ms = new MemoryStream();
                    using var w = new Utf8JsonWriter(ms);
                    w.WriteStartObject();
                    foreach (var p in root.EnumerateObject()) p.WriteTo(w);
                    w.WriteString("chatId", chatId);
                    w.WriteEndObject();
                    w.Flush();
                    payload = JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
                }
            }
            else
            {
                payload = Wrap(chatId, raw);
            }
        }
        catch
        {
            payload = Wrap(chatId, raw);
        }

        Console.WriteLine($"→ WS send: {JsonSerializer.Serialize(payload)}");
        await ws.SendAsync(payload, ct);

        static JsonElement Wrap(string c, string m)
        {
            var json = $$"""
            {"chatId":{{JsonSerializer.Serialize(c)}},"message":{{JsonSerializer.Serialize(m)}},"lesson_phase":0}
            """;
            using var wrapped = JsonDocument.Parse(json);
            return wrapped.RootElement.Clone();
        }
    }
}
