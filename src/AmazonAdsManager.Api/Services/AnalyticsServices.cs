using AmazonAdsManager.Shared.Models;
using System.Text.Json;

namespace AmazonAdsManager.Api.Services;

public class AmazonAdsReportService
{
    private readonly MockAnalyticsSeedService _seed;

    public AmazonAdsReportService(MockAnalyticsSeedService seed)
    {
        _seed = seed;
    }

    public Task<AnalyticsImportResult> RunImportAsync(AnalyticsImportRequest request)
    {
        // TODO: Replace seed generation with Amazon Ads Reporting API report creation,
        // polling, download, raw blob storage, and normalized DB upsert.
        var rows = _seed.SeedAmazonAdsReportingData(request.AccountKey, request.DateRangeStart, request.DateRangeEnd);
        return Task.FromResult(new AnalyticsImportResult
        {
            Success = true,
            RowsImported = rows,
            Summary = $"Imported {rows} Amazon Ads reporting rows into analytics storage."
        });
    }
}

public class AmcWorkflowService
{
    public Task<AnalyticsImportResult> RunWorkflowsAsync(AnalyticsImportRequest request)
    {
        // TODO: Use backend-only AMC credentials to execute saved workflows for hourly traffic,
        // conversion-time metrics, and attribution lag. Store execution IDs/status server-side.
        return Task.FromResult(new AnalyticsImportResult
        {
            Success = true,
            RowsImported = 0,
            Summary = "AMC workflow execution queued. Connect AMC credentials/workflow IDs to run real jobs."
        });
    }
}

public class AmcResultIngestionService
{
    private readonly MockAnalyticsSeedService _seed;

    public AmcResultIngestionService(MockAnalyticsSeedService seed)
    {
        _seed = seed;
    }

    public Task<AnalyticsImportResult> ImportResultsAsync(AnalyticsImportRequest request)
    {
        // TODO: Parse AMC CSV results from workflow output, persist raw files to blob if desired,
        // then upsert normalized rows into AmcTrafficHourly, AmcConversionsHourly, and AmcAttributionLag.
        var rows = _seed.SeedAmcData(request.AccountKey, request.DateRangeStart, request.DateRangeEnd);
        return Task.FromResult(new AnalyticsImportResult
        {
            Success = true,
            RowsImported = rows,
            Summary = $"Imported {rows} AMC hourly/attribution rows into analytics storage."
        });
    }
}

public class MockAnalyticsSeedService
{
    private readonly ProductAnalyticsRepository _products;
    private readonly AdMetricsRepository _metrics;

    public MockAnalyticsSeedService(ProductAnalyticsRepository products, AdMetricsRepository metrics)
    {
        _products = products;
        _metrics = metrics;
    }

    public void EnsureProductSeeded(string accountKey, string productId, DateOnly? start = null, DateOnly? end = null)
    {
        if (_metrics.HasAnalyticsRows(accountKey, productId)) return;
        var rangeEnd = end ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var rangeStart = start ?? rangeEnd.AddDays(-29);
        SeedAmazonAdsReportingData(accountKey, rangeStart, rangeEnd);
        SeedAmcData(accountKey, rangeStart, rangeEnd);
    }

    public int SeedAmazonAdsReportingData(string accountKey, DateOnly? start = null, DateOnly? end = null)
    {
        var rangeEnd = end ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var rangeStart = start ?? rangeEnd.AddDays(-29);
        var rows = new List<AdPerformanceDaily>();
        var snapshots = new List<CampaignSnapshot>();

        foreach (var product in _products.GetProductsWithCampaigns(accountKey))
        {
            var mappings = _products.GetMappings(accountKey, product.Id);
            foreach (var mapping in mappings)
            {
                snapshots.Add(new CampaignSnapshot
                {
                    SnapshotDate = rangeEnd,
                    AccountKey = accountKey,
                    CampaignId = mapping.CampaignId.ToString(),
                    CampaignName = mapping.CampaignName,
                    AdProduct = string.IsNullOrWhiteSpace(mapping.CampaignType) ? "Sponsored Products" : mapping.CampaignType,
                    CampaignStatus = mapping.IsActive ? "enabled" : "paused",
                    BudgetAmount = 25,
                    BudgetType = "daily",
                    BiddingStrategy = "dynamic bids - down only"
                });

                var keywords = new[] { product.DisplayName.Split(',')[0], "gift", "office", "case", "premium" };
                for (var day = rangeStart; day <= rangeEnd; day = day.AddDays(1))
                {
                    for (var i = 0; i < keywords.Length; i++)
                    {
                        var seed = Math.Abs(HashCode.Combine(product.Id, mapping.CampaignId, day.DayNumber, i));
                        var impressions = 80 + seed % 500;
                        var clicks = 3 + seed % 26;
                        var spend = decimal.Round(clicks * (0.45m + (seed % 90) / 100m), 2);
                        var strong = i <= 1;
                        var purchases = strong && seed % 3 != 0 ? 1 + seed % 3 : seed % 11 == 0 ? 1 : 0;
                        var sales = purchases * (18 + seed % 55);
                        rows.Add(new AdPerformanceDaily
                        {
                            Date = day,
                            AccountKey = accountKey,
                            ProfileId = "",
                            ProductId = product.Id,
                            Asin = product.ASIN,
                            CampaignId = mapping.CampaignId.ToString(),
                            CampaignName = mapping.CampaignName,
                            AdGroupId = $"ag-{mapping.CampaignId}",
                            AdGroupName = "Default ad group",
                            TargetingText = keywords[i],
                            TargetingType = "keyword",
                            MatchType = i % 2 == 0 ? "exact" : "phrase",
                            SearchTerm = $"{keywords[i]} search",
                            Impressions = impressions,
                            Clicks = clicks,
                            Spend = spend,
                            Purchases = purchases,
                            Sales = sales,
                            UnitsSold = purchases,
                            DetailPageViews = clicks * 2,
                            ROAS = spend > 0 ? decimal.Round(sales / spend, 2) : 0,
                            ACOS = sales > 0 ? decimal.Round(spend / sales, 4) : 0,
                            CPC = clicks > 0 ? decimal.Round(spend / clicks, 2) : 0,
                            CTR = impressions > 0 ? decimal.Round((decimal)clicks / impressions, 4) : 0,
                            CVR = clicks > 0 ? decimal.Round((decimal)purchases / clicks, 4) : 0,
                            CostPerPurchase = purchases > 0 ? decimal.Round(spend / purchases, 2) : spend,
                            PurchaseRate = clicks > 0 ? decimal.Round((decimal)purchases / clicks, 4) : 0
                        });
                    }
                }
            }
        }

        _metrics.UpsertCampaignSnapshots(snapshots);
        _metrics.UpsertDailyMetrics(rows);
        return rows.Count + snapshots.Count;
    }

    public int SeedAmcData(string accountKey, DateOnly? start = null, DateOnly? end = null)
    {
        var rangeEnd = end ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var rangeStart = start ?? rangeEnd.AddDays(-29);
        var traffic = new List<AmcTrafficHourly>();
        var conversions = new List<AmcConversionsHourly>();
        var attribution = new List<AmcAttributionLag>();

        foreach (var product in _products.GetProductsWithCampaigns(accountKey))
        {
            foreach (var mapping in _products.GetMappings(accountKey, product.Id))
            {
                for (var day = rangeStart; day <= rangeEnd; day = day.AddDays(1))
                {
                    for (var hour = 0; hour < 24; hour++)
                    {
                        var seed = Math.Abs(HashCode.Combine(product.Id, mapping.CampaignId, day.DayNumber, hour));
                        var daytime = hour is >= 8 and <= 20;
                        var peak = hour is >= 10 and <= 14;
                        var impressions = (daytime ? 90 : 35) + seed % 180;
                        var clicks = (daytime ? 5 : 2) + seed % 15;
                        var spend = decimal.Round(clicks * (peak ? 0.72m : 0.96m), 2);
                        var purchases = peak && seed % 4 != 0 ? 1 + seed % 2 : hour is >= 1 and <= 5 ? 0 : seed % 17 == 0 ? 1 : 0;
                        var sales = purchases * (22 + seed % 65);

                        traffic.Add(new AmcTrafficHourly
                        {
                            Date = day,
                            Hour = hour,
                            TimeZone = "America/New_York",
                            AccountKey = accountKey,
                            CampaignId = mapping.CampaignId.ToString(),
                            CampaignName = mapping.CampaignName,
                            AdGroupId = $"ag-{mapping.CampaignId}",
                            AdGroupName = "Default ad group",
                            AdProductType = string.IsNullOrWhiteSpace(mapping.CampaignType) ? "Sponsored Products" : mapping.CampaignType,
                            TargetingText = peak ? "high intent keyword" : "broad discovery keyword",
                            MatchType = peak ? "exact" : "phrase",
                            CustomerSearchTerm = peak ? "best converting search" : "research search",
                            Impressions = impressions,
                            Clicks = clicks,
                            Spend = spend
                        });

                        conversions.Add(new AmcConversionsHourly
                        {
                            ConversionDate = day,
                            ConversionHour = hour,
                            TimeZone = "America/New_York",
                            AccountKey = accountKey,
                            CampaignId = mapping.CampaignId.ToString(),
                            CampaignName = mapping.CampaignName,
                            AdGroupId = $"ag-{mapping.CampaignId}",
                            AdGroupName = "Default ad group",
                            AdProductType = string.IsNullOrWhiteSpace(mapping.CampaignType) ? "Sponsored Products" : mapping.CampaignType,
                            TrackedAsin = product.ASIN,
                            ConversionEventType = "purchase",
                            Purchases = purchases,
                            UnitsSold = purchases,
                            Sales = sales,
                            NewToBrandPurchases = purchases > 0 && seed % 2 == 0 ? 1 : 0,
                            NewToBrandSales = purchases > 0 && seed % 2 == 0 ? decimal.Round(sales * 0.55m, 2) : 0
                        });

                        if (purchases > 0)
                        {
                            var lag = seed % 6;
                            attribution.Add(new AmcAttributionLag
                            {
                                AccountKey = accountKey,
                                CampaignId = mapping.CampaignId.ToString(),
                                AdGroupId = $"ag-{mapping.CampaignId}",
                                TargetingText = peak ? "high intent keyword" : "broad discovery keyword",
                                SearchTerm = peak ? "best converting search" : "research search",
                                TrafficDate = day,
                                TrafficHour = Math.Max(0, hour - lag),
                                ConversionDate = day,
                                ConversionHour = hour,
                                HoursToConversion = lag,
                                Purchases = purchases,
                                Sales = sales
                            });
                        }
                    }
                }
            }
        }

        _metrics.UpsertAmcTrafficHourly(traffic);
        _metrics.UpsertAmcConversionsHourly(conversions);
        _metrics.UpsertAmcAttributionLag(attribution);
        return traffic.Count + conversions.Count + attribution.Count;
    }
}

public class HourlyScorecardService
{
    private readonly AdMetricsRepository _metrics;
    private readonly ProductAnalyticsRepository _products;
    private readonly MockAnalyticsSeedService _seed;

    public HourlyScorecardService(AdMetricsRepository metrics, ProductAnalyticsRepository products, MockAnalyticsSeedService seed)
    {
        _metrics = metrics;
        _products = products;
        _seed = seed;
    }

    public IReadOnlyList<HourlyScorecard> BuildScorecard(string accountKey, string productId, DateOnly? start = null, DateOnly? end = null)
    {
        var product = _products.GetProduct(productId) ?? throw new InvalidOperationException($"Product {productId} not found");
        var rangeEnd = end ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        var rangeStart = start ?? rangeEnd.AddDays(-29);
        _seed.EnsureProductSeeded(accountKey, productId, rangeStart, rangeEnd);

        var mappings = _products.GetMappings(accountKey, productId);
        var campaignIds = mappings.Select(m => m.CampaignId.ToString()).ToList();
        var traffic = _metrics.GetTrafficHourly(accountKey, campaignIds, rangeStart, rangeEnd);
        var conversions = _metrics.GetConversionsHourly(accountKey, campaignIds, rangeStart, rangeEnd);

        var totalSpend = traffic.Sum(t => t.Spend);
        var totalPurchases = Math.Max(1, conversions.Sum(c => c.Purchases));
        var targetAcos = product.TargetAcos <= 0 ? 0.30m : product.TargetAcos;
        var targetRoas = targetAcos > 0 ? 1 / targetAcos : 3.33m;

        var rows = traffic.GroupBy(t => new { t.Date.DayOfWeek, t.Hour })
            .Select(group =>
            {
                var purchaseRows = conversions.Where(c => c.ConversionDate.DayOfWeek == group.Key.DayOfWeek && c.ConversionHour == group.Key.Hour).ToList();
                var spend = group.Sum(t => t.Spend);
                var clicks = group.Sum(t => t.Clicks);
                var impressions = group.Sum(t => t.Impressions);
                var purchases = purchaseRows.Sum(c => c.Purchases);
                var sales = purchaseRows.Sum(c => c.Sales);
                var roas = spend > 0 ? sales / spend : 0;
                var acos = sales > 0 ? spend / sales : 0;
                var spendShare = totalSpend > 0 ? spend / totalSpend : 0;
                var purchaseShare = (decimal)purchases / totalPurchases;
                var score = (roas / targetRoas) * 55m;
                score += purchaseShare > spendShare ? 22m : -8m;
                if (spend > 8 && purchases == 0) score -= 24m;
                if (acos > targetAcos && sales > 0) score -= 16m;
                score = Math.Clamp(decimal.Round(score, 1), 0, 100);
                var action = score switch
                {
                    >= 72 => "Protect or increase budget for this hour",
                    <= 30 => "Consider pausing or reducing bids for this hour",
                    _ => "Monitor"
                };

                return new HourlyScorecard
                {
                    AccountKey = accountKey,
                    ProductId = productId,
                    Asin = product.ASIN,
                    DateRangeStart = rangeStart,
                    DateRangeEnd = rangeEnd,
                    DayOfWeek = group.Key.DayOfWeek,
                    Hour = group.Key.Hour,
                    Impressions = impressions,
                    Clicks = clicks,
                    Spend = decimal.Round(spend, 2),
                    Purchases = purchases,
                    Sales = decimal.Round(sales, 2),
                    Units = purchaseRows.Sum(c => c.UnitsSold),
                    ROAS = decimal.Round(roas, 2),
                    ACOS = decimal.Round(acos, 4),
                    CPC = clicks > 0 ? decimal.Round(spend / clicks, 2) : 0,
                    CTR = impressions > 0 ? decimal.Round((decimal)clicks / impressions, 4) : 0,
                    CVR = clicks > 0 ? decimal.Round((decimal)purchases / clicks, 4) : 0,
                    SalesPerDollar = spend > 0 ? decimal.Round(sales / spend, 2) : 0,
                    PurchaseShare = decimal.Round(purchaseShare, 4),
                    SpendShare = decimal.Round(spendShare, 4),
                    EfficiencyScore = score,
                    RecommendedAction = action
                };
            })
            .OrderBy(r => r.DayOfWeek)
            .ThenBy(r => r.Hour)
            .ToList();

        _metrics.ReplaceScorecard(accountKey, productId, rangeStart, rangeEnd, rows);
        return rows;
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
            bestHours = scorecard.OrderByDescending(s => s.EfficiencyScore).Take(8).Select(ToDto),
            worstHours = scorecard.OrderBy(s => s.EfficiencyScore).Take(8).Select(ToDto),
            keywordWinners = winners,
            keywordLosers = losers,
            beforeAfterLearning = experiments
        };

        return $$"""
You are an Amazon Ads analyst. Use the provided stable summarized data only. Return strict JSON.
Do not mention SQL, AMC table names, raw CSV, or internal schemas in the business-facing text.

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
      "confidence": 0.0,
      "sourceMetrics": [ { "name": "...", "value": "..." } ]
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

    public ProductAiRecommendationServiceV2(
        AdMetricsRepository metrics,
        ProductAnalyticsRepository products,
        HourlyScorecardService scorecards,
        RecommendationExperimentService experiments,
        AiRecommendationPromptBuilder promptBuilder,
        AiRecommendationEvidenceService evidenceService,
        IAiClient ai)
    {
        _metrics = metrics;
        _products = products;
        _scorecards = scorecards;
        _experiments = experiments;
        _promptBuilder = promptBuilder;
        _evidenceService = evidenceService;
        _ai = ai;
    }

    public async Task<ProductAiAnalysisResult> AnalyzeAsync(string accountKey, string productId, DateOnly? start = null, DateOnly? end = null)
    {
        var product = _products.GetProduct(productId) ?? throw new InvalidOperationException($"Product {productId} not found");
        var mappings = _products.GetMappings(accountKey, productId);
        if (!mappings.Any()) throw new InvalidOperationException("This product has no mapped campaigns.");

        var scorecard = _scorecards.BuildScorecard(accountKey, productId, start, end);
        var rangeStart = scorecard.First().DateRangeStart;
        var rangeEnd = scorecard.First().DateRangeEnd;
        var winners = BuildKeywordPerformance(accountKey, productId, rangeStart, rangeEnd, winners: true);
        var losers = BuildKeywordPerformance(accountKey, productId, rangeStart, rangeEnd, winners: false);
        var experimentDtos = _experiments.GetExperiments(productId).Select(AnalyticsMappers.ToDto).ToList();
        var prompt = _promptBuilder.Build(product, mappings, scorecard, winners, losers, experimentDtos);
        _ = await _ai.AnalyzeProductAsync(prompt);

        var recommendations = BuildDeterministicRecommendations(accountKey, product, mappings, scorecard, winners, losers, experimentDtos);
        foreach (var rec in recommendations)
        {
            _metrics.UpsertRecommendation(rec);
            _metrics.ReplaceEvidence(rec.RecommendationId, _evidenceService.BuildEvidence(rec, scorecard, winners, losers, experimentDtos));
        }

        return new ProductAiAnalysisResult
        {
            Success = true,
            V2Recommendations = recommendations.Select(AnalyticsMappers.ToDto).ToList(),
            HourlyScorecard = scorecard.Select(AnalyticsMappers.ToDto).ToList()
        };
    }

    public IReadOnlyList<AiRecommendationDto> GetRecommendations(string accountKey, string productId) =>
        _metrics.GetRecommendations(accountKey, productId).Select(AnalyticsMappers.ToDto).ToList().AsReadOnly();

    public TechnicalRecommendationDetailsDto GetTechnicalDetails(string accountKey, string productId, string recommendationId)
    {
        var rec = _metrics.GetRecommendation(recommendationId) ?? throw new InvalidOperationException("Recommendation not found");
        var scorecard = _metrics.GetScorecard(accountKey, productId).Select(AnalyticsMappers.ToDto).ToList();
        var keywordPerformance = BuildKeywordPerformance(accountKey, productId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd, winners: true)
            .Concat(BuildKeywordPerformance(accountKey, productId, rec.SourceDateRangeStart, rec.SourceDateRangeEnd, winners: false))
            .ToList();
        var experiments = _experiments.GetExperiments(productId).Select(AnalyticsMappers.ToDto).ToList();

        return new TechnicalRecommendationDetailsDto
        {
            Recommendation = AnalyticsMappers.ToDto(rec),
            Evidence = _metrics.GetEvidence(recommendationId).Select(AnalyticsMappers.ToDto).ToList(),
            HourlyScorecard = scorecard,
            KeywordPerformance = keywordPerformance,
            BeforeAfterComparisons = experiments,
            Charts = BuildCharts(scorecard, keywordPerformance, experiments)
        };
    }

    public void SetStatus(string recommendationId, string status, string? editedAction = null)
    {
        var rec = _metrics.GetRecommendation(recommendationId) ?? throw new InvalidOperationException("Recommendation not found");
        rec.Status = status;
        if (status == "Approved") rec.ApprovedAt = DateTimeOffset.UtcNow;
        if (status == "Ignored") rec.IgnoredAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(editedAction)) rec.RecommendedState = editedAction;
        _metrics.UpsertRecommendation(rec);
    }

    public IReadOnlyList<KeywordPerformanceDto> BuildKeywordPerformance(string accountKey, string productId, DateOnly start, DateOnly end, bool winners)
    {
        var rows = _metrics.GetDailyMetrics(accountKey, productId, start, end)
            .GroupBy(d => d.SearchTerm ?? d.TargetingText ?? "(none)")
            .Select(g =>
            {
                var spend = g.Sum(x => x.Spend);
                var clicks = g.Sum(x => x.Clicks);
                var impressions = g.Sum(x => x.Impressions);
                var sales = g.Sum(x => x.Sales);
                var purchases = g.Sum(x => x.Purchases);
                return new KeywordPerformanceDto
                {
                    KeywordOrSearchTerm = g.Key,
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

    private static List<AiRecommendation> BuildDeterministicRecommendations(string accountKey, ProductProfile product, IReadOnlyList<ProductCampaignMapping> mappings,
        IReadOnlyList<HourlyScorecard> scorecard, IReadOnlyList<KeywordPerformanceDto> winners, IReadOnlyList<KeywordPerformanceDto> losers,
        IReadOnlyList<BeforeAfterComparisonDto> experiments)
    {
        var start = scorecard.First().DateRangeStart;
        var end = scorecard.First().DateRangeEnd;
        var best = scorecard.OrderByDescending(s => s.EfficiencyScore).First();
        var weakHours = scorecard.Where(s => s.EfficiencyScore <= 30 && s.Spend > 0).OrderBy(s => s.Hour).Take(5).ToList();
        var recs = new List<AiRecommendation>();

        if (weakHours.Any())
        {
            recs.Add(NewRecommendation(accountKey, product.Id, mappings.First().CampaignId.ToString(), "Dayparting",
                "Pause low-efficiency hours",
                $"Ads are currently spending during {FormatHours(weakHours)}.",
                $"Pause or reduce ads during {FormatHours(weakHours)}.",
                $"Those hours spent ${weakHours.Sum(h => h.Spend):F2} with only {weakHours.Sum(h => h.Purchases)} purchases in the selected period.",
                "Reduce wasted spend while protecting stronger hours.",
                0.82m, start, end));
        }

        recs.Add(NewRecommendation(accountKey, product.Id, mappings.First().CampaignId.ToString(), "Budget",
            "Protect budget for strongest conversion hours",
            $"Best hour is {best.Hour:00}:00 with ROAS {best.ROAS:F2}.",
            $"Protect or increase budget around {best.Hour:00}:00.",
            "AMC conversion-time and traffic-hour summaries show this hour converts more efficiently than average.",
            "Shift budget toward hours with stronger sales per dollar.",
            0.76m, start, end));

        var winner = winners.FirstOrDefault();
        if (winner is not null)
        {
            recs.Add(NewRecommendation(accountKey, product.Id, winner.CampaignId, "KeywordHarvest",
                "Harvest winning search term",
                $"{winner.KeywordOrSearchTerm} is producing sales efficiently.",
                $"Add or protect exact-match coverage for '{winner.KeywordOrSearchTerm}'.",
                $"It generated ${winner.Sales:F2} sales at ROAS {winner.ROAS:F2}.",
                "Give proven search terms more controlled budget.",
                0.74m, start, end));
        }

        var loser = losers.FirstOrDefault(l => l.Purchases == 0 && l.Spend > 5) ?? losers.FirstOrDefault();
        if (loser is not null)
        {
            recs.Add(NewRecommendation(accountKey, product.Id, loser.CampaignId, "NegativeKeyword",
                "Reduce inefficient search term spend",
                $"{loser.KeywordOrSearchTerm} is using budget inefficiently.",
                $"Lower bids or add a negative match for '{loser.KeywordOrSearchTerm}'.",
                $"It spent ${loser.Spend:F2} with {loser.Purchases} purchases.",
                "Reduce spend that is unlikely to convert.",
                0.71m, start, end));
        }

        var positiveExperiment = experiments.FirstOrDefault(e => e.Result == "Positive");
        if (positiveExperiment is not null)
        {
            recs.Add(NewRecommendation(accountKey, product.Id, positiveExperiment.CampaignId, "ExperimentLearning",
                "Repeat what improved after the last recommendation",
                "A previous approved recommendation improved after-period performance.",
                "Use similar changes on comparable campaigns.",
                positiveExperiment.LearningNote,
                "Use before/after learning instead of one-off guesses.",
                0.68m, start, end));
        }

        return recs;
    }

    private static AiRecommendation NewRecommendation(string accountKey, string productId, string? campaignId, string type, string title,
        string currentState, string action, string reason, string impact, decimal confidence, DateOnly start, DateOnly end) =>
        new()
        {
            AccountKey = accountKey,
            ProductId = productId,
            CampaignId = campaignId,
            RecommendationType = type,
            Title = title,
            CurrentState = currentState,
            RecommendedState = action,
            Reason = reason,
            ExpectedImpact = impact,
            Confidence = confidence,
            SourceDateRangeStart = start,
            SourceDateRangeEnd = end
        };

    private static string FormatHours(IEnumerable<HourlyScorecard> hours) =>
        string.Join(", ", hours.Select(h => $"{h.Hour:00}:00").Distinct());

    private static List<ChartSeriesDto> BuildCharts(List<HourlyScorecardDto> scorecard, List<KeywordPerformanceDto> keywords, List<BeforeAfterComparisonDto> experiments) =>
        new()
        {
            new ChartSeriesDto { Name = "Hourly conversions", Points = scorecard.GroupBy(s => s.Hour).OrderBy(g => g.Key).Select(g => new ChartPointDto { Label = $"{g.Key:00}", Value = g.Sum(x => x.Purchases) }).ToList() },
            new ChartSeriesDto { Name = "Hourly spend", Points = scorecard.GroupBy(s => s.Hour).OrderBy(g => g.Key).Select(g => new ChartPointDto { Label = $"{g.Key:00}", Value = g.Sum(x => x.Spend) }).ToList() },
            new ChartSeriesDto { Name = "Hourly sales", Points = scorecard.GroupBy(s => s.Hour).OrderBy(g => g.Key).Select(g => new ChartPointDto { Label = $"{g.Key:00}", Value = g.Sum(x => x.Sales) }).ToList() },
            new ChartSeriesDto { Name = "Hourly ROAS", Points = scorecard.GroupBy(s => s.Hour).OrderBy(g => g.Key).Select(g => new ChartPointDto { Label = $"{g.Key:00}", Value = decimal.Round(g.Average(x => x.ROAS), 2) }).ToList() },
            new ChartSeriesDto { Name = "Keyword ROAS", Points = keywords.Take(8).Select(k => new ChartPointDto { Label = k.KeywordOrSearchTerm, Value = k.ROAS }).ToList() },
            new ChartSeriesDto { Name = "Before vs after ROAS", Points = experiments.Take(1).SelectMany(e => new[] { new ChartPointDto { Label = "Before", Value = e.BaselineROAS }, new ChartPointDto { Label = "After", Value = e.AfterROAS } }).ToList() }
        };
}

public class AiRecommendationEvidenceService
{
    public IReadOnlyList<AiRecommendationEvidence> BuildEvidence(AiRecommendation recommendation, IReadOnlyList<HourlyScorecard> scorecard,
        IReadOnlyList<KeywordPerformanceDto> winners, IReadOnlyList<KeywordPerformanceDto> losers, IReadOnlyList<BeforeAfterComparisonDto> experiments)
    {
        var rows = new List<AiRecommendationEvidence>
        {
            Evidence(recommendation, "AmazonAdsReporting", "AdPerformanceDaily", "Spend", scorecard.Sum(s => s.Spend), "Spend", "Amazon Ads Reporting daily spend normalized by product/campaign."),
            Evidence(recommendation, "AMC", "AmcConversionsHourly", "ConversionHour", scorecard.OrderByDescending(s => s.Purchases).FirstOrDefault()?.Hour ?? 0, "Top conversion hour", "AMC conversion-time summary."),
            Evidence(recommendation, "AMC", "AmcTrafficHourly", "Hour", scorecard.OrderByDescending(s => s.Spend).FirstOrDefault()?.Hour ?? 0, "Top traffic spend hour", "AMC traffic-hour summary."),
            Evidence(recommendation, "Scorecard", "HourlyScorecard", "EfficiencyScore", scorecard.Any() ? scorecard.Average(s => s.EfficiencyScore) : 0, "Average efficiency score", "Deterministic pre-AI score.")
        };

        foreach (var keyword in winners.Take(2))
            rows.Add(Evidence(recommendation, "AmazonAdsReporting", "AdPerformanceDaily", "SearchTerm", keyword.ROAS, "Winning keyword ROAS", keyword.KeywordOrSearchTerm));
        foreach (var keyword in losers.Take(2))
            rows.Add(Evidence(recommendation, "AmazonAdsReporting", "AdPerformanceDaily", "SearchTerm", keyword.Spend, "Inefficient keyword spend", keyword.KeywordOrSearchTerm));
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
        var baselineSpend = before.Sum(r => r.Spend);
        var afterSpend = after.Sum(r => r.Spend);
        var baselineSales = before.Sum(r => r.Sales);
        var afterSales = after.Sum(r => r.Sales);
        var baselineRoas = baselineSpend > 0 ? baselineSales / baselineSpend : 0;
        var afterRoas = afterSpend > 0 ? afterSales / afterSpend : 0;
        var result = afterRoas > baselineRoas * 1.08m ? "Positive" : afterRoas < baselineRoas * 0.92m ? "Negative" : "Inconclusive";

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
            LearningNote = result == "Positive"
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
