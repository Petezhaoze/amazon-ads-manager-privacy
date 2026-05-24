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

    private static AmcResultIngestionService NewIngestionService(CapturingMetricsRepository metrics) =>
        new(metrics, accountKey => new AmazonAccountConfig
        {
            AccountKey = accountKey,
            ProfileId = ProfileId
        });
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
    {
        var ids = campaignIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Traffic
            .Where(r => r.AccountKey == accountKey &&
                        ids.Contains(r.CampaignId) &&
                        r.Date >= start &&
                        r.Date <= end)
            .ToList();
    }

    public override IReadOnlyList<AmcConversionsHourly> GetConversionsHourly(
        string accountKey,
        IEnumerable<string> campaignIds,
        DateOnly start,
        DateOnly end)
    {
        var ids = campaignIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Conversions
            .Where(r => r.AccountKey == accountKey &&
                        ids.Contains(r.CampaignId) &&
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
