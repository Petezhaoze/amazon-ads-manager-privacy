namespace AmazonAdsManager.Shared.Models;

public class AmazonAccountConfig
{
    public string AccountKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string BaseUrl { get; set; } = "https://advertising-api.amazon.com";
}
