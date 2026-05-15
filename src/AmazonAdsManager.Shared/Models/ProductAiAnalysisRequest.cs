namespace AmazonAdsManager.Shared.Models;

public class ProductAiAnalysisRequest
{
    public string AccountKey { get; set; } = "";
    public string ProductId { get; set; } = "";
    public ProductTrendSummary Trend { get; set; } = new();
    public DateOnly? DateRangeStart { get; set; }
    public DateOnly? DateRangeEnd { get; set; }
    public List<HourlyScorecardDto> HourlyScorecard { get; set; } = new();
    public List<KeywordPerformanceDto> KeywordWinners { get; set; } = new();
    public List<KeywordPerformanceDto> KeywordLosers { get; set; } = new();
    public List<BeforeAfterComparisonDto> BeforeAfterComparisons { get; set; } = new();
}

public class ProductAiAnalysisResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<ProductAiRecommendation> Recommendations { get; set; } = new();
    public Dictionary<string, List<ProductActionPreview>> ActionPreviews { get; set; } = new();
    public ProductTrendSummary? Trend { get; set; }
    public string RawAiOutput { get; set; } = "";
    public List<AiRecommendationDto> V2Recommendations { get; set; } = new();
    public List<HourlyScorecardDto> HourlyScorecard { get; set; } = new();
}
