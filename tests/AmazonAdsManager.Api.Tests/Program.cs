using AmazonAdsManager.Api.Services;
using AmazonAdsManager.Shared.Models;
using Microsoft.Extensions.Configuration;
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

    private static AmcResultIngestionService NewIngestionService(CapturingMetricsRepository metrics) =>
        new(metrics, accountKey => new AmazonAccountConfig
        {
            AccountKey = accountKey,
            ProfileId = ProfileId
        });
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
