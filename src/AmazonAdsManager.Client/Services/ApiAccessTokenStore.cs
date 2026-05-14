using Microsoft.JSInterop;
using System.Net.Http.Headers;

namespace AmazonAdsManager.Client.Services;

public class ApiAccessTokenStore
{
    private const string TokenKey = "api_access_token";
    private const string TokenExpiryKey = "api_access_token_expires_at";
    private const string LegacyUnlockedKey = "app_unlocked";
    private readonly IJSRuntime _js;

    public ApiAccessTokenStore(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<bool> TryApplyStoredTokenAsync(HttpClient http)
    {
        var token = await _js.InvokeAsync<string?>("localStorage.getItem", TokenKey);
        var expiresAtText = await _js.InvokeAsync<string?>("localStorage.getItem", TokenExpiryKey);

        if (string.IsNullOrWhiteSpace(token) ||
            !DateTimeOffset.TryParse(expiresAtText, out var expiresAt) ||
            expiresAt <= DateTimeOffset.UtcNow)
        {
            await ClearAsync(http);
            return false;
        }

        Apply(http, token);
        return true;
    }

    public async Task StoreAsync(HttpClient http, string token, DateTimeOffset expiresAt)
    {
        await _js.InvokeVoidAsync("localStorage.setItem", TokenKey, token);
        await _js.InvokeVoidAsync("localStorage.setItem", TokenExpiryKey, expiresAt.ToString("O"));
        await _js.InvokeVoidAsync("localStorage.setItem", LegacyUnlockedKey, "1");
        Apply(http, token);
    }

    public async Task ClearAsync(HttpClient http)
    {
        http.DefaultRequestHeaders.Authorization = null;
        await _js.InvokeVoidAsync("localStorage.removeItem", TokenKey);
        await _js.InvokeVoidAsync("localStorage.removeItem", TokenExpiryKey);
        await _js.InvokeVoidAsync("localStorage.removeItem", LegacyUnlockedKey);
    }

    private static void Apply(HttpClient http, string token)
    {
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
