namespace AmazonAdsManager.Shared.Models;

public class AccessCheckResponse
{
    public bool Ok { get; set; }
    public string? Token { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
