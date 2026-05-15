using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace AmazonAdsManager.Api.Functions;

public class AnalyticsFunctions
{
    private readonly AmazonAdsReportService _reports;
    private readonly AmcWorkflowService _amcWorkflows;
    private readonly AmcResultIngestionService _amcIngestion;
    private readonly ProductAnalyticsRepository _products;
    private readonly HourlyScorecardService _scorecards;
    private readonly ProductAiRecommendationServiceV2 _recommendations;
    private readonly RecommendationExperimentService _experiments;
    private readonly ApiAccessService _access;
    private readonly IConfiguration _config;

    public AnalyticsFunctions(
        AmazonAdsReportService reports,
        AmcWorkflowService amcWorkflows,
        AmcResultIngestionService amcIngestion,
        ProductAnalyticsRepository products,
        HourlyScorecardService scorecards,
        ProductAiRecommendationServiceV2 recommendations,
        RecommendationExperimentService experiments,
        ApiAccessService access,
        IConfiguration config)
    {
        _reports = reports;
        _amcWorkflows = amcWorkflows;
        _amcIngestion = amcIngestion;
        _products = products;
        _scorecards = scorecards;
        _recommendations = recommendations;
        _experiments = experiments;
        _access = access;
        _config = config;
    }

    [Function("RunReportImport")]
    public async Task<IActionResult> RunReportImport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reports/run-import")] HttpRequest req)
    {
        var unauthorized = RequireRunner(req);
        if (unauthorized is not null) return unauthorized;

        var request = await ReadImportRequest(req);
        if (string.IsNullOrWhiteSpace(request.AccountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        var result = await _reports.RunImportAsync(request);
        return new OkObjectResult(ApiResult<AnalyticsImportResult>.Ok(result));
    }

    [Function("RunAmcWorkflow")]
    public async Task<IActionResult> RunAmcWorkflow(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "amc/run-workflow")] HttpRequest req)
    {
        var unauthorized = RequireRunner(req);
        if (unauthorized is not null) return unauthorized;

        var request = await ReadImportRequest(req);
        if (string.IsNullOrWhiteSpace(request.AccountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        var result = await _amcWorkflows.RunWorkflowsAsync(request);
        return new OkObjectResult(ApiResult<AnalyticsImportResult>.Ok(result));
    }

    [Function("ImportAmcResults")]
    public async Task<IActionResult> ImportAmcResults(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "amc/import-results")] HttpRequest req)
    {
        var unauthorized = RequireRunner(req);
        if (unauthorized is not null) return unauthorized;

        var request = await ReadImportRequest(req);
        if (string.IsNullOrWhiteSpace(request.AccountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        var result = await _amcIngestion.ImportResultsAsync(request);
        return new OkObjectResult(ApiResult<AnalyticsImportResult>.Ok(result));
    }

    [Function("RunDailyAnalytics")]
    public async Task<IActionResult> RunDailyAnalytics(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "analytics/run-daily")] HttpRequest req)
    {
        var unauthorized = RequireRunner(req);
        if (unauthorized is not null) return unauthorized;

        var request = await ReadImportRequest(req);
        if (string.IsNullOrWhiteSpace(request.AccountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        var report = await _reports.RunImportAsync(request);
        var amc = await _amcIngestion.ImportResultsAsync(request);
        var analyzed = 0;
        foreach (var product in _products.GetProductsWithCampaigns(request.AccountKey))
        {
            await _recommendations.AnalyzeAsync(request.AccountKey, product.Id, request.DateRangeStart, request.DateRangeEnd);
            analyzed++;
        }

        return new OkObjectResult(ApiResult<AnalyticsImportResult>.Ok(new AnalyticsImportResult
        {
            Success = true,
            RowsImported = report.RowsImported + amc.RowsImported,
            Summary = $"Daily analytics complete. {report.RowsImported} report rows, {amc.RowsImported} AMC rows, {analyzed} products analyzed."
        }));
    }

    [Function("GetProductsWithCampaigns")]
    public IActionResult GetProductsWithCampaigns(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/with-campaigns")] HttpRequest req)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        return new OkObjectResult(ApiResult<IReadOnlyList<ProductProfile>>.Ok(_products.GetProductsWithCampaigns(accountKey)));
    }

    [Function("AnalyzeProductV2")]
    public async Task<IActionResult> AnalyzeProductV2(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "products/{productId}/analyze-v2")] HttpRequest req,
        string productId)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        try
        {
            var result = await _recommendations.AnalyzeAsync(accountKey, productId);
            return new OkObjectResult(ApiResult<ProductAiAnalysisResult>.Ok(result));
        }
        catch (Exception ex)
        {
            return new ObjectResult(ApiResult.Fail(ex.Message)) { StatusCode = 500 };
        }
    }

    [Function("GetProductHourlyScorecard")]
    public IActionResult GetProductHourlyScorecard(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productId}/scorecard/hourly")] HttpRequest req,
        string productId)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        try
        {
            var rows = _scorecards.BuildScorecard(accountKey, productId).Select(AnalyticsMappers.ToDto).ToList();
            return new OkObjectResult(ApiResult<IReadOnlyList<HourlyScorecardDto>>.Ok(rows));
        }
        catch (Exception ex)
        {
            return new ObjectResult(ApiResult.Fail(ex.Message)) { StatusCode = 500 };
        }
    }

    [Function("GetProductRecommendationsV2")]
    public IActionResult GetProductRecommendationsV2(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productId}/recommendations-v2")] HttpRequest req,
        string productId)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        return new OkObjectResult(ApiResult<IReadOnlyList<AiRecommendationDto>>.Ok(_recommendations.GetRecommendations(accountKey, productId)));
    }

    [Function("GetProductRecommendationTechnicalDetails")]
    public IActionResult GetProductRecommendationTechnicalDetails(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productId}/recommendations/{recommendationId}/technical-details")] HttpRequest req,
        string productId,
        string recommendationId)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var accountKey = req.Query["accountKey"].ToString();
        if (string.IsNullOrWhiteSpace(accountKey))
            return new BadRequestObjectResult(ApiResult.Fail("accountKey is required"));

        try
        {
            var details = _recommendations.GetTechnicalDetails(accountKey, productId, recommendationId);
            return new OkObjectResult(ApiResult<TechnicalRecommendationDetailsDto>.Ok(details));
        }
        catch (InvalidOperationException ex)
        {
            return new NotFoundObjectResult(ApiResult<TechnicalRecommendationDetailsDto>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return new ObjectResult(ApiResult.Fail(ex.Message)) { StatusCode = 500 };
        }
    }

    [Function("GetProductExperiments")]
    public IActionResult GetProductExperiments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productId}/experiments")] HttpRequest req,
        string productId)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;

        var rows = _experiments.GetExperiments(productId).Select(AnalyticsMappers.ToDto).ToList();
        return new OkObjectResult(ApiResult<IReadOnlyList<BeforeAfterComparisonDto>>.Ok(rows));
    }

    [Function("ApproveRecommendationV2")]
    public IActionResult ApproveRecommendationV2(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "recommendations/{recommendationId}/approve-v2")] HttpRequest req,
        string recommendationId)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;
        _recommendations.SetStatus(recommendationId, "Approved");
        return new OkObjectResult(ApiResult.Ok());
    }

    [Function("IgnoreRecommendationV2")]
    public IActionResult IgnoreRecommendationV2(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "recommendations/{recommendationId}/ignore-v2")] HttpRequest req,
        string recommendationId)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;
        _recommendations.SetStatus(recommendationId, "Ignored");
        return new OkObjectResult(ApiResult.Ok());
    }

    [Function("EditRecommendationV2")]
    public async Task<IActionResult> EditRecommendationV2(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "recommendations/{recommendationId}/edit-v2")] HttpRequest req,
        string recommendationId)
    {
        var unauthorized = _access.RequireAuthorized(req);
        if (unauthorized is not null) return unauthorized;
        var editedAction = await new StreamReader(req.Body).ReadToEndAsync();
        _recommendations.SetStatus(recommendationId, "Edited", editedAction.Trim('"'));
        return new OkObjectResult(ApiResult.Ok());
    }

    private IActionResult? RequireRunner(HttpRequest req)
    {
        var expected = _config["AnalyticsRunnerKey"];
        if (string.IsNullOrWhiteSpace(expected))
            expected = _config["RunnerKey"];

        if (string.IsNullOrWhiteSpace(expected))
            return _access.RequireAuthorized(req);

        return req.Headers.TryGetValue("x-runner-key", out var provided) && provided == expected
            ? null
            : new UnauthorizedResult();
    }

    private static async Task<AnalyticsImportRequest> ReadImportRequest(HttpRequest req)
    {
        if (req.Body.CanSeek) req.Body.Position = 0;
        try
        {
            var body = await JsonSerializer.DeserializeAsync<AnalyticsImportRequest>(
                req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (body is not null) return body;
        }
        catch { }

        return new AnalyticsImportRequest
        {
            AccountKey = req.Query["accountKey"].ToString()
        };
    }
}
