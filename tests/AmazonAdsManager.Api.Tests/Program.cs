using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class AmcImportTests
{
    private const string AccountKey = "test-account";
    private const string ProfileId = "test-profile";
    private const string ProductId = "test-product";

    private static readonly string[] MappedCampaignIds =
    [
        "A00285502F3SFLYZBXR9H",
        "A0837808OIGJIZ3WVK01",
        "A00313342AJFGCFRWJDNX"
    ];

    [Fact]
    public async Task ConversionImportPrefersCampaignIdString()
    {
        var metrics = new CapturingMetricsRepository();
        var service = NewIngestionService(metrics);
        var csv = """
conversion_date,conversion_hour,time_zone,campaign_id,campaign_id_string,campaign_name,ad_product_type,tracked_asin,conversion_event_type,purchases,units_sold,sales
2026-05-08,10,UTC,397504685996681,A00285502F3SFLYZBXR9H,Alpha,SPONSORED_PRODUCTS,B0TEST,purchase,2,2,49.98
2026-05-09,11,UTC,397504685996682,A0837808OIGJIZ3WVK01,Beta,SPONSORED_PRODUCTS,B0TEST,purchase,1,1,19.99
""";

        var result = await service.ImportCsvAsync(new AmcResultImportRequest(AccountKey, "conversion-hourly", ProfileId, "UTC"), csv);

        Assert.Equal(2, result.RowsImported);
        Assert.Equal(
            ["A00285502F3SFLYZBXR9H", "A0837808OIGJIZ3WVK01"],
            metrics.Conversions.Select(r => r.CampaignId));
    }

    [Fact]
    public async Task TrafficImportPrefersCampaignIdString()
    {
        var metrics = new CapturingMetricsRepository();
        var service = NewIngestionService(metrics);
        var csv = """
traffic_date,traffic_hour,time_zone,campaign_id,campaign_id_string,campaign_name,ad_product_type,targeting_text,match_type,customer_search_term,impressions,clicks,spend
2026-05-08,9,UTC,397504685996681,A00285502F3SFLYZBXR9H,Alpha,SPONSORED_PRODUCTS,widgets,exact,blue widget,100,7,3.21
""";

        var result = await service.ImportCsvAsync(new AmcResultImportRequest(AccountKey, "traffic-hourly", ProfileId, "UTC"), csv);

        Assert.Equal(1, result.RowsImported);
        Assert.Equal("A00285502F3SFLYZBXR9H", metrics.Traffic.Single().CampaignId);
    }

    [Fact]
    public async Task HeaderOnlyConversionImportSucceedsWithZeroRows()
    {
        var metrics = new CapturingMetricsRepository();
        var service = NewIngestionService(metrics);
        var csv = "conversion_date,conversion_hour,campaign_id,campaign_name,ad_product_type,purchases,units_sold,sales\n";

        var result = await service.ImportCsvAsync(new AmcResultImportRequest(AccountKey, "conversion-hourly", ProfileId, "UTC"), csv);

        Assert.True(result.Success);
        Assert.Equal(0, result.RowsImported);
        Assert.Empty(metrics.Conversions);
    }

    [Fact]
    public async Task HeaderOnlyTrafficImportSucceedsWithZeroRows()
    {
        var metrics = new CapturingMetricsRepository();
        var service = NewIngestionService(metrics);
        var csv = "traffic_date,traffic_hour,campaign_id,campaign_name,ad_product_type,impressions,clicks,spend\n";

        var result = await service.ImportCsvAsync(new AmcResultImportRequest(AccountKey, "traffic-hourly", ProfileId, "UTC"), csv);

        Assert.True(result.Success);
        Assert.Equal(0, result.RowsImported);
        Assert.Empty(metrics.Traffic);
    }

    [Fact]
    public async Task MalformedSingleLineImportStillFails()
    {
        var metrics = new CapturingMetricsRepository();
        var service = NewIngestionService(metrics);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportCsvAsync(new AmcResultImportRequest(AccountKey, "traffic-hourly", ProfileId, "UTC"), "not_an_amc_header\n"));
    }

    [Fact]
    public async Task HourlyScorecardUsesMappedCampaignIds()
    {
        var metrics = new CapturingMetricsRepository();
        var service = NewIngestionService(metrics);
        var csv = """
conversion_date,conversion_hour,time_zone,campaign_id,campaign_id_string,campaign_name,ad_product_type,tracked_asin,conversion_event_type,purchases,units_sold,sales
2026-05-08,10,UTC,397504685996681,A00285502F3SFLYZBXR9H,Alpha,SPONSORED_PRODUCTS,B0TEST,purchase,2,2,49.98
2026-05-08,11,UTC,397504685996682,A0837808OIGJIZ3WVK01,Beta,SPONSORED_PRODUCTS,B0TEST,purchase,1,1,19.99
2026-05-08,12,UTC,397504685996683,A00313342AJFGCFRWJDNX,Gamma,SPONSORED_PRODUCTS,B0TEST,purchase,3,3,89.97
""";
        await service.ImportCsvAsync(new AmcResultImportRequest(AccountKey, "conversion-hourly", ProfileId, "UTC"), csv);

        metrics.Daily.AddRange(MappedCampaignIds.Select(id => new AdPerformanceDaily
        {
            Date = new DateOnly(2026, 5, 8),
            AccountKey = AccountKey,
            ProfileId = ProfileId,
            ProductId = ProductId,
            CampaignId = id,
            CampaignName = id,
            Impressions = 100,
            Clicks = 10,
            Spend = 5,
            Purchases = 1,
            Sales = 20
        }));

        var products = new StubProductAnalyticsRepository(MappedCampaignIds);
        var scorecard = new HourlyScorecardService(metrics, products)
            .BuildScorecard(AccountKey, ProductId, new DateOnly(2026, 5, 8), new DateOnly(2026, 5, 14));

        Assert.Equal(6, scorecard.Sum(r => r.Purchases));
        Assert.Contains(scorecard, r => r.Hour == 10);
    }

    [Fact]
    public async Task HourlyScorecardFallsBackToMappedCampaignNamesWhenAmcCampaignIdsDiffer()
    {
        var metrics = new CapturingMetricsRepository();
        var service = NewIngestionService(metrics);
        var csv = """
traffic_date,traffic_hour,time_zone,campaign_id,campaign_name,ad_product_type,impressions,clicks,spend
2026-05-17,10,UTC,A00285502F3SFLYZBXR9H,Women small phrase target,SPONSORED_PRODUCTS,189,5,2.96
""";
        await service.ImportCsvAsync(new AmcResultImportRequest(AccountKey, "traffic-hourly", ProfileId, "UTC"), csv);

        var products = new StubProductAnalyticsRepository([
            new ProductCampaignMapping
            {
                AccountKey = AccountKey,
                ProductId = ProductId,
                CampaignId = "397504685996681",
                CampaignName = "Women small phrase target",
                IsActive = true
            }
        ]);

        var scorecards = new HourlyScorecardService(metrics, products);
        var status = scorecards.GetAmcHourlyDataStatus(AccountKey, ProductId, new DateOnly(2026, 5, 17), new DateOnly(2026, 5, 17));
        var scorecard = scorecards.BuildScorecard(AccountKey, ProductId, new DateOnly(2026, 5, 17), new DateOnly(2026, 5, 17));

        Assert.Equal(1, status.TrafficRows);
        var row = Assert.Single(scorecard);
        Assert.Equal(189, row.Impressions);
        Assert.Equal(5, row.Clicks);
        Assert.Equal(2.96m, row.Spend);
    }

    [Fact]
    public async Task HourlyScorecardUsesAmcRowsWhenDailyReportingIsMissing()
    {
        var metrics = new CapturingMetricsRepository();
        var service = NewIngestionService(metrics);
        var csv = """
conversion_date,conversion_hour,time_zone,campaign_id,campaign_id_string,campaign_name,ad_product_type,tracked_asin,conversion_event_type,purchases,units_sold,sales
2026-05-08,10,UTC,397504685996681,A00285502F3SFLYZBXR9H,Alpha,SPONSORED_PRODUCTS,B0TEST,purchase,2,2,49.98
""";
        await service.ImportCsvAsync(new AmcResultImportRequest(AccountKey, "conversion-hourly", ProfileId, "UTC"), csv);

        var products = new StubProductAnalyticsRepository(MappedCampaignIds);
        var scorecard = new HourlyScorecardService(metrics, products)
            .BuildScorecard(AccountKey, ProductId, new DateOnly(2026, 5, 8), new DateOnly(2026, 5, 14));

        Assert.Single(scorecard);
        Assert.Equal(2, scorecard.Single().Purchases);
        Assert.Equal(10, scorecard.Single().Hour);
    }

    [Fact]
    public async Task AmcHourlyStatusReportsMissingAndPresentRanges()
    {
        var metrics = new CapturingMetricsRepository();
        var products = new StubProductAnalyticsRepository(MappedCampaignIds);
        var scorecards = new HourlyScorecardService(metrics, products);

        var missing = scorecards.GetAmcHourlyDataStatus(
            AccountKey,
            ProductId,
            new DateOnly(2026, 5, 8),
            new DateOnly(2026, 5, 14));
        Assert.True(missing.IsMissing);
        Assert.Equal(0, missing.ConversionRows);

        var service = NewIngestionService(metrics);
        var csv = """
conversion_date,conversion_hour,time_zone,campaign_id,campaign_id_string,campaign_name,ad_product_type,tracked_asin,conversion_event_type,purchases,units_sold,sales
2026-05-08,10,UTC,397504685996681,A00285502F3SFLYZBXR9H,Alpha,SPONSORED_PRODUCTS,B0TEST,purchase,2,2,49.98
""";
        await service.ImportCsvAsync(new AmcResultImportRequest(AccountKey, "conversion-hourly", ProfileId, "UTC"), csv);

        var present = scorecards.GetAmcHourlyDataStatus(
            AccountKey,
            ProductId,
            new DateOnly(2026, 5, 8),
            new DateOnly(2026, 5, 14));
        Assert.False(present.IsMissing);
        Assert.Equal(1, present.ConversionRows);
    }

    [Fact]
    public void AmcHourlyStatusIsNotMissingWhenCoverageCompleteEvenWithZeroRows()
    {
        // Reproduces the user-reported bug: AmcTrafficHourly / AmcConversionsHourly have zero
        // rows for the product's mapped campaigns (AMC returned an empty result for the account),
        // but coverage was marked Queried. The status should report IsMissing=false so AI Review
        // does not re-prompt the user to start an AMC workflow that has already been run.
        var metrics = new CapturingMetricsRepository();
        var products = new StubProductAnalyticsRepository(MappedCampaignIds);
        var scorecards = new HourlyScorecardService(metrics, products);

        var start = new DateOnly(2026, 5, 18);
        var end = new DateOnly(2026, 5, 21);

        var coverageRows = new List<AmcQueryCoverageRow>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            coverageRows.Add(new AmcQueryCoverageRow { AccountKey = AccountKey, ResultType = "traffic-hourly", Date = d, Status = AmcCoverageStatus.Queried });
            coverageRows.Add(new AmcQueryCoverageRow { AccountKey = AccountKey, ResultType = "conversion-hourly", Date = d, Status = AmcCoverageStatus.Queried });
        }
        metrics.UpsertAmcCoverage(coverageRows);

        var status = scorecards.GetAmcHourlyDataStatus(AccountKey, ProductId, start, end);
        Assert.True(status.CoverageComplete);
        Assert.False(status.IsMissing);
        Assert.Equal(0, status.TrafficRows);
        Assert.Equal(0, status.ConversionRows);
        Assert.Equal(0, status.PendingDays);
    }

    [Fact]
    public void AmcHourlyStatusReportsPendingWhenWorkflowsStillRunning()
    {
        var metrics = new CapturingMetricsRepository();
        var products = new StubProductAnalyticsRepository(MappedCampaignIds);
        var scorecards = new HourlyScorecardService(metrics, products);

        var start = new DateOnly(2026, 5, 18);
        var end = new DateOnly(2026, 5, 21);
        var pendingRows = new List<AmcQueryCoverageRow>();
        for (var d = start; d <= end; d = d.AddDays(1))
            pendingRows.Add(new AmcQueryCoverageRow { AccountKey = AccountKey, ResultType = "traffic-hourly", Date = d, Status = AmcCoverageStatus.Pending, WorkflowExecutionId = "exec-1" });
        metrics.UpsertAmcCoverage(pendingRows);

        var status = scorecards.GetAmcHourlyDataStatus(AccountKey, ProductId, start, end);
        Assert.False(status.CoverageComplete);
        Assert.Equal(4, status.PendingDays);
        // IsMissing remains true (no Queried coverage, no row data) — caller can still distinguish
        // via PendingDays > 0 so it shows a "still running" warning instead of a re-prompt.
        Assert.True(status.IsMissing);
    }

    [Fact]
    public async Task AiReviewCreatesSearchTermAndTargetingActionsBeforeHourlyActions()
    {
        var metrics = new CapturingMetricsRepository();
        var products = new StubProductAnalyticsRepository(MappedCampaignIds);
        var service = NewRecommendationService(metrics, products);
        var start = new DateOnly(2026, 5, 22);
        var end = new DateOnly(2026, 5, 28);

        metrics.Daily.AddRange([
            Daily("SearchTerm", start, "bad money box", spend: 14.20m, sales: 0, purchases: 0, clicks: 9, keywordId: "kw-1", adGroupId: "ag-1"),
            Daily("SearchTerm", start, "clear smash bank", spend: 5.10m, sales: 49.99m, purchases: 1, clicks: 3, keywordId: "kw-2", adGroupId: "ag-1"),
            Daily("Targeting", start, "asin-expanded=\"B0BADTARGET\"", spend: 18.75m, sales: 0, purchases: 0, clicks: 12, targetId: "target-1", adGroupId: "ag-2", bid: 0.60m)
        ]);

        var result = await service.AnalyzeAsync(AccountKey, ProductId, start, end);

        Assert.Contains(result.V2Recommendations, r => r.SellerCentralArea == "Negative targeting" && r.ObjectLabel == "bad money box");
        Assert.Contains(result.V2Recommendations, r => r.SellerCentralArea == "Targeting" && r.ObjectLabel == "clear smash bank");
        Assert.Contains(result.V2Recommendations, r => r.SellerCentralArea == "Targeting" && r.ObjectLabel.Contains("B0BADTARGET"));
        Assert.DoesNotContain(result.V2Recommendations, r => r.RecommendationType == "Dayparting");
    }

    [Fact]
    public async Task AiReviewCreatesDaypartingActionsFromAmcHourlyWhenDailyReportsAreMissing()
    {
        var metrics = new CapturingMetricsRepository();
        var products = new StubProductAnalyticsRepository(MappedCampaignIds);
        var service = NewRecommendationService(metrics, products);
        var start = new DateOnly(2026, 5, 23);
        var end = new DateOnly(2026, 5, 29);

        metrics.Traffic.AddRange([
            new AmcTrafficHourly
            {
                AccountKey = AccountKey,
                ProfileId = ProfileId,
                CampaignId = MappedCampaignIds[0],
                CampaignName = MappedCampaignIds[0],
                Date = new DateOnly(2026, 5, 25),
                Hour = 7,
                Spend = 2.45m,
                Clicks = 5,
                Impressions = 185
            },
            new AmcTrafficHourly
            {
                AccountKey = AccountKey,
                ProfileId = ProfileId,
                CampaignId = MappedCampaignIds[0],
                CampaignName = MappedCampaignIds[0],
                Date = new DateOnly(2026, 5, 25),
                Hour = 16,
                Spend = 7.14m,
                Clicks = 9,
                Impressions = 125
            },
            new AmcTrafficHourly
            {
                AccountKey = AccountKey,
                ProfileId = ProfileId,
                CampaignId = MappedCampaignIds[0],
                CampaignName = MappedCampaignIds[0],
                Date = new DateOnly(2026, 5, 27),
                Hour = 13,
                Spend = 6.46m,
                Clicks = 9,
                Impressions = 172
            }
        ]);
        metrics.Conversions.Add(new AmcConversionsHourly
        {
            AccountKey = AccountKey,
            ProfileId = ProfileId,
            CampaignId = MappedCampaignIds[0],
            CampaignName = MappedCampaignIds[0],
            ConversionDate = new DateOnly(2026, 5, 25),
            ConversionHour = 7,
            Purchases = 2,
            UnitsSold = 2,
            Sales = 37.98m
        });

        var result = await service.AnalyzeAsync(AccountKey, ProductId, start, end);

        Assert.True(result.Success);
        Assert.NotEmpty(result.HourlyScorecard);
        Assert.Contains(result.Warnings, w => w.Contains("Search term report data is missing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.V2Recommendations, r =>
            r.RecommendationType == "Dayparting" &&
            r.Title.Contains("inefficient hours", StringComparison.OrdinalIgnoreCase) &&
            r.Reason.Contains("no purchases", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.V2Recommendations, r =>
            r.RecommendationType == "Dayparting" &&
            r.Title.Contains("high-converting", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AiReviewMarksBudgetLimitedDataAndSuppressesDayparting()
    {
        var metrics = new CapturingMetricsRepository();
        var products = new StubProductAnalyticsRepository(MappedCampaignIds);
        var service = NewRecommendationService(metrics, products);
        var date = new DateOnly(2026, 5, 22);

        metrics.Daily.Add(Daily("Campaign", date, "", spend: 19m, sales: 0, purchases: 0, clicks: 20, budget: 20m));
        metrics.Traffic.Add(new AmcTrafficHourly
        {
            AccountKey = AccountKey,
            ProfileId = ProfileId,
            CampaignId = MappedCampaignIds[0],
            CampaignName = MappedCampaignIds[0],
            Date = date,
            Hour = 9,
            Spend = 19m,
            Clicks = 20,
            Impressions = 900
        });

        var result = await service.AnalyzeAsync(AccountKey, ProductId, date, date);

        Assert.Contains(result.V2Recommendations, r => r.DataQualityLabel == "Budget-limited");
        Assert.DoesNotContain(result.V2Recommendations, r => r.RecommendationType == "Dayparting");
        Assert.Contains(result.Warnings, w => w.Contains("Budget-limited data", StringComparison.OrdinalIgnoreCase));
    }

    private static AmcResultIngestionService NewIngestionService(CapturingMetricsRepository metrics) =>
        new(metrics, accountKey => new AmazonAccountConfig
        {
            AccountKey = accountKey,
            ProfileId = ProfileId
        });

    private static ProductAiRecommendationServiceV2 NewRecommendationService(
        CapturingMetricsRepository metrics,
        ProductAnalyticsRepository products)
    {
        var scorecards = new HourlyScorecardService(metrics, products);
        return new ProductAiRecommendationServiceV2(
            metrics,
            products,
            scorecards,
            new RecommendationExperimentService(metrics),
            new AiRecommendationPromptBuilder(),
            new AiRecommendationEvidenceService(),
            null!,
            null!,
            NullLogger<ProductAiRecommendationServiceV2>.Instance);
    }

    private static AdPerformanceDaily Daily(
        string source,
        DateOnly date,
        string label,
        decimal spend,
        decimal sales,
        int purchases,
        int clicks,
        string? keywordId = null,
        string? targetId = null,
        string? adGroupId = null,
        decimal? bid = null,
        decimal? budget = null) => new()
        {
            Date = date,
            SourceReportType = source,
            AccountKey = AccountKey,
            ProfileId = ProfileId,
            ProductId = ProductId,
            CampaignId = MappedCampaignIds[0],
            CampaignName = MappedCampaignIds[0],
            AdGroupId = adGroupId,
            SearchTerm = source == "SearchTerm" ? label : null,
            TargetingText = source == "Targeting" ? label : null,
            SearchTermKind = label.StartsWith("B0", StringComparison.OrdinalIgnoreCase) || label.StartsWith("asin", StringComparison.OrdinalIgnoreCase) ? "ASIN" : "Text",
            KeywordId = keywordId,
            TargetId = targetId,
            Bid = bid,
            CampaignBudgetAmount = budget,
            CampaignStatus = "enabled",
            Impressions = Math.Max(clicks * 20, 1),
            Clicks = clicks,
            Spend = spend,
            Sales = sales,
            Purchases = purchases,
            UnitsSold = purchases,
            ROAS = spend > 0 ? decimal.Round(sales / spend, 2) : 0,
            ACOS = sales > 0 ? decimal.Round(spend / sales, 4) : 0,
            CPC = clicks > 0 ? decimal.Round(spend / clicks, 2) : 0,
            CTR = clicks > 0 ? 0.05m : 0,
            CVR = clicks > 0 ? decimal.Round((decimal)purchases / clicks, 4) : 0,
            CostPerPurchase = purchases > 0 ? spend / purchases : spend,
            PurchaseRate = clicks > 0 ? (decimal)purchases / clicks : 0
        };
}

public sealed class AmazonProductSyncTitleTests
{
    [Fact]
    public void ReplacesPlaceholderTitleWithFetchedAmazonTitle()
    {
        var product = new ProductProfile
        {
            ASIN = "B0FVWSQSVP",
            SKU = "I8-1KFY-KSEM",
            DisplayName = "B0FVWSQSVP / I8-1KFY-KSEM"
        };

        Assert.True(AmazonProductSyncService.ShouldReplaceProductTitle(
            product,
            "10K Smash Box Money Saving Challenge, Clear Acrylic Piggy Bank for Adults, Break to Open Cash Vault, Cash and Coin Collection Box (5.7 inch)"));
    }

    [Fact]
    public void ReplacesStaleVariantSizeTitleWhenProductFamilyMatches()
    {
        var product = new ProductProfile
        {
            ASIN = "B0FVWSQSVP",
            SKU = "I8-1KFY-KSEM",
            DisplayName = "10K Smash Box Money Saving Challenge 6.7 inches"
        };

        Assert.True(AmazonProductSyncService.ShouldReplaceProductTitle(
            product,
            "10K Smash Box Money Saving Challenge, Clear Acrylic Piggy Bank for Adults, Break to Open Cash Vault, Cash and Coin Collection Box (5.7 inch)"));
    }

    [Fact]
    public void DoesNotReplaceCustomTitleJustBecauseFetchedTitleExists()
    {
        var product = new ProductProfile
        {
            ASIN = "B0FVWSQSVP",
            SKU = "I8-1KFY-KSEM",
            DisplayName = "Women small hero product"
        };

        Assert.False(AmazonProductSyncService.ShouldReplaceProductTitle(
            product,
            "10K Smash Box Money Saving Challenge, Clear Acrylic Piggy Bank for Adults, Break to Open Cash Vault, Cash and Coin Collection Box (5.7 inch)"));
    }

    [Fact]
    public void ReplacesPreviouslyTruncatedAmazonTitleWithFullTitle()
    {
        var product = new ProductProfile
        {
            ASIN = "B0DYDH2TYB",
            SKU = "I8-1KFY-KSEX",
            DisplayName = "10K Smash Box Money Saving Challenge, Clear Acrylic Piggy Bank for Adults, Break to Open Cash Vault, Cash and Coin..."
        };

        Assert.True(AmazonProductSyncService.ShouldReplaceProductTitle(
            product,
            "10K Smash Box Money Saving Challenge, Clear Acrylic Piggy Bank for Adults, Break to Open Cash Vault, Cash and Coin Collection Box, Size Large (6.7 inch)"));
    }
}

public sealed class AmcCoveragePlannerTests
{
    [Fact]
    public void ReturnsFullRangeWhenNothingCovered()
    {
        var segments = AmcCoveragePlanner.ComputeMissingSegments(
            new DateOnly(2026, 5, 17),
            new DateOnly(2026, 5, 23),
            new HashSet<DateOnly>());
        var single = Assert.Single(segments);
        Assert.Equal(new DateOnly(2026, 5, 17), single.Start);
        Assert.Equal(new DateOnly(2026, 5, 23), single.End);
    }

    [Fact]
    public void ReturnsEmptyWhenFullyCovered()
    {
        var covered = Enumerable.Range(0, 7).Select(i => new DateOnly(2026, 5, 17).AddDays(i)).ToHashSet();
        var segments = AmcCoveragePlanner.ComputeMissingSegments(
            new DateOnly(2026, 5, 17),
            new DateOnly(2026, 5, 23),
            covered);
        Assert.Empty(segments);
    }

    [Fact]
    public void SplitsOnInternalHoles()
    {
        // Requested: 17..23. Covered: 17, 18, 21. Missing segments: 19-20, 22-23.
        var covered = new HashSet<DateOnly>
        {
            new(2026, 5, 17),
            new(2026, 5, 18),
            new(2026, 5, 21)
        };
        var segments = AmcCoveragePlanner.ComputeMissingSegments(
            new DateOnly(2026, 5, 17),
            new DateOnly(2026, 5, 23),
            covered);
        Assert.Equal(2, segments.Count);
        Assert.Equal(new DateOnly(2026, 5, 19), segments[0].Start);
        Assert.Equal(new DateOnly(2026, 5, 20), segments[0].End);
        Assert.Equal(new DateOnly(2026, 5, 22), segments[1].Start);
        Assert.Equal(new DateOnly(2026, 5, 23), segments[1].End);
    }

    [Fact]
    public void TrailingHoleProducesFinalSegment()
    {
        var covered = new HashSet<DateOnly>
        {
            new(2026, 5, 17),
            new(2026, 5, 18),
            new(2026, 5, 19)
        };
        var segments = AmcCoveragePlanner.ComputeMissingSegments(
            new DateOnly(2026, 5, 17),
            new DateOnly(2026, 5, 23),
            covered);
        var single = Assert.Single(segments);
        Assert.Equal(new DateOnly(2026, 5, 20), single.Start);
        Assert.Equal(new DateOnly(2026, 5, 23), single.End);
    }

    [Fact]
    public void CleanWorkflowSqlStripsTrailingSemicolon()
    {
        // Regression: AMC treats `;` as a statement terminator; a trailing `;` makes the
        // execution SUCCEEDED with a header-only CSV. CleanWorkflowSql must strip it.
        const string sql = "SELECT 1 AS x FROM sponsored_ads_traffic;";
        var cleaned = AmcWorkflowService.CleanWorkflowSql(sql);
        Assert.False(cleaned.EndsWith(";"), $"trailing semicolon not stripped: <{cleaned}>");
        Assert.EndsWith("sponsored_ads_traffic", cleaned);
    }

    [Fact]
    public void CleanWorkflowSqlStripsCommentsAndCollapsesWhitespace()
    {
        const string sql = @"-- header comment
SELECT
  CAST(event_dt AS DATE) AS traffic_date,
  /* inline block */ event_hour
FROM sponsored_ads_traffic
GROUP BY CAST(event_dt AS DATE), event_hour;";
        var cleaned = AmcWorkflowService.CleanWorkflowSql(sql);
        Assert.False(cleaned.Contains("--"), "line comment leaked");
        Assert.False(cleaned.Contains("/*"), "block comment leaked");
        Assert.False(cleaned.Contains("\n"), "newline leaked");
        Assert.False(cleaned.EndsWith(";"), "semicolon leaked");
        Assert.StartsWith("SELECT ", cleaned);
    }

    [Fact]
    public void AssertSuppressionDetectorThrowsWhenAllDatesEmpty()
    {
        // Mirrors the prod failure: AMC returned thousands of rows but the date column was
        // suppressed (empty on every row) because the SELECT/GROUP BY grain was too fine. The
        // ingester must throw a clear error pointing at the privacy-threshold / malformed-SQL causes
        // instead of silently importing 0 rows and marking coverage Queried.
        var rows = new List<Dictionary<string, string>>
        {
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["trafficdate"] = "",
                ["traffichour"] = "9",
                ["campaignid"] = "A00285502F3SFLYZBXR9H",
                ["impressions"] = "7"
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["trafficdate"] = "",
                ["traffichour"] = "10",
                ["campaignid"] = "A0837808OIGJIZ3WVK01",
                ["impressions"] = "12"
            }
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AmcResultIngestionService.AssertDateAndCampaignSurvivedAggregation(rows, "traffic", "traffic_date", "event_date"));
        Assert.Contains("privacy thresholds", ex.Message);
        Assert.Contains("malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssertSuppressionDetectorPassesWhenAtLeastOneRowHasDate()
    {
        var rows = new List<Dictionary<string, string>>
        {
            new(StringComparer.OrdinalIgnoreCase) { ["trafficdate"] = "", ["campaignid"] = "C1" },
            new(StringComparer.OrdinalIgnoreCase) { ["trafficdate"] = "2026-05-18", ["campaignid"] = "C1" }
        };
        // Should not throw — at least one row has date+campaign populated.
        AmcResultIngestionService.AssertDateAndCampaignSurvivedAggregation(rows, "traffic", "traffic_date", "event_date");
    }

    [Fact]
    public void DeleteAmcCoverageClearsRangeOnly()
    {
        var metrics = new CapturingMetricsRepository();
        var rows = Enumerable.Range(0, 7).Select(i => new AmcQueryCoverageRow
        {
            AccountKey = "a",
            ResultType = "traffic-hourly",
            Date = new DateOnly(2026, 5, 17).AddDays(i),
            Status = AmcCoverageStatus.Queried
        }).ToList();
        metrics.UpsertAmcCoverage(rows);

        // Stale-reset the last 3 days only (May 21, 22, 23).
        metrics.DeleteAmcCoverage("a", new DateOnly(2026, 5, 21), new DateOnly(2026, 5, 23));

        var remaining = metrics.GetAmcCoverage("a", "traffic-hourly", new DateOnly(2026, 5, 17), new DateOnly(2026, 5, 23));
        Assert.Equal(4, remaining.Count);
        Assert.All(remaining, r => Assert.True(r.Date <= new DateOnly(2026, 5, 20)));
    }

    [Fact]
    public void RoundTripsThroughRepositoryStub()
    {
        var metrics = new CapturingMetricsRepository();
        metrics.UpsertAmcCoverage(new[]
        {
            new AmcQueryCoverageRow { AccountKey = "a", ResultType = "traffic-hourly", Date = new DateOnly(2026, 5, 17), Status = AmcCoverageStatus.Queried },
            new AmcQueryCoverageRow { AccountKey = "a", ResultType = "traffic-hourly", Date = new DateOnly(2026, 5, 18), Status = AmcCoverageStatus.Pending, WorkflowExecutionId = "exec-1" }
        });

        // Overwriting the same key replaces the row.
        metrics.UpsertAmcCoverage(new[]
        {
            new AmcQueryCoverageRow { AccountKey = "a", ResultType = "traffic-hourly", Date = new DateOnly(2026, 5, 18), Status = AmcCoverageStatus.Queried }
        });

        var rows = metrics.GetAmcCoverage("a", "traffic-hourly", new DateOnly(2026, 5, 17), new DateOnly(2026, 5, 23));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(AmcCoverageStatus.Queried, r.Status));
    }
}

internal sealed class CapturingMetricsRepository : AdMetricsRepository
{
    public CapturingMetricsRepository()
        : base(new ConfigurationBuilder().Build())
    {
    }

    public List<AmcTrafficHourly> Traffic { get; } = [];
    public List<AmcConversionsHourly> Conversions { get; } = [];
    public List<AmcAttributionLag> AttributionLag { get; } = [];
    public List<AdPerformanceDaily> Daily { get; } = [];
    public List<HourlyScorecard> Scorecard { get; } = [];
    public List<AmcQueryCoverageRow> Coverage { get; } = [];
    public List<AiRecommendation> Recommendations { get; } = [];
    public Dictionary<string, List<AiRecommendationEvidence>> Evidence { get; } = new();
    public List<RecommendationExperiment> Experiments { get; } = [];

    public override void UpsertAmcTrafficHourly(IEnumerable<AmcTrafficHourly> rows) =>
        Traffic.AddRange(rows);

    public override void UpsertAmcConversionsHourly(IEnumerable<AmcConversionsHourly> rows) =>
        Conversions.AddRange(rows);

    public override void UpsertAmcAttributionLag(IEnumerable<AmcAttributionLag> rows) =>
        AttributionLag.AddRange(rows);

    public override IReadOnlyList<AmcQueryCoverageRow> GetAmcCoverage(string accountKey, string resultType, DateOnly start, DateOnly end) =>
        Coverage
            .Where(c => c.AccountKey == accountKey && c.ResultType == resultType && c.Date >= start && c.Date <= end)
            .ToList();

    public override IReadOnlyList<AmcQueryCoverageRow> GetAmcCoverageByExecutionId(string accountKey, string resultType, string workflowExecutionId) =>
        Coverage
            .Where(c => c.AccountKey == accountKey && c.ResultType == resultType && c.WorkflowExecutionId == workflowExecutionId)
            .ToList();

    public override void UpsertAmcCoverage(IEnumerable<AmcQueryCoverageRow> rows)
    {
        foreach (var row in rows)
        {
            Coverage.RemoveAll(c => c.AccountKey == row.AccountKey && c.ResultType == row.ResultType && c.Date == row.Date);
            Coverage.Add(row);
        }
    }

    public override void DeleteAmcCoverage(string accountKey, DateOnly start, DateOnly end) =>
        Coverage.RemoveAll(c => c.AccountKey == accountKey && c.Date >= start && c.Date <= end);

    public override AiRecommendation UpsertRecommendation(AiRecommendation row)
    {
        Recommendations.RemoveAll(r => r.RecommendationId == row.RecommendationId);
        Recommendations.Add(row);
        return row;
    }

    public override void DeleteOpenRecommendations(string accountKey, string productId) =>
        Recommendations.RemoveAll(r => r.AccountKey == accountKey && r.ProductId == productId && r.Status != "Applied");

    public override IReadOnlyList<AiRecommendation> GetRecommendations(string accountKey, string productId) =>
        Recommendations
            .Where(r => r.AccountKey == accountKey && r.ProductId == productId)
            .ToList();

    public override AiRecommendation? GetRecommendation(string recommendationId) =>
        Recommendations.FirstOrDefault(r => r.RecommendationId == recommendationId);

    public override void ReplaceEvidence(string recommendationId, IEnumerable<AiRecommendationEvidence> rows) =>
        Evidence[recommendationId] = rows.ToList();

    public override IReadOnlyList<AiRecommendationEvidence> GetEvidence(string recommendationId) =>
        Evidence.TryGetValue(recommendationId, out var rows) ? rows : [];

    public override IReadOnlyList<RecommendationExperiment> GetExperiments(string productId) =>
        Experiments.Where(e => e.ProductId == productId).ToList();

    public override IReadOnlyList<AdPerformanceDaily> GetDailyMetrics(
        string accountKey,
        string productId,
        IEnumerable<string> campaignIds,
        DateOnly start,
        DateOnly end)
    {
        var ids = campaignIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Daily
            .Where(r => r.AccountKey == accountKey &&
                        r.ProductId == productId &&
                        ids.Contains(r.CampaignId) &&
                        r.Date >= start &&
                        r.Date <= end)
            .ToList();
    }

    public override IReadOnlyList<AmcTrafficHourly> GetTrafficHourly(
        string accountKey,
        IEnumerable<string> campaignIds,
        DateOnly start,
        DateOnly end)
        => GetTrafficHourly(accountKey, campaignIds, [], start, end);

    public override IReadOnlyList<AmcTrafficHourly> GetTrafficHourly(
        string accountKey,
        IEnumerable<string> campaignIds,
        IEnumerable<string> campaignNames,
        DateOnly start,
        DateOnly end)
    {
        var ids = campaignIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = campaignNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Traffic
            .Where(r => r.AccountKey == accountKey &&
                        (ids.Contains(r.CampaignId) || names.Contains(r.CampaignName)) &&
                        r.Date >= start &&
                        r.Date <= end)
            .ToList();
    }

    public override IReadOnlyList<AmcConversionsHourly> GetConversionsHourly(
        string accountKey,
        IEnumerable<string> campaignIds,
        DateOnly start,
        DateOnly end)
        => GetConversionsHourly(accountKey, campaignIds, [], start, end);

    public override IReadOnlyList<AmcConversionsHourly> GetConversionsHourly(
        string accountKey,
        IEnumerable<string> campaignIds,
        IEnumerable<string> campaignNames,
        DateOnly start,
        DateOnly end)
    {
        var ids = campaignIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = campaignNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Conversions
            .Where(r => r.AccountKey == accountKey &&
                        (ids.Contains(r.CampaignId) || names.Contains(r.CampaignName)) &&
                        r.ConversionDate >= start &&
                        r.ConversionDate <= end)
            .ToList();
    }

    public override void ReplaceScorecard(
        string accountKey,
        string productId,
        DateOnly start,
        DateOnly end,
        IEnumerable<HourlyScorecard> rows)
    {
        Scorecard.Clear();
        Scorecard.AddRange(rows);
    }
}

internal sealed class StubProductAnalyticsRepository : ProductAnalyticsRepository
{
    private readonly IReadOnlyList<ProductCampaignMapping> _mappings;

    public StubProductAnalyticsRepository(IEnumerable<string> campaignIds)
    {
        _mappings = campaignIds
            .Select(id => new ProductCampaignMapping
            {
                AccountKey = "test-account",
                ProductId = "test-product",
                CampaignId = id,
                CampaignName = id,
                IsActive = true
            })
            .ToList();
    }

    public StubProductAnalyticsRepository(IEnumerable<ProductCampaignMapping> mappings)
    {
        _mappings = mappings.ToList();
    }

    public override ProductProfile? GetProduct(string productId) =>
        productId == "test-product"
            ? new ProductProfile
            {
                Id = productId,
                AccountKey = "test-account",
                DisplayName = "Test product",
                ASIN = "B0TEST",
                TargetAcos = 0.30m
            }
            : null;

    public override IReadOnlyList<ProductCampaignMapping> GetMappings(string accountKey, string productId) =>
        _mappings
            .Where(m => m.AccountKey == accountKey && m.ProductId == productId)
            .ToList();
}
