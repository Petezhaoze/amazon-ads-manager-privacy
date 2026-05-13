namespace AmazonAdsManager.Shared.Models;

public class SafeAmazonAccountDto
{
    public string AccountKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool ProfileNeedsSetup { get; set; }  // true when ProfileId is not yet a numeric profile ID
}
