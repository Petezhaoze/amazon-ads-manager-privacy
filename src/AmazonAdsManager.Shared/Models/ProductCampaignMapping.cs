namespace AmazonAdsManager.Shared.Models;

public class ProductCampaignMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public long CampaignId { get; set; }
    public string CampaignName { get; set; } = "";
    public string CampaignType { get; set; } = "";
    public bool IsActive { get; set; } = true;
}
