namespace AmazonAdsManager.Shared.Models;

public class ProductProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AccountKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ASIN { get; set; } = "";
    public string SKU { get; set; } = "";
    public decimal TargetAcos { get; set; } = 0.30m;
    public decimal? DefaultDailyBudget { get; set; }
    public string Notes { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int? StockQuantity { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
