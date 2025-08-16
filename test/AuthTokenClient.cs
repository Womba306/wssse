using System.Net.Http.Headers;
using System.Text;

namespace FrogProg.KafkaProxyClient;

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

public sealed class AuthConfig
{
    public string Authority { get; set; } = "http://91.206.15.217:8080/api/auth";
    public string TokenEndpointPath { get; set; } = "/connect/token";
    public bool AllowHttp { get; set; } = true;
    public bool SkipTlsVerify { get; set; } = false;

    public string GrantType { get; set; } = "password";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "AdminPassword123!";
    public string ClientId { get; set; } = "api_gateway";
    public string ClientSecret { get; set; } = "secret-for-api-gateway";
    public string Scope { get; set; } = "frog_api";
}

public sealed class AuthTokenClient : IDisposable
{
    private readonly AuthConfig _cfg;
    private readonly HttpClient _http;

    public AuthTokenClient(AuthConfig cfg)
    {
        _cfg = cfg;
        var handler = new HttpClientHandler();
        if (_cfg.SkipTlsVerify)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        if (!_cfg.AllowHttp && _cfg.Authority.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HTTPS required");

        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<AccessToken> GetPasswordTokenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_cfg.Username) || string.IsNullOrWhiteSpace(_cfg.Password))
            throw new InvalidOperationException("Username/Password are required.");

        var url = BuildAuthorityUri();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = _cfg.Username,
            ["password"] = _cfg.Password,
            ["client_id"] = _cfg.ClientId,
            ["client_secret"] = _cfg.ClientSecret,
            ["scope"] = _cfg.Scope
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token request failed {resp.StatusCode}: {content}");

        using var doc = System.Text.Json.JsonDocument.Parse(content);
        var token = doc.RootElement.GetProperty("access_token").GetString();
        var expires = doc.RootElement.TryGetProperty("expires_in", out var exp)
            ? TimeSpan.FromSeconds(exp.GetInt32())
            : TimeSpan.FromHours(1);

        return new AccessToken(token!, DateTimeOffset.UtcNow.Add(expires));
    }

    private Uri BuildAuthorityUri()
    {
        var baseUri = _cfg.Authority.TrimEnd('/');
        var path = _cfg.TokenEndpointPath.StartsWith("/") ? _cfg.TokenEndpointPath : "/" + _cfg.TokenEndpointPath;
        return new Uri(baseUri + path, UriKind.Absolute);
    }

    public void Dispose() => _http.Dispose();
}
