namespace AmazonAdsManager.Shared.Models;

public class CampaignActionLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string AccountKey { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public string? Asin { get; set; }
    public string RequestedState { get; set; } = "";  // "paused" or "enabled"
    public string? VerifiedState { get; set; }         // state Amazon confirmed, null on failure
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string Source { get; set; } = "";           // "scheduler" or "manual"
}
