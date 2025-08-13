using System.Text.Json;

namespace FrogProg.KafkaProxyClient;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // --mode ws|sse   (default ws)
        // --once          (send one produce and exit)
        var mode = args.SkipWhile(a => a != "--mode").Skip(1).FirstOrDefault() ?? "ws";
        var once = args.Contains("--once");

        var cfg = LoadConfig();
        Console.WriteLine($"Mode={mode} Once={once}");
        Console.WriteLine($"Auth={cfg.Auth.Authority}  WS={cfg.Proxy.WsUrl}  SSE={cfg.Proxy.SseUrl}");

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

            if (mode.Equals("sse", StringComparison.OrdinalIgnoreCase))
            {
                using var sse = new SseProxyClient(cfg.Proxy, cfg.Auth.SkipTlsVerify);
                Console.WriteLine($"→ SSE подписка на [{string.Join(",", cfg.Proxy.Topics)}]");
                await sse.SubscribeAsync(token!, OnMessage, cts.Token);
                return 0;
            }

            await using var ws = new WsProxyClient(cfg.Proxy);
            Console.WriteLine("→ WS подключение...");
            await ws.ConnectAsync(token!, cts.Token);
            await ws.SubscribeAsync(cfg.Proxy.Topics, "latest", cts.Token);

            // тестовая отправка
            var sample = new { hello = "world", ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            await ws.ProduceAsync(sample, key: cfg.Proxy.ProduceKey, headers: cfg.Proxy.ProduceHeaders, ct: cts.Token);
            Console.WriteLine($"✓ Отправлено в {cfg.Proxy.ProduceTopic}");

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var readTask = ws.ReceiveLoopAsync(OnMessage, readCts.Token);

            // пинги
            _ = Task.Run(async () =>
            {
                while (!readCts.IsCancellationRequested)
                {
                    try { await ws.PingAsync(readCts.Token); } catch { }
                    await Task.Delay(TimeSpan.FromSeconds(20), readCts.Token);
                }
            }, readCts.Token);

            if (once)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                readCts.Cancel(); await readTask; return 0;
            }

            Console.WriteLine("Вводи JSON для отправки. Пустая строка — пропуск. Ctrl+C — выход.");
            string? line;
            while (!cts.IsCancellationRequested && (line = Console.ReadLine()) is not null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    await ws.ProduceAsync(doc.RootElement.Clone(), key: cfg.Proxy.ProduceKey, headers: cfg.Proxy.ProduceHeaders, ct: cts.Token);
                    Console.WriteLine("✓ Отправлено");
                }
                catch (Exception ex) { Console.WriteLine($"! Некорректный JSON: {ex.Message}"); }
            }

            readCts.Cancel(); await readTask; return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"Fatal: {ex}"); return 1; }
    }

    private static RootConfig LoadConfig()
    {
        var json = File.Exists("appsettings.json") ? File.ReadAllText("appsettings.json") : "{}";
        var cfg = JsonSerializer.Deserialize<RootConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();

        // ENV overrides (минимально полезные)
        
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
        cfg.Proxy.Topics = Env("TOPICS", string.Join(",", cfg.Proxy.Topics)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

    private static Task OnMessage(JsonElement json)
    {
        if (json.TryGetProperty("type", out var t))
        {
            var type = t.GetString();
            if (type is "message" or "ack")
            {
                Console.WriteLine($"← {type}: {JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true })}");
                return Task.CompletedTask;
            }
        }
        Console.WriteLine($"← {json}");
        return Task.CompletedTask;
    }
}
