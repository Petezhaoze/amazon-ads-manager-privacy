namespace AmazonAdsManager.Shared.Models;

public class AmazonAdsOptions
{
    public const string Section = "AmazonAds";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public List<AmazonAccountConfig> Accounts { get; set; } = new();
}
