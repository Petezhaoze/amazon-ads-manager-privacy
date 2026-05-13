namespace AmazonAdsManager.Shared.Models;

public class ProductAiAnalysisRequest
{
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public ProductTrendSummary Trend { get; set; } = new();
}

public class ProductAiAnalysisResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<ProductAiRecommendation> Recommendations { get; set; } = new();
    public Dictionary<string, List<ProductActionPreview>> ActionPreviews { get; set; } = new();
    public ProductTrendSummary? Trend { get; set; }
    public string RawAiOutput { get; set; } = "";
}
