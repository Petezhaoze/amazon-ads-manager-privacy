using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class OAuthService
{
    private readonly AmazonAdsOptions _options;
    private readonly HttpClient _http;

    private record OAuthSession(string RefreshToken, List<AmazonAdsProfile> Profiles, bool ProfileFetchFailed = false, string? ProfileFetchError = null);
    private readonly Dictionary<string, OAuthSession> _sessions = new();
    private readonly Dictionary<string, string> _errors = new();

    public OAuthService(IOptions<AmazonAdsOptions> options, IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _http = httpClientFactory.CreateClient();
    }

    public (string LoginUrl, string State) GetLoginUrl(string redirectUri)
    {
        var state = Guid.NewGuid().ToString("N");
        var scopes = "advertising::campaign_management advertising::audiences";
        var url = "https://www.amazon.com/ap/oa" +
                  $"?client_id={Uri.EscapeDataString(_options.ClientId)}" +
                  $"&scope={Uri.EscapeDataString(scopes)}" +
                  "&response_type=code" +
                  $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                  $"&state={Uri.EscapeDataString(state)}";
        return (url, state);
    }

    public async Task HandleCallbackAsync(string code, string state, string redirectUri)
    {
        try
        {
            var (refreshToken, accessToken) = await ExchangeCodeAsync(code, redirectUri);

            // Try to fetch profiles — may fail if app isn't registered with Advertising API
            List<AmazonAdsProfile> profiles;
            bool profileFetchFailed = false;
            string? profileFetchError = null;
            try
            {
                profiles = await FetchProfilesAsync(accessToken);
            }
            catch (Exception ex)
            {
                profiles = new List<AmazonAdsProfile>();
                profileFetchFailed = true;
                profileFetchError = ex.Message;
            }

            lock (_sessions) _sessions[state] = new OAuthSession(refreshToken, profiles, profileFetchFailed, profileFetchError);
        }
        catch (Exception ex)
        {
            lock (_errors) _errors[state] = ex.Message;
        }
    }

    public OAuthPendingResult GetPending(string state)
    {
        lock (_sessions)
        {
            if (_sessions.TryGetValue(state, out var session))
                return new OAuthPendingResult
                {
                    Ready = true,
                    TokensOk = true,
                    Profiles = session.Profiles,
                    ProfileFetchFailed = session.ProfileFetchFailed,
                    ProfileFetchError = session.ProfileFetchError
                };
        }
        lock (_errors)
        {
            if (_errors.TryGetValue(state, out var err))
                return new OAuthPendingResult { Ready = true, Error = err };
        }
        return new OAuthPendingResult { Ready = false };
    }

    public AmazonAccountConfig? BuildAccount(string state, string accountKey, string displayName, string profileId)
    {
        OAuthSession? session;
        lock (_sessions) _sessions.TryGetValue(state, out session);
        if (session is null) return null;

        lock (_sessions) _sessions.Remove(state);

        return new AmazonAccountConfig
        {
            AccountKey = accountKey,
            DisplayName = displayName,
            RefreshToken = session.RefreshToken,
            ProfileId = profileId,
            BaseUrl = "https://advertising-api.amazon.com"
        };
    }

    public async Task<List<AmazonAdsProfile>> ResolveProfilesFromRefreshTokenAsync(string refreshToken)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret
        });

        var resp = await _http.PostAsync("https://api.amazon.com/auth/o2/token", body);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Token refresh failed {(int)resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;

        return await FetchProfilesAsync(accessToken);
    }

    private async Task<(string RefreshToken, string AccessToken)> ExchangeCodeAsync(string code, string redirectUri)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret
        });

        var resp = await _http.PostAsync("https://api.amazon.com/auth/o2/token", body);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Amazon token exchange {(int)resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        return (
            root.GetProperty("refresh_token").GetString()!,
            root.GetProperty("access_token").GetString()!
        );
    }

    private async Task<List<AmazonAdsProfile>> FetchProfilesAsync(string accessToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "https://advertising-api.amazon.com/v2/profiles");
        req.Headers.Add("Authorization", $"Bearer {accessToken}");
        req.Headers.Add("Amazon-Advertising-API-ClientId", _options.ClientId);

        var resp = await _http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Amazon profiles API {(int)resp.StatusCode}: {raw}");

        var profiles = new List<AmazonAdsProfile>();
        using var doc = JsonDocument.Parse(raw);

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var profileId = el.GetProperty("profileId").GetRawText();
            var countryCode = el.TryGetProperty("countryCode", out var cc) ? cc.GetString() ?? "" : "";
            var currencyCode = el.TryGetProperty("currencyCode", out var cur) ? cur.GetString() ?? "" : "";
            var timeZone = el.TryGetProperty("timezone", out var tz) ? tz.GetString() ?? "" : "";

            string name = profileId, type = "unknown";
            if (el.TryGetProperty("accountInfo", out var info))
            {
                name = info.TryGetProperty("name", out var n) ? n.GetString() ?? profileId : profileId;
                type = info.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown";
            }

            profiles.Add(new AmazonAdsProfile
            {
                ProfileId = profileId,
                Name = name,
                Type = type,
                CountryCode = countryCode,
                CurrencyCode = currencyCode,
                TimeZone = timeZone
            });
        }

        return profiles;
    }
}
