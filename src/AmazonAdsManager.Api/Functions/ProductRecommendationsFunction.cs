using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace AmazonAdsManager.Api.Functions;

public class ProductRecommendationsFunction
{
    private readonly ProductAiRecommendationRepository _repo;
    private readonly ProductRecommendationDecisionService _decisions;
    private readonly ProductTrainingDataExportService _training;

    public ProductRecommendationsFunction(
        ProductAiRecommendationRepository repo,
        ProductRecommendationDecisionService decisions,
        ProductTrainingDataExportService training)
    {
        _repo = repo;
        _decisions = decisions;
        _training = training;
    }

    [Function("ListProductRecommendations")]
    public IActionResult List([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productId}/recommendations")] HttpRequest req, string productId)
    {
        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        var recommendations = _repo.GetByProduct(accountKey, productId);
        return new OkObjectResult(ApiResult<IReadOnlyList<ProductAiRecommendation>>.Ok(recommendations));
    }

    [Function("ApproveRecommendation")]
    public IActionResult Approve([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "products/recommendations/{recommendationId}/approve")] HttpRequest req, string recommendationId)
    {
        _decisions.Approve(recommendationId);
        return new OkObjectResult(ApiResult.Ok());
    }

    [Function("IgnoreRecommendation")]
    public IActionResult Ignore([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "products/recommendations/{recommendationId}/ignore")] HttpRequest req, string recommendationId)
    {
        _decisions.Ignore(recommendationId);
        return new OkObjectResult(ApiResult.Ok());
    }

    [Function("EditRecommendation")]
    public async Task<IActionResult> Edit([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "products/recommendations/{recommendationId}/edit")] HttpRequest req, string recommendationId)
    {
        string? editedAction;
        try
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            editedAction = body;
        }
        catch
        {
            return new BadRequestObjectResult(ApiResult.Fail("Invalid body"));
        }

        _decisions.Edit(recommendationId, editedAction);
        return new OkObjectResult(ApiResult.Ok());
    }

    [Function("ExportProductTraining")]
    public IActionResult ExportTraining([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productId}/training-export")] HttpRequest req, string productId)
    {
        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        var jsonl = _training.ExportProductTrainingDataAsJsonL(accountKey, productId);
        return new OkObjectResult(new { data = jsonl });
    }
}
