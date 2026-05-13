namespace AmazonAdsManager.Shared.Models;

public class CampaignSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AccountKey { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public string TimeZoneId { get; set; } = "UTC";
    // 7 days x 24 hours; index [dayOfWeek][hour], true = campaign should be PAUSED this hour
    public bool[][] PauseHours { get; set; } = Enumerable.Range(0, 7).Select(_ => new bool[24]).ToArray();
    public string? Asin { get; set; }
    public string LastKnownState { get; set; } = "enabled";
}
