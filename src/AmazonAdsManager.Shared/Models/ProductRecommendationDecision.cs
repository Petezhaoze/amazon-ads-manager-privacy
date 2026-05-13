namespace AmazonAdsManager.Shared.Models;

public class ProductRecommendationDecision
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AccountKey { get; set; } = "";
    public string RecommendationId { get; set; } = "";
    public string Decision { get; set; } = ""; // Approved, Ignored, Edited
    public string? EditedAction { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset DecidedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ProductTrainingExample
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string InputJson { get; set; } = ""; // Serialized ProductTrendSummary
    public string RecommendationJson { get; set; } = ""; // Original AI recommendation
    public string Decision { get; set; } = ""; // Approved, Ignored, Edited
    public string? EditedAction { get; set; }
    public string? Outcome { get; set; } // For future use
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
