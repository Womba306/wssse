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

        var handler = new HttpClientHandler();
        if (_cfg.SkipTlsVerify)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        if (!_cfg.AllowHttp && _cfg.Authority.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HTTPS is required by configuration");
        _http.BaseAddress = new Uri(_cfg.Authority.TrimEnd('/') + "/");
    }

    public async Task<AccessToken> GetPasswordTokenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_cfg.Username) || string.IsNullOrWhiteSpace(_cfg.Password))
            throw new InvalidOperationException("Username/Password are required.");

        if (string.IsNullOrWhiteSpace(_cfg.ClientId))
            throw new InvalidOperationException("ClientId is required.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = string.IsNullOrWhiteSpace(_cfg.grant_type) ? "password" : _cfg.grant_type!,
            ["username"] = _cfg.Username!,
            ["password"] = _cfg.Password!,
            ["client_id"] = _cfg.ClientId!
        };
        if (!string.IsNullOrWhiteSpace(_cfg.ClientSecret))
            form["client_secret"] = _cfg.ClientSecret!;
        if (!string.IsNullOrWhiteSpace(_cfg.Scope))
            form["scope"] = _cfg.Scope!;

        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.TokenEndpointPath.TrimStart('/'))
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token request failed {resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()
                    ?? throw new InvalidOperationException("No access_token in response");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

        return new AccessToken(token, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 30)));
    }
}
