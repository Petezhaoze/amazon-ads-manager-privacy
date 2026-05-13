namespace AmazonAdsManager.Shared.Models;

public class ProductAiRecommendation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string RecommendationType { get; set; } = "";
    public string Severity { get; set; } = ""; // "High", "Medium", "Low", "Info"
    public string Explanation { get; set; } = "";
    public string SuggestedAction { get; set; } = "";
    public decimal? SuggestedBudgetChangePercent { get; set; }
    public string? SuggestedDayparting { get; set; }
    public long? SuggestedCampaignId { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Approved, Ignored, Edited, Applied
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string OriginalInputJson { get; set; } = "";
    public string OriginalAiOutputJson { get; set; } = "";
}
