using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Globalization;
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

    public async Task<AnalyticsImportResult> RunImportAsync(AnalyticsImportRequest request, CancellationToken ct = default)
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

        var fetch = await _reporting.FetchAsync(account, allMappings, start, end, ct);
        var rows = fetch.Rows;
        _metrics.UpsertDailyMetrics(rows);
        _metrics.UpsertSponsoredProductsReportCoverage(BuildReportCoverageRows(request.AccountKey, allMappings, start, end, fetch));

        return new AnalyticsImportResult
        {
            Success = true,
            RowsImported = rows.Count,
            RowsImportedBySourceReportType = rows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.SourceReportType) ? "Unknown" : r.SourceReportType)
                .ToDictionary(g => g.Key, g => g.Count()),
            Summary = fetch.Warnings.Any()
                ? $"Imported {rows.Count} real rows from Amazon Ads Reporting API ({start:MMM d} - {end:MMM d, yyyy}). Warnings: {string.Join(" | ", fetch.Warnings)}"
                : $"Imported {rows.Count} real rows from Amazon Ads Reporting API ({start:MMM d} - {end:MMM d, yyyy})."
        };
    }

    private static IReadOnlyList<SponsoredProductsReportCoverageRow> BuildReportCoverageRows(
        string accountKey,
        IReadOnlyList<ProductCampaignMapping> mappings,
        DateOnly start,
        DateOnly end,
        SponsoredProductsReportFetchResult fetch)
    {
        var now = DateTimeOffset.UtcNow;
        var productIds = mappings
            .Select(m => m.ProductId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return productIds
            .SelectMany(productId => fetch.ReportSuccessBySourceType.Select(report => new SponsoredProductsReportCoverageRow
            {
                AccountKey = accountKey,
                ProductId = productId,
                SourceReportType = report.Key,
                DateRangeStart = start,
                DateRangeEnd = end,
                Status = report.Value ? "Succeeded" : "Failed",
                Message = report.Value ? "Report fetched from Amazon Ads Reporting API." : "Report fetch failed or returned no downloadable result.",
                ImportedAt = now
            }))
            .ToList();
    }
}

public class AiReviewDataRefreshService
{
    private readonly AmazonAdsReportService _reports;
    private readonly ProductAiRecommendationServiceV2 _recommendations;
    private readonly ConcurrentDictionary<string, AiReviewRefreshJobDto> _jobs = new();

    public AiReviewDataRefreshService(AmazonAdsReportService reports, ProductAiRecommendationServiceV2 recommendations)
    {
        _reports = reports;
        _recommendations = recommendations;
    }

    public AiReviewRefreshJobDto StartRefresh(string accountKey, string productId, DateOnly start, DateOnly end)
    {
        var key = JobKey(accountKey, productId, start, end);
        if (_jobs.TryGetValue(key, out var existing) &&
            existing.Status is "Queued" or "Running")
        {
            return existing;
        }

        var job = new AiReviewRefreshJobDto
        {
            JobId = key,
            AccountKey = accountKey,
            ProductId = productId,
            DateRangeStart = start,
            DateRangeEnd = end,
            Status = "Queued",
            Message = "Amazon Ads report refresh queued. AI Review will keep using cached rows until the job finishes.",
            StartedAt = DateTimeOffset.UtcNow
        };

        _jobs[key] = job;
        _ = Task.Run(async () => await RunRefreshAsync(key));
        return job;
    }

    public AiReviewRefreshJobDto GetStatus(string accountKey, string productId, DateOnly start, DateOnly end)
    {
        var key = JobKey(accountKey, productId, start, end);
        if (_jobs.TryGetValue(key, out var existing))
            return existing;

        return new AiReviewRefreshJobDto
        {
            JobId = key,
            AccountKey = accountKey,
            ProductId = productId,
            DateRangeStart = start,
            DateRangeEnd = end,
            Status = "NotStarted",
            Message = "No background refresh has been started for this product/date range.",
            Coverage = _recommendations.GetDataCoverage(accountKey, productId, start, end)
        };
    }

    private async Task RunRefreshAsync(string key)
    {
        if (!_jobs.TryGetValue(key, out var job)) return;

        try
        {
            job.Status = "Running";
            job.Message = "Refreshing Sponsored Products reports in the background.";
            _jobs[key] = job;

            var result = await _reports.RunImportAsync(new AnalyticsImportRequest
            {
                AccountKey = job.AccountKey,
                DateRangeStart = job.DateRangeStart,
                DateRangeEnd = job.DateRangeEnd
            }, CancellationToken.None);

            job.Result = result;
            job.Coverage = _recommendations.GetDataCoverage(job.AccountKey, job.ProductId, job.DateRangeStart, job.DateRangeEnd);
            job.Status = result.Success ? "Succeeded" : "Failed";
            job.Message = result.Summary;
            job.CompletedAt = DateTimeOffset.UtcNow;
            _jobs[key] = job;
        }
        catch (Exception ex)
        {
            job.Status = "Failed";
            job.Error = ex.Message;
            job.Message = "Amazon Ads report refresh failed. AI Review is still using cached rows.";
            job.CompletedAt = DateTimeOffset.UtcNow;
            try
            {
                job.Coverage = _recommendations.GetDataCoverage(job.AccountKey, job.ProductId, job.DateRangeStart, job.DateRangeEnd);
            }
            catch
            {
                // Keep the original refresh failure visible if coverage lookup also fails.
            }
            _jobs[key] = job;
        }
    }

    private static string JobKey(string accountKey, string productId, DateOnly start, DateOnly end) =>
        $"{accountKey.Trim().ToLowerInvariant()}:{productId.Trim().ToLowerInvariant()}:{start:yyyyMMdd}:{end:yyyyMMdd}";
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

    public AmcHourlyDataStatusDto GetAmcHourlyDataStatus(string accountKey, string productId, DateOnly start, DateOnly end)
    {
        var mappings = _products.GetMappings(accountKey, productId);
        var campaignIds = mappings
            .Select(m => m.CampaignId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var campaignNames = mappings
            .Select(m => m.CampaignName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var traffic = _metrics.GetTrafficHourly(accountKey, campaignIds, campaignNames, start, end);
        var conversions = _metrics.GetConversionsHourly(accountKey, campaignIds, campaignNames, start, end);

        var requested = AmcCoveragePlanner.EnumerateDates(start, end).ToHashSet();
        var trafficCoverage = _metrics.GetAmcCoverage(accountKey, "traffic-hourly", start, end);
        var conversionCoverage = _metrics.GetAmcCoverage(accountKey, "conversion-hourly", start, end);
        var trafficQueried = trafficCoverage.Where(c => c.Status == AmcCoverageStatus.Queried).Select(c => c.Date).ToHashSet();
        var conversionQueried = conversionCoverage.Where(c => c.Status == AmcCoverageStatus.Queried).Select(c => c.Date).ToHashSet();
        var coverageComplete = requested.All(d => trafficQueried.Contains(d) && conversionQueried.Contains(d));
        var pendingDays = trafficCoverage.Concat(conversionCoverage)
            .Where(c => c.Status == AmcCoverageStatus.Pending)
            .Select(c => c.Date)
            .Distinct()
            .Count();

        return new AmcHourlyDataStatusDto
        {
            AccountKey = accountKey,
            ProductId = productId,
            DateRangeStart = start,
            DateRangeEnd = end,
            MappedCampaignCount = campaignIds.Count,
            TrafficRows = traffic.Count,
            ConversionRows = conversions.Count,
            CoverageComplete = coverageComplete,
            PendingDays = pendingDays
        };
    }

    public IReadOnlyList<HourlyScorecard> BuildScorecard(string accountKey, string productId, DateOnly? start = null, DateOnly? end = null)
    {
        var product = _products.GetProduct(productId)
            ?? throw new InvalidOperationException($"Product {productId} not found");

        var rangeEnd = end ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var rangeStart = start ?? rangeEnd.AddDays(-29);

        var mappings = _products.GetMappings(accountKey, productId);
        var campaignIds = mappings
            .Select(m => m.CampaignId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var campaignNames = mappings
            .Select(m => m.CampaignName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var traffic = _metrics.GetTrafficHourly(accountKey, campaignIds, campaignNames, rangeStart, rangeEnd);
        var conversions = _metrics.GetConversionsHourly(accountKey, campaignIds, campaignNames, rangeStart, rangeEnd);

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
    private static readonly JsonSerializerOptions AiReviewJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AdMetricsRepository _metrics;
    private readonly ProductAnalyticsRepository _products;
    private readonly HourlyScorecardService _scorecards;
    private readonly RecommendationExperimentService _experiments;
    private readonly AiRecommendationPromptBuilder _promptBuilder;
    private readonly AiRecommendationEvidenceService _evidenceService;
    private readonly AmcWorkflowService _amcWorkflows;
    private readonly IAiClient _ai;
    private readonly ILogger<ProductAiRecommendationServiceV2> _logger;

    public ProductAiRecommendationServiceV2(
        AdMetricsRepository metrics,
        ProductAnalyticsRepository products,
        HourlyScorecardService scorecards,
        RecommendationExperimentService experiments,
        AiRecommendationPromptBuilder promptBuilder,
        AiRecommendationEvidenceService evidenceService,
        AmcWorkflowService amcWorkflows,
        IAiClient ai,
        ILogger<ProductAiRecommendationServiceV2> logger)
    {
        _metrics = metrics;
        _products = products;
        _scorecards = scorecards;
        _experiments = experiments;
        _promptBuilder = promptBuilder;
        _evidenceService = evidenceService;
        _amcWorkflows = amcWorkflows;
        _ai = ai;
        _logger = logger;
    }

    public async Task<ProductAiAnalysisResult> AnalyzeAsync(string accountKey, string productId, DateOnly? start = null, DateOnly? end = null, bool ensureAmcData = false)
    {
        var product = _products.GetProduct(productId)
            ?? throw new InvalidOperationException($"Product {productId} not found");
        var mappings = _products.GetMappings(accountKey, productId);
        if (!mappings.Any()) throw new InvalidOperationException("This product has no mapped campaigns.");

        var rangeEnd = end ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var rangeStart = start ?? rangeEnd.AddDays(-29);
        var warnings = new List<string>();
        var amcSqlByType = new Dictionary<string, string>();
        if (ensureAmcData)
        {
            var outcome = await EnsureAmcHourlyDataAsync(accountKey, productId, rangeStart, rangeEnd);
            warnings.AddRange(outcome.Warnings);
            foreach (var pair in outcome.SqlByType)
                amcSqlByType[pair.Key] = pair.Value;
        }

        var experimentDtos = _experiments.GetExperiments(productId).Select(AnalyticsMappers.ToDto).ToList();
        var campaignIds = mappings.Select(m => m.CampaignId).ToList();
        var dailyRows = _metrics.GetSponsoredProductsAiReviewRows(accountKey, productId, campaignIds, rangeStart, rangeEnd);
        var scorecard = _scorecards.BuildScorecard(accountKey, productId, rangeStart, rangeEnd);
        var winners = BuildKeywordPerformance(accountKey, productId, rangeStart, rangeEnd, winners: true);
        var losers = BuildKeywordPerformance(accountKey, productId, rangeStart, rangeEnd, winners: false);
        var reportCoverage = _metrics.GetSponsoredProductsReportCoverage(accountKey, productId, rangeStart, rangeEnd);
        var coverage = BuildDataCoverage(accountKey, productId, rangeStart, rangeEnd, dailyRows, scorecard, reportCoverage);
        var dataWarnings = BuildDataCoverageWarnings(coverage, dailyRows, scorecard);
        warnings.AddRange(dataWarnings);

        try
        {
            var recommendations = BuildActionRecommendations(accountKey, product, mappings, dailyRows, scorecard, rangeStart, rangeEnd);
            var aiRanked = false;
            (recommendations, aiRanked) = await RankAndPhraseCandidatesAsync(product, mappings, dailyRows, scorecard, coverage, recommendations, rangeStart, rangeEnd);
            var inputPacket = BuildAiReviewInputPacket(accountKey, product, dailyRows, scorecard, coverage, recommendations, rangeStart, rangeEnd);
            var inputPacketJson = JsonSerializer.Serialize(inputPacket, AiReviewJsonOptions);
            foreach (var rec in recommendations)
                rec.AiReviewInputPacketJson = inputPacketJson;

            _metrics.DeleteOpenRecommendations(accountKey, productId);
            foreach (var rec in recommendations)
            {
                _metrics.UpsertRecommendation(rec);
                _metrics.ReplaceEvidence(rec.RecommendationId, _evidenceService.BuildEvidence(rec, dailyRows, scorecard, winners, losers, experimentDtos));
            }

            return new ProductAiAnalysisResult
            {
                Success = true,
                IsAiGenerated = aiRanked,
                UsedFallback = !aiRanked,
                V2Recommendations = recommendations.Select(AnalyticsMappers.ToDto).ToList(),
                HourlyScorecard = scorecard.Select(AnalyticsMappers.ToDto).ToList(),
                AiInputPacket = inputPacket,
                DataCoverage = coverage,
                Warnings = BuildAnalysisWarnings(scorecard, warnings),
                AmcWorkflowSqlByType = amcSqlByType
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "OpenAI returned invalid JSON for product {ProductId}", productId);
            return FailedAnalysis("OpenAI returned invalid JSON. No AI recommendations were generated.", scorecard, warnings, amcSqlByType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI analysis failed for product {ProductId}", productId);
            return FailedAnalysis(SafeAiError(ex), scorecard, warnings, amcSqlByType);
        }
    }

    private record AmcEnsureOutcome(IReadOnlyList<string> Warnings, IReadOnlyDictionary<string, string> SqlByType);

    private async Task<AmcEnsureOutcome> EnsureAmcHourlyDataAsync(string accountKey, string productId, DateOnly start, DateOnly end)
    {
        var sqlByType = (IReadOnlyDictionary<string, string>)_amcWorkflows.RenderWorkflowSql(start, end);
        try
        {
            var result = await _amcWorkflows.EnsureWorkflowsAsync(accountKey, start, end);
            if (result.SqlByType.Any())
                sqlByType = result.SqlByType;

            var warnings = new List<string>(result.Warnings);
            if (result.ImportedRowsByType.Any())
            {
                var importedList = string.Join(", ", result.ImportedRowsByType.Select(p => $"{p.Key}={p.Value} rows"));
                warnings.Add($"Imported newly-arrived AMC results into the database ({importedList}).");
            }
            if (result.StartedExecutionIdsByType.Any())
            {
                var executionList = string.Join(", ", result.StartedExecutionIdsByType.Select(pair =>
                    $"{pair.Key}={string.Join("/", pair.Value)}"));
                warnings.Add($"AMC hourly data was missing for some dates in {start:MMM d} - {end:MMM d, yyyy}, so the app started AMC workflow executions for the gap only ({executionList}). AMC usually finishes in 5-15 minutes; re-run AI Analysis after that for time-of-day insights. Cached dates from earlier runs were reused.");
            }
            return new AmcEnsureOutcome(warnings, sqlByType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automatic AMC query failed for product {ProductId}", productId);
            return new AmcEnsureOutcome(
                [$"AMC hourly data check failed: {ex.Message}"],
                sqlByType);
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

        var product = _products.GetProduct(productId)
            ?? throw new InvalidOperationException($"Product {productId} not found");
        var mappings = _products.GetMappings(accountKey, productId);
        var campaignIds = mappings.Select(m => m.CampaignId).ToList();
        var dailyRows = _metrics.GetSponsoredProductsAiReviewRows(accountKey, productId, campaignIds, rec.SourceDateRangeStart, rec.SourceDateRangeEnd);
        var scorecardRows = _metrics.GetScorecard(accountKey, productId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd);
        if (!scorecardRows.Any())
            scorecardRows = _scorecards.BuildScorecard(accountKey, productId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd);

        var rawScorecard = scorecardRows.ToList();
        var reportCoverage = _metrics.GetSponsoredProductsReportCoverage(accountKey, productId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd);
        var coverage = BuildDataCoverage(accountKey, productId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd, dailyRows, rawScorecard, reportCoverage);
        var inputPacket = ParseInputPacket(rec.AiReviewInputPacketJson)
            ?? BuildAiReviewInputPacket(accountKey, product, dailyRows, rawScorecard, coverage, _metrics.GetRecommendations(accountKey, productId), rec.SourceDateRangeStart, rec.SourceDateRangeEnd);
        var scorecard = scorecardRows.Select(AnalyticsMappers.ToDto).ToList();
        var allKeywords = BuildKeywordPerformance(accountKey, productId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd, winners: true)
            .Concat(BuildKeywordPerformance(accountKey, productId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd, winners: false))
            .ToList();
        var experiments = _experiments.GetExperiments(productId).Select(AnalyticsMappers.ToDto).ToList();

        var evidence = _metrics.GetEvidence(recommendationId);
        if (!evidence.Any())
        {
            var winners = allKeywords.OrderByDescending(k => k.ROAS).ThenByDescending(k => k.Purchases).Take(6).ToList();
            var losers = allKeywords.OrderByDescending(k => k.Spend).ThenBy(k => k.Purchases).Take(6).ToList();
            evidence = _evidenceService.BuildEvidence(rec, dailyRows, rawScorecard, winners, losers, experiments);
            _metrics.ReplaceEvidence(recommendationId, evidence);
        }

        return new TechnicalRecommendationDetailsDto
        {
            Recommendation = AnalyticsMappers.ToDto(rec),
            Evidence = evidence.Select(AnalyticsMappers.ToDto).ToList(),
            HourlyScorecard = scorecard,
            KeywordPerformance = allKeywords,
            BeforeAfterComparisons = experiments,
            Charts = BuildCharts(scorecard, allKeywords, experiments),
            AiInputPacket = inputPacket,
            DataCoverage = coverage
        };
    }

    private static AiReviewInputPacketDto? ParseInputPacket(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<AiReviewInputPacketDto>(json, AiReviewJsonOptions);
        }
        catch
        {
            return null;
        }
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
            .Select(m => m.CampaignId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rows = _metrics.GetSponsoredProductsAiReviewRows(accountKey, productId, campaignIds, start, end)
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

    public AiReviewDataCoverageDto GetDataCoverage(string accountKey, string productId, DateOnly start, DateOnly end)
    {
        var mappings = _products.GetMappings(accountKey, productId);
        var rows = _metrics.GetSponsoredProductsAiReviewRows(accountKey, productId, mappings.Select(m => m.CampaignId), start, end);
        var scorecard = GetStoredScorecard(accountKey, productId, start, end);
        var reportCoverage = _metrics.GetSponsoredProductsReportCoverage(accountKey, productId, start, end);
        return BuildDataCoverage(accountKey, productId, start, end, rows, scorecard, reportCoverage);
    }

    private IReadOnlyList<HourlyScorecard> GetStoredScorecard(string accountKey, string productId, DateOnly start, DateOnly end)
    {
        try
        {
            return _metrics.GetScorecard(accountKey, productId, start, end);
        }
        catch
        {
            return [];
        }
    }

    private static AiReviewDataCoverageDto BuildDataCoverage(
        string accountKey,
        string productId,
        DateOnly start,
        DateOnly end,
        IReadOnlyList<AdPerformanceDaily> rows,
        IReadOnlyList<HourlyScorecard> scorecard,
        IReadOnlyList<SponsoredProductsReportCoverageRow> reportCoverage)
    {
        var sourceSpecs = new (string Type, string Label, bool Required)[]
        {
            ("Campaign", "Campaign/config", true),
            ("Targeting", "Targets, keywords, bids", true),
            ("SearchTerm", "Search terms", true),
            ("AdvertisedProduct", "Advertised products", true),
            ("PurchasedProduct", "Purchased products", true),
            ("AMC", "Time-of-day evidence", false)
        };
        var coverageBySource = reportCoverage
            .GroupBy(c => c.SourceReportType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(c => c.ImportedAt).First(),
                StringComparer.OrdinalIgnoreCase);

        var sources = sourceSpecs.Select(spec =>
        {
            var matchingRows = spec.Type == "AMC"
                ? []
                : rows.Where(r => string.Equals(r.SourceReportType, spec.Type, StringComparison.OrdinalIgnoreCase)).ToList();
            var amcRows = spec.Type == "AMC" ? scorecard.Count(r => r.Spend > 0 || r.Purchases > 0 || r.Sales > 0) : 0;
            coverageBySource.TryGetValue(spec.Type, out var report);
            var reportReady = report is not null && string.Equals(report.Status, "Succeeded", StringComparison.OrdinalIgnoreCase);
            var rowLastImported = matchingRows
                .Where(r => r.ImportedAt != default && r.ImportedAt != DateTimeOffset.MinValue)
                .Select(r => (DateTimeOffset?)r.ImportedAt)
                .DefaultIfEmpty()
                .Max();
            return new AiReviewDataSourceCoverageDto
            {
                SourceReportType = spec.Type,
                Label = spec.Label,
                IsRequired = spec.Required,
                RowCount = spec.Type == "AMC" ? amcRows : matchingRows.Count,
                IsReady = spec.Type == "AMC" ? amcRows > 0 : matchingRows.Count > 0 || reportReady,
                LastImportedAt = MaxDate(rowLastImported, report?.ImportedAt)
            };
        }).ToList();

        var requiredReady = sources.Where(s => s.IsRequired).All(s => s.IsReady);
        var hasAmc = sources.First(s => s.SourceReportType == "AMC").IsReady;
        var lastRefresh = sources
            .Where(s => s.SourceReportType != "AMC")
            .Select(s => s.LastImportedAt)
            .Where(d => d.HasValue)
            .DefaultIfEmpty()
            .Max();
        var stale = lastRefresh.HasValue && lastRefresh.Value < DateTimeOffset.UtcNow.AddHours(-36);
        var missing = sources.Where(s => s.IsRequired && !s.IsReady).Select(s => s.Label).ToList();

        return new AiReviewDataCoverageDto
        {
            AccountKey = accountKey,
            ProductId = productId,
            DateRangeStart = start,
            DateRangeEnd = end,
            Sources = sources,
            IsReady = requiredReady,
            IsStale = stale,
            LastRefreshAt = lastRefresh,
            Status = requiredReady ? (stale ? "Stale" : "Ready") : "Missing",
            DataQualityLabel = requiredReady
                ? (hasAmc ? "Good" : "Limited")
                : "Missing Amazon Ads reports",
            DataQualityMessage = requiredReady
                ? (hasAmc ? "Required Amazon Ads reports and optional AMC time-of-day evidence are available." : "Required Amazon Ads reports are available; optional AMC hourly evidence is missing.")
                : $"Missing required Amazon Ads data: {string.Join(", ", missing)}."
        };
    }

    private static DateTimeOffset? MaxDate(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left > right ? left : right;
    }

    private static AiReviewInputPacketDto BuildAiReviewInputPacket(
        string accountKey,
        ProductProfile product,
        IReadOnlyList<AdPerformanceDaily> rows,
        IReadOnlyList<HourlyScorecard> scorecard,
        AiReviewDataCoverageDto coverage,
        IReadOnlyList<AiRecommendation> recommendations,
        DateOnly start,
        DateOnly end)
    {
        return new AiReviewInputPacketDto
        {
            AccountKey = accountKey,
            ProductId = product.Id,
            ProductName = product.DisplayName,
            Asin = product.ASIN,
            Sku = product.SKU,
            TargetAcos = product.TargetAcos,
            DefaultDailyBudget = product.DefaultDailyBudget ?? 0,
            DateRangeStart = start,
            DateRangeEnd = end,
            Coverage = coverage,
            Campaigns = TopRows(rows, "Campaign", r => r.CampaignName),
            Targets = TopRows(rows, "Targeting", r => r.TargetingText ?? r.KeywordId ?? r.TargetId ?? r.CampaignName),
            SearchTerms = TopRows(rows, "SearchTerm", r => r.SearchTerm ?? r.TargetingText ?? r.CampaignName),
            AdvertisedProducts = TopRows(rows, "AdvertisedProduct", r => r.AdvertisedAsin ?? r.AdvertisedSku ?? r.CampaignName),
            PurchasedProducts = TopRows(rows, "PurchasedProduct", r => r.PurchasedAsin ?? r.AdvertisedAsin ?? r.CampaignName),
            TimeOfDayEvidence = scorecard
                .Where(r => r.Spend > 0 || r.Purchases > 0 || r.Sales > 0)
                .OrderByDescending(r => r.Spend)
                .Take(24)
                .Select(AnalyticsMappers.ToDto)
                .ToList(),
            Candidates = recommendations.Select(ToActionCandidate).ToList()
        };
    }

    private static List<AiReviewPerformanceRowDto> TopRows(
        IReadOnlyList<AdPerformanceDaily> rows,
        string sourceReportType,
        Func<AdPerformanceDaily, string> labelSelector) =>
        rows
            .Where(r => string.Equals(r.SourceReportType, sourceReportType, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Spend)
            .ThenByDescending(r => r.Sales)
            .Take(50)
            .Select(r => ToPerformanceRow(r, labelSelector(r)))
            .ToList();

    private static AiReviewPerformanceRowDto ToPerformanceRow(AdPerformanceDaily row, string label) => new()
    {
        SourceReportType = row.SourceReportType,
        SellerCentralArea = SellerCentralAreaForSource(row.SourceReportType),
        Label = label,
        CampaignId = row.CampaignId,
        CampaignName = row.CampaignName,
        AdGroupId = row.AdGroupId,
        AdGroupName = row.AdGroupName,
        KeywordId = row.KeywordId,
        TargetId = row.TargetId,
        AdId = row.AdId,
        MatchType = row.MatchType,
        TargetingType = row.TargetingType,
        SearchTermKind = row.SearchTermKind,
        AdvertisedAsin = row.AdvertisedAsin,
        PurchasedAsin = row.PurchasedAsin,
        Bid = row.Bid,
        CampaignBudgetAmount = row.CampaignBudgetAmount,
        CampaignBudgetType = row.CampaignBudgetType,
        CampaignStatus = row.CampaignStatus,
        ServingStatus = row.ServingStatus,
        Spend = row.Spend,
        Sales = row.Sales,
        Purchases = row.Purchases,
        Clicks = row.Clicks,
        Impressions = row.Impressions,
        UnitsSold = row.UnitsSold,
        ROAS = row.ROAS,
        ACOS = row.ACOS,
        CPC = row.CPC,
        CTR = row.CTR,
        CVR = row.CVR,
        ImportedAt = row.ImportedAt == DateTimeOffset.MinValue ? null : row.ImportedAt
    };

    private static AiReviewActionCandidateDto ToActionCandidate(AiRecommendation rec)
    {
        var facts = ParseMetricFacts(rec.MetricFactsJson);
        return new AiReviewActionCandidateDto
        {
            CandidateId = rec.RecommendationId,
            RecommendationType = rec.RecommendationType,
            SellerCentralArea = rec.SellerCentralArea,
            ObjectLabel = rec.ObjectLabel,
            FieldName = rec.FieldName,
            CurrentValue = rec.CurrentValue,
            RecommendedValue = rec.RecommendedValue,
            Problem = rec.Reason,
            Action = rec.RecommendedState,
            ExpectedImpact = rec.ExpectedImpact,
            Confidence = rec.Confidence,
            CanApplyAutomatically = rec.CanApplyAutomatically,
            BlockedReason = rec.BlockedReason,
            DataQualityLabel = rec.DataQualityLabel,
            MetricFacts = facts,
            Evidence =
            [
                new AiReviewEvidenceDto
                {
                    SourceType = rec.RecommendationType == "Dayparting" ? "AMC" : "AmazonAdsReporting",
                    SourceReportType = EvidenceSourceForRecommendation(rec.RecommendationType),
                    SellerCentralArea = rec.SellerCentralArea,
                    ObjectLabel = rec.ObjectLabel,
                    Notes = rec.Reason,
                    MetricFacts = facts
                }
            ]
        };
    }

    private async Task<(IReadOnlyList<AiRecommendation> Recommendations, bool AiRanked)> RankAndPhraseCandidatesAsync(
        ProductProfile product,
        IReadOnlyList<ProductCampaignMapping> mappings,
        IReadOnlyList<AdPerformanceDaily> rows,
        IReadOnlyList<HourlyScorecard> scorecard,
        AiReviewDataCoverageDto coverage,
        IReadOnlyList<AiRecommendation> candidates,
        DateOnly start,
        DateOnly end)
    {
        if (_ai is null || !candidates.Any())
            return (candidates, false);

        var packet = BuildAiReviewInputPacket("", product, rows, scorecard, coverage, candidates, start, end);
        var aiInput = new
        {
            instruction = "Rank and rewrite only the supplied candidates. Do not invent new actions. Keep text concise and Amazon Ads UI oriented.",
            product = new { product.DisplayName, product.ASIN, product.SKU, product.TargetAcos },
            dateRange = new { start, end },
            coverage,
            campaignMappings = mappings.Select(m => new { m.CampaignId, m.CampaignName, m.CampaignType, m.IsActive }),
            candidates = packet.Candidates.Select(c => new
            {
                c.CandidateId,
                c.RecommendationType,
                c.SellerCentralArea,
                c.ObjectLabel,
                c.FieldName,
                c.CurrentValue,
                c.RecommendedValue,
                c.Problem,
                c.Action,
                c.ExpectedImpact,
                c.Confidence,
                c.MetricFacts
            })
        };

        try
        {
            var json = await _ai.CompleteAsync(
                "You are an Amazon Ads analyst. Return strict JSON only. Use only supplied candidateIds and evidence. If evidence is missing, omit that candidate.",
                $$"""
{{JsonSerializer.Serialize(aiInput, AiReviewJsonOptions)}}

Return:
{
  "recommendations": [
    {
      "candidateId": "existing candidate id",
      "rank": 1,
      "title": "short pain point",
      "action": "exact user action in Amazon Ads",
      "reason": "one short sentence using evidence",
      "expectedImpact": "one short sentence",
      "confidence": 0.0
    }
  ]
}
""");
            var ranked = ApplyAiRanking(json, candidates);
            return ranked.Any() ? (ranked, true) : (candidates, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI ranking failed; using deterministic AI Review candidates for product {ProductId}", product.Id);
            return (candidates, false);
        }
    }

    private static IReadOnlyList<AiRecommendation> ApplyAiRanking(string json, IReadOnlyList<AiRecommendation> candidates)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("recommendations", out var arr)) return [];
        var byId = candidates.ToDictionary(c => c.RecommendationId, StringComparer.OrdinalIgnoreCase);
        var ranked = new List<(int Rank, AiRecommendation Recommendation)>();
        foreach (var el in arr.EnumerateArray())
        {
            var id = el.TryGetProperty("candidateId", out var idEl) ? idEl.GetString() ?? "" : "";
            if (!byId.TryGetValue(id, out var rec)) continue;
            if (!ParseMetricFacts(rec.MetricFactsJson).Any()) continue;

            var title = el.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
            var action = el.TryGetProperty("action", out var actionEl) ? actionEl.GetString() ?? "" : "";
            var reason = el.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? "" : "";
            var impact = el.TryGetProperty("expectedImpact", out var impactEl) ? impactEl.GetString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(title)) rec.Title = title;
            if (!string.IsNullOrWhiteSpace(action)) rec.RecommendedState = action;
            if (!string.IsNullOrWhiteSpace(reason)) rec.Reason = reason;
            if (!string.IsNullOrWhiteSpace(impact)) rec.ExpectedImpact = impact;
            var rank = el.TryGetProperty("rank", out var rankEl) && rankEl.TryGetInt32(out var parsedRank) ? parsedRank : ranked.Count + 1;
            ranked.Add((rank, rec));
        }

        return ranked
            .OrderBy(r => r.Rank)
            .ThenByDescending(r => r.Recommendation.Confidence)
            .Select(r => r.Recommendation)
            .Take(8)
            .ToList();
    }

    private static List<string> ParseMetricFacts(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string SellerCentralAreaForSource(string sourceReportType) => sourceReportType switch
    {
        "Campaign" => "Campaign settings",
        "Targeting" => "Targeting",
        "SearchTerm" => "Search terms",
        "AdvertisedProduct" => "Ads/Product",
        "PurchasedProduct" => "Ads/Product",
        _ => "Amazon Ads"
    };

    private static string EvidenceSourceForRecommendation(string type) => type switch
    {
        "NegativeKeyword" or "KeywordHarvest" => "SearchTerm",
        "BidIncrease" or "BidDecrease" => "Targeting",
        "Budget" => "Campaign",
        "ProductConversion" => "AdvertisedProduct/PurchasedProduct",
        "Dayparting" => "AMC",
        _ => "AmazonAdsReporting"
    };

    private static List<string> BuildDataCoverageWarnings(AiReviewDataCoverageDto coverage, IReadOnlyList<AdPerformanceDaily> rows, IReadOnlyList<HourlyScorecard> scorecard)
    {
        var warnings = new List<string>();
        if (!SourceReady(coverage, "SearchTerm"))
            warnings.Add("Search term report data is missing. AI Review can show targeting actions, but it cannot safely recommend negative keywords or keyword harvests from actual customer searches yet.");
        if (!SourceReady(coverage, "AdvertisedProduct"))
            warnings.Add("Advertised product report data is missing. Product/ASIN conversion actions may be limited.");
        if (!SourceReady(coverage, "PurchasedProduct"))
            warnings.Add("Purchased product report data is missing. Cross-ASIN purchase insights may be limited.");
        if (IsBudgetLimited(rows, scorecard))
            warnings.Add("Budget-limited data detected: spend appears to cap early, so afternoon/evening performance should not be treated as reliable evidence until pacing is fixed.");
        return warnings;
    }

    private static bool SourceReady(AiReviewDataCoverageDto coverage, string sourceReportType) =>
        coverage.Sources.Any(s => string.Equals(s.SourceReportType, sourceReportType, StringComparison.OrdinalIgnoreCase) && s.IsReady);

    private static IReadOnlyList<AiRecommendation> BuildActionRecommendations(
        string accountKey,
        ProductProfile product,
        IReadOnlyList<ProductCampaignMapping> mappings,
        IReadOnlyList<AdPerformanceDaily> rows,
        IReadOnlyList<HourlyScorecard> scorecard,
        DateOnly start,
        DateOnly end)
    {
        var recommendations = new List<AiRecommendation>();
        var targetAcos = product.TargetAcos > 0 ? product.TargetAcos : 0.30m;
        var targetRoas = 1m / targetAcos;
        var minWaste = Math.Max(2m, (product.DefaultDailyBudget ?? 20m) * 0.10m);
        var budgetLimited = IsBudgetLimited(rows, scorecard);

        if (budgetLimited)
        {
            var budgetRow = rows
                .Where(r => r.CampaignBudgetAmount is > 0)
                .OrderByDescending(r => r.Spend)
                .FirstOrDefault();
            var campaignId = budgetRow?.CampaignId ?? mappings.FirstOrDefault()?.CampaignId;
            recommendations.Add(NewRecommendation(
                accountKey, product.Id, campaignId, budgetRow?.AdGroupId,
                "Budget",
                "Budget is running out early",
                "Campaign settings",
                budgetRow?.CampaignName ?? mappings.FirstOrDefault()?.CampaignName ?? "Mapped campaign",
                "Daily budget / pacing",
                budgetRow?.CampaignBudgetAmount is > 0 ? Money(budgetRow.CampaignBudgetAmount.Value) : "Budget not imported",
                "Raise budget if profitable, or lower bids on wasteful early traffic before judging later hours",
                "Ads appear to stop spending early in the day, so later hours are missing delivery rather than proving weak demand.",
                "Fix pacing first, then re-run AI Review after the campaign can serve through the full day.",
                0.94m,
                start,
                end,
                [
                    "Data quality: budget-limited",
                    BudgetPacingFact(rows),
                    scorecard.Any(r => r.Spend > 0 || r.Clicks > 0) ? $"Last spend hour in AMC data: {LastSpendHourLabel(scorecard)}" : "AMC hourly pacing evidence not available",
                    budgetRow?.CampaignBudgetAmount is > 0 ? $"Daily budget: {Money(budgetRow.CampaignBudgetAmount.Value)}" : "Daily budget not imported"
                ],
                dataQualityLabel: "Budget-limited",
                dataQualityMessage: "Do not treat afternoon/evening no-data as poor performance until budget pacing is fixed.",
                canApply: false,
                blockedReason: "Review the campaign budget and the wasteful targets first; automatic pacing changes are not supported yet."));
        }
        else
        {
            recommendations.AddRange(BuildDaypartingRecommendations(
                accountKey, product, mappings, scorecard, start, end, minWaste, targetRoas));
        }

        var searchTerms = AggregateRows(rows
            .Where(r => string.Equals(r.SourceReportType, "SearchTerm", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.SearchTerm)),
            r => r.SearchTerm!);

        foreach (var row in searchTerms
            .Where(r => IsWasteful(r, targetAcos, minWaste))
            .OrderByDescending(WasteScore)
            .Take(3))
        {
            var isAsinTerm = string.Equals(row.SearchTermKind, "ASIN", StringComparison.OrdinalIgnoreCase);
            recommendations.Add(NewRecommendation(
                accountKey, product.Id, row.CampaignId, row.AdGroupId,
                "NegativeKeyword",
                isAsinTerm ? "Block losing ASIN search term" : "Add negative for losing search term",
                "Negative targeting",
                row.Label,
                isAsinTerm ? "Negative product target" : "Negative keyword",
                "Not added",
                isAsinTerm ? $"Add negative exact ASIN {row.Label}" : $"Add negative exact keyword \"{row.Label}\"",
                $"{row.Label} spent {Money(row.Spend)} with {row.Purchases} orders in the selected range.",
                "Stop paying for search traffic that is not producing enough sales.",
                ConfidenceFromSpend(row.Spend, 0.88m),
                start,
                end,
                MetricFacts(row),
                canApply: !string.IsNullOrWhiteSpace(row.AdGroupId),
                blockedReason: string.IsNullOrWhiteSpace(row.AdGroupId) ? "Missing ad group ID from the Search Term report." : ""));
        }

        foreach (var row in searchTerms
            .Where(r => r.Purchases > 0 && r.ROAS >= targetRoas * 1.15m)
            .OrderByDescending(r => r.Sales)
            .Take(2))
        {
            var isAsinTerm = string.Equals(row.SearchTermKind, "ASIN", StringComparison.OrdinalIgnoreCase);
            recommendations.Add(NewRecommendation(
                accountKey, product.Id, row.CampaignId, row.AdGroupId,
                "KeywordHarvest",
                isAsinTerm ? "Harvest winning ASIN search term" : "Harvest winning customer search term",
                "Targeting",
                row.Label,
                isAsinTerm ? "Product target" : "Keyword",
                "Only from search-term traffic",
                isAsinTerm ? $"Add {row.Label} as a product target" : $"Add \"{row.Label}\" as exact match",
                $"{row.Label} produced {Money(row.Sales)} sales from {Money(row.Spend)} spend.",
                "Move proven traffic into a target you can bid and budget directly.",
                ConfidenceFromSpend(row.Spend, 0.84m),
                start,
                end,
                MetricFacts(row),
                canApply: false,
                blockedReason: "Creating new keywords/product targets automatically is not wired yet."));
        }

        var targets = AggregateRows(rows
            .Where(r => string.Equals(r.SourceReportType, "Targeting", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.TargetingText)),
            r => r.TargetingText!);

        foreach (var row in targets
            .Where(r => IsWasteful(r, targetAcos, minWaste))
            .OrderByDescending(WasteScore)
            .Take(3))
        {
            var hasLiveObjectId = !string.IsNullOrWhiteSpace(row.TargetId) || !string.IsNullOrWhiteSpace(row.KeywordId);
            recommendations.Add(NewRecommendation(
                accountKey, product.Id, row.CampaignId, row.AdGroupId,
                "BidDecrease",
                "Lower bid on losing target",
                "Targeting",
                row.Label,
                "Bid",
                row.Bid is > 0 ? Money(row.Bid.Value) : "Check live bid in review",
                row.Bid is > 0 ? Money(Math.Max(0.02m, decimal.Round(row.Bid.Value * 0.8m, 2))) : "Lower bid 20% after live bid check",
                $"{row.Label} is spending inefficiently against the {targetAcos:P0} ACOS target.",
                "Reduce wasted spend while keeping the target available for a smaller test.",
                ConfidenceFromSpend(row.Spend, 0.86m),
                start,
                end,
                MetricFacts(row),
                canApply: hasLiveObjectId,
                blockedReason: hasLiveObjectId ? "" : "Missing live keyword/target ID from the Targeting report."));
        }

        foreach (var row in targets
            .Where(r => r.Purchases > 0 && r.ROAS >= targetRoas * 1.15m)
            .OrderByDescending(r => r.Sales)
            .Take(2))
        {
            var hasLiveObjectId = !string.IsNullOrWhiteSpace(row.TargetId) || !string.IsNullOrWhiteSpace(row.KeywordId);
            recommendations.Add(NewRecommendation(
                accountKey, product.Id, row.CampaignId, row.AdGroupId,
                "BidIncrease",
                "Increase bid on winning target",
                "Targeting",
                row.Label,
                "Bid",
                row.Bid is > 0 ? Money(row.Bid.Value) : "Check live bid in review",
                row.Bid is > 0 ? Money(decimal.Round(row.Bid.Value * 1.15m, 2)) : "Increase bid 10-15% after live bid check",
                $"{row.Label} is producing sales efficiently versus the {targetAcos:P0} ACOS target.",
                "Capture more traffic from a target that already converts.",
                ConfidenceFromSpend(row.Spend, 0.82m),
                start,
                end,
                MetricFacts(row),
                canApply: hasLiveObjectId,
                blockedReason: hasLiveObjectId ? "" : "Missing live keyword/target ID from the Targeting report."));
        }

        foreach (var row in AggregateRows(rows
                .Where(r => string.Equals(r.SourceReportType, "AdvertisedProduct", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.AdvertisedAsin)),
                r => r.AdvertisedAsin!)
            .Where(r => r.Clicks >= 10 && r.CVR < 0.05m)
            .OrderByDescending(r => r.Clicks)
            .Take(1))
        {
            recommendations.Add(NewRecommendation(
                accountKey, product.Id, row.CampaignId, row.AdGroupId,
                "ProductConversion",
                "Fix product page conversion before scaling",
                "Ads",
                row.Label,
                "Advertised ASIN conversion",
                $"{row.Clicks} clicks / {row.Purchases} orders",
                "Review main image, price, title, offer, reviews, and coupon before increasing spend",
                "The advertised ASIN is getting clicks but not enough orders.",
                "Improving conversion can lower ACOS without buying more traffic.",
                0.70m,
                start,
                end,
                MetricFacts(row),
                canApply: false,
                blockedReason: "Product listing changes must be made in Seller Central."));
        }

        foreach (var row in AggregateRows(rows
                .Where(r => string.Equals(r.SourceReportType, "PurchasedProduct", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(r.PurchasedAsin) &&
                            !string.Equals(r.PurchasedAsin, r.AdvertisedAsin, StringComparison.OrdinalIgnoreCase)),
                r => r.PurchasedAsin!)
            .Where(r => r.Purchases > 0)
            .OrderByDescending(r => r.Sales)
            .Take(1))
        {
            recommendations.Add(NewRecommendation(
                accountKey, product.Id, row.CampaignId, row.AdGroupId,
                "ProductConversion",
                "Review cross-ASIN purchases",
                "Ads",
                row.Label,
                "Purchased ASIN",
                row.AdvertisedAsin ?? product.ASIN,
                $"Shoppers bought {row.Label}",
                $"Ads for this product generated purchases of {row.Label}.",
                "Use this to spot product variations, competitor leakage, or a better ASIN to advertise.",
                0.68m,
                start,
                end,
                MetricFacts(row),
                canApply: false,
                blockedReason: "This is an insight; no safe automatic change is available."));
        }

        return recommendations
            .GroupBy(r => string.IsNullOrWhiteSpace(r.ActionKey) ? $"{r.RecommendationType}:{r.Title}" : r.ActionKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.Confidence).First())
            .GroupBy(r => r.RecommendationType, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g
                .OrderByDescending(r => r.Confidence)
                .Take(RecommendationTypeLimit(g.Key)))
            .OrderBy(r => RecommendationPriority(r))
            .ThenByDescending(r => r.Confidence)
            .Take(8)
            .ToList();
    }

    private sealed record PerformanceSlice(
        string Label,
        string SourceReportType,
        string CampaignId,
        string CampaignName,
        string? AdGroupId,
        string? AdGroupName,
        string? KeywordId,
        string? TargetId,
        decimal? Bid,
        string? SearchTermKind,
        string? AdvertisedAsin,
        decimal Spend,
        decimal Sales,
        int Purchases,
        int Clicks,
        int Impressions)
    {
        public decimal ROAS => Spend > 0 ? Sales / Spend : 0;
        public decimal ACOS => Sales > 0 ? Spend / Sales : 0;
        public decimal CVR => Clicks > 0 ? (decimal)Purchases / Clicks : 0;
    }

    private sealed record HourlySlice(
        DayOfWeek DayOfWeek,
        int Hour,
        int Impressions,
        int Clicks,
        decimal Spend,
        int Purchases,
        decimal Sales,
        int Units)
    {
        public decimal ROAS => Spend > 0 ? Sales / Spend : 0;
        public decimal CVR => Clicks > 0 ? (decimal)Purchases / Clicks : 0;
    }

    private static IReadOnlyList<AiRecommendation> BuildDaypartingRecommendations(
        string accountKey,
        ProductProfile product,
        IReadOnlyList<ProductCampaignMapping> mappings,
        IReadOnlyList<HourlyScorecard> scorecard,
        DateOnly start,
        DateOnly end,
        decimal minWaste,
        decimal targetRoas)
    {
        if (!scorecard.Any()) return [];

        var hourly = scorecard
            .Where(r => r.Spend > 0 || r.Clicks > 0 || r.Purchases > 0)
            .GroupBy(r => new { r.DayOfWeek, r.Hour })
            .Select(g => new HourlySlice(
                g.Key.DayOfWeek,
                g.Key.Hour,
                g.Sum(r => r.Impressions),
                g.Sum(r => r.Clicks),
                decimal.Round(g.Sum(r => r.Spend), 2),
                g.Sum(r => r.Purchases),
                decimal.Round(g.Sum(r => r.Sales), 2),
                g.Sum(r => r.Units)))
            .ToList();
        if (!hourly.Any()) return [];

        var recommendations = new List<AiRecommendation>();
        var campaignId = mappings.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.CampaignId))?.CampaignId;
        var minHourWaste = Math.Max(1.50m, Math.Min(minWaste, 2.50m));
        var wastefulHours = hourly
            .Where(h => h.Spend >= minHourWaste && h.Clicks >= 2 && h.Purchases == 0)
            .OrderByDescending(h => h.Spend)
            .Take(6)
            .ToList();

        if (wastefulHours.Any())
        {
            var spend = wastefulHours.Sum(h => h.Spend);
            var clicks = wastefulHours.Sum(h => h.Clicks);
            var labels = FormatHourList(wastefulHours);
            recommendations.Add(NewRecommendation(
                accountKey, product.Id, campaignId, null,
                "Dayparting",
                "Lower bids or pause inefficient hours",
                "Dayparting",
                labels,
                "Hourly bid schedule",
                $"{Money(spend)} spend / {clicks} clicks / 0 purchases",
                $"Reduce bids 15-30% or pause during {labels}",
                $"These AMC hourly slots spent {Money(spend)} and generated {clicks} clicks with no purchases in the selected date range.",
                "Move budget away from hours that are currently producing clicks and spend without orders.",
                ConfidenceFromSpend(spend, 0.78m),
                start,
                end,
                [
                    $"Inefficient hour slots: {labels}",
                    $"Spend {Money(spend)}",
                    $"{clicks} clicks / 0 orders",
                    $"AMC hourly rows reviewed: {scorecard.Count}"
                ],
                dataQualityLabel: "Good",
                dataQualityMessage: "Based on imported AMC hourly traffic and conversion rows.",
                canApply: false,
                blockedReason: "Automatic hourly dayparting changes are not wired yet."));
        }

        var winningHours = hourly
            .Where(h => h.Purchases > 0 && h.Spend > 0 && h.ROAS >= targetRoas)
            .OrderByDescending(h => h.Sales)
            .ThenByDescending(h => h.Purchases)
            .Take(4)
            .ToList();

        if (winningHours.Any())
        {
            var spend = winningHours.Sum(h => h.Spend);
            var sales = winningHours.Sum(h => h.Sales);
            var purchases = winningHours.Sum(h => h.Purchases);
            var labels = FormatHourList(winningHours);
            recommendations.Add(NewRecommendation(
                accountKey, product.Id, campaignId, null,
                "Dayparting",
                "Protect high-converting hours",
                "Dayparting",
                labels,
                "Hourly bid schedule",
                $"{purchases} purchases / {Money(sales)} sales / {Money(spend)} spend",
                $"Keep bids active, and consider modest bid increases during {labels}",
                $"These AMC hourly slots produced {purchases} purchases and {Money(sales)} sales at {decimal.Round(sales / spend, 2)}x ROAS.",
                "Protect the hours that are already converting before shifting budget into weaker time slots.",
                ConfidenceFromSpend(spend, 0.74m),
                start,
                end,
                [
                    $"Winning hour slots: {labels}",
                    $"Sales {Money(sales)}",
                    $"Spend {Money(spend)}",
                    $"{purchases} orders"
                ],
                dataQualityLabel: "Good",
                dataQualityMessage: "Based on imported AMC hourly traffic and conversion rows.",
                canApply: false,
                blockedReason: "Automatic hourly dayparting changes are not wired yet."));
        }

        return recommendations;
    }

    private static IReadOnlyList<PerformanceSlice> AggregateRows(IEnumerable<AdPerformanceDaily> rows, Func<AdPerformanceDaily, string> labelSelector) =>
        rows.GroupBy(r => new
            {
                Label = labelSelector(r),
                r.SourceReportType,
                r.CampaignId,
                r.CampaignName,
                r.AdGroupId,
                r.AdGroupName,
                r.KeywordId,
                r.TargetId,
                r.SearchTermKind,
                r.AdvertisedAsin
            })
            .Select(g => new PerformanceSlice(
                g.Key.Label,
                g.Key.SourceReportType,
                g.Key.CampaignId,
                g.Key.CampaignName,
                g.Key.AdGroupId,
                g.Key.AdGroupName,
                g.Key.KeywordId,
                g.Key.TargetId,
                g.FirstOrDefault(x => x.Bid is > 0)?.Bid,
                g.Key.SearchTermKind,
                g.Key.AdvertisedAsin,
                decimal.Round(g.Sum(x => x.Spend), 2),
                decimal.Round(g.Sum(x => x.Sales), 2),
                g.Sum(x => x.Purchases),
                g.Sum(x => x.Clicks),
                g.Sum(x => x.Impressions)))
            .ToList();

    private static bool IsWasteful(PerformanceSlice row, decimal targetAcos, decimal minWaste) =>
        row.Spend >= minWaste &&
        ((row.Purchases == 0 && row.Clicks >= 3) || (row.Sales > 0 && row.ACOS > targetAcos * 1.5m));

    private static decimal WasteScore(PerformanceSlice row) =>
        row.Sales <= 0 ? row.Spend * 2 : row.Spend * Math.Min(row.ACOS, 3m);

    private static decimal ConfidenceFromSpend(decimal spend, decimal baseConfidence) =>
        Math.Clamp(baseConfidence + Math.Min(0.08m, spend / 500m), 0.55m, 0.97m);

    private static IReadOnlyList<string> MetricFacts(PerformanceSlice row)
    {
        var facts = new List<string>
        {
            $"Spend {Money(row.Spend)}",
            $"Sales {Money(row.Sales)}",
            row.Spend > 0 ? $"ROAS {row.ROAS:0.##}x / ACOS {(row.Sales > 0 ? row.ACOS.ToString("P1") : "no sales")}" : "No spend",
            $"{row.Clicks} clicks / {row.Purchases} orders"
        };

        if (string.Equals(row.SourceReportType, "SearchTerm", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(row.SearchTermKind, "ASIN", StringComparison.OrdinalIgnoreCase))
            facts.Add("Search term type: ASIN-like heuristic");

        return facts;
    }

    private static int RecommendationTypeLimit(string type) =>
        type.ToLowerInvariant() switch
        {
            "negativekeyword" => 3,
            "keywordharvest" => 2,
            "biddecrease" => 2,
            "bidincrease" => 1,
            "productconversion" => 2,
            "dayparting" => 2,
            "budget" => 1,
            _ => 1
        };

    private static int RecommendationPriority(AiRecommendation recommendation)
    {
        if (string.Equals(recommendation.DataQualityLabel, "Budget-limited", StringComparison.OrdinalIgnoreCase))
            return 0;

        return recommendation.RecommendationType.ToLowerInvariant() switch
        {
            "negativekeyword" => 1,
            "biddecrease" => 2,
            "keywordharvest" => 3,
            "bidincrease" => 4,
            "productconversion" => 5,
            "dayparting" => 6,
            "budget" => 7,
            _ => 9
        };
    }

    private static string FormatHourList(IReadOnlyList<HourlySlice> hours)
    {
        var labels = hours.Select(h => $"{h.DayOfWeek} {HourLabel(h.Hour)}").Distinct().ToList();
        return labels.Count <= 4
            ? string.Join(", ", labels)
            : $"{string.Join(", ", labels.Take(4))}, and {labels.Count - 4} more";
    }

    private static string HourLabel(int hour)
    {
        var normalized = ((hour % 24) + 24) % 24;
        return normalized switch
        {
            0 => "12 AM",
            < 12 => $"{normalized} AM",
            12 => "12 PM",
            _ => $"{normalized - 12} PM"
        };
    }

    private static AiRecommendation NewRecommendation(
        string accountKey,
        string productId,
        string? campaignId,
        string? adGroupId,
        string type,
        string title,
        string area,
        string objectLabel,
        string field,
        string currentValue,
        string recommendedValue,
        string reason,
        string impact,
        decimal confidence,
        DateOnly start,
        DateOnly end,
        IReadOnlyList<string> metricFacts,
        string dataQualityLabel = "Good",
        string dataQualityMessage = "",
        bool canApply = false,
        string blockedReason = "") => new()
        {
            AccountKey = accountKey,
            ProductId = productId,
            CampaignId = campaignId,
            AdGroupId = adGroupId,
            RecommendationType = type,
            Title = title,
            CurrentState = currentValue,
            RecommendedState = recommendedValue,
            Reason = reason,
            ExpectedImpact = impact,
            ActionKey = $"{area}:{type}:{campaignId}:{adGroupId}:{objectLabel}:{field}",
            SellerCentralArea = area,
            ObjectLabel = objectLabel,
            FieldName = field,
            CurrentValue = currentValue,
            RecommendedValue = recommendedValue,
            DataQualityLabel = dataQualityLabel,
            DataQualityMessage = dataQualityMessage,
            MetricFactsJson = JsonSerializer.Serialize(metricFacts),
            CanApplyAutomatically = canApply,
            BlockedReason = canApply ? "" : blockedReason,
            Confidence = Math.Clamp(confidence, 0.50m, 0.98m),
            SourceDateRangeStart = start,
            SourceDateRangeEnd = end
        };

    private static bool IsBudgetLimited(IReadOnlyList<AdPerformanceDaily> rows, IReadOnlyList<HourlyScorecard> scorecard)
    {
        var campaignRows = rows
            .Where(r => string.Equals(r.SourceReportType, "Campaign", StringComparison.OrdinalIgnoreCase) && r.CampaignBudgetAmount is > 0)
            .ToList();
        if (campaignRows
            .GroupBy(r => new { r.CampaignId, r.Date })
            .Any(g =>
            {
                var budget = g.Max(r => r.CampaignBudgetAmount ?? 0);
                var spend = g.Sum(r => r.Spend);
                return budget > 0 && spend >= budget * 0.85m;
            }))
        {
            return true;
        }

        var lastSpendHour = scorecard.Where(r => r.Spend > 0 || r.Clicks > 0).Select(r => (int?)r.Hour).Max();
        return lastSpendHour is <= 12 && scorecard.Sum(r => r.Spend) > 0;
    }

    private static string LastSpendHourLabel(IReadOnlyList<HourlyScorecard> scorecard)
    {
        var hour = scorecard.Where(r => r.Spend > 0 || r.Clicks > 0).Select(r => (int?)r.Hour).Max();
        return hour.HasValue ? $"{hour.Value:00}:00" : "not available";
    }

    private static string BudgetPacingFact(IReadOnlyList<AdPerformanceDaily> rows)
    {
        var row = rows
            .Where(r => string.Equals(r.SourceReportType, "Campaign", StringComparison.OrdinalIgnoreCase) && r.CampaignBudgetAmount is > 0)
            .GroupBy(r => new { r.CampaignId, r.CampaignName, r.Date })
            .Select(g => new
            {
                g.Key.CampaignName,
                g.Key.Date,
                Budget = g.Max(r => r.CampaignBudgetAmount ?? 0),
                Spend = g.Sum(r => r.Spend)
            })
            .OrderByDescending(r => r.Budget > 0 ? r.Spend / r.Budget : 0)
            .FirstOrDefault();

        return row is null || row.Budget <= 0
            ? "Budget pacing: campaign budget not imported"
            : $"Budget pacing: {row.CampaignName} spent {Money(row.Spend)} of {Money(row.Budget)} on {row.Date:MMM d}";
    }

    private static string Money(decimal value) => value.ToString("C", CultureInfo.GetCultureInfo("en-US"));

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

    private static ProductAiAnalysisResult FailedAnalysis(string message, IReadOnlyList<HourlyScorecard> scorecard, IReadOnlyList<string>? warnings = null, IReadOnlyDictionary<string, string>? amcSqlByType = null) => new()
    {
        Success = false,
        IsAiGenerated = false,
        UsedFallback = false,
        Error = message,
        ErrorMessage = message,
        V2Recommendations = [],
        HourlyScorecard = scorecard.Select(AnalyticsMappers.ToDto).ToList(),
        Warnings = BuildAnalysisWarnings(scorecard, warnings),
        AmcWorkflowSqlByType = amcSqlByType?.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<string, string>()
    };

    private static List<string> BuildAnalysisWarnings(IReadOnlyList<HourlyScorecard> scorecard, IReadOnlyList<string>? warnings = null)
    {
        var result = warnings?.ToList() ?? new List<string>();
        return result;
    }

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
    public IReadOnlyList<AiRecommendationEvidence> BuildEvidence(
        AiRecommendation recommendation,
        IReadOnlyList<AdPerformanceDaily> dailyRows,
        IReadOnlyList<HourlyScorecard> scorecard,
        IReadOnlyList<KeywordPerformanceDto> winners, IReadOnlyList<KeywordPerformanceDto> losers, IReadOnlyList<BeforeAfterComparisonDto> experiments)
    {
        var rows = new List<AiRecommendationEvidence>();
        var reportType = EvidenceReportType(recommendation.RecommendationType);
        var supportingRows = dailyRows
            .Where(r => MatchesRecommendation(recommendation, reportType, r))
            .OrderByDescending(r => r.Spend)
            .ThenByDescending(r => r.Sales)
            .Take(5)
            .ToList();
        if (!supportingRows.Any())
            supportingRows = dailyRows
                .Where(r => string.Equals(r.SourceReportType, PrimaryReportType(reportType), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Spend)
                .ThenByDescending(r => r.Sales)
                .Take(5)
                .ToList();

        var amazonRows = supportingRows.Any()
            ? supportingRows
            : dailyRows.OrderByDescending(r => r.Spend).ThenByDescending(r => r.Sales).Take(5).ToList();
        var spend = amazonRows.Sum(r => r.Spend);
        var sales = amazonRows.Sum(r => r.Sales);
        var purchases = amazonRows.Sum(r => r.Purchases);
        var clicks = amazonRows.Sum(r => r.Clicks);
        var roas = spend > 0 ? sales / spend : 0;
        var acos = sales > 0 ? spend / sales : 0;

        rows.Add(Evidence(recommendation, "AmazonAdsReporting", TableForReportType(reportType), "Spend", spend, "Supporting spend", SourceNotes(amazonRows)));
        rows.Add(Evidence(recommendation, "AmazonAdsReporting", TableForReportType(reportType), "Sales", sales, "Supporting sales", SourceNotes(amazonRows)));
        rows.Add(Evidence(recommendation, "AmazonAdsReporting", TableForReportType(reportType), "Purchases", purchases, "Supporting orders", $"{purchases} orders / {clicks} clicks."));
        rows.Add(Evidence(recommendation, "AmazonAdsReporting", TableForReportType(reportType), "ROAS", roas, "Supporting ROAS", acos > 0 ? $"ACOS {acos:P1} from stored Amazon Ads report rows." : "No tracked sales in the supporting Amazon Ads report rows."));

        foreach (var fact in ParseMetricFacts(recommendation.MetricFactsJson).Take(6))
            rows.Add(Evidence(recommendation, "AmazonAdsReporting", TableForReportType(reportType), "MetricFacts", ExtractFirstDecimal(fact), $"Fact: {fact}", recommendation.ObjectLabel));

        foreach (var keyword in winners.Take(2))
            rows.Add(Evidence(recommendation, "AmazonAdsReporting", "AdPerformanceDaily", keyword.SourceReportType == "SearchTerm" ? "SearchTerm" : "TargetingText", keyword.ROAS, $"Winning {keyword.SourceReportType} ROAS", keyword.KeywordOrSearchTerm));
        foreach (var keyword in losers.Take(2))
            rows.Add(Evidence(recommendation, "AmazonAdsReporting", "AdPerformanceDaily", keyword.SourceReportType == "SearchTerm" ? "SearchTerm" : "TargetingText", keyword.Spend, $"Inefficient {keyword.SourceReportType} spend", keyword.KeywordOrSearchTerm));
        if (recommendation.RecommendationType == "Dayparting" && scorecard.Any())
        {
            rows.Add(Evidence(recommendation, "AMC", "HourlyScorecard", "EfficiencyScore", scorecard.Average(s => s.EfficiencyScore), "Average hourly efficiency", "Optional AMC time-of-day evidence only."));
            rows.Add(Evidence(recommendation, "AMC", "HourlyScorecard", "Hour", scorecard.OrderByDescending(s => s.Purchases).FirstOrDefault()?.Hour ?? 0, "Top conversion hour", "Hour with highest stored AMC purchase volume."));
            rows.Add(Evidence(recommendation, "AMC", "HourlyScorecard", "Hour", scorecard.OrderByDescending(s => s.Spend).FirstOrDefault()?.Hour ?? 0, "Top spend hour", "Hour with highest stored AMC spend."));
        }

        foreach (var experiment in experiments.Take(1))
            rows.Add(Evidence(recommendation, "Experiment", "RecommendationExperiment", "AfterROAS", experiment.AfterROAS, "After recommendation ROAS", experiment.LearningNote));

        return rows;
    }

    private static string EvidenceReportType(string recommendationType) => recommendationType switch
    {
        "NegativeKeyword" or "KeywordHarvest" => "SearchTerm",
        "BidIncrease" or "BidDecrease" => "Targeting",
        "Budget" => "Campaign",
        "ProductConversion" => "AdvertisedProduct/PurchasedProduct",
        "Dayparting" => "AMC",
        _ => "AmazonAdsReporting"
    };

    private static string PrimaryReportType(string reportType) =>
        reportType.Contains('/') ? reportType.Split('/')[0] : reportType;

    private static string TableForReportType(string reportType) => reportType switch
    {
        "Campaign" => "SpCampaignDailyPerformance",
        "Targeting" => "SpTargetingDailyPerformance",
        "SearchTerm" => "SpSearchTermDailyPerformance",
        "AdvertisedProduct/PurchasedProduct" => "SpAdvertisedProductDailyPerformance + SpPurchasedProductDailyPerformance",
        "AMC" => "HourlyScorecard",
        _ => "Sponsored Products reporting"
    };

    private static bool MatchesRecommendation(AiRecommendation recommendation, string reportType, AdPerformanceDaily row)
    {
        if (reportType == "AdvertisedProduct/PurchasedProduct")
        {
            if (!string.Equals(row.SourceReportType, "AdvertisedProduct", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(row.SourceReportType, "PurchasedProduct", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        else if (reportType != "AMC" &&
                 !string.Equals(row.SourceReportType, reportType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(recommendation.CampaignId) &&
            !string.Equals(row.CampaignId, recommendation.CampaignId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(recommendation.AdGroupId) &&
            !string.Equals(row.AdGroupId, recommendation.AdGroupId, StringComparison.OrdinalIgnoreCase))
            return false;

        var objectLabel = recommendation.ObjectLabel?.Trim();
        if (string.IsNullOrWhiteSpace(objectLabel)) return true;
        return MatchesLabel(objectLabel, row.SearchTerm) ||
               MatchesLabel(objectLabel, row.TargetingText) ||
               MatchesLabel(objectLabel, row.AdvertisedAsin) ||
               MatchesLabel(objectLabel, row.PurchasedAsin) ||
               MatchesLabel(objectLabel, row.CampaignName);
    }

    private static bool MatchesLabel(string objectLabel, string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        (string.Equals(objectLabel, candidate, StringComparison.OrdinalIgnoreCase) ||
         objectLabel.Contains(candidate, StringComparison.OrdinalIgnoreCase) ||
         candidate.Contains(objectLabel, StringComparison.OrdinalIgnoreCase));

    private static string SourceNotes(IReadOnlyList<AdPerformanceDaily> rows)
    {
        if (!rows.Any()) return "No matching Amazon Ads report rows were available.";
        var labels = rows
            .Select(r => r.SearchTerm ?? r.TargetingText ?? r.AdvertisedAsin ?? r.PurchasedAsin ?? r.CampaignName)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4);
        return $"Stored Amazon Ads report rows: {string.Join(", ", labels)}.";
    }

    private static IReadOnlyList<string> ParseMetricFacts(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static decimal ExtractFirstDecimal(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"-?\d+(\.\d+)?");
        return match.Success && decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
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
        var anchor = (recommendation.AppliedAt ?? recommendation.CreatedAt).Date;
        var beforeEnd = anchor.AddDays(-1);
        var beforeStart = beforeEnd.AddDays(-6);
        var afterStart = anchor.AddDays(1);
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
        ActionKey = row.ActionKey,
        SellerCentralArea = row.SellerCentralArea,
        ObjectLabel = row.ObjectLabel,
        FieldName = row.FieldName,
        CurrentValue = row.CurrentValue,
        RecommendedValue = row.RecommendedValue,
        DataQualityLabel = row.DataQualityLabel,
        DataQualityMessage = row.DataQualityMessage,
        MetricFacts = ParseMetricFacts(row.MetricFactsJson),
        AiReviewInputPacketJson = row.AiReviewInputPacketJson,
        CanApplyAutomatically = row.CanApplyAutomatically,
        BlockedReason = row.BlockedReason,
        Confidence = row.Confidence,
        SourceDateRangeStart = row.SourceDateRangeStart,
        SourceDateRangeEnd = row.SourceDateRangeEnd,
        Status = row.Status
    };

    private static List<string> ParseMetricFacts(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

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
