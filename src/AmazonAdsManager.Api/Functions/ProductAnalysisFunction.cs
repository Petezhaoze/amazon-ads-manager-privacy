using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace AmazonAdsManager.Api.Functions;

public class ProductAnalysisFunction
{
    private readonly ProductTrendAnalyzer _analyzer;
    private readonly ProductAiRecommendationService _aiService;
    private readonly ProductActionPreviewService _actionPreviews;

    public ProductAnalysisFunction(
        ProductTrendAnalyzer analyzer,
        ProductAiRecommendationService aiService,
        ProductActionPreviewService actionPreviews)
    {
        _analyzer = analyzer;
        _aiService = aiService;
        _actionPreviews = actionPreviews;
    }

    [Function("AnalyzeProduct")]
    public async Task<IActionResult> Analyze([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "products/{productId}/analyze")] HttpRequest req, string productId)
    {
        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        try
        {
            var trend = _analyzer.AnalyzeProduct(accountKey, productId);
            var result = await _aiService.AnalyzeProductAsync(
                new ProductAiAnalysisRequest { AccountKey = accountKey, ProductId = productId, Trend = trend });

            if (!result.Success)
                return new ObjectResult(ApiResult.Fail(result.Error ?? "AI analysis failed")) { StatusCode = 500 };

            result.Trend = trend;

            foreach (var rec in result.Recommendations)
            {
                var previews = _actionPreviews.GenerateActionPreviews(rec, trend);
                result.ActionPreviews[rec.Id] = previews;
            }

            return new OkObjectResult(ApiResult<ProductAiAnalysisResult>.Ok(result));
        }
        catch (Exception ex)
        {
            return new ObjectResult(ApiResult.Fail(ex.Message)) { StatusCode = 500 };
        }
    }
}
