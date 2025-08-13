using System.Text.Json.Serialization;

namespace FrogProg.KafkaProxyClient;

public sealed class RootConfig
{
    public AuthConfig Auth { get; set; } = new();
    public ProxyConfig Proxy { get; set; } = new();
}

public sealed class AuthConfig
{
    public string grant_type { get; set; } = "password";
    public string Authority { get; set; } = "http://91.206.15.217:8080/api/auth";
    public string TokenEndpointPath { get; set; } = "/connect/token";
    public bool AllowHttp { get; set; } = true;
    public bool SkipTlsVerify { get; set; } = false;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string ClientId { get; set; } = "api_gateway";
    public string? ClientSecret { get; set; }
    public string Scope { get; set; } = "frog_api";
    
}

public sealed class ProxyConfig
{
    public string WsUrl { get; set; } = "ws://91.206.15.217:8083/ws";
    public string SseUrl { get; set; } = "http://91.206.15.217:8083/stream";
    public string[] Topics { get; set; } = new[] { "ai-requests", "ai-responses" };
    public string ProduceTopic { get; set; } = "ai-requests";
    public string? ProduceKey { get; set; }
    public Dictionary<string, string>? ProduceHeaders { get; set; } = new();
    public int ConnectTimeoutSeconds { get; set; } = 20;
    public int SendTimeoutSeconds { get; set; } = 15;
    public int ReceiveBufferBytes { get; set; } = 1_048_576;
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt)
{
    [JsonIgnore] public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt.AddSeconds(-30);
}
