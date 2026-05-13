namespace AmazonAdsManager.Shared.Models;

public class OAuthLoginUrlResponse
{
    public string LoginUrl { get; set; } = "";
    public string State { get; set; } = "";
}

public class AmazonAdsProfile
{
    public string ProfileId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = ""; // seller, vendor, agency
    public string CountryCode { get; set; } = "";
    public string CurrencyCode { get; set; } = "";
    public string TimeZone { get; set; } = "";
}

public class OAuthPendingResult
{
    public bool Ready { get; set; }
    public List<AmazonAdsProfile> Profiles { get; set; } = new();
    public bool TokensOk { get; set; }       // token exchange succeeded
    public bool ProfileFetchFailed { get; set; } // Ads API returned 401 — need manual profile ID
    public string? ProfileFetchError { get; set; }
    public string? Error { get; set; }
}

public class SaveAccountRequest
{
    public string State { get; set; } = "";
    public string AccountKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ProfileId { get; set; } = "";
}

public class UpdateProfileRequest
{
    public string AccountKey { get; set; } = "";
    public string ProfileId { get; set; } = "";
}
