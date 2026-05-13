using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class AmazonAdsAuthService
{
    private readonly AmazonAdsOptions _options;
    private readonly HttpClient _http;
    private readonly Dictionary<string, (string Token, DateTimeOffset Expiry)> _cache = new();

    public AmazonAdsAuthService(IOptions<AmazonAdsOptions> options, IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _http = httpClientFactory.CreateClient();
    }

    public async Task<string> GetAccessTokenAsync(AmazonAccountConfig account)
    {
        if (_cache.TryGetValue(account.AccountKey, out var cached) && cached.Expiry > DateTimeOffset.UtcNow.AddSeconds(30))
            return cached.Token;

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["refresh_token"] = account.RefreshToken
        });

        var resp = await _http.PostAsync("https://api.amazon.com/auth/o2/token", body);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var token = root.GetProperty("access_token").GetString()!;
        var expiresIn = root.GetProperty("expires_in").GetInt32();

        _cache[account.AccountKey] = (token, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        return token;
    }
}
