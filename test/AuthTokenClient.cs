using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;

namespace FrogProg.KafkaProxyClient;

public sealed class AuthTokenClient
{
    private readonly AuthConfig _cfg;
    private readonly HttpClient _http;

    public AuthTokenClient(AuthConfig cfg)
    {
        _cfg = cfg;

        var handler = new HttpClientHandler
        {
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            // Для HTTP это игнорируется; для HTTPS можно включить строгую проверку
            ServerCertificateCustomValidationCallback = _cfg.SkipTlsVerify
                ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                : null
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
            BaseAddress = BuildAuthorityUri()
        };
    }

    private Uri BuildAuthorityUri()
    {
        if (!_cfg.AllowHttp && _cfg.Authority.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HTTPS required by config. Set Auth:AllowHttp=true to use HTTP.");

        return new Uri(_cfg.Authority.EndsWith("/") ? _cfg.Authority : _cfg.Authority + "/");
    }

    public async Task<AccessToken> GetPasswordTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_cfg.Username) || string.IsNullOrWhiteSpace(_cfg.Password))
            throw new InvalidOperationException("Username/Password are required.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = "admin",
            ["password"] = _cfg.Password!,
            ["client_id"] = _cfg.ClientId
        };
        if (!string.IsNullOrWhiteSpace(_cfg.ClientSecret))
            form["client_secret"] = _cfg.ClientSecret!;
        if (!string.IsNullOrWhiteSpace(_cfg.Scope))
            form["scope"] = _cfg.Scope;

        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.TokenEndpointPath)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token request failed {resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()
                    ?? throw new InvalidOperationException("No access_token in response");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

        return new AccessToken(token, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 30)));
    }
}
