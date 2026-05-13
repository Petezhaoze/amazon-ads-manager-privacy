namespace AmazonAdsManager.Shared.Models;

public class CampaignStateUpdateRequest
{
    public string AccountKey { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string State { get; set; } = ""; // "enabled" or "paused"
    public string? CampaignName { get; set; }
    public string? Asin { get; set; }
}
