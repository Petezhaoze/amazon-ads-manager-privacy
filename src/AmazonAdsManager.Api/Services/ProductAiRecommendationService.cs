using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class ProductAiRecommendationService
{
    private readonly IAiClient _ai;
    private readonly ProductAiRecommendationRepository _repo;
    private readonly ILogger<ProductAiRecommendationService> _logger;

    public ProductAiRecommendationService(
        IAiClient ai,
        ProductAiRecommendationRepository repo,
        ILogger<ProductAiRecommendationService> logger)
    {
        _ai = ai;
        _repo = repo;
        _logger = logger;
    }

    public async Task<ProductAiAnalysisResult> AnalyzeProductAsync(ProductAiAnalysisRequest request)
    {
        var trend = request.Trend;
        var prompt = BuildPrompt(trend);

        try
        {
            var aiOutput = await _ai.AnalyzeProductAsync(prompt);
            var recommendations = ParseAiResponse(aiOutput, trend);

            // Apply safety rules
            recommendations = ApplySafetyRules(recommendations, trend);

            // Save recommendations to repo
            foreach (var rec in recommendations)
            {
                rec.OriginalInputJson = JsonSerializer.Serialize(trend);
                rec.OriginalAiOutputJson = aiOutput;
                _repo.Upsert(rec);
            }

            return new ProductAiAnalysisResult
            {
                Success = true,
                Recommendations = recommendations,
                RawAiOutput = aiOutput
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing product {ProductId}", request.ProductId);
            return new ProductAiAnalysisResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    private string BuildPrompt(ProductTrendSummary trend)
    {
        return $$"""
You are an Amazon Ads analyst. Analyze this product's ad performance and provide JSON recommendations only.

Product: {{trend.ProductName}} ({{trend.ASIN}}/{{trend.SKU}})
Target ACOS: {{trend.TargetAcos:P}}

Last 7 Days:
- Spend: ${{trend.Last7DaysSpend:F2}}
- Sales: ${{trend.Last7DaysSales:F2}}
- ACOS: {{trend.Last7DaysAcos:P}}
- Clicks: {{trend.Last7DaysClicks}}
- Orders: {{trend.Last7DaysOrders}}

Previous 7 Days:
- Spend: ${{trend.Previous7DaysSpend:F2}}
- Sales: ${{trend.Previous7DaysSales:F2}}
- ACOS: {{trend.Previous7DaysAcos:P}}
- Clicks: {{trend.Previous7DaysClicks}}
- Orders: {{trend.Previous7DaysOrders}}

Changes: Spend {{trend.SpendChangePercent:+0.0;-0.0;0.0}}%, Sales {{trend.SalesChangePercent:+0.0;-0.0;0.0}}%, ACOS {{trend.AcosChangePercent:+0.0;-0.0;0.0}}%

Trend Notes:
{string.Join("\n", trend.TrendNotes.Select(n => $"- {n}"))}

Linked Campaigns: {{trend.LinkedCampaigns.Count}}

Return ONLY valid JSON with this structure:
{
  "recommendations": [
    {
      "recommendationType": "ReduceBudget|IncreaseBudget|PauseOvernight|AdjustDayparting|WatchOnly|AddNegativeKeyword|MoveBudgetToBetterCampaign|PauseLowPerformer|IncreaseHighPerformerBudget",
      "severity": "High|Medium|Low|Info",
      "explanation": "...",
      "suggestedAction": "...",
      "suggestedBudgetChangePercent": -15 or null,
      "suggestedDayparting": "..." or null,
      "suggestedCampaignId": 123 or null
    }
  ]
}
""";
    }

    private List<ProductAiRecommendation> ParseAiResponse(string jsonStr, ProductTrendSummary trend)
    {
        var recommendations = new List<ProductAiRecommendation>();

        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            if (root.TryGetProperty("recommendations", out var recsArray))
            {
                foreach (var recEl in recsArray.EnumerateArray())
                {
                    var rec = new ProductAiRecommendation
                    {
                        AccountKey = trend.AccountKey,
                        ProductId = trend.ProductId,
                        ProductName = trend.ProductName,
                        RecommendationType = recEl.GetProperty("recommendationType").GetString() ?? "",
                        Severity = recEl.GetProperty("severity").GetString() ?? "",
                        Explanation = recEl.GetProperty("explanation").GetString() ?? "",
                        SuggestedAction = recEl.GetProperty("suggestedAction").GetString() ?? "",
                        SuggestedBudgetChangePercent = recEl.TryGetProperty("suggestedBudgetChangePercent", out var sbc) &&
                                                       sbc.ValueKind != JsonValueKind.Null
                            ? sbc.GetDecimal()
                            : null,
                        SuggestedDayparting = recEl.TryGetProperty("suggestedDayparting", out var sd) &&
                                            sd.ValueKind != JsonValueKind.Null
                            ? sd.GetString()
                            : null,
                        SuggestedCampaignId = recEl.TryGetProperty("suggestedCampaignId", out var sci) &&
                                            sci.ValueKind != JsonValueKind.Null
                            ? sci.GetInt64()
                            : null,
                        Status = "Pending"
                    };
                    recommendations.Add(rec);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse AI response JSON");
        }

        return recommendations;
    }

    private List<ProductAiRecommendation> ApplySafetyRules(List<ProductAiRecommendation> recommendations, ProductTrendSummary trend)
    {
        var safe = new List<ProductAiRecommendation>();

        foreach (var rec in recommendations)
        {
            // Don't recommend budget increase if ACOS is above target
            if (rec.RecommendationType == "IncreaseBudget" && trend.Last7DaysAcos > trend.TargetAcos)
            {
                rec.Status = "Ignored";
                rec.Explanation += " (Safety: ACOS above target)";
                continue;
            }

            // Don't recommend aggressive changes with <20 clicks unless spend is high
            if (trend.Last7DaysClicks < 20 && trend.Last7DaysSpend < 100 &&
                (rec.RecommendationType == "PauseLowPerformer" || rec.RecommendationType == "PauseOvernight"))
            {
                rec.RecommendationType = "WatchOnly";
                rec.Explanation += " (Safety: Insufficient data volume)";
            }

            // Clamp budget changes to 10-25%
            if (rec.SuggestedBudgetChangePercent.HasValue)
            {
                var change = rec.SuggestedBudgetChangePercent.Value;
                rec.SuggestedBudgetChangePercent = Math.Clamp(change, -25, 25);
            }

            safe.Add(rec);
        }

        return safe;
    }
}
