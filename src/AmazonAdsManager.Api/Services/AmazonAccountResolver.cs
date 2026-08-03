using AmazonAdsManager.Shared.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class AmazonAccountResolver
{
    private readonly AmazonAdsOptions _options;
    private readonly List<AmazonAccountConfig> _runtimeAccounts = new();
    private readonly object _sync = new();
    private readonly BlobContainerClient? _container;
    private const string BlobName = "connected-accounts.json";
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public AmazonAccountResolver(IOptions<AmazonAdsOptions> options, IConfiguration config)
    {
        _options = options.Value;
        var connStr = config["AzureWebJobsStorage"];
        if (!string.IsNullOrEmpty(connStr) && connStr != "UseDevelopmentStorage=true")
        {
            _container = new BlobContainerClient(connStr, "amazon-ads-manager-data");
            _container.CreateIfNotExists();
        }
        LoadPersistedAccounts();
    }

    public AmazonAccountConfig? Resolve(string accountKey) =>
        AllAccounts.FirstOrDefault(a => string.Equals(a.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<SafeAmazonAccountDto> GetSafeList() =>
        AllAccounts.Select(a => new SafeAmazonAccountDto
        {
            AccountKey = a.AccountKey,
            DisplayName = a.DisplayName,
            ProfileNeedsSetup = string.IsNullOrEmpty(a.ProfileId) || !a.ProfileId.All(char.IsDigit)
        });

    public void UpdateProfileId(string accountKey, string profileId)
    {
        lock (_sync)
        {
            LoadPersistedAccountsNoLock();
            var existing = _runtimeAccounts.FirstOrDefault(a =>
                string.Equals(a.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
            if (existing is null) return;
            existing.ProfileId = profileId;
            PersistAccounts();
        }
    }

    public void AddAccount(AmazonAccountConfig account)
    {
        lock (_sync)
        {
            LoadPersistedAccountsNoLock();
            var existing = _runtimeAccounts.FirstOrDefault(a =>
                string.Equals(a.AccountKey, account.AccountKey, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) _runtimeAccounts.Remove(existing);
            _runtimeAccounts.Add(account);
            PersistAccounts();
        }
    }

    private IEnumerable<AmazonAccountConfig> AllAccounts
    {
        get
        {
            lock (_sync)
            {
                LoadPersistedAccountsNoLock();
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
            lock (_sync)
            {
                LoadPersistedAccountsNoLock();
            }
        }
        catch { }
    }

    private void LoadPersistedAccountsNoLock()
    {
        string? json = null;

        if (_container is not null)
        {
            var blob = _container.GetBlobClient(BlobName);
            if (blob.Exists())
            {
                var download = blob.DownloadContent();
                json = download.Value.Content.ToString();
            }
        }
        else
        {
            var path = LocalPath();
            if (File.Exists(path)) json = File.ReadAllText(path);
        }

        if (json is null) return;
        var accounts = JsonSerializer.Deserialize<List<AmazonAccountConfig>>(json, _json);
        if (accounts is null) return;

        _runtimeAccounts.Clear();
        _runtimeAccounts.AddRange(accounts);
    }

    private void PersistAccounts()
    {
        try
        {
            var json = JsonSerializer.Serialize(_runtimeAccounts, _json);

            if (_container is not null)
            {
                var blob = _container.GetBlobClient(BlobName);
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
                blob.Upload(stream, overwrite: true);
            }
            else
            {
                File.WriteAllText(LocalPath(), json);
            }
        }
        catch { }
    }

    private static string LocalPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        var dir = Path.Combine(home, ".amazon-ads-manager");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "connected_accounts.json");
    }
}
