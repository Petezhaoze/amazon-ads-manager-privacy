using AmazonAdsManager.Shared.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class CampaignLogRepository
{
    private readonly List<CampaignActionLog> _logs = new();
    private readonly BlobContainerClient? _container;
    private const string BlobName = "campaign-action-logs.json";
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public CampaignLogRepository(IConfiguration config)
    {
        var connStr = config["AzureWebJobsStorage"];
        if (!string.IsNullOrEmpty(connStr) && connStr != "UseDevelopmentStorage=true")
        {
            _container = new BlobContainerClient(connStr, "amazon-ads-manager-data");
            _container.CreateIfNotExists();
        }
        Load();
    }

    private void Load()
    {
        try
        {
            string? json = null;
            if (_container is not null)
            {
                var blob = _container.GetBlobClient(BlobName);
                if (blob.Exists())
                    json = blob.DownloadContent().Value.Content.ToString();
            }
            else
            {
                var path = LocalPath();
                if (File.Exists(path)) json = File.ReadAllText(path);
            }
            if (json is null) return;
            var loaded = JsonSerializer.Deserialize<List<CampaignActionLog>>(json, _opts);
            if (loaded is not null) _logs.AddRange(loaded);
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_logs, _opts);
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
        return Path.Combine(dir, "campaign_action_logs.json");
    }

    public void Add(CampaignActionLog entry)
    {
        lock (_logs)
        {
            _logs.Insert(0, entry);
            if (_logs.Count > 1000) _logs.RemoveRange(1000, _logs.Count - 1000);
            Save();
        }
    }

    public IReadOnlyList<CampaignActionLog> GetByAccount(string accountKey, int limit = 200) =>
        _logs.Where(l => string.Equals(l.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase))
             .Take(limit)
             .ToList()
             .AsReadOnly();
}
