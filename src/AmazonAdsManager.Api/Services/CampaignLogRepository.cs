using AmazonAdsManager.Shared.Models;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class CampaignLogRepository
{
    private readonly List<CampaignActionLog> _logs = new();
    private static readonly string _persistPath = GetPersistPath();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public CampaignLogRepository() => Load();

    private static string GetPersistPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        var dir = Path.Combine(home, ".amazon-ads-manager");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "campaign_action_logs.json");
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_persistPath)) return;
            var loaded = JsonSerializer.Deserialize<List<CampaignActionLog>>(File.ReadAllText(_persistPath), _opts);
            if (loaded is not null) _logs.AddRange(loaded);
        }
        catch { }
    }

    private void Save()
    {
        try { File.WriteAllText(_persistPath, JsonSerializer.Serialize(_logs, _opts)); }
        catch { }
    }

    public void Add(CampaignActionLog entry)
    {
        _logs.Insert(0, entry); // newest first
        // Keep last 1000 entries
        if (_logs.Count > 1000) _logs.RemoveRange(1000, _logs.Count - 1000);
        Save();
    }

    public IReadOnlyList<CampaignActionLog> GetByAccount(string accountKey, int limit = 200) =>
        _logs.Where(l => string.Equals(l.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase))
             .Take(limit)
             .ToList()
             .AsReadOnly();
}
