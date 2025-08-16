using System.Text.Json;

namespace FrogProg.KafkaProxyClient;

public sealed class ProxyConfig
{
    public string WsUrl { get; set; } = "ws://91.206.15.217:8083/ws";
    public string SseUrl { get; set; } = "http://91.206.15.217:8083/stream_chat";
}

public sealed class RootConfig
{
    public AuthConfig Auth { get; set; } = new();
    public ProxyConfig Proxy { get; set; } = new();
}

public static class ConfigLoader
{
    public static RootConfig Load(string path)
    {
        var json = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "{}";
        var cfg = JsonSerializer.Deserialize<RootConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();

        // ENV overrides
        cfg.Auth.Authority = Env("AUTH_AUTHORITY", cfg.Auth.Authority);
        cfg.Auth.TokenEndpointPath = Env("AUTH_TOKEN_PATH", cfg.Auth.TokenEndpointPath);
        cfg.Auth.AllowHttp = Bool("AUTH_ALLOW_HTTP", cfg.Auth.AllowHttp);
        cfg.Auth.SkipTlsVerify = Bool("AUTH_SKIP_TLS", cfg.Auth.SkipTlsVerify);
        cfg.Auth.Username = Env("AUTH_USERNAME", cfg.Auth.Username);
        cfg.Auth.Password = Env("AUTH_PASSWORD", cfg.Auth.Password);
        cfg.Auth.ClientId = Env("AUTH_CLIENT_ID", cfg.Auth.ClientId);
        cfg.Auth.ClientSecret = Env("AUTH_CLIENT_SECRET", cfg.Auth.ClientSecret);
        cfg.Auth.Scope = Env("AUTH_SCOPE", cfg.Auth.Scope);

        cfg.Proxy.WsUrl = Env("PROXY_WS_URL", cfg.Proxy.WsUrl);
        cfg.Proxy.SseUrl = Env("PROXY_SSE_URL", cfg.Proxy.SseUrl);
        return cfg;

        static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) ?? d;
        static bool Bool(string k, bool d) => bool.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
    }
}
