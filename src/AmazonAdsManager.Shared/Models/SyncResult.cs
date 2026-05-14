namespace AmazonAdsManager.Shared.Models;

public class SyncResult
{
    public int ProductsUpserted { get; set; }
    public int MappingsUpserted { get; set; }
    public int TotalCampaigns { get; set; }
    public int TotalProductAds { get; set; }
    public int TitlesUpdated { get; set; }
    public string Summary => $"Synced {TotalCampaigns} campaigns, {TotalProductAds} product ads → {ProductsUpserted} new products, {MappingsUpserted} new mappings, {TitlesUpdated} titles fetched.";
}
