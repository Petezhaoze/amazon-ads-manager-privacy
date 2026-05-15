using Microsoft.JSInterop;

namespace AmazonAdsManager.Client.Services;

public class AppPreferencesService
{
    private readonly IJSRuntime _js;
    public bool HideOutOfStock { get; private set; }
    public bool ShowActiveOnly { get; private set; }
    public bool ShowActiveCampaignsOnly { get; private set; }
    public bool ShowAiProductsWithActiveCampaignsOnly { get; private set; }

    public event Action? OnChange;

    public AppPreferencesService(IJSRuntime js) => _js = js;

    public async Task LoadAsync()
    {
        HideOutOfStock = await GetBool("pref_hideOutOfStock");
        ShowActiveOnly = await GetBool("pref_showActiveOnly", defaultValue: true);
        ShowActiveCampaignsOnly = await GetBool("pref_showActiveCampaignsOnly");
        ShowAiProductsWithActiveCampaignsOnly = await GetBool("pref_showAiProductsWithActiveCampaignsOnly", defaultValue: true);
    }

    public async Task SetHideOutOfStockAsync(bool value)
    {
        HideOutOfStock = value;
        await _js.InvokeVoidAsync("localStorage.setItem", "pref_hideOutOfStock", value ? "true" : "false");
        OnChange?.Invoke();
    }

    public async Task SetShowActiveOnlyAsync(bool value)
    {
        ShowActiveOnly = value;
        await _js.InvokeVoidAsync("localStorage.setItem", "pref_showActiveOnly", value ? "true" : "false");
        OnChange?.Invoke();
    }

    public async Task SetShowActiveCampaignsOnlyAsync(bool value)
    {
        ShowActiveCampaignsOnly = value;
        await _js.InvokeVoidAsync("localStorage.setItem", "pref_showActiveCampaignsOnly", value ? "true" : "false");
        OnChange?.Invoke();
    }

    public async Task SetShowAiProductsWithActiveCampaignsOnlyAsync(bool value)
    {
        ShowAiProductsWithActiveCampaignsOnly = value;
        await _js.InvokeVoidAsync("localStorage.setItem", "pref_showAiProductsWithActiveCampaignsOnly", value ? "true" : "false");
        OnChange?.Invoke();
    }

    private async Task<bool> GetBool(string key, bool defaultValue = false)
    {
        try
        {
            var val = await _js.InvokeAsync<string?>("localStorage.getItem", key);
            if (val is null) return defaultValue;
            return val == "true";
        }
        catch { return defaultValue; }
    }
}
