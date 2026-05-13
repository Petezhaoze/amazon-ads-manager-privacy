using AmazonAdsManager.Shared.Models;

namespace AmazonAdsManager.Client.Services;

public class AccountState
{
    private readonly AdsApiClient _api;

    public AccountState(AdsApiClient api) => _api = api;

    public SafeAmazonAccountDto? SelectedAccount { get; private set; }
    public List<SafeAmazonAccountDto> Accounts { get; private set; } = new();

    public event Action? OnChange;

    public void SelectAccount(SafeAmazonAccountDto account)
    {
        SelectedAccount = account;
        OnChange?.Invoke();
    }

    public async Task ReloadAccountsAsync()
    {
        Accounts = await _api.GetAccountsAsync();
        OnChange?.Invoke();
    }
}
