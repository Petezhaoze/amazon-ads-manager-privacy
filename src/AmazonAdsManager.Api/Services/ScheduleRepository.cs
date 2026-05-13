using AmazonAdsManager.Shared.Models;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class ScheduleRepository
{
    private readonly List<CampaignSchedule> _schedules = new();
    private static readonly string _persistPath = GetPersistPath();

    public ScheduleRepository()
    {
        Load();
    }

    private static string GetPersistPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        var dir = Path.Combine(home, ".amazon-ads-manager");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "schedules.json");
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_persistPath)) return;
            var json = File.ReadAllText(_persistPath);
            var loaded = JsonSerializer.Deserialize<List<CampaignSchedule>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (loaded is not null) _schedules.AddRange(loaded);
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_schedules,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_persistPath, json);
        }
        catch { }
    }

    public IReadOnlyList<CampaignSchedule> GetAll() => _schedules.ToList().AsReadOnly();

    public IReadOnlyList<CampaignSchedule> GetByAccount(string accountKey) =>
        _schedules.Where(s => string.Equals(s.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase))
                  .ToList().AsReadOnly();

    public CampaignSchedule? GetById(string id) =>
        _schedules.FirstOrDefault(s => s.Id == id);

    public CampaignSchedule Upsert(CampaignSchedule schedule)
    {
        var existing = _schedules.FirstOrDefault(s => s.Id == schedule.Id);
        if (existing is not null) _schedules.Remove(existing);
        _schedules.Add(schedule);
        Save();
        return schedule;
    }

    public bool Delete(string id)
    {
        var existing = _schedules.FirstOrDefault(s => s.Id == id);
        if (existing is null) return false;
        _schedules.Remove(existing);
        Save();
        return true;
    }
}
