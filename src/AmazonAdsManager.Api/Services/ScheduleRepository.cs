using AmazonAdsManager.Shared.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class ScheduleRepository
{
    private readonly List<CampaignSchedule> _schedules = new();
    private readonly BlobContainerClient? _container;
    private const string BlobName = "schedules.json";
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public ScheduleRepository(IConfiguration config)
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
            var loaded = JsonSerializer.Deserialize<List<CampaignSchedule>>(json, _opts);
            if (loaded is not null) _schedules.AddRange(loaded);
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_schedules, _opts);
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
        return Path.Combine(dir, "schedules.json");
    }

    public IReadOnlyList<CampaignSchedule> GetAll() => _schedules.ToList().AsReadOnly();

    public IReadOnlyList<CampaignSchedule> GetByAccount(string accountKey) =>
        _schedules.Where(s => string.Equals(s.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase))
                  .ToList().AsReadOnly();

    public CampaignSchedule? GetById(string id) =>
        _schedules.FirstOrDefault(s => s.Id == id);

    public CampaignSchedule Upsert(CampaignSchedule schedule)
    {
        lock (_schedules)
        {
            var existing = _schedules.FirstOrDefault(s => s.Id == schedule.Id);
            if (existing is not null) _schedules.Remove(existing);
            _schedules.Add(schedule);
            Save();
        }
        return schedule;
    }

    public bool Delete(string id)
    {
        lock (_schedules)
        {
            var existing = _schedules.FirstOrDefault(s => s.Id == id);
            if (existing is null) return false;
            _schedules.Remove(existing);
            Save();
        }
        return true;
    }
}
