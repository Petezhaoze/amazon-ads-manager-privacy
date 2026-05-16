using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class AmazonAdsReportService
{
    private readonly AmazonSPReportingService _reporting;
    private readonly AmazonAccountResolver _accounts;
    private readonly ProductCampaignMappingRepository _mappings;
    private readonly AdMetricsRepository _metrics;
    private readonly AmazonAdsOptions _options;

    public AmazonAdsReportService(
        AmazonSPReportingService reporting,
        AmazonAccountResolver accounts,
        ProductCampaignMappingRepository mappings,
        AdMetricsRepository metrics,
        IOptions<AmazonAdsOptions> options)
    {
        _reporting = reporting;
        _accounts = accounts;
        _mappings = mappings;
        _metrics = metrics;
        _options = options.Value;
    }

    public async Task<AnalyticsImportResult> RunImportAsync(AnalyticsImportRequest request)
    {
        var account = _accounts.Resolve(request.AccountKey)
            ?? throw new InvalidOperationException($"Account '{request.AccountKey}' not found.");
        if (string.IsNullOrWhiteSpace(_options.ClientId) || string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new InvalidOperationException("Amazon Ads credentials are missing. Add AmazonAds:ClientId and AmazonAds:ClientSecret.");
        if (string.IsNullOrWhiteSpace(account.RefreshToken))
            throw new InvalidOperationException($"Amazon Ads refresh token is missing for account '{request.AccountKey}'. Reconnect the account.");
        if (string.IsNullOrWhiteSpace(account.ProfileId))
            throw new InvalidOperationException($"Amazon Ads profileId is missing for account '{request.AccountKey}'. Resolve and save the profile first.");

        var end = request.DateRangeEnd ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var start = request.DateRangeStart ?? end.AddDays(-29);

        var allMappings = _mappings.GetAll()
            .Where(m => string.Equals(m.AccountKey, request.AccountKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var rows = await _reporting.FetchAsync(account, allMappings, start, end);
        _metrics.UpsertDailyMetrics(rows);

        return new AnalyticsImportResult
        {
            Success = true,
            RowsImported = rows.Count,
            RowsImportedBySourceReportType = rows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.SourceReportType) ? "Unknown" : r.SourceReportType)
                .ToDictionary(g => g.Key, g => g.Count()),
            Summary = $"Imported {rows.Count} real rows from Amazon Ads Reporting API ({start:MMM d} - {end:MMM d, yyyy})."
        };
    }
}

public class HourlyScorecardService
{
    private readonly AdMetricsRepository _metrics;
    private readonly ProductAnalyticsRepository _products;

    public HourlyScorecardService(AdMetricsRepository metrics, ProductAnalyticsRepository products)
    {
        _metrics = metrics;
        _products = products;
    }

    public IReadOnlyList<HourlyScorecard> BuildScorecard(string accountKey, string productId, DateOnly? start = null, DateOnly? end = null)
    {
        var product = _products.GetProduct(productId)
            ?? throw new InvalidOperationException($"Product {productId} not found");

        var rangeEnd = end ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var rangeStart = start ?? rangeEnd.AddDays(-29);

        var campaignIds = _products.GetMappings(accountKey, productId)
            .Select(m => m.CampaignId.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var daily = _metrics.GetDailyMetrics(accountKey, productId, campaignIds, rangeStart, rangeEnd);
        if (!daily.Any())
            throw new InvalidOperationException(
                "No real Amazon Ads reporting data found for this product/date range. Run report import first, choose a date range where this product's mapped campaigns had traffic, or update the product's campaign mappings.");

        var traffic = _metrics.GetTrafficHourly(accountKey, campaignIds, rangeStart, rangeEnd);
        var conversions = _metrics.GetConversionsHourly(accountKey, campaignIds, rangeStart, rangeEnd);
        if (!traffic.Any() && !conversions.Any())
        {
            _metrics.ReplaceScorecard(accountKey, productId, rangeStart, rangeEnd, Array.Empty<HourlyScorecard>());
            return Array.Empty<HourlyScorecard>();
        }

        var trafficByHour = traffic
            .GroupBy(t => (t.Date, t.Hour))
            .ToDictionary(g => g.Key, g => new
            {
                Impressions = g.Sum(x => x.Impressions),
                Clicks = g.Sum(x => x.Clicks),
                Spend = g.Sum(x => x.Spend)
            });
        var conversionsByHour = conversions
            .GroupBy(c => (Date: c.ConversionDate, Hour: c.ConversionHour))
            .ToDictionary(g => g.Key, g => new
            {
                Purchases = g.Sum(x => x.Purchases),
                Sales = g.Sum(x => x.Sales),
                Units = g.Sum(x => x.UnitsSold)
            });
        var keys = trafficByHour.Keys.Concat(conversionsByHour.Keys).Distinct().OrderBy(k => k.Date).ThenBy(k => k.Hour).ToList();
        var totalSpend = trafficByHour.Values.Sum(t => t.Spend);
        var totalPurchases = Math.Max(1, conversionsByHour.Values.Sum(c => c.Purchases));
        var targetAcos = product.TargetAcos > 0 ? product.TargetAcos : 0.30m;
        var targetRoas = 1m / targetAcos;

        var rows = new List<HourlyScorecard>();
        foreach (var key in keys)
        {
            trafficByHour.TryGetValue(key, out var t);
            conversionsByHour.TryGetValue(key, out var c);
            var spend = decimal.Round(t?.Spend ?? 0, 2);
            var clicks = t?.Clicks ?? 0;
            var impressions = t?.Impressions ?? 0;
            var purchases = c?.Purchases ?? 0;
            var sales = decimal.Round(c?.Sales ?? 0, 2);
            var units = c?.Units ?? purchases;

            var roas = spend > 0 ? sales / spend : 0m;
            var acos = sales > 0 ? spend / sales : 0m;
            var spendShare = totalSpend > 0 ? spend / totalSpend : 0m;
            var purchaseShare = (decimal)purchases / totalPurchases;

            var score = roas / targetRoas * 55m;
            score += purchaseShare > spendShare ? 22m : -8m;
            if (spend > 8 && purchases == 0) score -= 24m;
            if (acos > targetAcos && sales > 0) score -= 16m;
            score = Math.Clamp(decimal.Round(score, 1), 0, 100);

            rows.Add(new HourlyScorecard
            {
                AccountKey = accountKey,
                ProductId = productId,
                Asin = product.ASIN,
                DateRangeStart = rangeStart,
                DateRangeEnd = rangeEnd,
                DayOfWeek = key.Date.DayOfWeek,
                Hour = key.Hour,
                Impressions = impressions,
                Clicks = clicks,
                Spend = spend,
                Purchases = purchases,
                Sales = sales,
                Units = units,
                ROAS = decimal.Round(roas, 2),
                ACOS = decimal.Round(acos, 4),
                CPC = clicks > 0 ? decimal.Round(spend / clicks, 2) : 0,
                CTR = impressions > 0 ? decimal.Round((decimal)clicks / impressions, 4) : 0,
                CVR = clicks > 0 ? decimal.Round((decimal)purchases / clicks, 4) : 0,
                SalesPerDollar = spend > 0 ? decimal.Round(sales / spend, 2) : 0,
                PurchaseShare = decimal.Round(purchaseShare, 4),
                SpendShare = decimal.Round(spendShare, 4),
                EfficiencyScore = score,
                RecommendedAction = score >= 72 ? "Protect or increase budget for this hour"
                    : score <= 30 ? "Consider pausing or reducing bids for this hour"
                    : "Monitor"
            });
        }

        _metrics.ReplaceScorecard(accountKey, productId, rangeStart, rangeEnd, rows);
        return rows.AsReadOnly();
    }
}

public class AiRecommendationPromptBuilder
{
    public string Build(ProductProfile product, IReadOnlyList<ProductCampaignMapping> mappings, IReadOnlyList<HourlyScorecard> scorecard,
        IReadOnlyList<KeywordPerformanceDto> winners, IReadOnlyList<KeywordPerformanceDto> losers,
        IReadOnlyList<BeforeAfterComparisonDto> experiments)
    {
        var input = new
        {
            product = new { product.Id, product.DisplayName, product.ASIN, product.SKU, product.TargetAcos, product.DefaultDailyBudget },
            campaignMappings = mappings.Select(m => new { m.CampaignId, m.CampaignName, m.CampaignType, m.IsActive }),
            hasAmcHourlyData = scorecard.Any(),
            bestHours = scorecard.OrderByDescending(s => s.EfficiencyScore).Take(8).Select(ToDto),
            worstHours = scorecard.OrderBy(s => s.EfficiencyScore).Take(8).Select(ToDto),
            keywordWinners = winners,
            keywordLosers = losers,
            beforeAfterLearning = experiments
        };

        return $$"""
You are an Amazon Ads analyst. Use the provided summarized data only. Return strict JSON with no markdown fences.
Do not mention SQL, table names, or internal schemas in business-facing text.
Use KeywordHarvest or NegativeKeyword only when sourceReportType is SearchTerm. If the row source is Targeting, recommend BidIncrease, BidDecrease, or CampaignStructure instead.
If hasAmcHourlyData is false, do not create Dayparting or time-of-day recommendations. Explain only keyword, targeting, campaign, budget, or product conversion actions supported by Amazon Ads reporting data.

Input:
{{JsonSerializer.Serialize(input)}}

Return:
{
  "recommendations": [
    {
      "type": "Dayparting|Budget|KeywordHarvest|NegativeKeyword|BidIncrease|BidDecrease|CampaignStructure|ProductConversion|ExperimentLearning",
      "title": "...",
      "action": "...",
      "reason": "...",
      "expectedImpact": "...",
      "confidence": 0.0
    }
  ]
}
""";
    }

    private static HourlyScorecardDto ToDto(HourlyScorecard row) => AnalyticsMappers.ToDto(row);
}

public class ProductAiRecommendationServiceV2
{
    private readonly AdMetricsRepository _metrics;
    private readonly ProductAnalyticsRepository _products;
    private readonly HourlyScorecardService _scorecards;
    private readonly RecommendationExperimentService _experiments;
    private readonly AiRecommendationPromptBuilder _promptBuilder;
    private readonly AiRecommendationEvidenceService _evidenceService;
    private readonly IAiClient _ai;
    private readonly ILogger<ProductAiRecommendationServiceV2> _logger;

    public ProductAiRecommendationServiceV2(
        AdMetricsRepository metrics,
        ProductAnalyticsRepository products,
        HourlyScorecardService scorecards,
        RecommendationExperimentService experiments,
        AiRecommendationPromptBuilder promptBuilder,
        AiRecommendationEvidenceService evidenceService,
        IAiClient ai,
        ILogger<ProductAiRecommendationServiceV2> logger)
    {
        _metrics = metrics;
        _products = products;
        _scorecards = scorecards;
        _experiments = experiments;
        _promptBuilder = promptBuilder;
        _evidenceService = evidenceService;
        _ai = ai;
        _logger = logger;
    }

    public async Task<ProductAiAnalysisResult> AnalyzeAsync(string accountKey, string productId, DateOnly? start = null, DateOnly? end = null)
    {
        var product = _products.GetProduct(productId)
            ?? throw new InvalidOperationException($"Product {productId} not found");
        var mappings = _products.GetMappings(accountKey, productId);
        if (!mappings.Any()) throw new InvalidOperationException("This product has no mapped campaigns.");

        var rangeEnd = end ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var rangeStart = start ?? rangeEnd.AddDays(-29);
        var scorecard = _scorecards.BuildScorecard(accountKey, productId, rangeStart, rangeEnd);
        var winners = BuildKeywordPerformance(accountKey, productId, rangeStart, rangeEnd, winners: true);
        var losers = BuildKeywordPerformance(accountKey, productId, rangeStart, rangeEnd, winners: false);
        var experimentDtos = _experiments.GetExperiments(productId).Select(AnalyticsMappers.ToDto).ToList();

        var prompt = _promptBuilder.Build(product, mappings, scorecard, winners, losers, experimentDtos);
        var primaryCampaignId = mappings.First().CampaignId.ToString();

        try
        {
            var aiJson = await _ai.AnalyzeProductAsync(prompt);
            var recommendations = ParseAiRecommendations(aiJson, accountKey, productId, primaryCampaignId, rangeStart, rangeEnd, winners, losers, scorecard.Any());
            foreach (var rec in recommendations)
            {
                _metrics.UpsertRecommendation(rec);
                _metrics.ReplaceEvidence(rec.RecommendationId, _evidenceService.BuildEvidence(rec, scorecard, winners, losers, experimentDtos));
            }

            return new ProductAiAnalysisResult
            {
                Success = true,
                IsAiGenerated = true,
                UsedFallback = false,
                V2Recommendations = recommendations.Select(AnalyticsMappers.ToDto).ToList(),
                HourlyScorecard = scorecard.Select(AnalyticsMappers.ToDto).ToList(),
                Warnings = scorecard.Any()
                    ? []
                    : ["No AMC conversion-hour data found. Recommendations may be limited to Amazon Ads reporting data only."]
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "OpenAI returned invalid JSON for product {ProductId}", productId);
            return FailedAnalysis("OpenAI returned invalid JSON. No AI recommendations were generated.", scorecard);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI analysis failed for product {ProductId}", productId);
            return FailedAnalysis(SafeAiError(ex), scorecard);
        }
    }

    public IReadOnlyList<AiRecommendationDto> GetRecommendations(string accountKey, string productId) =>
        _metrics.GetRecommendations(accountKey, productId).Select(AnalyticsMappers.ToDto).ToList().AsReadOnly();

    public TechnicalRecommendationDetailsDto GetTechnicalDetails(string accountKey, string productId, string recommendationId)
    {
        var rec = _metrics.GetRecommendation(recommendationId)
            ?? throw new InvalidOperationException("Recommendation not found");
        if (!string.Equals(rec.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(rec.ProductId, productId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Recommendation does not belong to this product.");

        var scorecardRows = _metrics.GetScorecard(accountKey, productId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd);
        if (!scorecardRows.Any())
            scorecardRows = _scorecards.BuildScorecard(accountKey, productId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd);

        var scorecard = scorecardRows.Select(AnalyticsMappers.ToDto).ToList();
        var allKeywords = BuildKeywordPerformance(accountKey, productId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd, winners: true)
            .Concat(BuildKeywordPerformance(accountKey, productId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd, winners: false))
            .ToList();
        var experiments = _experiments.GetExperiments(productId).Select(AnalyticsMappers.ToDto).ToList();

        var evidence = _metrics.GetEvidence(recommendationId);
        if (!evidence.Any())
        {
            var rawScorecard = scorecardRows.ToList();
            var winners = allKeywords.OrderByDescending(k => k.ROAS).ThenByDescending(k => k.Purchases).Take(6).ToList();
            var losers = allKeywords.OrderByDescending(k => k.Spend).ThenBy(k => k.Purchases).Take(6).ToList();
            evidence = _evidenceService.BuildEvidence(rec, rawScorecard, winners, losers, experiments);
            _metrics.ReplaceEvidence(recommendationId, evidence);
        }

        return new TechnicalRecommendationDetailsDto
        {
            Recommendation = AnalyticsMappers.ToDto(rec),
            Evidence = evidence.Select(AnalyticsMappers.ToDto).ToList(),
            HourlyScorecard = scorecard,
            KeywordPerformance = allKeywords,
            BeforeAfterComparisons = experiments,
            Charts = BuildCharts(scorecard, allKeywords, experiments)
        };
    }

    public void SetStatus(string recommendationId, string status, string? editedAction = null)
    {
        var rec = _metrics.GetRecommendation(recommendationId)
            ?? throw new InvalidOperationException("Recommendation not found");
        rec.Status = status;
        if (status == "Approved") rec.ApprovedAt = DateTimeOffset.UtcNow;
        if (status == "Ignored") rec.IgnoredAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(editedAction)) rec.RecommendedState = editedAction;
        _metrics.UpsertRecommendation(rec);
    }

    public IReadOnlyList<KeywordPerformanceDto> BuildKeywordPerformance(string accountKey, string productId, DateOnly start, DateOnly end, bool winners)
    {
        var campaignIds = _products.GetMappings(accountKey, productId)
            .Select(m => m.CampaignId.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rows = _metrics.GetDailyMetrics(accountKey, productId, campaignIds, start, end)
            .Where(d => !string.IsNullOrWhiteSpace(d.SearchTerm) || !string.IsNullOrWhiteSpace(d.TargetingText))
            .GroupBy(d => new
            {
                SourceReportType = string.IsNullOrWhiteSpace(d.SourceReportType) ? "Targeting" : d.SourceReportType,
                Text = !string.IsNullOrWhiteSpace(d.SearchTerm) ? d.SearchTerm! : d.TargetingText!
            })
            .Select(g =>
            {
                var spend = g.Sum(x => x.Spend);
                var clicks = g.Sum(x => x.Clicks);
                var impressions = g.Sum(x => x.Impressions);
                var sales = g.Sum(x => x.Sales);
                var purchases = g.Sum(x => x.Purchases);
                return new KeywordPerformanceDto
                {
                    KeywordOrSearchTerm = g.Key.Text,
                    SourceReportType = g.Key.SourceReportType,
                    CampaignId = g.First().CampaignId,
                    CampaignName = g.First().CampaignName,
                    Spend = decimal.Round(spend, 2),
                    Clicks = clicks,
                    Impressions = impressions,
                    Sales = decimal.Round(sales, 2),
                    Purchases = purchases,
                    ROAS = spend > 0 ? decimal.Round(sales / spend, 2) : 0,
                    ACOS = sales > 0 ? decimal.Round(spend / sales, 4) : 0,
                    CTR = impressions > 0 ? decimal.Round((decimal)clicks / impressions, 4) : 0,
                    CVR = clicks > 0 ? decimal.Round((decimal)purchases / clicks, 4) : 0
                };
            });

        return (winners
                ? rows.OrderByDescending(r => r.ROAS).ThenByDescending(r => r.Purchases)
                : rows.OrderByDescending(r => r.Spend).ThenBy(r => r.Purchases))
            .Take(6)
            .ToList()
            .AsReadOnly();
    }

    private static List<AiRecommendation> ParseAiRecommendations(
        string json, string accountKey, string productId, string? primaryCampaignId,
        DateOnly start, DateOnly end,
        IReadOnlyList<KeywordPerformanceDto> winners,
        IReadOnlyList<KeywordPerformanceDto> losers,
        bool hasAmcHourlyData)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("recommendations", out var arr)) return [];
        var hasSearchTermData = winners.Concat(losers)
            .Any(k => string.Equals(k.SourceReportType, "SearchTerm", StringComparison.OrdinalIgnoreCase) &&
                      !string.IsNullOrWhiteSpace(k.KeywordOrSearchTerm));

        var results = new List<AiRecommendation>();
        foreach (var el in arr.EnumerateArray())
        {
            var type = el.TryGetProperty("type", out var t) ? t.GetString() ?? "Budget" : "Budget";
            if ((type == "NegativeKeyword" || type == "KeywordHarvest") && !hasSearchTermData)
                continue;
            if (type == "Dayparting" && !hasAmcHourlyData)
                continue;

            var title = el.TryGetProperty("title", out var tl) ? tl.GetString() ?? "" : "";
            var action = el.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            var reason = el.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var impact = el.TryGetProperty("expectedImpact", out var ei) ? ei.GetString() ?? "" : "";
            var confidence = el.TryGetProperty("confidence", out var c) ? c.GetDecimal() : 0.70m;

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(action)) continue;

            results.Add(new AiRecommendation
            {
                AccountKey = accountKey,
                ProductId = productId,
                CampaignId = primaryCampaignId,
                RecommendationType = type,
                Title = title,
                CurrentState = "",
                RecommendedState = action,
                Reason = reason,
                ExpectedImpact = impact,
                Confidence = Math.Clamp(confidence, 0.50m, 0.98m),
                SourceDateRangeStart = start,
                SourceDateRangeEnd = end
            });
        }
        return results;
    }

    private static ProductAiAnalysisResult FailedAnalysis(string message, IReadOnlyList<HourlyScorecard> scorecard) => new()
    {
        Success = false,
        IsAiGenerated = false,
        UsedFallback = false,
        Error = message,
        ErrorMessage = message,
        V2Recommendations = [],
        HourlyScorecard = scorecard.Select(AnalyticsMappers.ToDto).ToList(),
        Warnings = scorecard.Any()
            ? []
            : ["No AMC conversion-hour data found. Recommendations may be limited to Amazon Ads reporting data only."]
    };

    private static string SafeAiError(Exception ex)
    {
        if (ex is InvalidOperationException && ex.Message.Contains("OpenAI is not configured", StringComparison.OrdinalIgnoreCase))
            return "OpenAI is not configured. Add OpenAI:ApiKey and OpenAI:Model to run AI analysis.";
        return $"AI analysis failed. No AI recommendations were generated. {ex.Message}";
    }

    private static List<ChartSeriesDto> BuildCharts(List<HourlyScorecardDto> scorecard, List<KeywordPerformanceDto> keywords, List<BeforeAfterComparisonDto> experiments) =>
    [
        new() { Name = "Hourly conversions", Points = scorecard.GroupBy(s => s.Hour).OrderBy(g => g.Key).Select(g => new ChartPointDto { Label = $"{g.Key:00}:00", Value = g.Sum(x => x.Purchases) }).ToList() },
        new() { Name = "Hourly spend", Points = scorecard.GroupBy(s => s.Hour).OrderBy(g => g.Key).Select(g => new ChartPointDto { Label = $"{g.Key:00}:00", Value = g.Sum(x => x.Spend) }).ToList() },
        new() { Name = "Hourly sales", Points = scorecard.GroupBy(s => s.Hour).OrderBy(g => g.Key).Select(g => new ChartPointDto { Label = $"{g.Key:00}:00", Value = g.Sum(x => x.Sales) }).ToList() },
        new() { Name = "Hourly ROAS", Points = scorecard.GroupBy(s => s.Hour).OrderBy(g => g.Key).Select(g => new ChartPointDto { Label = $"{g.Key:00}:00", Value = decimal.Round(g.Average(x => x.ROAS), 2) }).ToList() },
        new() { Name = "Keyword ROAS", Points = keywords.Take(8).Select(k => new ChartPointDto { Label = k.KeywordOrSearchTerm, Value = k.ROAS }).ToList() },
        new() { Name = "Before vs after ROAS", Points = experiments.Take(1).SelectMany(e => new[] { new ChartPointDto { Label = "Before", Value = e.BaselineROAS }, new ChartPointDto { Label = "After", Value = e.AfterROAS } }).ToList() }
    ];
}

public class AiRecommendationEvidenceService
{
    public IReadOnlyList<AiRecommendationEvidence> BuildEvidence(AiRecommendation recommendation, IReadOnlyList<HourlyScorecard> scorecard,
        IReadOnlyList<KeywordPerformanceDto> winners, IReadOnlyList<KeywordPerformanceDto> losers, IReadOnlyList<BeforeAfterComparisonDto> experiments)
    {
        var rows = new List<AiRecommendationEvidence>
        {
            Evidence(recommendation, "AmazonAdsReporting", "AdPerformanceDaily", "Spend", scorecard.Sum(s => s.Spend), "Total spend", "Amazon Ads Reporting daily spend by product/campaign."),
            Evidence(recommendation, "Scorecard", "HourlyScorecard", "EfficiencyScore", scorecard.Any() ? scorecard.Average(s => s.EfficiencyScore) : 0, "Average efficiency score", "Deterministic score from stored Amazon Ads and AMC analytics."),
            Evidence(recommendation, "Scorecard", "HourlyScorecard", "Hour", scorecard.OrderByDescending(s => s.Purchases).FirstOrDefault()?.Hour ?? 0, "Top conversion hour", "Hour with highest stored AMC purchase volume."),
            Evidence(recommendation, "Scorecard", "HourlyScorecard", "Hour", scorecard.OrderByDescending(s => s.Spend).FirstOrDefault()?.Hour ?? 0, "Top spend hour", "Hour with highest stored AMC spend.")
        };

        foreach (var keyword in winners.Take(2))
            rows.Add(Evidence(recommendation, "AmazonAdsReporting", "AdPerformanceDaily", keyword.SourceReportType == "SearchTerm" ? "SearchTerm" : "TargetingText", keyword.ROAS, $"Winning {keyword.SourceReportType} ROAS", keyword.KeywordOrSearchTerm));
        foreach (var keyword in losers.Take(2))
            rows.Add(Evidence(recommendation, "AmazonAdsReporting", "AdPerformanceDaily", keyword.SourceReportType == "SearchTerm" ? "SearchTerm" : "TargetingText", keyword.Spend, $"Inefficient {keyword.SourceReportType} spend", keyword.KeywordOrSearchTerm));
        foreach (var experiment in experiments.Take(1))
            rows.Add(Evidence(recommendation, "Experiment", "RecommendationExperiment", "AfterROAS", experiment.AfterROAS, "After recommendation ROAS", experiment.LearningNote));

        return rows;
    }

    private static AiRecommendationEvidence Evidence(AiRecommendation recommendation, string sourceType, string table, string field,
        decimal value, string metric, string notes) =>
        new()
        {
            RecommendationId = recommendation.RecommendationId,
            SourceType = sourceType,
            SourceTable = table,
            SourceField = field,
            SourceValue = value.ToString("0.####"),
            MetricName = metric,
            MetricValue = value,
            Notes = notes
        };
}

public class RecommendationExperimentService
{
    private readonly AdMetricsRepository _metrics;

    public RecommendationExperimentService(AdMetricsRepository metrics)
    {
        _metrics = metrics;
    }

    public IReadOnlyList<RecommendationExperiment> GetExperiments(string productId) => _metrics.GetExperiments(productId);

    public RecommendationExperiment CompareAndSave(AiRecommendation recommendation)
    {
        var beforeEnd = recommendation.CreatedAt.Date.AddDays(-1);
        var beforeStart = beforeEnd.AddDays(-6);
        var afterStart = recommendation.CreatedAt.Date.AddDays(1);
        var afterEnd = afterStart.AddDays(6);
        var before = _metrics.GetDailyMetrics(recommendation.AccountKey, recommendation.ProductId, DateOnly.FromDateTime(beforeStart), DateOnly.FromDateTime(beforeEnd));
        var after = _metrics.GetDailyMetrics(recommendation.AccountKey, recommendation.ProductId, DateOnly.FromDateTime(afterStart), DateOnly.FromDateTime(afterEnd));
        var hasFullAfterWindow = DateTimeOffset.UtcNow.Date >= afterEnd.Date;
        var baselineSpend = before.Sum(r => r.Spend);
        var afterSpend = after.Sum(r => r.Spend);
        var baselineSales = before.Sum(r => r.Sales);
        var afterSales = after.Sum(r => r.Sales);
        var baselineRoas = baselineSpend > 0 ? baselineSales / baselineSpend : 0;
        var afterRoas = afterSpend > 0 ? afterSales / afterSpend : 0;
        var result = !hasFullAfterWindow || !after.Any()
            ? "Inconclusive"
            : afterRoas > baselineRoas * 1.08m ? "Positive" : afterRoas < baselineRoas * 0.92m ? "Negative" : "Inconclusive";

        return _metrics.UpsertExperiment(new RecommendationExperiment
        {
            RecommendationId = recommendation.RecommendationId,
            ProductId = recommendation.ProductId,
            CampaignId = recommendation.CampaignId,
            MetricBeforeStart = DateOnly.FromDateTime(beforeStart),
            MetricBeforeEnd = DateOnly.FromDateTime(beforeEnd),
            MetricAfterStart = DateOnly.FromDateTime(afterStart),
            MetricAfterEnd = DateOnly.FromDateTime(afterEnd),
            BaselineSpend = decimal.Round(baselineSpend, 2),
            AfterSpend = decimal.Round(afterSpend, 2),
            BaselineSales = decimal.Round(baselineSales, 2),
            AfterSales = decimal.Round(afterSales, 2),
            BaselineROAS = decimal.Round(baselineRoas, 2),
            AfterROAS = decimal.Round(afterRoas, 2),
            BaselineACOS = baselineSales > 0 ? decimal.Round(baselineSpend / baselineSales, 4) : 0,
            AfterACOS = afterSales > 0 ? decimal.Round(afterSpend / afterSales, 4) : 0,
            BaselinePurchases = before.Sum(r => r.Purchases),
            AfterPurchases = after.Sum(r => r.Purchases),
            Result = result,
            LearningNote = !hasFullAfterWindow || !after.Any()
                ? "Not enough post-change data yet."
                : result == "Positive"
                ? "After-period ROAS improved compared with the prior 7 days."
                : result == "Negative"
                    ? "After-period ROAS declined; use caution before repeating this action."
                    : "Performance did not move enough to call a clear result."
        });
    }
}

public static class AnalyticsMappers
{
    public static HourlyScorecardDto ToDto(HourlyScorecard row) => new()
    {
        AccountKey = row.AccountKey,
        ProductId = row.ProductId,
        Asin = row.Asin,
        DateRangeStart = row.DateRangeStart,
        DateRangeEnd = row.DateRangeEnd,
        DayOfWeek = row.DayOfWeek.ToString(),
        Hour = row.Hour,
        Impressions = row.Impressions,
        Clicks = row.Clicks,
        Spend = row.Spend,
        Purchases = row.Purchases,
        Sales = row.Sales,
        Units = row.Units,
        ROAS = row.ROAS,
        ACOS = row.ACOS,
        CPC = row.CPC,
        CTR = row.CTR,
        CVR = row.CVR,
        SalesPerDollar = row.SalesPerDollar,
        PurchaseShare = row.PurchaseShare,
        SpendShare = row.SpendShare,
        EfficiencyScore = row.EfficiencyScore,
        RecommendedAction = row.RecommendedAction
    };

    public static AiRecommendationDto ToDto(AiRecommendation row) => new()
    {
        RecommendationId = row.RecommendationId,
        AccountKey = row.AccountKey,
        ProductId = row.ProductId,
        CampaignId = row.CampaignId,
        RecommendationType = row.RecommendationType,
        Title = row.Title,
        Action = row.RecommendedState,
        Reason = row.Reason,
        ExpectedImpact = row.ExpectedImpact,
        Confidence = row.Confidence,
        SourceDateRangeStart = row.SourceDateRangeStart,
        SourceDateRangeEnd = row.SourceDateRangeEnd,
        Status = row.Status
    };

    public static AiRecommendationEvidenceDto ToDto(AiRecommendationEvidence row) => new()
    {
        EvidenceId = row.EvidenceId,
        RecommendationId = row.RecommendationId,
        SourceType = row.SourceType,
        SourceTable = row.SourceTable,
        SourceField = row.SourceField,
        SourceValue = row.SourceValue,
        MetricName = row.MetricName,
        MetricValue = row.MetricValue,
        Notes = row.Notes
    };

    public static BeforeAfterComparisonDto ToDto(RecommendationExperiment row) => new()
    {
        ExperimentId = row.ExperimentId,
        RecommendationId = row.RecommendationId,
        ProductId = row.ProductId,
        CampaignId = row.CampaignId,
        BaselineSpend = row.BaselineSpend,
        AfterSpend = row.AfterSpend,
        BaselineSales = row.BaselineSales,
        AfterSales = row.AfterSales,
        BaselineROAS = row.BaselineROAS,
        AfterROAS = row.AfterROAS,
        BaselineACOS = row.BaselineACOS,
        AfterACOS = row.AfterACOS,
        BaselinePurchases = row.BaselinePurchases,
        AfterPurchases = row.AfterPurchases,
        Result = row.Result,
        LearningNote = row.LearningNote
    };
}
