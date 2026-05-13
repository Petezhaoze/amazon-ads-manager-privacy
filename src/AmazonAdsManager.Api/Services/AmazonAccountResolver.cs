using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class AmazonAccountResolver
{
    private readonly AmazonAdsOptions _options;
    private readonly List<AmazonAccountConfig> _runtimeAccounts = new();
    private readonly string _persistPath;
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public AmazonAccountResolver(IOptions<AmazonAdsOptions> options)
    {
        _options = options.Value;
        _persistPath = FindPersistPath();
        LoadPersistedAccounts();
    }

    private static string FindPersistPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        var dir = Path.Combine(home, ".amazon-ads-manager");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "connected_accounts.json");
    }

    public AmazonAccountConfig? Resolve(string accountKey) =>
        AllAccounts.FirstOrDefault(a => string.Equals(a.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<SafeAmazonAccountDto> GetSafeList() =>
        AllAccounts.Select(a => new SafeAmazonAccountDto
        {
            AccountKey = a.AccountKey,
            DisplayName = a.DisplayName,
            ProfileNeedsSetup = !a.ProfileId.All(char.IsDigit)
        });

    public void UpdateProfileId(string accountKey, string profileId)
    {
        lock (_runtimeAccounts)
        {
            var existing = _runtimeAccounts.FirstOrDefault(a =>
                string.Equals(a.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
            if (existing is null) return;
            existing.ProfileId = profileId;
            PersistAccounts();
        }
    }

    public void AddAccount(AmazonAccountConfig account)
    {
        lock (_runtimeAccounts)
        {
            var existing = _runtimeAccounts.FirstOrDefault(a =>
                string.Equals(a.AccountKey, account.AccountKey, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) _runtimeAccounts.Remove(existing);
            _runtimeAccounts.Add(account);
            PersistAccounts();
        }
    }

    // Runtime accounts override config-file accounts with the same key
    private IEnumerable<AmazonAccountConfig> AllAccounts
    {
        get
        {
            lock (_runtimeAccounts)
            {
                var runtimeKeys = _runtimeAccounts
                    .Select(a => a.AccountKey.ToLowerInvariant())
                    .ToHashSet();

                return _options.Accounts
                    .Where(a => !runtimeKeys.Contains(a.AccountKey.ToLowerInvariant()))
                    .Concat(_runtimeAccounts)
                    .ToList();
            }
        }
    }

    private void LoadPersistedAccounts()
    {
        try
        {
            if (!File.Exists(_persistPath)) return;
            var json = File.ReadAllText(_persistPath);
            var accounts = JsonSerializer.Deserialize<List<AmazonAccountConfig>>(json, _json);
            if (accounts is not null)
                lock (_runtimeAccounts) _runtimeAccounts.AddRange(accounts);
        }
        catch { /* ignore corrupt file */ }
    }

    private void PersistAccounts()
    {
        try { File.WriteAllText(_persistPath, JsonSerializer.Serialize(_runtimeAccounts, _json)); }
        catch { /* best-effort persistence */ }
    }
}
