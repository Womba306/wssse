using System.Text.Json;

namespace FrogProg.KafkaProxyClient;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var mode = args.SkipWhile(a => a != "--mode").Skip(1).FirstOrDefault() ?? "ws";
        var once = args.Contains("--once");
        var chatArg = args.SkipWhile(a => a != "--chat").Skip(1).FirstOrDefault();

        var cfg = LoadConfig();
        Console.WriteLine($"Mode={mode} Once={once}");
        Console.WriteLine($"Auth={cfg.Auth.Authority}  WS={cfg.Proxy.WsUrl}  SSE={cfg.Proxy.SseUrl}  SSE_CHAT={cfg.Proxy.StreamChatUrl}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            // токен из ENV или password grant
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

            if (mode.Equals("sse-chat", StringComparison.OrdinalIgnoreCase))
            {
                var chatId = chatArg ?? Environment.GetEnvironmentVariable("CHAT_ID") ?? cfg.Proxy.ChatId;
                if (string.IsNullOrWhiteSpace(chatId))
                {
                    Console.Error.WriteLine("Нужен chatId: --chat <id> или ENV CHAT_ID или Proxy.ChatId в appsettings.json");
                    return 2;
                }

                await using var sse = new SseChatClient(cfg.Proxy.StreamChatUrl ?? "http://91.206.15.217:8083/stream_chat",
                                                        allowInsecureHttp: true,
                                                        skipTlsVerify: cfg.Auth.SkipTlsVerify);
                Console.WriteLine($"→ SSE подписка на чат {chatId}");
                await sse.SubscribeChatAsync(token!, chatId!, from: "latest",
                    onEvent: async data =>
                    {
                        Console.WriteLine($"← {DateTimeOffset.Now:HH:mm:ss} {data}");
                        await Task.CompletedTask;
                    }, ct: cts.Token);
                return 0;
            }

            if (mode.Equals("sse", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"→ SSE подписка на [{string.Join(",", cfg.Proxy.Topics)}]");
                Console.WriteLine("! Режим sse не реализован в этом минимальном клиенте. Используй sse-chat.");
                return 0;
            }

            // ws
            await using var ws = new WsProxyClient(cfg.Proxy);
            Console.WriteLine("→ WS подключение...");
            await ws.ConnectAsync(token!, cts.Token);

            // если --once: отправим демо-сообщение и выйдем
            if (once)
            {
                var chatId = chatArg ?? Environment.GetEnvironmentVariable("CHAT_ID") ?? cfg.Proxy.ChatId ?? Guid.NewGuid().ToString("N");
                var sample = new { chatId, message = $"hello @ {DateTimeOffset.UtcNow}" };
                await ws.ProduceAsync(sample, ct: cts.Token);
                Console.WriteLine($"✓ Отправлено (chatId={chatId})");
                return 0;
            }

            Console.WriteLine("Вводи JSON (например: {\"chatId\":\"<id>\",\"message\":\"текст\"}). Пустая строка — пропуск. Ctrl+C — выход.");
            _ = Task.Run(async () =>
            {
                // приём acks/ошибок
                await ws.ReceiveLoopAsync(async json =>
                {
                    Console.WriteLine($"← {JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true })}");
                    await Task.CompletedTask;
                }, cts.Token);
            }, cts.Token);

            string? line;
            while (!cts.IsCancellationRequested && (line = Console.ReadLine()) is not null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    await ws.ProduceAsync(doc.RootElement.Clone(), ct: cts.Token);
                    Console.WriteLine("✓ Отправлено");
                }
                catch (Exception ex) { Console.WriteLine($"! Некорректный JSON: {ex.Message}"); }
            }

            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"Fatal: {ex}"); return 1; }
    }

    private static RootConfig LoadConfig()
    {
        var json = "{}";
        if (File.Exists("appsettings.json"))
        {
            json = File.ReadAllText("appsettings.json");
        }
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        var cfg = JsonSerializer.Deserialize<RootConfig>(json, opts) ?? new();

        // ENV overrides
        cfg.Auth.Authority = Env("AUTH_AUTHORITY", cfg.Auth.Authority);
        cfg.Auth.TokenEndpointPath = Env("AUTH_TOKEN_PATH", cfg.Auth.TokenEndpointPath);
        cfg.Auth.AllowHttp = Bool("AUTH_ALLOW_HTTP", cfg.Auth.AllowHttp);
        cfg.Auth.SkipTlsVerify = Bool("AUTH_SKIP_TLS", cfg.Auth.SkipTlsVerify);
        cfg.Auth.Username = Env("AUTH_USERNAME", Env("USERNAME", cfg.Auth.Username ?? ""));
        cfg.Auth.Password = Env("AUTH_PASSWORD", Env("PASSWORD", cfg.Auth.Password ?? ""));
        cfg.Auth.ClientId = Env("AUTH_CLIENT_ID", Env("CLIENT_ID", cfg.Auth.ClientId));
        cfg.Auth.ClientSecret = Env("AUTH_CLIENT_SECRET", Env("CLIENT_SECRET", cfg.Auth.ClientSecret ?? ""));
        cfg.Auth.Scope = Env("AUTH_SCOPE", Env("SCOPE", cfg.Auth.Scope));

        cfg.Proxy.WsUrl = Env("PROXY_WS_URL", cfg.Proxy.WsUrl);
        cfg.Proxy.SseUrl = Env("PROXY_SSE_URL", cfg.Proxy.SseUrl);
        cfg.Proxy.StreamChatUrl = Env("PROXY_SSE_CHAT_URL", cfg.Proxy.StreamChatUrl ?? cfg.Proxy.SseUrl);
        cfg.Proxy.ChatId = Env("CHAT_ID", cfg.Proxy.ChatId ?? "");
        cfg.Proxy.Topics = Env("TOPICS", string.Join(",", cfg.Proxy.Topics))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        cfg.Proxy.ProduceTopic = Env("PRODUCE_TOPIC", cfg.Proxy.ProduceTopic);
        cfg.Proxy.ProduceKey = Env("PRODUCE_KEY", cfg.Proxy.ProduceKey ?? "");
        cfg.Proxy.ConnectTimeoutSeconds = Int("CONNECT_TIMEOUT", cfg.Proxy.ConnectTimeoutSeconds);
        cfg.Proxy.SendTimeoutSeconds = Int("SEND_TIMEOUT", cfg.Proxy.SendTimeoutSeconds);
        cfg.Proxy.ReceiveBufferBytes = Int("RECV_BUFFER", cfg.Proxy.ReceiveBufferBytes);

        var hdrCsv = Env("PRODUCE_HEADERS", "");
        if (!string.IsNullOrWhiteSpace(hdrCsv))
        {
            cfg.Proxy.ProduceHeaders ??= new();
            foreach (var p in hdrCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var i = p.IndexOf('=');
                if (i > 0) cfg.Proxy.ProduceHeaders[p[..i]] = p[(i + 1)..];
            }
        }
        return cfg;

        static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) ?? d;
        static bool Bool(string k, bool d) => bool.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
        static int Int(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
    }
}
